using System.Text;
using SharpDbg.Application;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli;

// Extremely lightweight MI-like protocol server that speaks a tiny subset of GDB/MI
// This is intentionally minimal: it supports breakpoint insertion, run/continue, and simple evaluate
// to get you started. It uses stdin/stdout line protocol where commands are token-prefixed
// and responses mimic MI-style records.
internal static class MiProtocol
{
    private static TextWriter? _writer;

    public static void Run(ManagedDebugger debugger)
    {
        var input = Console.OpenStandardInput();
        var reader = new StreamReader(input, Encoding.UTF8);
        _writer = Console.Out;

        // Subscribe to debugger events to emit async MI notifications
        debugger.OnStopped += (threadId, reason) => EmitStopped(threadId, reason);
        debugger.OnStopped2 += (threadId, filePath, line, reason) => EmitStoppedDetailed(threadId, filePath, line, reason);
        debugger.OnModuleLoaded += (id, name, path) => EmitModuleLoaded(id, name, path);
        debugger.OnOutput += (output) => EmitOutput(output);

        _writer.WriteLine("MI protocol (expanded) ready");

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // token may be present: e.g. "1-break-insert main"
            var token = string.Empty;
            var cmd = line;
            // tokens are digits before first '-'
            var firstDash = line.IndexOf('-');
            if (firstDash > 0)
            {
                var possibleToken = line.Substring(0, firstDash).Trim();
                if (int.TryParse(possibleToken, out _))
                {
                    token = possibleToken;
                    cmd = line.Substring(firstDash + 1).Trim();
                }
            }

            try
            {
                if (cmd.StartsWith("break-insert") || cmd.StartsWith("break "))
                {
                    var parts = cmd.Split(' ', 2);
                    var spec = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    if (spec.Contains(":"))
                    {
                        var kv = spec.Split(':');
                        var file = kv[0];
                        var lineNo = int.Parse(kv[1]);
                        var bps = debugger.SetBreakpoints(file, new[] { lineNo });
                        if (bps.Count > 0)
                        {
                            _writer.WriteLine($"{token}^done,bkpt={{number=\"{bps[0].Id}\",file=\"{file}\",line=\"{lineNo}\"}}");
                        }
                        else
                        {
                            _writer.WriteLine($"{token}^error,msg=\"failed to set breakpoint\"");
                        }
                    }
                    else
                    {
                        _writer.WriteLine($"{token}^error,msg=\"unsupported breakpoint format\"");
                    }
                }
                else if (cmd.StartsWith("break-delete") || cmd.StartsWith("delete "))
                {
                    // break-delete N
                    var parts = cmd.Split(' ', 2);
                    if (parts.Length > 1 && int.TryParse(parts[1], out var id))
                    {
                        var removed = debugger.RemoveBreakpoint(id);
                        if (removed)
                        {
                            _writer.WriteLine($"{token}^done");
                        }
                        else
                        {
                            _writer.WriteLine($"{token}^error,msg=\"breakpoint not found\"");
                        }
                    }
                    else
                    {
                        _writer.WriteLine($"{token}^error,msg=\"invalid breakpoint id\"");
                    }
                }
                else if (cmd.StartsWith("exec-run") || cmd.StartsWith("run"))
                {
                    // Support a simple form: exec-run --program=/path/to/dotnet --args="app.dll arg1"
                    // Or: run --program=/path/to/app --args="..."
                    var argsMap = ParseArgs(cmd);
                    if (argsMap.TryGetValue("program", out var prog))
                    {
                        var rawArgs = argsMap.TryGetValue("args", out var a) ? SplitArgs(a) : Array.Empty<string>();
                        var stopAtEntry = argsMap.TryGetValue("stop-at-entry", out var stopVal) && (stopVal == "1" || stopVal.Equals("true", StringComparison.OrdinalIgnoreCase));
                        argsMap.TryGetValue("diag-port", out var diagPort);
                        try
                        {
                            debugger.Launch(prog, rawArgs, argsMap.TryGetValue("cwd", out var cwd) ? cwd : null, null, stopAtEntry, diagPort);
                            // MI standard: token^running may be used, but better to use ^done with running state
                            _writer.WriteLine(FormatResult(token, "running", null));
                        }
                        catch (Exception ex)
                        {
                            _writer.WriteLine(FormatResult(token, "error", new Dictionary<string, string> { { "msg", ex.Message } }));
                        }
                    }
                    else
                    {
                        _writer.WriteLine(FormatResult(token, "error", new Dictionary<string, string> { { "msg", "missing program argument" } }));
                    }
                }
                else if (cmd.StartsWith("exec-continue") || cmd.StartsWith("continue"))
                {
                    debugger.Continue();
                    _writer.WriteLine($"{token}^running");
                }
                else if (cmd.StartsWith("data-evaluate-expression") || cmd.StartsWith("p "))
                {
                    var parts = cmd.Split(' ', 2);
                    var expr = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    var evalTask = debugger.Evaluate(expr, null);
                    evalTask.Wait(3000);
                    if (evalTask.IsCompleted)
                    {
                        var (result, type, variablesReference) = evalTask.Result;
                        _writer.WriteLine(FormatResult(token, "done", new Dictionary<string, string> { { "value", result }, { "type", type } }));
                    }
                    else
                    {
                        _writer.WriteLine(FormatResult(token, "error", new Dictionary<string, string> { { "msg", "evaluation timeout" } }));
                    }
                }
                else if (cmd.StartsWith("stack-list-frames"))
                {
                    // stack-list-frames --thread THREAD --frame START,LEVELS
                    // For simplicity, list frames for thread 1
                    var frames = debugger.GetStackTrace(1, 0, 20);
                    var stackItems = new List<string>();
                    for (int i = 0; i < frames.Count; i++)
                    {
                        var f = frames[i];
                        var tup = MiTuple(new Dictionary<string, string>
                        {
                            ["level"] = MiString(i.ToString()),
                            ["addr"] = MiString("0x0"),
                            ["func"] = MiString(f.Name ?? string.Empty),
                            ["file"] = MiString(f.Source ?? string.Empty),
                            ["line"] = MiString(f.Line.ToString())
                        });
                        stackItems.Add(tup);
                    }
                    var stackList = MiList(stackItems);
                    _writer.WriteLine(FormatResult(token, "done", new Dictionary<string, string> { { "stack", stackList } }));
                }
                else if (cmd.StartsWith("stack-list-variables"))
                {
                    // stack-list-variables --frame N
                    // We'll attempt to list locals from first scope
                    var vars = debugger.GetVariables(0).Result; // sync call to simplify
                    var varItems = new List<string>();
                    for (int i = 0; i < vars.Count; i++)
                    {
                        var v = vars[i];
                        var tup = MiTuple(new Dictionary<string, string>
                        {
                            ["name"] = MiString(v.Name ?? string.Empty),
                            ["value"] = MiString(v.Value ?? string.Empty)
                        });
                        varItems.Add(tup);
                    }
                    var varsList = MiList(varItems);
                    _writer.WriteLine(FormatResult(token, "done", new Dictionary<string, string> { { "variables", varsList } }));
                }
                else if (cmd == "gdb-exit" || cmd == "quit")
                {
                    _writer.WriteLine($"{token}^done");
                    break;
                }
                else
                {
                    _writer.WriteLine(FormatResult(token, "error", new Dictionary<string, string> { { "msg", $"unknown command: {cmd}" } }));
                }
            }
            catch (Exception ex)
            {
                _writer.WriteLine(FormatResult(token, "error", new Dictionary<string, string> { { "msg", ex.Message } }));
            }
        }
    }

    private static void EmitStopped(int threadId, string reason)
    {
        _writer?.WriteLine($"*stopped,reason=\"{EscapeMi(reason)}\",thread-id=\"{threadId}\"");
    }

    private static void EmitStoppedDetailed(int threadId, string filePath, int line, string reason)
    {
        _writer?.WriteLine($"*stopped,reason=\"{EscapeMi(reason)}\",frame={{func=\"{EscapeMi(filePath)}\",file=\"{EscapeMi(filePath)}\",line=\"{line}\"}},thread-id=\"{threadId}\"");
    }

    private static void EmitModuleLoaded(string id, string name, string path)
    {
        // emit library info with name and path
        _writer?.WriteLine($"=library-loaded,library={{name=\"{EscapeMi(name)}\",path=\"{EscapeMi(path)}\"}}");
    }

    private static void EmitOutput(string output)
    {
        // ~ is console stream
        _writer?.WriteLine($"~\"{EscapeMi(output)}\"");
    }

    private static string EscapeMi(string? s)
    {
        if (s == null) return string.Empty;
        // MI uses C-style escaping for strings in many front-ends; keep it conservative
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static string FormatResult(string token, string resultClass, Dictionary<string, string>? fields)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(token)) sb.Append(token);
        sb.Append("^");
        sb.Append(resultClass);
        if (fields != null && fields.Count > 0)
        {
            sb.Append(',');
            var first = true;
            foreach (var kv in fields)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(kv.Key);
                sb.Append("=");
                // For complex values, caller should supply MI-compliant formatted value. We'll quote scalar strings.
                sb.Append('"');
                sb.Append(EscapeMi(kv.Value));
                sb.Append('"');
            }
        }
        return sb.ToString();
    }

    // MI value helpers: emit strings, tuples, and lists
    private static string MiString(string s) => $"\"{EscapeMi(s)}\"";

    private static string MiTuple(Dictionary<string, string> pairs)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var kv in pairs)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string MiList(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var it in items)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(it);
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseArgs(string cmd)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // simplistic parse: look for --key=value pairs
        var parts = cmd.Split(' ');
        foreach (var p in parts)
        {
            if (p.StartsWith("--"))
            {
                var kv = p.Substring(2).Split('=', 2);
                if (kv.Length == 2)
                {
                    dict[kv[0]] = kv[1].Trim('"');
                }
            }
        }
        return dict;
    }

    private static string[] SplitArgs(string raw)
    {
        // naive split honoring quotes
        var list = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in raw)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list.ToArray();
    }
}
