using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ardalis.GuardClauses;
using SharpDbg.Application.Protocol;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.DapProtocol;
using static SharpDbg.DapProtocol.DapResponse;
using DapBreakpoint = SharpDbg.DapProtocol.Breakpoint;
using DapStackFrame = SharpDbg.DapProtocol.StackFrame;
using DapThread = SharpDbg.DapProtocol.Thread;

namespace SharpDbg.Application;

/// <summary>
/// Main debug adapter that coordinates DAP protocol and debugger engine
/// </summary>
public class DebugAdapter : IDapRequestHandler
{
	private ManagedDebugger _debugger;
	private readonly Action<string>? _logger;
	private bool _clientLinesStartAt1 = true;
	private bool _clientColumnsStartAt1 = true;
	private int _shutdownRequested;

	// The last launch/attach, replayed by a restart request.
	private LaunchInfo? _lastLaunchInfo;
	private bool _lastJustMyCode = true;
	private int? _lastAttachProcessId;
	private string? _lastAttachDebuggerLibraryDirectory;
	private RemoteAttachInfo? _lastRemoteAttachInfo;
	private bool _enableEditAndContinue;
	private bool _decompileExternalCode = true;
	private TimeSpan _clientSymbolsTimeout = ManagedDebugger.DefaultClientSymbolsTimeout;
	private readonly Dictionary<string, SharpDbgBreakpointRequest[]> _sourceBreakpoints = new(StringComparer.Ordinal);
	private SharpDbgFunctionBreakpointRequest[] _functionBreakpoints = [];
	private IReadOnlyList<SharpDbgExceptionBreakpointRequest> _exceptionBreakpoints = [];
	// The subscription whose events may reach the client; see SubscribeToDebuggerEvents.
	private object? _debuggerEventToken;

	/// <summary>Monotonically increases whenever a restart constructs a replacement debugger.</summary>
	public int DebuggerGeneration { get; private set; } = 1;

	/// <summary>
	/// Raised once the debug session has ended (a disconnect request has been fully handled).
	/// Hosts which own the process lifetime (e.g. SharpDbg.Cli) should subscribe and exit when it fires.
	/// </summary>
	public event Action? ShutdownRequested;

	public DapConnection Connection { get; private set; } = null!;

	public DebugAdapter(Action<string>? logger = null)
	{
		_logger = logger;
		_debugger = new ManagedDebugger(logger);

		// Subscribe to debugger events
		SubscribeToDebuggerEvents();
	}

	/// <summary>
	/// Signals the end of the debug session and raises <see cref="ShutdownRequested"/> so the hosting
	/// process can shut down. Idempotent.
	/// Only called once a 'disconnect' request has been fully handled - 'terminate' must keep the adapter alive.
	/// </summary>
	private void RequestDebuggerProcessShutdown()
	{
		if (Interlocked.Exchange(ref _shutdownRequested, 1) is not 0) return;

		_logger?.Invoke("RequestDebuggerProcessShutdown");
		ShutdownRequested?.Invoke();
	}

	/// <summary>
	/// Releases debugger resources when the session ends without a disconnect request having been received
	/// (e.g. the client crashed or closed its stream)
	/// </summary>
	public void DapAborted_ShutdownDebugger()
	{
		try
		{
			// No-op if a disconnect request was already handled - Disconnect only disposes once
			_debugger.Disconnect(true);
		}
		catch { /* */ }
	}

	/// <summary>
	/// Creates the protocol connection over the given streams. Call <see cref="DapConnection.Start"/> to begin processing messages.
	/// </summary>
	public void Initialize(Stream input, Stream output)
	{
		Connection = new DapConnection(input, output, this, new DapConnectionOptions { Log = _logger });
		Connection.RegisterRequest<ResolveStackFrameArguments>();
		Connection.RegisterRequest<ApplyDeltasArguments>();
		Connection.RegisterRequest<LoadSymbolsArguments>();
		Connection.RegisterRequest<VsCustomMessageArguments>();
	}

	public ValueTask<DapResponse> HandleRequestAsync(IDapRequest request, DapRequestContext context)
		=> TrackRequestAsync(request.Command, HandleRequestCoreAsync(request, context));

	private ValueTask<DapResponse> HandleRequestCoreAsync(IDapRequest request, DapRequestContext context) => request switch
	{
		InitializeArguments args => Respond(args, a => HandleInitialize(a, context)),
		LaunchArguments args => Respond(args, HandleLaunchAsync),
		AttachArguments args => Respond(args, HandleAttachAsync),
		RestartArguments args => Respond(args, HandleRestartAsync),
		ConfigurationDoneArguments args => Respond(args, HandleConfigurationDoneAsync),
		SetBreakpointsArguments args => Respond(args, HandleSetBreakpointsAsync),
		BreakpointLocationsArguments args => Respond(args, HandleBreakpointLocationsAsync),
		SetFunctionBreakpointsArguments args => Respond(args, HandleSetFunctionBreakpointsAsync),
		SetExceptionBreakpointsArguments args => Respond(args, HandleSetExceptionBreakpointsAsync),
		ThreadsArguments args => Respond(args, HandleThreadsAsync),
		StackTraceArguments args => Respond(args, HandleStackTraceAsync),
		ResolveStackFrameArguments args => Respond(args, HandleResolveStackFrameAsync),
		ApplyDeltasArguments args => Respond(args, HandleApplyDeltasAsync),
		// Not under the request lock: the thread that asked for symbols holds it while it waits for this answer.
		LoadSymbolsArguments args => Respond(args, HandleLoadSymbols),
		ScopesArguments args => Respond(args, HandleScopesAsync),
		VariablesArguments args => Respond(args, HandleVariablesAsync),
		SetVariableArguments args => Respond(args, HandleSetVariableAsync),
		LoadedSourcesArguments args => Respond(args, HandleLoadedSourcesAsync),
		EvaluateArguments args => Respond(args, HandleEvaluateAsync),
		ContinueArguments args => Respond(args, HandleContinueAsync),
		NextArguments args => Respond(args, HandleNextAsync),
		StepInArguments args => Respond(args, HandleStepInAsync),
		StepOutArguments args => Respond(args, HandleStepOutAsync),
		PauseArguments args => Respond(args, HandlePauseAsync),
		DisconnectArguments args => Respond(args, a => HandleDisconnectAsync(a, context)),
		TerminateArguments args => Respond(args, HandleTerminateAsync),
		ExceptionInfoArguments args => Respond(args, HandleExceptionInfoAsync),
		VsCustomMessageArguments args => Respond(args, HandleVsCustomMessage),
		_ => NotSupported(request),
	};

	// A request that never answers otherwise leaves nothing in the log. This watchdog runs on a dedicated thread, so it
	// still reports when the thread pool is starved, with the request's phase and the pool's counters.
	private sealed class InFlightRequest(string command)
	{
		public readonly string Command = command;
		public readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();
		public volatile string Phase = "dispatching";
		public double NextReportSeconds = 5;
	}

	private readonly ConcurrentDictionary<InFlightRequest, byte> _inFlight = new();
	private readonly AsyncLocal<InFlightRequest?> _currentRequest = new();
	private volatile InFlightRequest? _lockHolder;
	private System.Threading.Thread? _requestWatchdog;

	private void StartRequestWatchdog()
	{
		if (_requestWatchdog is not null || _logger is null) return;
		var watchdog = new System.Threading.Thread(WatchRequests) { IsBackground = true, Name = "SharpDbg request watchdog" };
		if (Interlocked.CompareExchange(ref _requestWatchdog, watchdog, null) is null) watchdog.Start();
	}

	private void WatchRequests()
	{
		while (true)
		{
			System.Threading.Thread.Sleep(1000);
			foreach (var request in _inFlight.Keys)
			{
				var seconds = request.Watch.Elapsed.TotalSeconds;
				if (seconds < request.NextReportSeconds) continue;
				request.NextReportSeconds = seconds + 10;
				var holder = _lockHolder;
				_logger?.Invoke($"DAP request {request.Command} still running after {seconds:0}s: {request.Phase}; lock held by "
					+ (holder is null ? "no request (free, or a runtime event)" : holder == request ? "this request" : $"{holder.Command} ({holder.Phase}, {holder.Watch.Elapsed.TotalSeconds:0}s)")
					+ $"; thread pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued");
			}
		}
	}

	private async ValueTask<DapResponse> TrackRequestAsync(string command, ValueTask<DapResponse> pending)
	{
		_logger?.Invoke("DAP request: " + command);
		StartRequestWatchdog();
		var inFlight = new InFlightRequest(command);
		_inFlight.TryAdd(inFlight, 0);
		_currentRequest.Value = inFlight;
		try
		{
			return await pending;
		}
		finally
		{
			_inFlight.TryRemove(inFlight, out _);
		}
	}

	/// <summary>
	/// Runs <paramref name="func"/> while holding the debugger's request/runtime-event lock, converting any exception
	/// thrown by the debugger engine into an error response.
	/// </summary>
	private ValueTask<DapResult<T>> RunLocked<T>(string failureMessage, Func<DapResult<T>> func)
	{
		return RunLocked(failureMessage, () => ValueTask.FromResult(func()));
	}

	private async ValueTask<DapResult<T>> RunLocked<T>(string failureMessage, Func<ValueTask<DapResult<T>>> func)
	{
		try
		{
			var request = _currentRequest.Value;
			if (request is not null) request.Phase = "waiting for the request/runtime-event lock";
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				_lockHolder = request;
				if (request is not null) request.Phase = "draining queued runtime events";
				try
				{
					await _debugger.DrainRuntimeEventQueue();
					if (request is not null) request.Phase = "running the handler";
					return await func().ConfigureAwait(false);
				}
				finally
				{
					if (_lockHolder == request) _lockHolder = null;
				}
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"{failureMessage}: {ex.Message} , {ex}");
			return new DapError($"{failureMessage}: {ex.Message}");
		}
	}

	private static void ApplyExpressionEvaluationOptions(JsonObject config)
	{
		ManagedDebugger.AllowToString = true;
		ManagedDebugger.ShowRawValues = false;
		if (config["expressionEvaluationOptions"] is not JsonObject options) return;
		if (GetConfigValue<bool?>(options, "allowToString") is bool allowToString) ManagedDebugger.AllowToString = allowToString;
		if (GetConfigValue<bool?>(options, "showRawValues") is bool showRawValues) ManagedDebugger.ShowRawValues = showRawValues;
	}

	private static T? GetConfigValue<T>(JsonObject? config, string key)
	{
		if (config?.TryGetPropertyValue(key, out var node) is true && node is not null)
		{
			return node.Deserialize<T>(ConfigurationJsonOptions);
		}
		return default;
	}

	// Matches the leniency of the previous implementation, e.g. VS Code's process picker sends processId as a string
	private static readonly JsonSerializerOptions ConfigurationJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
	};

	private void SubscribeToDebuggerEvents()
	{
		// A restart replaces the debugger while the old one is still winding down: killing its debuggee raises
		// exited/terminated on it, which the client would take for the end of the whole session. Each subscription
		// holds its own token and a restart withdraws the current one before it touches the old debugger, so
		// nothing the old debugger raises from then on reaches the client.
		var token = new object();
		_debuggerEventToken = token;
		bool IsCurrentDebugger() => ReferenceEquals(token, Volatile.Read(ref _debuggerEventToken));

		_debugger.OnStopped += (threadId, reason) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new StoppedEvent
			{
				Reason = ConvertStopReason(reason),
				ThreadId = threadId,
				AllThreadsStopped = true
			});
		};

		_debugger.OnStopped2 += (threadId, filePath, line, column, reason, hitBreakpointIds) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new StoppedEvent
			{
				Reason = ConvertStopReason(reason),
				ThreadId = threadId,
				AllThreadsStopped = true,
				HitBreakpointIds = hitBreakpointIds,
				// Not part of the DAP specification - lets clients show the stop location without a stackTrace request
				AdditionalProperties = new JsonObject
				{
					["source"] = new JsonObject { ["path"] = filePath },
					["line"] = line,
					["column"] = column
				}
			});
		};

		_debugger.OnBreakpointChanged += (breakpoint, hitMessage) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new BreakpointEvent
			{
				Reason = BreakpointEvent.ReasonValue.Changed,
				Breakpoint = new DapBreakpoint
				{
					Id = breakpoint.Id,
					Verified = breakpoint.Verified,
					Line = breakpoint.IsFunctionBreakpoint ? null : ConvertDebuggerLineToClient(breakpoint.Line),
					Column = breakpoint is { IsFunctionBreakpoint: false, Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(breakpoint.Column.Value) : null,
					EndLine = breakpoint is { IsFunctionBreakpoint: false, EndLine: { } endLine } ? ConvertDebuggerLineToClient(endLine) : null,
					EndColumn = breakpoint is { IsFunctionBreakpoint: false, EndColumn: { } endColumn } ? ConvertDebuggerColumnToClient(endColumn) : null,
					Offset = breakpoint is { IsFunctionBreakpoint: false, Verified: true } ? 0 : null,
					Message = hitMessage ?? breakpoint.Message,
					Source = breakpoint is not { IsFunctionBreakpoint: false, Verified: true } ? null : new Source
					{
						Path = breakpoint.FilePath,
						Name = Path.GetFileName(breakpoint.FilePath),
						SourceReference = 0
					}
				}
			});
		};

		_debugger.OnContinued += (threadId) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new ContinuedEvent
			{
				ThreadId = threadId,
				AllThreadsContinued = true
			});
		};

		_debugger.OnExited += exitCode =>
		{
			if (!IsCurrentDebugger()) return;
			// exitCode will be null in the attach scenario, as we cannot obtain the exit code of the process we attached to
			Connection.SendEvent(new ExitedEvent
			{
				ExitCode = exitCode ?? 0
			});
		};

		_debugger.OnTerminated += () =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new TerminatedEvent());
		};

		_debugger.OnThreadStarted += threadId =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new ThreadEvent
			{
				Reason = ThreadEvent.ReasonValue.Started,
				ThreadId = threadId
			});
		};

		_debugger.OnThreadExited += threadId =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new ThreadEvent
			{
				Reason = ThreadEvent.ReasonValue.Exited,
				ThreadId = threadId
			});
		};

		_debugger.OnSymbolsNeeded += (name, path, mvid) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new SymbolsNeededEvent
			{
				ModuleName = name,
				ModulePath = path,
				Mvid = mvid.ToString()
			});
		};
		_debugger.OnModuleLoaded += (id, name, path) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new ModuleEvent
			{
				Reason = ModuleEvent.ReasonValue.New,
				Module = new Module
				{
					Id = id,
					Name = name,
					Path = path
				}
			});
		};
		_debugger.OnModuleUnloaded += (id, name, path) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new ModuleEvent
			{
				Reason = ModuleEvent.ReasonValue.Removed,
				Module = new Module
				{
					Id = id,
					Name = name,
					Path = path
				}
			});
		};

		_debugger.OnProcessStarted += (processId, name) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new ProcessEvent
			{
				Name = name,
				SystemProcessId = processId,
				StartMethod = ProcessEvent.StartMethodValue.Launch,
				IsLocalProcess = true
			});
		};

		_debugger.OnOutput += (output, isStdError) =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new OutputEvent
			{
				Category = isStdError ? OutputEvent.CategoryValue.Stderr : OutputEvent.CategoryValue.Stdout,
				Output = output
			});
		};
		_debugger.OnImportantOutput += output =>
		{
			if (!IsCurrentDebugger()) return;
			Connection.SendEvent(new OutputEvent
			{
				Category = OutputEvent.CategoryValue.Important,
				Output = output
			});
		};
		_debugger.SendRunInTerminalRequest += launchInfo =>
		{
			if (!IsCurrentDebugger()) return 0;
			var programForTitle = launchInfo.Program is "dotnet" ? launchInfo.Arguments[0] : launchInfo.Program;
			var runInTerminalArguments = new RunInTerminalArguments
			{
				Kind = launchInfo.LaunchRequestConsoleType switch
				{
					LaunchRequestConsoleType.IntegratedTerminal => RunInTerminalArguments.KindValue.Integrated,
					LaunchRequestConsoleType.ExternalTerminal => RunInTerminalArguments.KindValue.External,
					_ => throw new ArgumentOutOfRangeException(nameof(launchInfo.LaunchRequestConsoleType), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{launchInfo.LaunchRequestConsoleType}'")
				},
				Args = [launchInfo.Program, ..launchInfo.Arguments],
				Cwd = launchInfo.Cwd ?? Environment.CurrentDirectory,
				Env = new Dictionary<string, string?>(launchInfo.Env.Select(kvp => KeyValuePair.Create(kvp.Key, (string?)kvp.Value)))
				{
					["DOTNET_DefaultDiagnosticPortSuspend"] = "1"
				},
				Title = $"{Path.GetFileName(programForTitle)} [DEBUG]"
			};
			// Called synchronously from within a request handler (launch), which is safe as responses are read independently of request dispatch
			var result = Connection.SendRequest(runInTerminalArguments);
			return result switch
			{
				DapError error => throw new InvalidOperationException($"RunInTerminalRequest failed: {error.Message}"),
				// https://github.com/microsoft/vscode/issues/61640 - ProcessId will not be returned for integratedTerminal or externalTerminal
				// ShellProcessId will be returned for integratedTerminal but not externalTerminal
				RunInTerminalResponse { ProcessId: null } => throw new InvalidOperationException("RunInTerminalRequest did not return a process ID. VSCode does not return a process ID for integratedTerminal or externalTerminal. Use internalConsole instead, or use a compliant DAP client. See: https://github.com/microsoft/vscode/issues/61640"),
				RunInTerminalResponse response => response.ProcessId.Value,
				null => throw new InvalidOperationException("RunInTerminalRequest returned no result")
			};
		};
	}

	// Command handlers
	private static DapResult<VsCustomMessageResponse> HandleVsCustomMessage(VsCustomMessageArguments arguments) => new VsCustomMessageResponse();

	private DapResult<Capabilities> HandleInitialize(InitializeArguments arguments, DapRequestContext context)
	{
		_clientLinesStartAt1 = arguments.LinesStartAt1 ?? true;
		_clientColumnsStartAt1 = arguments.ColumnsStartAt1 ?? true;

		// The initialized event must be sent after the initialize response
		context.OnResponseSent(() => Connection.SendEvent(new InitializedEvent()));

		return new Capabilities
		{
			SupportsConfigurationDoneRequest = true,
			SupportsFunctionBreakpoints = true,
			SupportsConditionalBreakpoints = true,
			SupportsHitConditionalBreakpoints = true,
			SupportsEvaluateForHovers = true,
			SupportsStepBack = false,
			SupportsSetVariable = true,
			SupportsRestartFrame = false,
			SupportsTerminateRequest = true,
			SupportsExceptionInfoRequest = true,
			SupportsExceptionFilterOptions = true,
			SupportsStepInTargetsRequest = false,
			SupportsGotoTargetsRequest = false,
			SupportsRestartRequest = true,
			SupportsLoadedSourcesRequest = true,
			SupportsBreakpointLocationsRequest = true,
			SupportsSingleThreadExecutionRequests = true,
			// SharpDbg-specific: launch/attach decompileExternalCode, the symbolsNeeded event and the loadSymbols request.
			AdditionalProperties = new JsonObject { ["supportsClientSymbols"] = true },
			ExceptionBreakpointFilters =
			[
				new ExceptionBreakpointsFilter { Filter = "all", Label = "All Exceptions", Default = false },
				new ExceptionBreakpointsFilter { Filter = "user-unhandled", Label = "User-Unhandled Exceptions", Default = true }
			]
		};
	}

	private ValueTask<DapResult<LaunchResponse>> HandleLaunchAsync(LaunchArguments arguments)
	{
		return RunLocked<LaunchResponse>("Failed to launch", () =>
		{
			var config = arguments.AdditionalProperties;
			var program = GetConfigValue<string>(config, "program");
			if (string.IsNullOrEmpty(program))
			{
				return new DapError("Failed to launch: Missing program path");
			}

			var args = GetConfigValue<List<string>>(config, "args") ?? [];
			var cwd = GetConfigValue<string>(config, "cwd");
			var env = GetConfigValue<Dictionary<string, string>>(config, "env") ?? [];
			var stopAtEntry = GetConfigValue<bool?>(config, "stopAtEntry") ?? false;
			var justMyCode = GetConfigValue<bool?>(config, "justMyCode") ?? true;
			_enableEditAndContinue = GetConfigValue<bool?>(config, "enableEditAndContinue") ?? false;
			_decompileExternalCode = GetConfigValue<bool?>(config, "decompileExternalCode") ?? true;
			_clientSymbolsTimeout = GetConfigValue<int?>(config, "clientSymbolsTimeoutMs") is int timeoutMs && timeoutMs > 0
				? TimeSpan.FromMilliseconds(timeoutMs)
				: ManagedDebugger.DefaultClientSymbolsTimeout;
			_debugger.EnableEditAndContinue = _enableEditAndContinue;
			_debugger.DecompileExternalCode = _decompileExternalCode;
			_debugger.ClientSymbolsTimeout = _clientSymbolsTimeout;
			ApplyExpressionEvaluationOptions(config);
			var console = GetConfigValue<string>(config, "console");
			LaunchRequestConsoleType? launchRequestConsoleType = console switch
			{
				"integratedTerminal" => LaunchRequestConsoleType.IntegratedTerminal,
				"externalTerminal" => LaunchRequestConsoleType.ExternalTerminal,
				"internalConsole" => LaunchRequestConsoleType.InternalConsole,
				null => LaunchRequestConsoleType.InternalConsole, // Default to internalConsole if not specified
				_ => null
			};
			if (launchRequestConsoleType is null) return new DapError($"Failed to launch: Invalid console type: '{console}'");

			(program, args) = Path.GetExtension(program).Equals(".dll", StringComparison.OrdinalIgnoreCase)
				? ("dotnet", [program, ..args])
				: (program, args);

			var launchInfo = new LaunchInfo
			{
				Program = program,
				Arguments = args,
				Cwd = cwd,
				Env = env,
				StopAtEntry = stopAtEntry,
				LaunchRequestConsoleType = launchRequestConsoleType.Value,
				NoDebug = arguments.NoDebug ?? false
			};

			_lastLaunchInfo = launchInfo;
			_lastJustMyCode = justMyCode;
			_lastAttachProcessId = null;
			_lastAttachDebuggerLibraryDirectory = null;
			_lastRemoteAttachInfo = null;
			_debugger.Launch(launchInfo, justMyCode);
			return new LaunchResponse();
		});
	}

	private ValueTask<DapResult<AttachResponse>> HandleAttachAsync(AttachArguments arguments)
	{
		return RunLocked<AttachResponse>("Failed to attach", () =>
		{
			var config = arguments.AdditionalProperties;
			RemoteAttachInfo? remoteAttachInfo = null;
			var processId = GetConfigValue<int?>(config, "processId");
			if (processId is null)
			{
				return new DapError("Failed to attach: Missing process ID");
			}
			var coreClrMobileDebuggerOptions = GetConfigValue<CoreClrMobileDebuggerOptions>(config, "coreClrMobileDebuggerOptions");
			if (coreClrMobileDebuggerOptions is not null)
			{
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.Address);
				Guard.Against.Null(coreClrMobileDebuggerOptions.Port);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.Platform);
				Guard.Against.Null(coreClrMobileDebuggerOptions.IsServer);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.MscordbiPath);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.AssembliesPath);
				if (File.Exists(coreClrMobileDebuggerOptions.MscordbiPath) is false) return new DapError($"Failed to attach: coreClrMobileDebuggerOptions.mscordbiPath does not exist: {coreClrMobileDebuggerOptions.MscordbiPath}");
				if (Directory.Exists(coreClrMobileDebuggerOptions.AssembliesPath) is false) return new DapError($"Failed to attach: coreClrMobileDebuggerOptions.assembliesPath does not exist: {coreClrMobileDebuggerOptions.AssembliesPath}");
				remoteAttachInfo = new RemoteAttachInfo
				{
					Address = coreClrMobileDebuggerOptions.Address,
					Port = coreClrMobileDebuggerOptions.Port,
					Platform = coreClrMobileDebuggerOptions.Platform,
					IsServer = coreClrMobileDebuggerOptions.IsServer,
					MscordbiPath = coreClrMobileDebuggerOptions.MscordbiPath,
					AssembliesPath = coreClrMobileDebuggerOptions.AssembliesPath
				};
			}
			var justMyCode = GetConfigValue<bool?>(config, "justMyCode") ?? true;
			_enableEditAndContinue = GetConfigValue<bool?>(config, "enableEditAndContinue") ?? false;
			_decompileExternalCode = GetConfigValue<bool?>(config, "decompileExternalCode") ?? true;
			_clientSymbolsTimeout = GetConfigValue<int?>(config, "clientSymbolsTimeoutMs") is int timeoutMs && timeoutMs > 0
				? TimeSpan.FromMilliseconds(timeoutMs)
				: ManagedDebugger.DefaultClientSymbolsTimeout;
			_debugger.EnableEditAndContinue = _enableEditAndContinue;
			_debugger.DecompileExternalCode = _decompileExternalCode;
			_debugger.ClientSymbolsTimeout = _clientSymbolsTimeout;
			ApplyExpressionEvaluationOptions(config);
			var debuggerLibraryDirectory = GetConfigValue<string>(config, "debuggerLibraryDirectory");
			if (debuggerLibraryDirectory is not null && Directory.Exists(debuggerLibraryDirectory) is false)
				return new DapError($"Failed to attach: debuggerLibraryDirectory does not exist: {debuggerLibraryDirectory}");
			var continueStoppedProcess = GetConfigValue<bool?>(config, "continueStoppedProcess") ?? false;
			if (continueStoppedProcess && !OperatingSystem.IsMacOS())
				return new DapError("Failed to attach: continueStoppedProcess is supported on macOS only");
			_lastLaunchInfo = null;
			_lastJustMyCode = justMyCode;
			_lastAttachProcessId = remoteAttachInfo is null ? processId.Value : null;
			_lastAttachDebuggerLibraryDirectory = remoteAttachInfo is null ? debuggerLibraryDirectory : null;
			_lastRemoteAttachInfo = remoteAttachInfo;
			if (remoteAttachInfo is null) _debugger.Attach(processId.Value, justMyCode, debuggerLibraryDirectory, continueStoppedProcess);
			else _debugger.AttachRemote(remoteAttachInfo, justMyCode);
			return new AttachResponse();
		});
	}

	private async ValueTask<DapResult<RestartResponse>> HandleRestartAsync(RestartArguments arguments)
	{
		try
		{
			_debugger.CancelSymbolWaiters();
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				// Before anything touches the old debugger: its exit callbacks must not end the client's session.
				Volatile.Write(ref _debuggerEventToken, null);
				await _debugger.DrainRuntimeEventQueue();
				// A launch restart terminates the old debuggee; an attach restart only detaches.
				_debugger.Disconnect(_lastLaunchInfo is not null);
				_debugger = new ManagedDebugger(_logger);
				DebuggerGeneration++;
				_debugger.EnableEditAndContinue = _enableEditAndContinue;
				_debugger.DecompileExternalCode = _decompileExternalCode;
				_debugger.ClientSymbolsTimeout = _clientSymbolsTimeout;
				SubscribeToDebuggerEvents();
				if (_lastLaunchInfo is not null)
					_debugger.Launch(_lastLaunchInfo, _lastJustMyCode);
				else if (_lastAttachProcessId is int processId)
					_debugger.Attach(processId, _lastJustMyCode, _lastAttachDebuggerLibraryDirectory);
				else if (_lastRemoteAttachInfo is not null)
					_debugger.AttachRemote(_lastRemoteAttachInfo, _lastJustMyCode);
				else
					throw new InvalidOperationException("There is no launch or attach to restart.");
				ReapplyBreakpoints();
				await _debugger.ConfigurationDone();
			}
			return new RestartResponse();
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleRestartRequestAsync failed: {ex.Message} , {ex}");
			return new DapError($"Failed to restart: {ex.Message}");
		}
	}

	/// <summary>Sets the breakpoints the client had before a restart on the new debugger, as the initial setBreakpoints round would.</summary>
	private void ReapplyBreakpoints()
	{
		foreach (var (sourcePath, requests) in _sourceBreakpoints)
			_debugger.SetBreakpoints(sourcePath, requests);
		if (_functionBreakpoints.Length > 0)
			_debugger.SetFunctionBreakpoints(_functionBreakpoints);
		if (_exceptionBreakpoints.Count > 0)
			_debugger.SetExceptionBreakpoints(_exceptionBreakpoints);
	}

	private ValueTask<DapResult<ConfigurationDoneResponse>> HandleConfigurationDoneAsync(ConfigurationDoneArguments arguments)
	{
		_logger?.Invoke("Configuration done");
		return RunLocked<ConfigurationDoneResponse>("ConfigurationDone failed", async () =>
		{
			await _debugger.ConfigurationDone();
			return new ConfigurationDoneResponse();
		});
	}

	private ValueTask<DapResult<BreakpointLocationsResponse>> HandleBreakpointLocationsAsync(BreakpointLocationsArguments arguments)
	{
		if (arguments.Source.Path is not { } sourcePath)
		{
			return ValueTask.FromResult<DapResult<BreakpointLocationsResponse>>(new DapError("Failed to get breakpoint locations: Missing source path"));
		}

		var startLine = ConvertClientLineToDebugger(arguments.Line);
		var endLine = arguments.EndLine is int lastLine ? ConvertClientLineToDebugger(lastLine) : (int?)null;
		var startColumn = arguments.Column is int c ? ConvertClientColumnToDebugger(c) : (int?)null;
		var endColumn = arguments.EndColumn is int ec ? ConvertClientColumnToDebugger(ec) : (int?)null;

		return RunLocked<BreakpointLocationsResponse>("Failed to get breakpoint locations", () =>
		{
			var locations = _debugger.GetBreakpointLocations(sourcePath, startLine, startColumn, endLine, endColumn);
			return new BreakpointLocationsResponse
			{
				Breakpoints = locations.Select(l => new BreakpointLocation
				{
					Line = ConvertDebuggerLineToClient(l.Line),
					Column = ConvertDebuggerColumnToClient(l.Column),
					EndLine = ConvertDebuggerLineToClient(l.EndLine),
					EndColumn = ConvertDebuggerColumnToClient(l.EndColumn)
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<SetBreakpointsResponse>> HandleSetBreakpointsAsync(SetBreakpointsArguments arguments)
	{
		if (arguments.Source.Path is not { } sourcePath)
		{
			return ValueTask.FromResult<DapResult<SetBreakpointsResponse>>(new DapError("Failed to set breakpoints: Missing source path"));
		}

		var breakpointRequests = arguments.Breakpoints?
			.Select(bp => new SharpDbgBreakpointRequest(
				ConvertClientLineToDebugger(bp.Line),
				bp.Condition,
				bp.HitCondition,
				bp.Column is null ? null : ConvertClientColumnToDebugger(bp.Column.Value)))
			.ToArray() ?? [];
		_sourceBreakpoints[sourcePath] = breakpointRequests;

		return RunLocked<SetBreakpointsResponse>("Failed to set breakpoints", () =>
		{
			var breakpoints = _debugger.SetBreakpoints(sourcePath, breakpointRequests);
			return new SetBreakpointsResponse
			{
				Breakpoints = breakpoints.Select(bp => new DapBreakpoint
				{
					Id = bp.Id,
					Verified = bp.Verified,
					Line = ConvertDebuggerLineToClient(bp.Line),
					Column = bp is { Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(bp.Column.Value) : null,
					EndLine = bp.EndLine is { } endLine ? ConvertDebuggerLineToClient(endLine) : null,
					EndColumn = bp.EndColumn is { } endColumn ? ConvertDebuggerColumnToClient(endColumn) : null,
					Message = bp.Message,
					Source = new Source
					{
						Path = bp.FilePath,
						Checksums = ComputeSourceChecksums(bp.FilePath)
					}
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<SetFunctionBreakpointsResponse>> HandleSetFunctionBreakpointsAsync(SetFunctionBreakpointsArguments arguments)
	{
		var requests = arguments.Breakpoints.Select(bp => new SharpDbgFunctionBreakpointRequest(bp.Name, bp.Condition, bp.HitCondition)).ToArray();
		_functionBreakpoints = requests;
		return RunLocked<SetFunctionBreakpointsResponse>("Failed to set function breakpoints", () =>
		{
			var breakpoints = _debugger.SetFunctionBreakpoints(requests);
			return new SetFunctionBreakpointsResponse
			{
				Breakpoints = breakpoints.Select(bp => new DapBreakpoint
				{
					Id = bp.Id,
					Verified = bp.Verified,
					Message = bp.Message
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<SetExceptionBreakpointsResponse>> HandleSetExceptionBreakpointsAsync(SetExceptionBreakpointsArguments arguments)
	{
		var breakpointRequests = new List<SharpDbgExceptionBreakpointRequest>();
		foreach (var filter in arguments.Filters)
		{
			if (ParseExceptionFilter(filter) is not { } parsedFilter) return UnknownFilter(filter);
			breakpointRequests.Add(new SharpDbgExceptionBreakpointRequest(parsedFilter));
		}
		foreach (var option in arguments.FilterOptions ?? [])
		{
			if (ParseExceptionFilter(option.FilterId) is not { } parsedFilter) return UnknownFilter(option.FilterId);
			breakpointRequests.Add(new SharpDbgExceptionBreakpointRequest(parsedFilter, option.Condition));
		}
		_logger?.Invoke($"Exception breakpoints: {string.Join(", ", breakpointRequests)}");
		_exceptionBreakpoints = breakpointRequests;

		return RunLocked<SetExceptionBreakpointsResponse>("Failed to set exception breakpoints", () =>
		{
			_debugger.SetExceptionBreakpoints(breakpointRequests);
			return new SetExceptionBreakpointsResponse();
		});

		static SharpDbgExceptionBreakpointFilter? ParseExceptionFilter(string filter) => filter switch
		{
			"all" => SharpDbgExceptionBreakpointFilter.All,
			"user-unhandled" => SharpDbgExceptionBreakpointFilter.UserUnhandled,
			_ => null
		};

		static ValueTask<DapResult<SetExceptionBreakpointsResponse>> UnknownFilter(string filter) =>
			ValueTask.FromResult<DapResult<SetExceptionBreakpointsResponse>>(new DapError($"Failed to set exception breakpoints: Unknown exception breakpoint filter: '{filter}'"));
	}

	private ValueTask<DapResult<ThreadsResponse>> HandleThreadsAsync(ThreadsArguments arguments)
	{
		return RunLocked<ThreadsResponse>("Failed to get threads", () => new ThreadsResponse
		{
			Threads = _debugger.GetThreads().Select(t => new DapThread
			{
				Id = t.id,
				Name = t.name
			}).ToList()
		});
	}

	private ValueTask<DapResult<ApplyDeltasResponse>> HandleApplyDeltasAsync(ApplyDeltasArguments arguments)
	{
		return RunLocked<ApplyDeltasResponse>("Failed to apply Edit and Continue deltas", () =>
		{
			if (!Guid.TryParse(arguments.Mvid, out var mvid))
				throw new InvalidOperationException("sharpdbg/applyDeltas needs a valid mvid");
			static byte[] Decode(string? value, string name)
			{
				if (string.IsNullOrWhiteSpace(value))
					throw new InvalidOperationException($"sharpdbg/applyDeltas needs {name}");
				try { return Convert.FromBase64String(value); }
				catch (FormatException) { throw new InvalidOperationException($"sharpdbg/applyDeltas has invalid {name}"); }
			}
			static IReadOnlyList<SourceLineMap>? ParseLineMaps(JsonElement? node)
			{
				if (node is null) return null;
				static InvalidOperationException Invalid() => new("sharpdbg/applyDeltas has invalid lineMaps");
				if (node.Value.ValueKind is not JsonValueKind.Array) throw Invalid();
				var result = new List<SourceLineMap>();
				foreach (var map in node.Value.EnumerateArray())
				{
					if (map.ValueKind is not JsonValueKind.Object
						|| !map.TryGetProperty("documentPath", out var documentPath)
						|| documentPath.ValueKind is not JsonValueKind.String
						|| string.IsNullOrEmpty(documentPath.GetString())
						|| !map.TryGetProperty("unchangedLines", out var lines)
						|| lines.ValueKind is not JsonValueKind.Array)
						throw Invalid();
					var ranges = new List<UnchangedLineRange>();
					foreach (var line in lines.EnumerateArray())
					{
						if (line.ValueKind is not JsonValueKind.Object
							|| !line.TryGetProperty("oldStartLine", out var oldStart) || oldStart.ValueKind is not JsonValueKind.Number
							|| !line.TryGetProperty("newStartLine", out var newStart) || newStart.ValueKind is not JsonValueKind.Number
							|| !line.TryGetProperty("lineCount", out var count) || count.ValueKind is not JsonValueKind.Number)
							throw Invalid();
						var range = new UnchangedLineRange(oldStart.GetInt32(), newStart.GetInt32(), count.GetInt32());
						if (range.OldStartLine < 1 || range.NewStartLine < 1 || range.LineCount < 1) throw Invalid();
						ranges.Add(range);
					}
					result.Add(new SourceLineMap(documentPath.GetString()!, ranges));
				}
				return result;
			}

			var metadata = Decode(arguments.Metadata, "metadata");
			var il = Decode(arguments.Il, "il");
			var pdb = string.IsNullOrWhiteSpace(arguments.Pdb) ? null : Decode(arguments.Pdb, "pdb");
			var updatedMethodTokens = arguments.UpdatedMethodTokens ?? [];
			if (updatedMethodTokens.Any(token => (token & unchecked((int)0xFF000000)) != 0x06000000 || (token & 0x00FFFFFF) == 0))
				throw new InvalidOperationException("sharpdbg/applyDeltas has an invalid updatedMethodTokens entry");
			var lineMaps = ParseLineMaps(arguments.LineMaps);
			if (!_debugger.EnableEditAndContinue)
				throw new InvalidOperationException("sharpdbg/applyDeltas requires enableEditAndContinue at launch");
			var result = _debugger.ApplyDelta(mvid, metadata, il, pdb, updatedMethodTokens, lineMaps);
			if (!result.Success) throw new InvalidOperationException(result.Message);
			return new ApplyDeltasResponse { Applied = true, Message = result.Message, HResult = result.HResult };
		});
	}

	private DapResult<LoadSymbolsResponse> HandleLoadSymbols(LoadSymbolsArguments arguments)
	{
		if (!Guid.TryParse(arguments.Mvid, out var mvid))
			return new DapError("loadSymbols needs the module's 'mvid'");
		_logger?.Invoke($"loadSymbols: {mvid} -> {(string.IsNullOrEmpty(arguments.PdbPath) ? "(none)" : arguments.PdbPath)}");
		_debugger.ProvideSymbols(mvid, arguments.PdbPath);
		return new LoadSymbolsResponse { Accepted = true };
	}

	private ValueTask<DapResult<StackTraceResponse>> HandleStackTraceAsync(StackTraceArguments arguments)
	{
		return RunLocked<StackTraceResponse>("Failed to get stack trace", () =>
		{
			var frames = _debugger.GetStackTrace(arguments.ThreadId, arguments.StartFrame ?? 0, arguments.Levels);
			return new StackTraceResponse
			{
				StackFrames = frames.Select(ToProtocolStackFrame).ToList()
			};
		});
	}

	private ValueTask<DapResult<ResolveStackFrameResponse>> HandleResolveStackFrameAsync(ResolveStackFrameArguments arguments)
	{
		return RunLocked<ResolveStackFrameResponse>("Failed to resolve stack frame", async () =>
		{
			// Not on the dispatch loop: resolving a module without symbols can wait for the client's loadSymbols answer,
			// and the dispatch loop has to stay free to receive it.
			var frame = await Task.Run(() => _debugger.ResolveStackFrame(arguments.StackFrameId));
			return new ResolveStackFrameResponse { StackFrame = ToProtocolStackFrame(frame) };
		});
	}

	private static IReadOnlyList<Checksum>? ComputeSourceChecksums(string? filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
			var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(filePath));
			return [new Checksum { Algorithm = ChecksumAlgorithm.SHA256, ChecksumValue = Convert.ToHexString(hash) }];
		}
		catch
		{
			return null;
		}
	}

	private DapStackFrame ToProtocolStackFrame(StackFrameInfo frame)
	{
		var protocolFrame = new DapStackFrame
		{
			Id = frame.Id,
			Name = frame.Name,
			Line = ConvertDebuggerLineToClient(frame.Line),
			EndLine = frame.EndLine is int endLine ? ConvertDebuggerLineToClient(endLine) : null,
			Column = ConvertDebuggerColumnToClient(frame.Column),
			EndColumn = frame.EndColumn is int endColumn ? ConvertDebuggerColumnToClient(endColumn) : null,
			Source = frame.Source is not null ? new Source { Path = frame.Source, Name = Path.GetFileName(frame.Source), SourceReference = 0, Checksums = ComputeSourceChecksums(frame.Source) } : null,
			InstructionPointerReference = frame.InstructionPointerReference,
			PresentationHint = frame.IsUserCode ? null : (DapStackFrame.PresentationHintValue?)DapStackFrame.PresentationHintValue.Subtle
		};
		protocolFrame.IsResolved = frame.IsResolved;
		protocolFrame.DecompiledSourceInfo = frame.DecompiledSourceInfo;
		return protocolFrame;
	}

	private ValueTask<DapResult<ScopesResponse>> HandleScopesAsync(ScopesArguments arguments)
	{
		return RunLocked<ScopesResponse>("Failed to get scopes", () => new ScopesResponse
		{
			Scopes = _debugger.GetScopes(arguments.FrameId).Select(s => new Scope
			{
				Name = s.Name,
				VariablesReference = s.VariablesReference,
				Expensive = s.Expensive
			}).ToList()
		});
	}

	private ValueTask<DapResult<VariablesResponse>> HandleVariablesAsync(VariablesArguments arguments)
	{
		return RunLocked<VariablesResponse>("Failed to get variables", async () =>
		{
			var variables = await _debugger.GetVariables(arguments.VariablesReference);
			return new VariablesResponse
			{
				Variables = variables.Select(v => new Variable
				{
					Name = v.Name,
					EvaluateName = v.Name,
					Value = v.Value,
					Type = v.Type,
					PresentationHint = v.PresentationHint?.ToDto(),
					VariablesReference = v.VariablesReference,
					MemoryReference = v.MemoryReference
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<SetVariableResponse>> HandleSetVariableAsync(SetVariableArguments arguments)
	{
		return RunLocked<SetVariableResponse>("Failed to set variable", async () =>
		{
			var result = await _debugger.SetVariable(arguments.VariablesReference, arguments.Name, arguments.Value);
			return new SetVariableResponse
			{
				Value = result.Value,
				Type = result.Type,
				VariablesReference = result.VariablesReference
			};
		});
	}

	private ValueTask<DapResult<LoadedSourcesResponse>> HandleLoadedSourcesAsync(LoadedSourcesArguments arguments)
	{
		return RunLocked<LoadedSourcesResponse>("Failed to get loaded sources", () =>
		{
			var sources = _debugger.GetLoadedSources()
				.Select(path => new Source { Path = path, Name = System.IO.Path.GetFileName(path) })
				.ToList();
			return new LoadedSourcesResponse { Sources = sources };
		});
	}

	private ValueTask<DapResult<EvaluateResponse>> HandleEvaluateAsync(EvaluateArguments arguments)
	{
		return RunLocked<EvaluateResponse>("Failed to evaluate expression", async () =>
		{
			var variableInfo = await _debugger.Evaluate(arguments.Expression, arguments.FrameId);
			return new EvaluateResponse
			{
				Result = variableInfo.Value,
				Type = variableInfo.Type,
				VariablesReference = variableInfo.VariablesReference,
				PresentationHint = variableInfo.PresentationHint?.ToDto()
			};
		});
	}

	private ValueTask<DapResult<ContinueResponse>> HandleContinueAsync(ContinueArguments arguments)
	{
		return RunLocked<ContinueResponse>("Failed to continue", () =>
		{
			var allThreadsContinued = _debugger.HandleContinueRequest(arguments.ThreadId, arguments.SingleThread ?? false);
			return new ContinueResponse { AllThreadsContinued = allThreadsContinued };
		});
	}

	private ValueTask<DapResult<NextResponse>> HandleNextAsync(NextArguments arguments)
	{
		return RunLocked<NextResponse>("Failed to step", async () =>
		{
			await _debugger.StepNext(arguments.ThreadId, arguments.SingleThread ?? false);
			return new NextResponse();
		});
	}

	private ValueTask<DapResult<StepInResponse>> HandleStepInAsync(StepInArguments arguments)
	{
		return RunLocked<StepInResponse>("Failed to step in", async () =>
		{
			await _debugger.StepIn(arguments.ThreadId, arguments.SingleThread ?? false);
			return new StepInResponse();
		});
	}

	private ValueTask<DapResult<StepOutResponse>> HandleStepOutAsync(StepOutArguments arguments)
	{
		return RunLocked<StepOutResponse>("Failed to step out", async () =>
		{
			await _debugger.StepOut(arguments.ThreadId, arguments.SingleThread ?? false);
			return new StepOutResponse();
		});
	}

	private ValueTask<DapResult<PauseResponse>> HandlePauseAsync(PauseArguments arguments)
	{
		return RunLocked<PauseResponse>("Failed to pause", () =>
		{
			_debugger.Pause();
			return new PauseResponse();
		});
	}

	private async ValueTask<DapResult<DisconnectResponse>> HandleDisconnectAsync(DisconnectArguments arguments, DapRequestContext context)
	{
		// A thread waiting for the client's symbols holds the lock: release it before teardown waits for the lock.
		_debugger.CancelSymbolWaiters();
		var result = await RunLocked<DisconnectResponse>("Failed to disconnect", () =>
		{
			_debugger.Disconnect(arguments.TerminateDebuggee ?? false);
			return new DisconnectResponse();
		});
		if (result is DisconnectResponse) context.OnResponseSent(RequestDebuggerProcessShutdown);
		return result;
	}

	private ValueTask<DapResult<TerminateResponse>> HandleTerminateAsync(TerminateArguments arguments)
	{
		_debugger.CancelSymbolWaiters();
		return RunLocked<TerminateResponse>("Failed to terminate", () =>
		{
			_debugger.Terminate();
			return new TerminateResponse();
		});
	}

	private ValueTask<DapResult<ExceptionInfoResponse>> HandleExceptionInfoAsync(ExceptionInfoArguments arguments)
	{
		return RunLocked<ExceptionInfoResponse>("Failed to get exception info", async () =>
		{
			var exceptionInfo = await _debugger.ExceptionInfo(new ThreadId(arguments.ThreadId));
			return new ExceptionInfoResponse
			{
				ExceptionId = exceptionInfo.ExceptionId,
				Description = exceptionInfo.Description,
				BreakMode = exceptionInfo.BreakMode switch
				{
					SharpDbgExceptionBreakMode.Never => ExceptionBreakMode.Never,
					SharpDbgExceptionBreakMode.Always => ExceptionBreakMode.Always,
					SharpDbgExceptionBreakMode.Unhandled => ExceptionBreakMode.Unhandled,
					SharpDbgExceptionBreakMode.UserUnhandled => ExceptionBreakMode.UserUnhandled,
					_ => new ExceptionBreakMode("unknown")
				},
				Details = ToProtocolExceptionDetails(exceptionInfo.Details),
				// 'code' is a Visual Studio extension to the DAP specification
				AdditionalProperties = exceptionInfo.Code is 0 ? null : new JsonObject { ["code"] = exceptionInfo.Code }
			};
		});
	}

	private static ExceptionDetails ToProtocolExceptionDetails(ExceptionInfo.ExceptionDetails details)
	{
		// Visual Studio extensions to the DAP specification
		var additionalProperties = new JsonObject();
		if (details.FormattedDescription is not null) additionalProperties["formattedDescription"] = details.FormattedDescription;
		if (details.HResult is not 0) additionalProperties["hresult"] = details.HResult;
		if (details.Source is not null) additionalProperties["source"] = details.Source;

		return new ExceptionDetails
		{
			Message = details.Message,
			TypeName = details.TypeName,
			FullTypeName = details.FullTypeName,
			EvaluateName = details.EvaluateName,
			StackTrace = details.StackTrace,
			InnerException = details.InnerException is [] ? null : details.InnerException.Select(ToProtocolExceptionDetails).ToList(),
			AdditionalProperties = additionalProperties
		};
	}

	// Coordinate conversion helpers
	private int ConvertClientLineToDebugger(int line)
	{
		return _clientLinesStartAt1 ? line : line + 1;
	}

	private int ConvertDebuggerLineToClient(int line)
	{
		return _clientLinesStartAt1 ? line : line - 1;
	}

	private int ConvertClientColumnToDebugger(int column)
	{
		return _clientColumnsStartAt1 ? column : column + 1;
	}

	private int ConvertDebuggerColumnToClient(int column)
	{
		return _clientColumnsStartAt1 ? column : column - 1;
	}

	private static StoppedEvent.ReasonValue ConvertStopReason(string reason) => new(reason.ToLowerInvariant());
}
