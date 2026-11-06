// Copyright (c) LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Desktop DAP server built on the MIT-licensed OmniSharp DAP fork instead of
// Microsoft.VisualStudio.Shared.VSCodeDebugProtocol. The latter's nuspec requires acceptance of
// Microsoft's separate, non-open-source software license, which is why OpenDevelop cannot
// redistribute it. The Android agent already uses the same fork (see SharpDbg.AndroidAgent);
// this is the desktop equivalent, with the request/event payloads written directly as DAP JSON.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.DebugAdapter.Protocol;
using OmniSharp.Extensions.DebugAdapter.Protocol.Models;
using OmniSharp.Extensions.DebugAdapter.Server;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.Infrastructure.Debugger.PresentationHintModels;

namespace SharpDbg.Application;

/// <summary>
/// Main debug adapter that coordinates the DAP transport and the debugger engine.
/// </summary>
public sealed class DebugAdapter : IDisposable
{
	ManagedDebugger _debugger;
	readonly Action<string>? _logger;
	DebugAdapterServer? _server;
	TaskCompletionSource _eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
	readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
	readonly TaskCompletionSource _disconnectResponseWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
	bool _clientLinesStartAt1 = true;
	bool _clientColumnsStartAt1 = true;
	int _shutdownRequested;
	readonly ConcurrentDictionary<int, (string File, int Line, int? Column)> _gotoTargets = new();
	int _nextGotoTargetId;

	// The last launch/attach, replayed by a restart request.
	LaunchInfo? _lastLaunchInfo;
	bool _lastJustMyCode = true;
	int? _lastAttachProcessId;
	string? _lastAttachDebuggerLibraryDirectory;
	RemoteAttachInfo? _lastRemoteAttachInfo;

	/// <summary>
	/// Raised once the debug session has ended (a disconnect request has been fully handled).
	/// Hosts which own the process lifetime (e.g. SharpDbg.Cli) should subscribe and exit when it fires.
	/// </summary>
	public event Action? ShutdownRequested;

	public DebugAdapter(Action<string>? logger = null)
	{
		_logger = logger;
		_debugger = new ManagedDebugger(logger);
		SubscribeToDebuggerEvents();
	}

	public void Dispose()
	{
		_server?.Dispose();
		_server = null;
	}

	/// <summary>
	/// Signals the end of the debug session and raises <see cref="ShutdownRequested"/> so the hosting
	/// process can shut down. Idempotent.
	/// Only called once a 'disconnect' request has been fully handled - 'terminate' must keep the adapter alive.
	/// </summary>
	void RequestDebuggerProcessShutdown()
	{
		if (Interlocked.Exchange(ref _shutdownRequested, 1) is not 0) return;

		_logger?.Invoke("RequestDebuggerProcessShutdown");
		_shutdown.TrySetResult();
		ShutdownRequested?.Invoke();
	}

	/// <summary>
	/// Releases debugger resources when the session ends without a disconnect request having been received
	/// (e.g. the client crashed or closed its stream).
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
	/// Runs the DAP server over the supplied streams until the client closes its input or sends
	/// 'disconnect'. Requests are dispatched by the OmniSharp server; this method awaits that lifetime.
	/// </summary>
	public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
	{
		_eof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var trackedInput = new EofStream(input, () => _eof.TrySetResult());
		var trackedOutput = new ResponseWatchStream(output, "\"command\":\"disconnect\"", () => _disconnectResponseWritten.TrySetResult());

		_server = DebugAdapterServer.Create(options =>
		{
			options
				.WithInput(trackedInput)
				.WithOutput(trackedOutput)
				.WithLoggerFactory(new AdapterLogFactory())
				.WithUnhandledExceptionHandler(ex => _logger?.Invoke("DAP input: " + ex));
			ConfigureCapabilities(options.Capabilities);
			// DebugAdapterServer copies only a subset of Capabilities into the initialize response;
			// exception filter options are supported here but are not copied by the fork.
			options.OnInitialized((_, _, response, _) =>
			{
				response.SupportsExceptionFilterOptions = true;
				return Task.CompletedTask;
			});
			RegisterHandlers(options);
		});

		try
		{
			var initializing = _server.Initialize(cancellationToken);
			if (await Task.WhenAny(initializing, _eof.Task) != initializing)
				return;
			await initializing;
			_clientLinesStartAt1 = _server.ClientSettings.LinesStartAt1;
			_clientColumnsStartAt1 = _server.ClientSettings.ColumnsStartAt1;
			await Task.WhenAny(_eof.Task, _shutdown.Task);
			// The response to 'disconnect' is written after its handler returns: wait until it has really been
			// written (bounded, so a stuck output cannot keep the host alive) before the host disposes the
			// server and its streams.
			if (_shutdown.Task.IsCompleted)
				await Task.WhenAny(_disconnectResponseWritten.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) when (ex is ObjectDisposedException or IOException) { }
		finally
		{
			try { Dispose(); } catch { }
		}
	}

	static void ConfigureCapabilities(Capabilities capabilities)
	{
		capabilities.SupportsConfigurationDoneRequest = true;
		capabilities.SupportsFunctionBreakpoints = true;
		capabilities.SupportsConditionalBreakpoints = true;
		capabilities.SupportsHitConditionalBreakpoints = true;
		capabilities.SupportsEvaluateForHovers = true;
		capabilities.SupportsStepBack = false;
		capabilities.SupportsSetVariable = false;
		capabilities.SupportsRestartFrame = false;
		capabilities.SupportsTerminateRequest = true;
		capabilities.SupportsExceptionInfoRequest = true;
		capabilities.SupportsExceptionFilterOptions = true;
		capabilities.SupportsBreakpointLocationsRequest = true;
		capabilities.SupportsLoadedSourcesRequest = true;
		capabilities.SupportsGotoTargetsRequest = true;
		capabilities.SupportsRestartRequest = true;
		capabilities.SupportsSingleThreadExecutionRequests = true; // continue/next/stepIn/stepOut honour singleThread
		capabilities.SupportsStepInTargetsRequest = false;
		capabilities.ExceptionBreakpointFilters = new Container<ExceptionBreakpointsFilter>(
			new ExceptionBreakpointsFilter { Filter = "all", Label = "All Exceptions", Default = false },
			new ExceptionBreakpointsFilter { Filter = "user-unhandled", Label = "User-Unhandled Exceptions", Default = true });
	}

	void RegisterHandlers(DebugAdapterServerOptions options)
	{
		foreach (string command in new[]
		{
			"launch", "attach", "configurationDone", "setBreakpoints", "setFunctionBreakpoints",
			"setExceptionBreakpoints", "breakpointLocations", "loadedSources", "gotoTargets", "goto",
			"source", "restart", "resolveStackFrame", "sourceLink", "scopes", "variables", "evaluate",
			"continue", "next", "stepIn", "stepOut", "pause", "modules", "threads", "stackTrace",
			"disconnect", "terminate", "exceptionInfo"
		})
		{
			string request = command;
			options.OnJsonRequest(request, async (JsonElement raw, CancellationToken cancellationToken) =>
			{
				JsonObject arguments;
				try
				{
					arguments = raw.ValueKind == JsonValueKind.Object
						? (JsonNode.Parse(raw.GetRawText()) as JsonObject ?? new JsonObject())
						: new JsonObject();
				}
				catch
				{
					arguments = new JsonObject();
				}
				_logger?.Invoke("DAP request: " + request);
				try
				{
					JsonObject body = await HandleAsync(request, arguments);
					return ToJsonElement(body);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_logger?.Invoke("DAP request failed: " + request + " " + ex);
					throw ToDapError(ex);
				}
			});
		}

		// Requests the adapter intentionally does not implement. Returning the same DAP error the
		// Microsoft library produced keeps clients from surfacing a JSON-RPC "method not found".
		foreach (string command in new[]
		{
			"setVariable", "setExpression", "completions", "stepInTargets", "dataBreakpoints",
			"readMemory", "disassemble", "cancel", "terminateThreads", "setInstructionBreakpoints"
		})
		{
			string request = command;
			options.OnJsonRequest(request, (JsonElement _, CancellationToken _) =>
				throw ToDapError(new NotSupportedException($"Request '{request}' is not supported.")));
		}

		options.OnJsonRequest("vsCustomMessage", (JsonElement _, CancellationToken _) =>
			Task.FromResult(ToJsonElement(new JsonObject())));
	}

	// The library wraps every other exception as "Internal Error - <exception.ToString()>", which puts the exception
	// type and call stack into the message the client shows. RpcErrorException carries just our message.
	static RpcErrorException ToDapError(Exception ex) =>
		new(ErrorCodes.InternalError, ex.GetType().Name, ex.Message);

	async Task<JsonObject> HandleAsync(string command, JsonObject args)
	{
		if (command == "launch")
		{
			await HandleLaunchAsync(args);
			return new JsonObject();
		}

		if (command == "attach")
		{
			await HandleAttachAsync(args);
			return new JsonObject();
		}

		if (command == "restart")
		{
			await HandleRestartAsync();
			return new JsonObject();
		}

		if (command == "disconnect")
		{
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				_debugger.Disconnect(GetBool(args, "terminateDebuggee") ?? false);
			}
			RequestDebuggerProcessShutdown();
			return new JsonObject();
		}

		if (command == "terminate")
		{
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				_debugger.Terminate();
			}
			return new JsonObject();
		}

		using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
		{
			await _debugger.DrainRuntimeEventQueue();
			return command switch
			{
				"configurationDone" => await ConfigurationDoneAsync(),
				"setBreakpoints" => SetBreakpoints(args),
				"setFunctionBreakpoints" => SetFunctionBreakpoints(args),
				"setExceptionBreakpoints" => SetExceptionBreakpoints(args),
				"breakpointLocations" => BreakpointLocations(args),
				"loadedSources" => LoadedSources(),
				"gotoTargets" => GotoTargets(args),
				"goto" => await GotoAsync(args),
				"source" => await SourceAsync(args),
				"resolveStackFrame" => ResolveStackFrame(args),
				"sourceLink" => SourceLink(args),
				"scopes" => Scopes(args),
				"variables" => await VariablesAsync(args),
				"evaluate" => await EvaluateAsync(args),
				"continue" => Continue(args),
				"next" => await StepAsync(() => _debugger.StepNext(RequiredInt(args, "threadId"))),
				"stepIn" => await StepAsync(() => _debugger.StepIn(RequiredInt(args, "threadId"))),
				"stepOut" => await StepAsync(() => _debugger.StepOut(RequiredInt(args, "threadId"))),
				"pause" => Pause(),
				"modules" => Modules(),
				"threads" => Threads(),
				"stackTrace" => StackTrace(args),
				"exceptionInfo" => await ExceptionInfoAsync(args),
				_ => throw new InvalidOperationException($"Request '{command}' is not supported.")
			};
		}
	}

	async Task<JsonObject> ConfigurationDoneAsync()
	{
		_logger?.Invoke("Configuration done");
		await _debugger.ConfigurationDone();
		return new JsonObject();
	}

	async Task HandleLaunchAsync(JsonObject args)
	{
		try
		{
			var program = GetString(args, "program");
			if (string.IsNullOrEmpty(program))
				throw new InvalidOperationException("Missing program path");

			var launchArguments = GetStringArray(args, "args") ?? [];
			var cwd = GetString(args, "cwd");
			var env = GetStringDictionary(args, "env") ?? [];
			var stopAtEntry = GetBool(args, "stopAtEntry") ?? false;
			var justMyCode = GetBool(args, "justMyCode") ?? true;
			var console = GetString(args, "console");
			ApplyExpressionEvaluationOptions(args);
			var launchRequestConsoleType = console switch
			{
				"integratedTerminal" => LaunchRequestConsoleType.IntegratedTerminal,
				"externalTerminal" => LaunchRequestConsoleType.ExternalTerminal,
				"internalConsole" => LaunchRequestConsoleType.InternalConsole,
				null => LaunchRequestConsoleType.InternalConsole,
				_ => throw new ArgumentOutOfRangeException(nameof(console), $"Invalid console type: '{console}'")
			};

			(program, launchArguments) = Path.GetExtension(program).Equals(".dll", StringComparison.OrdinalIgnoreCase)
				? ("dotnet", [program, .. launchArguments])
				: (program, launchArguments);

			var launchInfo = new LaunchInfo
			{
				Program = program,
				Arguments = launchArguments,
				Cwd = cwd,
				Env = env,
				StopAtEntry = stopAtEntry,
				LaunchRequestConsoleType = launchRequestConsoleType,
				NoDebug = GetBool(args, "noDebug") ?? false
			};

			_lastLaunchInfo = launchInfo;
			_lastJustMyCode = justMyCode;
			_lastAttachProcessId = null;
			_lastAttachDebuggerLibraryDirectory = null;
			_lastRemoteAttachInfo = null;
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				_debugger.Launch(launchInfo, justMyCode);
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleLaunchRequestAsync failed: {ex.Message} , {ex}");
			throw new InvalidOperationException($"Failed to launch: {ex.Message}", ex);
		}
	}

	async Task HandleAttachAsync(JsonObject args)
	{
		try
		{
			RemoteAttachInfo? remoteAttachInfo = null;
			var processId = GetInt(args, "processId");
			if (processId is null)
				throw new InvalidOperationException("Missing process ID");

			if (args["coreClrMobileDebuggerOptions"] is JsonObject coreClrMobileDebuggerOptions)
			{
				var address = GetString(coreClrMobileDebuggerOptions, "address");
				var port = GetInt(coreClrMobileDebuggerOptions, "port");
				var platform = GetString(coreClrMobileDebuggerOptions, "platform");
				var isServer = GetBool(coreClrMobileDebuggerOptions, "isServer");
				var mscordbiPath = GetString(coreClrMobileDebuggerOptions, "mscordbiPath");
				var assembliesPath = GetString(coreClrMobileDebuggerOptions, "assembliesPath");
				if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("coreClrMobileDebuggerOptions.address is required.");
				if (port is null) throw new InvalidOperationException("coreClrMobileDebuggerOptions.port is required.");
				if (string.IsNullOrWhiteSpace(platform)) throw new InvalidOperationException("coreClrMobileDebuggerOptions.platform is required.");
				if (isServer is null) throw new InvalidOperationException("coreClrMobileDebuggerOptions.isServer is required.");
				if (string.IsNullOrWhiteSpace(mscordbiPath)) throw new InvalidOperationException("coreClrMobileDebuggerOptions.mscordbiPath is required.");
				if (string.IsNullOrWhiteSpace(assembliesPath)) throw new InvalidOperationException("coreClrMobileDebuggerOptions.assembliesPath is required.");
				if (!File.Exists(mscordbiPath)) throw new InvalidOperationException($"coreClrMobileDebuggerOptions.mscordbiPath does not exist: {mscordbiPath}");
				if (!Directory.Exists(assembliesPath)) throw new InvalidOperationException($"coreClrMobileDebuggerOptions.assembliesPath does not exist: {assembliesPath}");
				remoteAttachInfo = new RemoteAttachInfo
				{
					Address = address,
					Port = port.Value,
					Platform = platform,
					IsServer = isServer.Value,
					MscordbiPath = mscordbiPath,
					AssembliesPath = assembliesPath
				};
			}

			// A directory holding mscordbi and the DAC for the target runtime, for a target whose own
			// copies this process cannot load (iOS simulator).
			var debuggerLibraryDirectory = GetString(args, "debuggerLibraryDirectory");
			if (debuggerLibraryDirectory is not null && OperatingSystem.IsWindows())
				throw new InvalidOperationException("debuggerLibraryDirectory is not supported on Windows");
			if (debuggerLibraryDirectory is not null && !Directory.Exists(debuggerLibraryDirectory))
				throw new InvalidOperationException($"debuggerLibraryDirectory does not exist: {debuggerLibraryDirectory}");

			// The process was started stopped (SIGSTOP) before its runtime loaded; continue it once
			// attach is registered.
			var continueStoppedProcess = GetBool(args, "continueStoppedProcess") ?? false;
			if (continueStoppedProcess && !OperatingSystem.IsMacOS())
				throw new InvalidOperationException("continueStoppedProcess is supported on macOS only");

			var justMyCode = GetBool(args, "justMyCode") ?? true;
			// enableStepFiltering is the Just My Code step filter under another name.
			if (GetBool(args, "enableStepFiltering") is bool enableStepFiltering)
				justMyCode = enableStepFiltering;

			_logger?.Invoke($"Attach options: stopAtEntry={GetBool(args, "stopAtEntry")}, "
				+ $"suppressJITOptimizations={GetBool(args, "suppressJITOptimizations")}, "
				+ $"sourceFileMap={args.ContainsKey("sourceFileMap")}, "
				+ $"expressionEvaluationOptions={args.ContainsKey("expressionEvaluationOptions")}");
			ApplyExpressionEvaluationOptions(args);
			_lastLaunchInfo = null;
			_lastJustMyCode = justMyCode;
			_lastAttachProcessId = remoteAttachInfo is null ? processId.Value : null;
			_lastAttachDebuggerLibraryDirectory = remoteAttachInfo is null ? debuggerLibraryDirectory : null;
			_lastRemoteAttachInfo = remoteAttachInfo;

			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				if (remoteAttachInfo is null)
					_debugger.Attach(processId.Value, justMyCode, debuggerLibraryDirectory, continueStoppedProcess);
				else
					_debugger.AttachRemote(remoteAttachInfo, justMyCode);
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleAttachRequestAsync failed: {ex.Message} , {ex}");
			throw new InvalidOperationException($"Failed to attach: {ex.Message}", ex);
		}
	}

	async Task HandleRestartAsync()
	{
		try
		{
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				// A launch restart terminates the old debuggee; an attach restart only detaches.
				_debugger.Disconnect(_lastLaunchInfo is not null);
				_debugger = new ManagedDebugger(_logger);
				SubscribeToDebuggerEvents();
				if (_lastLaunchInfo is not null)
					_debugger.Launch(_lastLaunchInfo, _lastJustMyCode);
				else if (_lastAttachProcessId is int processId)
					// The process is running by now, so it is not continued again; the library directory still applies
					_debugger.Attach(processId, _lastJustMyCode, _lastAttachDebuggerLibraryDirectory);
				else if (_lastRemoteAttachInfo is not null)
					_debugger.AttachRemote(_lastRemoteAttachInfo, _lastJustMyCode);
				else
					throw new InvalidOperationException("There is no launch or attach to restart.");
				await _debugger.ConfigurationDone();
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleRestartRequestAsync failed: {ex.Message} , {ex}");
			throw new InvalidOperationException($"Failed to restart: {ex.Message}", ex);
		}
	}

	JsonObject SetBreakpoints(JsonObject args)
	{
		var sourcePath = GetSourcePath(args) ?? throw new InvalidOperationException("Failed to set breakpoints: Missing source path");
		var requests = (args["breakpoints"] as JsonArray ?? new JsonArray())
			.Select(bp => new SharpDbgBreakpointRequest(
				ConvertClientLineToDebugger(GetInt(bp as JsonObject, "line") ?? 0),
				GetString(bp as JsonObject, "condition"),
				GetString(bp as JsonObject, "hitCondition"),
				GetInt(bp as JsonObject, "column") is int column ? ConvertClientColumnToDebugger(column) : null,
				GetString(bp as JsonObject, "logMessage")))
			.ToArray();

		var breakpoints = _debugger.SetBreakpoints(sourcePath, requests);
		return new JsonObject
		{
			["breakpoints"] = new JsonArray(breakpoints.Select(bp => (JsonNode?)new JsonObject
			{
				["id"] = bp.Id,
				["verified"] = bp.Verified,
				["line"] = ConvertDebuggerLineToClient(bp.Line),
				["column"] = bp is { Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(bp.Column.Value) : null,
				["endLine"] = bp.Verified ? ConvertDebuggerLineToClient(bp.EndLine) : null,
				["endColumn"] = bp is { Verified: true, EndColumn: not null } ? ConvertDebuggerColumnToClient(bp.EndColumn.Value) : null,
				["message"] = bp.Message,
				["source"] = CreateSource(bp.FilePath)
			}).ToArray())
		};
	}

	JsonObject SetFunctionBreakpoints(JsonObject args)
	{
		var requests = (args["breakpoints"] as JsonArray ?? new JsonArray())
			.Select(bp => new SharpDbgFunctionBreakpointRequest(
				GetString(bp as JsonObject, "name") ?? string.Empty,
				GetString(bp as JsonObject, "condition"),
				GetString(bp as JsonObject, "hitCondition")))
			.ToArray();
		var breakpoints = _debugger.SetFunctionBreakpoints(requests);
		return new JsonObject
		{
			["breakpoints"] = new JsonArray(breakpoints.Select(bp => (JsonNode?)new JsonObject
			{
				["id"] = bp.Id,
				["verified"] = bp.Verified,
				["message"] = bp.Message
			}).ToArray())
		};
	}

	JsonObject SetExceptionBreakpoints(JsonObject args)
	{
		var requests = new List<SharpDbgExceptionBreakpointRequest>();
		foreach (var filter in args["filters"] as JsonArray ?? new JsonArray())
			requests.Add(new SharpDbgExceptionBreakpointRequest(ParseExceptionFilter(filter?.GetValue<string>())));
		foreach (var option in args["filterOptions"] as JsonArray ?? new JsonArray())
			requests.Add(new SharpDbgExceptionBreakpointRequest(
				ParseExceptionFilter(GetString(option as JsonObject, "filterId")),
				GetString(option as JsonObject, "condition")));
		_logger?.Invoke($"Exception breakpoints: {string.Join(", ", requests)}");
		_debugger.SetExceptionBreakpoints(requests);
		return new JsonObject();
	}

	static SharpDbgExceptionBreakpointFilter ParseExceptionFilter(string? filter) => filter switch
	{
		"all" => SharpDbgExceptionBreakpointFilter.All,
		"user-unhandled" => SharpDbgExceptionBreakpointFilter.UserUnhandled,
		_ => throw new InvalidOperationException($"Unknown exception breakpoint filter: '{filter}'")
	};

	JsonObject BreakpointLocations(JsonObject args)
	{
		var sourcePath = GetSourcePath(args) ?? throw new InvalidOperationException("Missing source path");
		var startLine = ConvertClientLineToDebugger(RequiredInt(args, "line"));
		var endLine = GetInt(args, "endLine") is int lastLine ? ConvertClientLineToDebugger(lastLine) : (int?)null;
		var locations = _debugger.GetBreakpointLocations(
			sourcePath, startLine, GetInt(args, "column"), endLine, GetInt(args, "endColumn"));
		return new JsonObject
		{
			["breakpoints"] = new JsonArray(locations.Select(l => (JsonNode?)new JsonObject
			{
				["line"] = ConvertDebuggerLineToClient(l.Line),
				["column"] = ConvertDebuggerColumnToClient(l.Column),
				["endLine"] = ConvertDebuggerLineToClient(l.EndLine),
				["endColumn"] = ConvertDebuggerColumnToClient(l.EndColumn)
			}).ToArray())
		};
	}

	JsonObject LoadedSources() => new()
	{
		["sources"] = new JsonArray(_debugger.GetLoadedSources().Select(s => (JsonNode?)CreateSource(s)).ToArray())
	};

	JsonObject GotoTargets(JsonObject args)
	{
		var sourcePath = GetSourcePath(args) ?? throw new InvalidOperationException("Missing source path");
		var line = ConvertClientLineToDebugger(RequiredInt(args, "line"));
		var targets = _debugger.GetGotoTargets(sourcePath, line, GetInt(args, "column"));
		var responseTargets = new JsonArray();
		foreach (var target in targets)
		{
			var id = Interlocked.Increment(ref _nextGotoTargetId);
			_gotoTargets[id] = (sourcePath, target.Line, target.Column);
			responseTargets.Add(new JsonObject
			{
				["id"] = id,
				["label"] = Path.GetFileName(sourcePath) + ":" + target.Line,
				["line"] = ConvertDebuggerLineToClient(target.Line),
				["column"] = ConvertDebuggerColumnToClient(target.Column),
				["endLine"] = ConvertDebuggerLineToClient(target.EndLine),
				["endColumn"] = ConvertDebuggerColumnToClient(target.EndColumn)
			});
		}
		return new JsonObject { ["targets"] = responseTargets };
	}

	async Task<JsonObject> GotoAsync(JsonObject args)
	{
		var targetId = RequiredInt(args, "targetId");
		if (!_gotoTargets.TryRemove(targetId, out var target))
			throw new InvalidOperationException($"Unknown goto target {targetId}.");
		var threadId = RequiredInt(args, "threadId");
		var location = _debugger.SetNextStatement(threadId, target.File, target.Line, target.Column);
		var stoppedEvent = new JsonObject
		{
			["reason"] = "goto",
			["threadId"] = threadId,
			["allThreadsStopped"] = true,
			["source"] = CreateSource(target.File),
			["line"] = ConvertDebuggerLineToClient(location.Line),
			["column"] = ConvertDebuggerColumnToClient(location.Column)
		};
		await Task.Yield();
		SendEvent("stopped", stoppedEvent);
		return new JsonObject();
	}

	async Task<JsonObject> SourceAsync(JsonObject args)
	{
		// Embedded source (sourceReference) takes precedence: the file may not exist on disk.
		var sourceReference = GetInt(args, "sourceReference") ?? 0;
		if (sourceReference > 0 && _debugger.TryGetEmbeddedSourceText(sourceReference, out var embedded))
			return new JsonObject { ["content"] = embedded, ["mimeType"] = "text/x-csharp" };

		var path = GetSourcePath(args);
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
			throw new InvalidOperationException($"Source not found: {path ?? "(sourceReference " + sourceReference + ")"}");
		var content = await File.ReadAllTextAsync(path);
		return new JsonObject { ["content"] = content, ["mimeType"] = "text/x-csharp" };
	}

	JsonObject ResolveStackFrame(JsonObject args)
	{
		var frame = _debugger.ResolveStackFrame(RequiredInt(args, "stackFrameId"));
		return new JsonObject { ["stackFrame"] = ToProtocolStackFrame(frame) };
	}

	JsonObject SourceLink(JsonObject args)
	{
		var url = _debugger.GetSourceLinkUrl(GetString(args, "path") ?? string.Empty);
		return new JsonObject { ["url"] = url };
	}

	JsonObject Scopes(JsonObject args) => new()
	{
		["scopes"] = new JsonArray(_debugger.GetScopes(RequiredInt(args, "frameId")).Select(s => (JsonNode?)new JsonObject
		{
			["name"] = s.Name,
			["variablesReference"] = s.VariablesReference,
			["expensive"] = s.Expensive
		}).ToArray())
	};

	async Task<JsonObject> VariablesAsync(JsonObject args)
	{
		// Variable paging: the client asks for a window (start/count) and keeps asking until fewer
		// than count come back.
		IEnumerable<VariableInfo> paged = await _debugger.GetVariables(RequiredInt(args, "variablesReference"));
		if (GetInt(args, "start") is int start && start > 0) paged = paged.Skip(start);
		if (GetInt(args, "count") is int count) paged = paged.Take(count);

		return new JsonObject
		{
			["variables"] = new JsonArray(paged.Select(v => (JsonNode?)new JsonObject
			{
				["name"] = v.Name,
				["evaluateName"] = v.Name,
				["value"] = v.Value,
				["type"] = v.Type,
				["presentationHint"] = PresentationHint(v.PresentationHint),
				["variablesReference"] = v.VariablesReference,
				["memoryReference"] = v.MemoryReference
			}).ToArray())
		};
	}

	async Task<JsonObject> EvaluateAsync(JsonObject args)
	{
		var variableInfo = await _debugger.Evaluate(GetString(args, "expression") ?? string.Empty, GetInt(args, "frameId"));
		return new JsonObject
		{
			["result"] = variableInfo.Value,
			["type"] = variableInfo.Type,
			["variablesReference"] = variableInfo.VariablesReference,
			["presentationHint"] = PresentationHint(variableInfo.PresentationHint),
			["memoryReference"] = variableInfo.MemoryReference
		};
	}

	static JsonObject? PresentationHint(SharpDbg.Infrastructure.Debugger.PresentationHintModels.VariablePresentationHint? hint)
	{
		if (hint is null) return null;
		var json = new JsonObject();
		if (hint.Value.Kind is PresentationHintKind kind)
			json["kind"] = kind switch
			{
				PresentationHintKind.Property => "property",
				PresentationHintKind.Method => "method",
				PresentationHintKind.Event => "event",
				PresentationHintKind.Class => "class",
				PresentationHintKind.Data => "data",
				PresentationHintKind.BaseClass => "baseClass",
				PresentationHintKind.InnerClass => "innerClass",
				PresentationHintKind.Interface => "interface",
				PresentationHintKind.MostDerivedClass => "mostDerivedClass",
				PresentationHintKind.Virtual => "virtual",
				_ => throw new ArgumentOutOfRangeException(nameof(hint), hint.Value.Kind, null)
			};
		if (hint.Value.Attributes is AttributesValue attributes)
			json["attributes"] = new JsonArray(attributes switch
			{
				AttributesValue.FailedEvaluation => "failedEvaluation",
				_ => throw new ArgumentOutOfRangeException(nameof(hint), attributes, null)
			});
		return json;
	}

	JsonObject Continue(JsonObject args)
	{
		_debugger.HandleContinueRequest();
		return new JsonObject { ["allThreadsContinued"] = !(GetBool(args, "singleThread") ?? false) };
	}

	static async Task<JsonObject> StepAsync(Func<Task> step)
	{
		await step();
		return new JsonObject();
	}

	JsonObject Pause()
	{
		_debugger.Pause();
		return new JsonObject();
	}

	JsonObject Modules()
	{
		var modules = _debugger.GetModules();
		return new JsonObject
		{
			["totalModules"] = modules.Count,
			["modules"] = new JsonArray(modules.Select(m => (JsonNode?)new JsonObject
			{
				["id"] = m.Id,
				["name"] = m.Name,
				["path"] = m.Path,
				["isUserCode"] = m.IsUserCode,
				["symbolStatus"] = m.HasSymbols ? "loaded" : "not loaded"
			}).ToArray())
		};
	}

	JsonObject Threads() => new()
	{
		["threads"] = new JsonArray(_debugger.GetThreads().Select(t => (JsonNode?)new JsonObject
		{
			["id"] = t.id,
			["name"] = t.name
		}).ToArray())
	};

	JsonObject StackTrace(JsonObject args)
	{
		var frames = _debugger.GetStackTrace(RequiredInt(args, "threadId"), GetInt(args, "startFrame") ?? 0, GetInt(args, "levels"));
		return new JsonObject
		{
			["stackFrames"] = new JsonArray(frames.Select(f => (JsonNode?)ToProtocolStackFrame(f)).ToArray())
		};
	}

	JsonObject ToProtocolStackFrame(StackFrameInfo frame) => new()
	{
		["id"] = frame.Id,
		["name"] = frame.Name,
		["line"] = ConvertDebuggerLineToClient(frame.Line),
		["endLine"] = frame.EndLine is int endLine ? ConvertDebuggerLineToClient(endLine) : null,
		["column"] = ConvertDebuggerColumnToClient(frame.Column),
		["endColumn"] = frame.EndColumn is int endColumn ? ConvertDebuggerColumnToClient(endColumn) : null,
		["source"] = frame.Source is not null ? CreateSource(frame.Source, frame.SourceReference) : null,
		["presentationHint"] = frame.IsUserCode ? null : "subtle",
		["instructionPointerReference"] = frame.InstructionPointerReference,
		["isResolved"] = frame.IsResolved,
		["decompiledSourceInfo"] = frame.DecompiledSourceInfo is null ? null : JsonSerializer.SerializeToNode(frame.DecompiledSourceInfo)
	};

	async Task<JsonObject> ExceptionInfoAsync(JsonObject args)
	{
		var exceptionInfo = await _debugger.ExceptionInfo(new ThreadId(RequiredInt(args, "threadId")));
		return new JsonObject
		{
			["exceptionId"] = exceptionInfo.ExceptionId,
			["description"] = exceptionInfo.Description,
			["breakMode"] = exceptionInfo.BreakMode switch
			{
				SharpDbgExceptionBreakMode.Always => "always",
				SharpDbgExceptionBreakMode.Unhandled => "unhandled",
				SharpDbgExceptionBreakMode.UserUnhandled => "userUnhandled",
				_ => "unknown"
			},
			["code"] = exceptionInfo.Code,
			["details"] = new JsonObject
			{
				["message"] = exceptionInfo.Details.Message,
				["typeName"] = exceptionInfo.Details.TypeName,
				["fullTypeName"] = exceptionInfo.Details.FullTypeName,
				["evaluateName"] = exceptionInfo.Details.EvaluateName,
				["stackTrace"] = exceptionInfo.Details.StackTrace,
				["innerException"] = new JsonArray(exceptionInfo.Details.InnerException.Select(inner => (JsonNode?)new JsonObject
				{
					["message"] = inner.Message,
					["typeName"] = inner.TypeName,
					["fullTypeName"] = inner.FullTypeName,
					["evaluateName"] = inner.EvaluateName,
					["stackTrace"] = inner.StackTrace,
					["innerException"] = new JsonArray()
				}).ToArray()),
				["formattedDescription"] = exceptionInfo.Details.FormattedDescription,
				["hResult"] = exceptionInfo.Details.HResult,
				["source"] = exceptionInfo.Details.Source
			}
		};
	}

	// ---------------------------------------------------------------------------------------------
	// Debugger events
	// ---------------------------------------------------------------------------------------------

	void SubscribeToDebuggerEvents()
	{
		_debugger.OnStopped += (threadId, reason) =>
		{
			SendEvent("stopped", new JsonObject
			{
				["reason"] = ConvertStopReason(reason),
				["threadId"] = threadId,
				["allThreadsStopped"] = true
			});
		};

		_debugger.OnStopped2 += (threadId, filePath, line, column, reason, hitBreakpointIds) =>
		{
			SendEvent("stopped", new JsonObject
			{
				["reason"] = ConvertStopReason(reason),
				["threadId"] = threadId,
				["allThreadsStopped"] = true,
				["hitBreakpointIds"] = hitBreakpointIds is null ? null : new JsonArray(hitBreakpointIds.Select(id => (JsonNode?)id).ToArray()),
				["source"] = CreateSource(filePath),
				["line"] = line,
				["column"] = column
			});
		};

		_debugger.OnBreakpointChanged += breakpoint =>
		{
			SendEvent("breakpoint", new JsonObject
			{
				["reason"] = "changed",
				["breakpoint"] = new JsonObject
				{
					["id"] = breakpoint.Id,
					["verified"] = breakpoint.Verified,
					["line"] = breakpoint.IsFunctionBreakpoint ? null : ConvertDebuggerLineToClient(breakpoint.Line),
					["column"] = breakpoint is { IsFunctionBreakpoint: false, Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(breakpoint.Column.Value) : null,
					["endLine"] = breakpoint is { IsFunctionBreakpoint: false, Verified: true } ? ConvertDebuggerLineToClient(breakpoint.EndLine) : null,
					["endColumn"] = breakpoint is { IsFunctionBreakpoint: false, Verified: true, EndColumn: not null } ? ConvertDebuggerColumnToClient(breakpoint.EndColumn.Value) : null,
					["offset"] = breakpoint is { IsFunctionBreakpoint: false, Verified: true } ? 0 : null,
					["message"] = breakpoint.Message,
					["source"] = breakpoint is not { IsFunctionBreakpoint: false, Verified: true } ? null : CreateSource(breakpoint.FilePath)
				}
			});
		};

		_debugger.OnContinued += threadId => SendEvent("continued", new JsonObject
		{
			["threadId"] = threadId,
			["allThreadsContinued"] = true
		});

		// exitCode will be null in the attach scenario, as we cannot obtain the exit code of the process we attached to
		_debugger.OnExited += exitCode => SendEvent("exited", new JsonObject { ["exitCode"] = exitCode ?? 0 });

		_debugger.OnTerminated += () => SendEvent("terminated", new JsonObject());

		_debugger.OnThreadStarted += threadId => SendEvent("thread", new JsonObject
		{
			["reason"] = "started",
			["threadId"] = threadId
		});

		_debugger.OnThreadExited += threadId => SendEvent("thread", new JsonObject
		{
			["reason"] = "exited",
			["threadId"] = threadId
		});

		_debugger.OnModuleLoaded += (id, name, path) => SendEvent("module", new JsonObject
		{
			["reason"] = "new",
			["module"] = new JsonObject { ["id"] = id, ["name"] = name, ["path"] = path }
		});
		_debugger.OnModuleUnloaded += (id, name, path) => SendEvent("module", new JsonObject
		{
			["reason"] = "removed",
			["module"] = new JsonObject { ["id"] = id, ["name"] = name, ["path"] = path }
		});

		_debugger.OnLoadedSource += (reason, path) => SendEvent("loadedSource", new JsonObject
		{
			["reason"] = reason == "removed" ? "removed" : "new",
			["source"] = CreateSource(path)
		});

		_debugger.OnProcessStarted += (processId, name, isAttach) => SendEvent("process", new JsonObject
		{
			["name"] = name,
			["systemProcessId"] = processId,
			["startMethod"] = isAttach ? "attach" : "launch",
			["isLocalProcess"] = true
		});

		_debugger.OnOutput += (output, isStdError) => SendEvent("output", new JsonObject
		{
			["category"] = isStdError ? "stderr" : "stdout",
			["output"] = output
		});

		_debugger.SendRunInTerminalRequest += SendRunInTerminal;
	}

	int SendRunInTerminal(LaunchInfo launchInfo)
	{
		var programForTitle = launchInfo.Program is "dotnet" ? launchInfo.Arguments[0] : launchInfo.Program;
		var runInTerminalArguments = new JsonObject
		{
			["kind"] = launchInfo.LaunchRequestConsoleType switch
			{
				LaunchRequestConsoleType.IntegratedTerminal => "integrated",
				LaunchRequestConsoleType.ExternalTerminal => "external",
				_ => throw new ArgumentOutOfRangeException(nameof(launchInfo), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{launchInfo.LaunchRequestConsoleType}'")
			},
			["title"] = $"{Path.GetFileName(programForTitle)} [DEBUG]",
			["cwd"] = launchInfo.Cwd,
			["args"] = new JsonArray(new[] { (JsonNode?)launchInfo.Program }.Concat(launchInfo.Arguments.Select(a => (JsonNode?)a)).ToArray()),
			["env"] = ToJsonObject(launchInfo.Env, value => value)
		};
		// Make the child suspend at startup so the debugger can attach before its runtime loads.
		((JsonObject)runInTerminalArguments["env"]!)["DOTNET_DefaultDiagnosticPortSuspend"] = "1";

		var response = _server!.SendRequest("runInTerminal", runInTerminalArguments)
			.Returning<JsonElement>(CancellationToken.None)
			.GetAwaiter().GetResult();
		// https://github.com/microsoft/vscode/issues/61640 - ProcessId will not be returned for
		// integratedTerminal or externalTerminal.
		if (!response.TryGetProperty("processId", out var processId) || processId.ValueKind is not JsonValueKind.Number)
			throw new InvalidOperationException("RunInTerminalRequest did not return a process ID. VSCode does not return a process ID for integratedTerminal or externalTerminal. Use internalConsole instead, or use a compliant DAP client. See: https://github.com/microsoft/vscode/issues/61640");
		return processId.GetInt32();
	}

	// ---------------------------------------------------------------------------------------------
	// Helpers
	// ---------------------------------------------------------------------------------------------

	void SendEvent(string name, JsonObject body)
	{
		var server = _server;
		if (server is null) return;
		try
		{
			server.SendNotification(name, ToJsonElement(body));
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"SendEvent {name} failed: {ex.Message}");
		}
	}

	static JsonElement ToJsonElement(JsonNode node)
	{
		using var document = JsonDocument.Parse(node.ToJsonString());
		return document.RootElement.Clone();
	}

	static JsonObject CreateSource(string filePath, int sourceReference = 0) => new()
	{
		["path"] = filePath,
		["name"] = Path.GetFileName(filePath),
		["sourceReference"] = sourceReference,
		["checksums"] = ComputeSourceChecksums(filePath)
	};

	static JsonArray? ComputeSourceChecksums(string filePath)
	{
		try
		{
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
				return null;
			var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(filePath));
			return [new JsonObject { ["algorithm"] = "SHA256", ["checksum"] = Convert.ToHexString(hash) }];
		}
		catch
		{
			return null;
		}
	}

	static string ConvertStopReason(string reason) => reason.ToLowerInvariant() switch
	{
		"step" => "step",
		"breakpoint" => "breakpoint",
		"exception" => "exception",
		"pause" => "pause",
		"entry" => "entry",
		"goto" => "goto",
		"function breakpoint" => "function breakpoint",
		"data breakpoint" => "data breakpoint",
		"instruction breakpoint" => "instruction breakpoint",
		_ => "unknown"
	};

	/// <summary>Applies ExpressionEvaluationOptions (allowToString / showRawValues) to the debugger.</summary>
	static void ApplyExpressionEvaluationOptions(JsonObject config)
	{
		ManagedDebugger.AllowToString = true;
		ManagedDebugger.ShowRawValues = false;
		if (config["expressionEvaluationOptions"] is not JsonObject options)
			return;
		if (GetBool(options, "allowToString") is bool allowToString) ManagedDebugger.AllowToString = allowToString;
		if (GetBool(options, "showRawValues") is bool showRawValues) ManagedDebugger.ShowRawValues = showRawValues;
	}

	int ConvertClientLineToDebugger(int line) => _clientLinesStartAt1 ? line : line + 1;

	int ConvertDebuggerLineToClient(int line) => _clientLinesStartAt1 ? line : line - 1;

	int ConvertClientColumnToDebugger(int column) => _clientColumnsStartAt1 ? column : column + 1;

	int ConvertDebuggerColumnToClient(int column) => _clientColumnsStartAt1 ? column : column - 1;

	static string? GetSourcePath(JsonObject args) => GetString(args["source"] as JsonObject, "path");

	static string? GetString(JsonObject? obj, string key)
		=> obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	static int? GetInt(JsonObject? obj, string key)
		=> obj?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

	static bool? GetBool(JsonObject? obj, string key)
		=> obj?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

	static int RequiredInt(JsonObject? obj, string key)
		=> GetInt(obj, key) ?? throw new InvalidOperationException($"Missing {key}.");

	static List<string>? GetStringArray(JsonObject obj, string key)
		=> obj[key] is JsonArray array
			? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList()
			: null;

	static Dictionary<string, string>? GetStringDictionary(JsonObject obj, string key)
		=> obj[key] is JsonObject dictionary
			? dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.GetValue<string>() ?? string.Empty)
			: null;

	static JsonObject ToJsonObject<T>(IEnumerable<KeyValuePair<string, T>> values, Func<T, string> convert)
	{
		var json = new JsonObject();
		foreach (var (key, value) in values)
			json[key] = convert(value);
		return json;
	}

	sealed class EofStream(Stream inner, Action onEof) : Stream
	{
		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count)
		{
			try { int read = inner.Read(buffer, offset, count); if (read == 0 && count != 0) onEof(); return read; }
			catch { onEof(); throw; }
		}
		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			try { int read = await inner.ReadAsync(buffer, cancellationToken); if (read == 0 && buffer.Length != 0) onEof(); return read; }
			catch { onEof(); throw; }
		}
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	/// <summary>Passes writes through and reports once a chunk containing <paramref name="marker"/> has been written.</summary>
	sealed class ResponseWatchStream(Stream inner, string marker, Action onMarkerWritten) : Stream
	{
		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => inner.CanWrite;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => inner.Flush();
		public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
		public override void Write(ReadOnlySpan<byte> buffer)
		{
			inner.Write(buffer);
			inner.Flush();
			Watch(buffer);
		}
		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await inner.WriteAsync(buffer, cancellationToken);
			await inner.FlushAsync(cancellationToken);
			Watch(buffer.Span);
		}
		void Watch(ReadOnlySpan<byte> written)
		{
			if (System.Text.Encoding.UTF8.GetString(written).Contains(marker, StringComparison.Ordinal))
				onMarkerWritten();
		}
	}

	sealed class AdapterLogFactory : ILoggerFactory
	{
		public void AddProvider(ILoggerProvider provider) { }
		public ILogger CreateLogger(string categoryName) => new AdapterLogger(categoryName);
		public void Dispose() { }
	}

	sealed class AdapterLogger(string category) : ILogger
	{
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => new AdapterLogScope();
		public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
		public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (IsEnabled(level))
				System.Diagnostics.Debug.WriteLine($"{category} {formatter(state, exception)} {exception}");
		}
	}

	sealed class AdapterLogScope : IDisposable { public void Dispose() { } }
}
