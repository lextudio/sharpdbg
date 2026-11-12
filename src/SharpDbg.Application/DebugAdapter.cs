// Copyright (c) LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Desktop DAP server built on SharpDbg.DapProtocol (MIT, generated from the DAP specification),
// instead of Microsoft.VisualStudio.Shared.VSCodeDebugProtocol. The latter's nuspec requires
// acceptance of Microsoft's separate, non-open-source software license, which is why OpenDevelop
// cannot redistribute it. The Android agent uses the same protocol implementation.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpDbg.DapProtocol;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.Infrastructure.Debugger.PresentationHintModels;

namespace SharpDbg.Application;

/// <summary>
/// Main debug adapter that coordinates the DAP transport and the debugger engine.
/// </summary>
public sealed class DebugAdapter : IDisposable, IDapRequestHandler
{
	ManagedDebugger _debugger;
	readonly Action<string>? _logger;
	bool _clientLinesStartAt1 = true;
	bool _clientColumnsStartAt1 = true;
	int _shutdownRequested;
	readonly ConcurrentDictionary<int, (string File, int Line, int? Column)> _gotoTargets = new();
	int _nextGotoTargetId;

	// The last launch/attach, replayed by a restart request.
	LaunchInfo? _lastLaunchInfo;
	bool _lastJustMyCode = true;
	// launch/attach 'decompileExternalCode': false hands the decision (and the PDB) for modules without symbols to the client
	bool _decompileExternalCode = true;
	TimeSpan _clientSymbolsTimeout = ManagedDebugger.DefaultClientSymbolsTimeout;
	int? _lastAttachProcessId;
	string? _lastAttachDebuggerLibraryDirectory;
	RemoteAttachInfo? _lastRemoteAttachInfo;
	// What the client last asked for, so a restart (which builds a new debugger) can set it again: the client
	// does not resend breakpoints for a `restart` request, and a relaunched program with none runs to completion.
	readonly Dictionary<string, SharpDbgBreakpointRequest[]> _sourceBreakpoints = new(StringComparer.Ordinal);
	SharpDbgFunctionBreakpointRequest[] _functionBreakpoints = [];
	// The subscription whose events may reach the client; see SubscribeToDebuggerEvents.
	object? _debuggerEventToken;
	List<SharpDbgExceptionBreakpointRequest> _exceptionBreakpoints = [];

	/// <summary>The DAP connection, created by <see cref="Initialize"/>.</summary>
	public DapConnection Connection { get; private set; } = null!;

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
		Connection?.Stop();
	}

	/// <summary>
	/// Creates the protocol connection over the given streams. Call <see cref="DapConnection.Start"/> to begin
	/// processing messages.
	/// </summary>
	public void Initialize(Stream input, Stream output)
	{
		Connection = new DapConnection(input, output, this, new DapConnectionOptions { Log = message => _logger?.Invoke(message) });
	}

	public ValueTask<DapResponse> HandleRequestAsync(IDapRequest request, DapRequestContext context)
	{
		if (request is InitializeArguments initialize)
		{
			_clientLinesStartAt1 = initialize.LinesStartAt1 ?? true;
			_clientColumnsStartAt1 = initialize.ColumnsStartAt1 ?? true;
			// DAP requires the 'initialized' event after the initialize response, not before it.
			context.OnResponseSent(() => Connection.SendEvent(new InitializedEvent()));
			return ValueTask.FromResult(DapResponse.Success(BuildCapabilities()));
		}

		if (request is UnknownRequest unknown)
		{
			// SharpDbg-specific commands are not part of the DAP specification, so the connection hands them
			// over as UnknownRequest with their raw arguments.
			if (unknown.Command is not ("resolveStackFrame" or "sourceLink" or "vsCustomMessage" or "loadSymbols"))
				return DapResponse.NotSupported(request);
			var rawArguments = unknown.Arguments is { ValueKind: JsonValueKind.Object } element
				? (JsonObject)JsonNode.Parse(element.GetRawText())!
				: new JsonObject();
			return HandleJsonAsync(unknown.Command, rawArguments, context);
		}

		// The handlers below work on DAP JSON, so re-serialize the typed arguments (including additional
		// properties such as launch/attach options). One code path for every command.
		var arguments = JsonSerializer.SerializeToNode(request, DapJson.DefaultOptions.GetTypeInfo(request.GetType())) as JsonObject ?? new JsonObject();
		return HandleJsonAsync(request.Command, arguments, context);
	}

	async ValueTask<DapResponse> HandleJsonAsync(string command, JsonObject arguments, DapRequestContext context)
	{
		_logger?.Invoke("DAP request: " + command);
		try
		{
			JsonObject body = await HandleAsync(command, arguments);
			// The response to 'disconnect' is written after this method returns: shut the host down only then,
			// or the response can be lost when the host exits.
			if (command is "disconnect") context.OnResponseSent(RequestDebuggerProcessShutdown);
			return DapResponse.Success(ToJsonElement(body));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger?.Invoke("DAP request failed: " + command + " " + ex);
			return DapResponse.Failure(new DapError(ex.Message));
		}
	}

	static JsonElement BuildCapabilities()
	{
		var capabilities = new JsonObject
		{
			["supportsConfigurationDoneRequest"] = true,
			// SharpDbg-specific: launch/attach decompileExternalCode, the symbolsNeeded event and the loadSymbols request
			["supportsClientSymbols"] = true,
			["supportsFunctionBreakpoints"] = true,
			["supportsConditionalBreakpoints"] = true,
			["supportsHitConditionalBreakpoints"] = true,
			["supportsEvaluateForHovers"] = true,
			["supportsStepBack"] = false,
			["supportsSetVariable"] = false,
			["supportsRestartFrame"] = false,
			["supportsTerminateRequest"] = true,
			["supportsExceptionInfoRequest"] = true,
			["supportsExceptionFilterOptions"] = true,
			["supportsBreakpointLocationsRequest"] = true,
			["supportsLoadedSourcesRequest"] = true,
			["supportsGotoTargetsRequest"] = true,
			["supportsRestartRequest"] = true,
			["supportsSingleThreadExecutionRequests"] = true, // continue/next/stepIn/stepOut honour singleThread
			["supportsStepInTargetsRequest"] = false,
			["supportsLogPoints"] = true,
			["exceptionBreakpointFilters"] = new JsonArray(
				new JsonObject { ["filter"] = "all", ["label"] = "All Exceptions", ["default"] = false },
				new JsonObject { ["filter"] = "user-unhandled", ["label"] = "User-Unhandled Exceptions", ["default"] = true })
		};
		return ToJsonElement(capabilities);
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

	async Task<JsonObject> HandleAsync(string command, JsonObject args)
	{
		// Visual Studio sends 'vsCustomMessage' requests, which SharpDbg acknowledges without acting on.
		if (command is "vsCustomMessage") return new JsonObject();

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
			// A thread waiting for the client's symbols holds the lock below: release it first, or teardown waits for the timeout
			_debugger.CancelSymbolWaiters();
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				_debugger.Disconnect(GetBool(args, "terminateDebuggee") ?? false);
			}
			return new JsonObject();
		}

		if (command == "terminate")
		{
			_debugger.CancelSymbolWaiters();
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				_debugger.Terminate();
			}
			return new JsonObject();
		}

		// Not under the lock below: the thread that asked for these symbols holds it while it waits for this answer.
		if (command == "loadSymbols") return LoadSymbols(args);

		using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
		{
			// With client symbols, draining the runtime events and the handlers themselves can wait for the client's symbols (see
			// loadSymbols). Requests are dispatched one at a time on the connection's dispatch loop until a handler yields, so
			// work that waits must not run there, or the answer could never be dispatched. The lock is already held, so the order
			// of requests is unchanged. Without client symbols nothing waits and the path is the one it always was.
			return _decompileExternalCode
				? await HandleLockedAsync(command, args)
				: await Task.Run(() => HandleLockedAsync(command, args));
		}
	}

	async Task<JsonObject> HandleLockedAsync(string command, JsonObject args)
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

	/// <summary>
	/// SharpDbg-specific request: the client's answer to a <c>symbolsNeeded</c> event (or symbols it provides up front) for the module
	/// with the given MVID. <c>pdbPath</c> missing or empty means the client has no symbols for it.
	/// </summary>
	JsonObject LoadSymbols(JsonObject args)
	{
		var mvidText = GetString(args, "mvid");
		if (!Guid.TryParse(mvidText, out var mvid))
			throw new InvalidOperationException("loadSymbols needs the module's 'mvid'");
		var pdbPath = GetString(args, "pdbPath");
		_logger?.Invoke($"loadSymbols: {mvid} -> {(string.IsNullOrEmpty(pdbPath) ? "(none)" : pdbPath)}");
		_debugger.ProvideSymbols(mvid, pdbPath);
		return new JsonObject { ["accepted"] = true };
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
			_decompileExternalCode = GetBool(args, "decompileExternalCode") ?? true;
			_clientSymbolsTimeout = GetInt(args, "clientSymbolsTimeoutMs") is int timeoutMs && timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : ManagedDebugger.DefaultClientSymbolsTimeout;
			_debugger.DecompileExternalCode = _decompileExternalCode;
			_debugger.ClientSymbolsTimeout = _clientSymbolsTimeout;
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
			_decompileExternalCode = GetBool(args, "decompileExternalCode") ?? true;
			_clientSymbolsTimeout = GetInt(args, "clientSymbolsTimeoutMs") is int timeoutMs && timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : ManagedDebugger.DefaultClientSymbolsTimeout;
			_debugger.DecompileExternalCode = _decompileExternalCode;
			_debugger.ClientSymbolsTimeout = _clientSymbolsTimeout;
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
			_debugger.CancelSymbolWaiters();
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				// Before anything touches the old debugger: its exit callbacks must not end the client's session.
				Volatile.Write(ref _debuggerEventToken, null);
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
				ReapplyBreakpoints();
				await _debugger.ConfigurationDone();
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleRestartRequestAsync failed: {ex.Message} , {ex}");
			throw new InvalidOperationException($"Failed to restart: {ex.Message}", ex);
		}
	}

	/// <summary>Sets the breakpoints the client had before a restart on the new debugger, as the initial setBreakpoints round would.</summary>
	void ReapplyBreakpoints()
	{
		foreach (var (sourcePath, requests) in _sourceBreakpoints)
			_debugger.SetBreakpoints(sourcePath, requests);
		if (_functionBreakpoints.Length > 0)
			_debugger.SetFunctionBreakpoints(_functionBreakpoints);
		if (_exceptionBreakpoints.Count > 0)
			_debugger.SetExceptionBreakpoints(_exceptionBreakpoints);
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

		if (requests.Length == 0)
			_sourceBreakpoints.Remove(sourcePath);
		else
			_sourceBreakpoints[sourcePath] = requests;
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
		_functionBreakpoints = requests;
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
		_exceptionBreakpoints = requests;
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
		var body = new JsonObject
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
			["details"] = ToExceptionDetails(exceptionInfo.Details)
		};
		// 'code' is a Visual Studio extension to the DAP specification; omit it when there is none.
		if (exceptionInfo.Code is not 0) body["code"] = exceptionInfo.Code;
		return body;
	}

	static JsonObject ToExceptionDetails(ExceptionInfo.ExceptionDetails details)
	{
		var result = new JsonObject
		{
			["message"] = details.Message,
			["typeName"] = details.TypeName,
			["fullTypeName"] = details.FullTypeName,
			["evaluateName"] = details.EvaluateName,
			["stackTrace"] = details.StackTrace
		};
		if (details.InnerException is { Count: > 0 } inner)
			result["innerException"] = new JsonArray(inner.Select(item => (JsonNode?)ToExceptionDetails(item)).ToArray());
		// Visual Studio extensions to the DAP specification
		if (details.FormattedDescription is not null) result["formattedDescription"] = details.FormattedDescription;
		if (details.HResult is not 0) result["hresult"] = details.HResult;
		if (details.Source is not null) result["source"] = details.Source;
		return result;
	}

	// ---------------------------------------------------------------------------------------------
	// Debugger events
	// ---------------------------------------------------------------------------------------------

	void SubscribeToDebuggerEvents()
	{
		// A restart replaces the debugger while the old one is still winding down: killing its debuggee raises
		// exited/terminated on it, which the client would take for the end of the whole session. Each subscription
		// holds its own token and a restart withdraws the current one before it touches the old debugger, so
		// nothing the old debugger raises from then on reaches the client (a comparison with the _debugger field
		// would still let events through while the old one is being disconnected, before the field is replaced).
		var token = new object();
		_debuggerEventToken = token;
		void SendFromCurrentDebugger(string name, JsonObject body)
		{
			if (ReferenceEquals(token, Volatile.Read(ref _debuggerEventToken)))
				SendEvent(name, body);
		}

		_debugger.OnStopped += (threadId, reason) =>
		{
			SendFromCurrentDebugger("stopped", new JsonObject
			{
				["reason"] = ConvertStopReason(reason),
				["threadId"] = threadId,
				["allThreadsStopped"] = true
			});
		};

		_debugger.OnStopped2 += (threadId, filePath, line, column, reason, hitBreakpointIds) =>
		{
			SendFromCurrentDebugger("stopped", new JsonObject
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
			SendFromCurrentDebugger("breakpoint", new JsonObject
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

		_debugger.OnContinued += threadId => SendFromCurrentDebugger("continued", new JsonObject
		{
			["threadId"] = threadId,
			["allThreadsContinued"] = true
		});

		// exitCode will be null in the attach scenario, as we cannot obtain the exit code of the process we attached to
		_debugger.OnExited += exitCode => SendFromCurrentDebugger("exited", new JsonObject { ["exitCode"] = exitCode ?? 0 });

		_debugger.OnTerminated += () => SendFromCurrentDebugger("terminated", new JsonObject());

		_debugger.OnThreadStarted += threadId => SendFromCurrentDebugger("thread", new JsonObject
		{
			["reason"] = "started",
			["threadId"] = threadId
		});

		_debugger.OnThreadExited += threadId => SendFromCurrentDebugger("thread", new JsonObject
		{
			["reason"] = "exited",
			["threadId"] = threadId
		});

		_debugger.DecompileExternalCode = _decompileExternalCode;
		_debugger.ClientSymbolsTimeout = _clientSymbolsTimeout;
		_debugger.OnSymbolsNeeded += (name, path, mvid) => SendFromCurrentDebugger("symbolsNeeded", new JsonObject
		{
			["moduleName"] = name,
			["modulePath"] = path,
			["mvid"] = mvid.ToString()
		});

		_debugger.OnModuleLoaded += (id, name, path) => SendFromCurrentDebugger("module", new JsonObject
		{
			["reason"] = "new",
			["module"] = new JsonObject { ["id"] = id, ["name"] = name, ["path"] = path }
		});
		_debugger.OnModuleUnloaded += (id, name, path) => SendFromCurrentDebugger("module", new JsonObject
		{
			["reason"] = "removed",
			["module"] = new JsonObject { ["id"] = id, ["name"] = name, ["path"] = path }
		});

		_debugger.OnLoadedSource += (reason, path) => SendFromCurrentDebugger("loadedSource", new JsonObject
		{
			["reason"] = reason == "removed" ? "removed" : "new",
			["source"] = CreateSource(path)
		});

		_debugger.OnProcessStarted += (processId, name, isAttach) => SendFromCurrentDebugger("process", new JsonObject
		{
			["name"] = name,
			["systemProcessId"] = processId,
			["startMethod"] = isAttach ? "attach" : "launch",
			["isLocalProcess"] = true
		});

		_debugger.OnOutput += (output, isStdError) => SendFromCurrentDebugger("output", new JsonObject
		{
			["category"] = isStdError ? "stderr" : "stdout",
			["output"] = output
		});

		// Debugger messages the user should see, e.g. a breakpoint condition that failed to evaluate.
		_debugger.OnImportantOutput += message => SendFromCurrentDebugger("output", new JsonObject
		{
			["category"] = "important",
			["output"] = message
		});

		_debugger.SendRunInTerminalRequest += SendRunInTerminal;
	}

	int SendRunInTerminal(LaunchInfo launchInfo)
	{
		var programForTitle = launchInfo.Program is "dotnet" ? launchInfo.Arguments[0] : launchInfo.Program;
		var runInTerminalArguments = new RunInTerminalArguments
		{
			Kind = launchInfo.LaunchRequestConsoleType switch
			{
				LaunchRequestConsoleType.IntegratedTerminal => RunInTerminalArguments.KindValue.Integrated,
				LaunchRequestConsoleType.ExternalTerminal => RunInTerminalArguments.KindValue.External,
				_ => throw new ArgumentOutOfRangeException(nameof(launchInfo), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{launchInfo.LaunchRequestConsoleType}'")
			},
			Title = $"{Path.GetFileName(programForTitle)} [DEBUG]",
			Cwd = launchInfo.Cwd ?? Environment.CurrentDirectory,
			Args = [launchInfo.Program, .. launchInfo.Arguments],
			// Make the child suspend at startup so the debugger can attach before its runtime loads.
			Env = new Dictionary<string, string?>(launchInfo.Env) { ["DOTNET_DefaultDiagnosticPortSuspend"] = "1" }
		};

		var response = Connection.SendRequest(runInTerminalArguments);
		if (response.Error is { } error)
			throw new InvalidOperationException($"RunInTerminalRequest failed: {error.Message}");
		// https://github.com/microsoft/vscode/issues/61640 - ProcessId will not be returned for
		// integratedTerminal or externalTerminal.
		if (response.Body?.ProcessId is not { } processId)
			throw new InvalidOperationException("RunInTerminalRequest did not return a process ID. VSCode does not return a process ID for integratedTerminal or externalTerminal. Use internalConsole instead, or use a compliant DAP client. See: https://github.com/microsoft/vscode/issues/61640");
		return processId;
	}

	// ---------------------------------------------------------------------------------------------
	// Helpers
	// ---------------------------------------------------------------------------------------------

	void SendEvent(string name, JsonObject body)
	{
		var connection = Connection;
		if (connection is null) return;
		try
		{
			connection.SendRawEvent(name, ToJsonElement(body));
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
}
