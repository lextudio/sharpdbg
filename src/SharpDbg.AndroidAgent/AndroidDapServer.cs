using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ICorDebugSharp;
using SharpDbg.DapProtocol;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models.Response;

// DAP transport for the in-process Android agent, on SharpDbg's own DapProtocol (MIT, generated from
// the DAP specification). It no longer depends on the OmniSharp DAP fork.
internal sealed class AndroidDapServer : IDapRequestHandler
{
    readonly Stream stream;
    readonly ManagedDebugger debugger;
    readonly Func<ICorDebug> factory;
    readonly Action ready;
    readonly byte[] token;
    DapConnection? connection;
    bool attached, disconnected;
    bool linesAtOne = true, columnsAtOne = true;

    internal AndroidDapServer(Stream stream, string token, string modules, Func<ICorDebug> factory, Action ready)
    {
        if (string.IsNullOrEmpty(token)) throw new ArgumentException("A session token is required.", nameof(token));
        this.stream = stream;
        this.token = Encoding.UTF8.GetBytes(token);
        this.factory = factory;
        this.ready = ready;
        debugger = new ManagedDebugger(message => Profiler.Log(4, "SharpDbg", message), modules);
        debugger.OnStopped += (thread, reason) => Event("stopped", new JsonObject { ["threadId"] = thread, ["reason"] = reason, ["allThreadsStopped"] = true });
        debugger.OnStopped2 += (thread, file, line, column, reason, ids) => Event("stopped", new JsonObject { ["threadId"] = thread, ["reason"] = reason, ["allThreadsStopped"] = true, ["source"] = Source(file), ["line"] = OutLine(line), ["column"] = OutColumn(column), ["hitBreakpointIds"] = ids is null ? null : new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) });
        debugger.OnBreakpointChanged += bp => Event("breakpoint", new JsonObject { ["reason"] = "changed", ["breakpoint"] = Breakpoint(bp) });
        debugger.OnContinued += thread => Event("continued", new JsonObject { ["threadId"] = thread, ["allThreadsContinued"] = true });
        debugger.OnThreadStarted += thread => Event("thread", new JsonObject { ["threadId"] = thread, ["reason"] = "started" });
        debugger.OnThreadExited += thread => Event("thread", new JsonObject { ["threadId"] = thread, ["reason"] = "exited" });
        debugger.OnTerminated += () => Event("terminated", new JsonObject());
        debugger.OnExited += code => Event("exited", new JsonObject { ["exitCode"] = code ?? 0 });
        debugger.OnOutput += (output, error) => Event("output", new JsonObject { ["category"] = error ? "stderr" : "stdout", ["output"] = output });
    }

    internal async Task RunAsync()
    {
        try
        {
            connection = new DapConnection(stream, stream, this, new DapConnectionOptions { Log = message => Profiler.Log(6, "SharpDbg", message) });
            connection.Start();
            await connection.Completion;
        }
        finally
        {
            Profiler.Log(4, "SharpDbg", "DAP server ending; attached=" + attached + " disconnected=" + disconnected);
            ready();
            if (attached && !disconnected) try { debugger.Disconnect(false); } catch { }
            connection?.Stop();
        }
    }

    public ValueTask<DapResponse> HandleRequestAsync(IDapRequest request, DapRequestContext context)
    {
        switch (request)
        {
            case InitializeArguments initialize:
                linesAtOne = initialize.LinesStartAt1 ?? true;
                columnsAtOne = initialize.ColumnsStartAt1 ?? true;
                // DAP requires the 'initialized' event after the initialize response, not before it.
                context.OnResponseSent(() => connection?.SendEvent(new InitializedEvent()));
                return ValueTask.FromResult(DapResponse.Success(Capabilities()));
            case UnknownRequest unknown when unknown.Command == "resolveStackFrame":
                var raw = unknown.Arguments is { ValueKind: JsonValueKind.Object } element
                    ? (JsonObject)JsonNode.Parse(element.GetRawText())!
                    : new JsonObject();
                return HandleJsonAsync(unknown.Command, raw);
            case UnknownRequest:
                return DapResponse.NotSupported(request);
        }

        if (!attached && request is not AttachArguments)
            return ValueTask.FromResult(DapResponse.Failure(new DapError("Attach with a valid session token first.")));

        // The handlers below work on DAP JSON. Re-serialize the typed arguments (including their
        // additional properties, e.g. attach's sessionToken/justMyCode) so there is one code path.
        var arguments = JsonSerializer.SerializeToNode(request, DapJson.DefaultOptions.GetTypeInfo(request.GetType())) as JsonObject ?? new JsonObject();
        return HandleJsonAsync(request.Command, arguments);
    }

    async ValueTask<DapResponse> HandleJsonAsync(string command, JsonObject arguments)
    {
        Profiler.Log(4, "SharpDbg", "DAP request: " + command);
        try
        {
            JsonObject body = await HandleAsync(command, arguments);
            using var document = JsonDocument.Parse(body.ToJsonString());
            return DapResponse.Success(document.RootElement.Clone());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Profiler.Log(6, "SharpDbg", "DAP request failed: " + command + " " + ex);
            return DapResponse.Failure(new DapError(ex.Message));
        }
    }

    static Capabilities Capabilities() => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsConditionalBreakpoints = true,
        SupportsHitConditionalBreakpoints = true,
        SupportsFunctionBreakpoints = true,
        SupportsLogPoints = true,
        ExceptionBreakpointFilters = [
            new ExceptionBreakpointsFilter { Filter = "all", Label = "All exceptions" },
            new ExceptionBreakpointsFilter { Filter = "user-unhandled", Label = "User-unhandled exceptions" }
        ]
    };

    async Task<JsonObject> HandleAsync(string command, JsonObject args)
    {
        if (command == "attach")
        {
            var supplied = Encoding.UTF8.GetBytes((string?)args["sessionToken"] ?? "");
            if (!CryptographicOperations.FixedTimeEquals(token, supplied)) throw new InvalidOperationException("Invalid debug session token.");
            if (attached) throw new InvalidOperationException("Already attached.");
            using (await debugger.DapRequestAndRuntimeEventLock.LockAsync())
            {
                await debugger.DrainRuntimeEventQueue();
                debugger.AttachInProcess(Environment.ProcessId, (bool?)args["justMyCode"] ?? true, factory, ready);
                attached = true;
            }
            return new JsonObject();
        }
        using (await debugger.DapRequestAndRuntimeEventLock.LockAsync())
        {
            await debugger.DrainRuntimeEventQueue();
            switch (command)
            {
                case "configurationDone": await debugger.ConfigurationDone(); return new();
                case "setBreakpoints":
                    var path = (string?)args["source"]?["path"] ?? throw new InvalidOperationException("Missing source path.");
                    var bps = (args["breakpoints"] as JsonArray ?? new()).Select(n => new SharpDbgBreakpointRequest(InLine((int?)n?["line"] ?? 0), (string?)n?["condition"], (string?)n?["hitCondition"], n?["column"] is null ? null : InColumn((int)n["column"]!), (string?)n?["logMessage"])).ToArray();
                    return new JsonObject { ["breakpoints"] = new JsonArray(debugger.SetBreakpoints(path, bps).Select(bp => (JsonNode?)Breakpoint(bp)).ToArray()) };
                case "setFunctionBreakpoints":
                    var funcs = (args["breakpoints"] as JsonArray ?? new()).Select(n => new SharpDbgFunctionBreakpointRequest((string?)n?["name"] ?? "", (string?)n?["condition"], (string?)n?["hitCondition"])).ToArray();
                    return new JsonObject { ["breakpoints"] = new JsonArray(debugger.SetFunctionBreakpoints(funcs).Select(bp => (JsonNode?)Breakpoint(bp)).ToArray()) };
                case "setExceptionBreakpoints":
                    var filters = (args["filters"] as JsonArray ?? new()).Select(n => new SharpDbgExceptionBreakpointRequest((string?)n switch { "all" => SharpDbgExceptionBreakpointFilter.All, "user-unhandled" => SharpDbgExceptionBreakpointFilter.UserUnhandled, _ => throw new InvalidOperationException("Unknown exception filter.") })).ToArray();
                    debugger.SetExceptionBreakpoints(filters); return new();
                case "threads": return new JsonObject { ["threads"] = new JsonArray(debugger.GetThreads().Select(t => (JsonNode?)new JsonObject { ["id"] = t.id, ["name"] = t.name }).ToArray()) };
                case "stackTrace":
                    var frames = debugger.GetStackTrace(RequiredInt(args, "threadId"), (int?)args["startFrame"] ?? 0, (int?)args["levels"]);
                    return new JsonObject { ["stackFrames"] = new JsonArray(frames.Select(f => (JsonNode?)Frame(f)).ToArray()) };
                case "resolveStackFrame": return new JsonObject { ["stackFrame"] = Frame(debugger.ResolveStackFrame(RequiredInt(args, "stackFrameId"))) };
                case "scopes": return new JsonObject { ["scopes"] = new JsonArray(debugger.GetScopes(RequiredInt(args, "frameId")).Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["variablesReference"] = s.VariablesReference, ["expensive"] = s.Expensive }).ToArray()) };
                case "variables":
                    var vars = await debugger.GetVariables(RequiredInt(args, "variablesReference"));
                    return new JsonObject { ["variables"] = new JsonArray(vars.Skip((int?)args["start"] ?? 0).Take((int?)args["count"] ?? int.MaxValue).Select(v => (JsonNode?)Variable(v)).ToArray()) };
                case "evaluate":
                    var value = await debugger.Evaluate((string?)args["expression"] ?? "", (int?)args["frameId"]);
                    return new JsonObject { ["result"] = value.Value, ["type"] = value.Type, ["variablesReference"] = value.VariablesReference, ["memoryReference"] = value.MemoryReference };
                case "continue": debugger.HandleContinueRequest(); return new JsonObject { ["allThreadsContinued"] = true };
                case "next": await debugger.StepNext(RequiredInt(args, "threadId")); return new();
                case "stepIn": await debugger.StepIn(RequiredInt(args, "threadId")); return new();
                case "stepOut": await debugger.StepOut(RequiredInt(args, "threadId")); return new();
                case "pause": debugger.Pause(); return new();
                case "modules": return new JsonObject { ["modules"] = new JsonArray(debugger.GetModules().Select(m => (JsonNode?)new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["path"] = m.Path, ["isUserCode"] = m.IsUserCode, ["symbolStatus"] = m.HasSymbols ? "loaded" : "not loaded" }).ToArray()) };
                case "loadedSources": return new JsonObject { ["sources"] = new JsonArray(debugger.GetLoadedSources().Select(s => (JsonNode?)Source(s)).ToArray()) };
                case "disconnect": debugger.Disconnect((bool?)args["terminateDebuggee"] ?? false); disconnected = true; return new();
                default: throw new NotSupportedException($"Request '{command}' is not supported.");
            }
        }
    }

    static int RequiredInt(JsonObject o, string key) => (int?)o[key] ?? throw new InvalidOperationException($"Missing {key}.");
    int InLine(int n) => linesAtOne ? n : n + 1;
    int InColumn(int n) => columnsAtOne ? n : n + 1;
    int OutLine(int n) => linesAtOne ? n : n - 1;
    int OutColumn(int n) => columnsAtOne ? n : n - 1;
    static JsonObject Source(string path) => new() { ["path"] = path, ["name"] = Path.GetFileName(path) };
    JsonObject Breakpoint(BreakpointManager.BreakpointInfo bp) => new() { ["id"] = bp.Id, ["verified"] = bp.Verified, ["line"] = OutLine(bp.Line), ["column"] = bp.Column is int c ? OutColumn(c) : null, ["message"] = bp.Message, ["source"] = bp.IsFunctionBreakpoint ? null : Source(bp.FilePath) };
    JsonObject Frame(StackFrameInfo f) => new() { ["id"] = f.Id, ["name"] = f.Name, ["line"] = OutLine(f.Line), ["column"] = OutColumn(f.Column), ["source"] = f.Source is null ? null : Source(f.Source), ["isResolved"] = f.IsResolved, ["instructionPointerReference"] = f.InstructionPointerReference };
    static JsonObject Variable(VariableInfo v) => new() { ["name"] = v.Name, ["evaluateName"] = v.Name, ["value"] = v.Value, ["type"] = v.Type, ["variablesReference"] = v.VariablesReference, ["memoryReference"] = v.MemoryReference };
    void Event(string name, JsonObject body)
    {
        using var document = JsonDocument.Parse(body.ToJsonString());
        connection?.SendRawEvent(name, document.RootElement.Clone());
    }

}
