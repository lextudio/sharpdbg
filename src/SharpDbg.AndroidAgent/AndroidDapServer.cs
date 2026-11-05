using Microsoft.Extensions.Logging;
using System.Text.Json;
using OmniSharp.Extensions.DebugAdapter.Server;
using OmniSharp.Extensions.DebugAdapter.Protocol;
using OmniSharp.Extensions.DebugAdapter.Protocol.Models;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models.Response;

// DAP transport for the in-process Android agent. The protocol and this implementation
// are independent of the Microsoft VSCodeDebugProtocol library used by the desktop adapter.
internal sealed class AndroidDapServer
{
    readonly Stream stream;
    readonly ManagedDebugger debugger;
    readonly Func<ICorDebug> factory;
    readonly Action ready;
    readonly byte[] token;
    DebugAdapterServer? server;
    readonly TaskCompletionSource<bool> eof = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            using var tracked = new EofStream(stream, () => eof.TrySetResult(true));
            server = DebugAdapterServer.Create(options => {
                options.WithInput(tracked).WithOutput(stream).WithLoggerFactory(new DeviceLogFactory()).WithUnhandledExceptionHandler(ex => Profiler.Log(6, "SharpDbg", "DAP input: " + ex));
                options.Capabilities.SupportsConfigurationDoneRequest = true;
                options.Capabilities.SupportsConditionalBreakpoints = true;
                options.Capabilities.SupportsHitConditionalBreakpoints = true;
                options.Capabilities.SupportsFunctionBreakpoints = true;
                options.Capabilities.SupportsLogPoints = true;
                options.Capabilities.ExceptionBreakpointFilters = new Container<ExceptionBreakpointsFilter>(
                    new ExceptionBreakpointsFilter { Filter = "all", Label = "All exceptions" },
                    new ExceptionBreakpointsFilter { Filter = "user-unhandled", Label = "User-unhandled exceptions" });
                foreach (string command in new[] { "attach", "configurationDone", "setBreakpoints", "setFunctionBreakpoints", "setExceptionBreakpoints", "threads", "stackTrace", "resolveStackFrame", "scopes", "variables", "evaluate", "continue", "next", "stepIn", "stepOut", "pause", "modules", "loadedSources", "disconnect" })
                {
                    options.OnJsonRequest(command, async (JsonElement raw, CancellationToken cancellationToken) => {
                        if (command != "attach" && !attached) throw ToDapError(new InvalidOperationException("Attach with a valid session token first."));
                        var arguments = JsonNode.Parse(raw.GetRawText()) as JsonObject ?? new JsonObject();
                        Profiler.Log(4, "SharpDbg", "DAP request: " + command);
                        JsonObject body;
                        try { body = await HandleAsync(command, arguments); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { Profiler.Log(6, "SharpDbg", "DAP request failed: " + command + " " + ex); throw ToDapError(ex); }
                        Profiler.Log(4, "SharpDbg", "DAP request completed: " + command);
                        using var document = JsonDocument.Parse(body.ToJsonString());
                        return document.RootElement.Clone();
                    });
                }
            });
            var initializing = server.Initialize(CancellationToken.None);
            if (await Task.WhenAny(initializing, eof.Task) == initializing)
            {
                await initializing;
                linesAtOne = server.ClientSettings.LinesStartAt1;
                columnsAtOne = server.ClientSettings.ColumnsStartAt1;
                await eof.Task;
            }
        }
        finally
        {
            Profiler.Log(4, "SharpDbg", "DAP server ending; attached=" + attached + " disconnected=" + disconnected);
            ready();
            if (attached && !disconnected) try { debugger.Disconnect(false); } catch { }
            server?.Dispose();
        }
    }

    // The library wraps any other exception as "Internal Error - <exception.ToString()>", which would send the exception
    // type and call stack over adb to the IDE. RpcErrorException carries just our message.
    static RpcErrorException ToDapError(Exception ex) => new(ErrorCodes.InternalError, ex.GetType().Name, ex.Message);

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
        server?.SendNotification(name, document.RootElement.Clone());
    }

    private sealed class EofStream(Stream inner, Action onEof) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { int read = inner.Read(buffer, offset, count); if (read == 0 && count != 0) { Profiler.Log(4, "SharpDbg", "DAP stream EOF (sync)"); onEof(); } return read; }
            catch (Exception ex) { Profiler.Log(6, "SharpDbg", "DAP stream read: " + ex); onEof(); throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { int read = await inner.ReadAsync(buffer, cancellationToken); if (read == 0 && buffer.Length != 0) { Profiler.Log(4, "SharpDbg", "DAP stream EOF (async)"); onEof(); } return read; }
            catch (Exception ex) { Profiler.Log(6, "SharpDbg", "DAP stream read: " + ex); onEof(); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class DeviceLogFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new DeviceLogger(categoryName);
        public void Dispose() { }
    }
    private sealed class DeviceLogger(string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new DeviceScope();
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) Profiler.Log(6, "SharpDbg", category + " " + formatter(state, exception) + " " + exception); }
    }
    private sealed class DeviceScope : IDisposable { public void Dispose() { } }
}
