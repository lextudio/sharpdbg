// Ported from Samsung/netcoredbg test-suite
// Original tests related to evaluation live across many netcoredbg MI tests (see test-suite/MITestExpression and related folders)
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiModeEvaluateExpressionTests
{
    readonly ITestOutputHelper _output;
    public MiModeEvaluateExpressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Evaluate_Expression_Works()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var sourcePath = Path.JoinFromGitRoot(new string[] { "test", "mi-integration", "TestAppExpression", "Program.cs" });
        var breakLine1 = 29;
        var breakLine2 = 32;
        var breakLine3 = 34;
        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestAppExpression", "debug", "MiIntegrationTestAppExpression" });

        var commandId = 1;
        var pendingStops = new Queue<string>();
        await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-break-insert {sourcePath}:{breakLine1}");
        await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-break-insert {sourcePath}:{breakLine2}");
        await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-break-insert {sourcePath}:{breakLine3}");
        await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-exec-run --program=\"{appPath}\"");

        var expressionStages = new[]
        {
            new (string Expression, string Expected)[]
            {
                ("a", "10"),
                ("b", "11"),
                ("a + b", "21"),
                ("tc.a + b", "22"),
                ("str1 + str2", "string1string2"),
                ("valueArray[2]", "30"),
                ("Program.Greeting", "hello"),
                ("isTrue && !isFalse", "true"),
                ("Program.Multiply(2, 3)", "6"),
                ("optionalValue ?? fallbackValue", "5"),
            },
            new (string Expression, string Expected)[]
            {
                ("d + a", "109"),
                ("e - c", "10"),
                ("a < b", "true"),
                ("!isFalse", "true"),
                ("valueArray[0]", "10"),
                ("isTrue || isFalse", "true"),
            },
            new (string Expression, string Expected)[]
            {
                ("tc.a", "12"),
                ("tc.Sum", "23"),
                ("tc.b == b", "true"),
                ("tc.a + tc.b", "23"),
            }
        };

        for (int i = 0; i < expressionStages.Length; i++)
        {
            Assert.True(await WaitForStoppedNotificationAsync(reader, pendingStops, 30) != null);
            var frameLine = i switch
            {
                0 => breakLine1,
                1 => breakLine2,
                _ => breakLine3,
            };
            var frameId = await RequestFrameIdAsync(writer, reader, pendingStops, frameLine);
            foreach (var (expression, expected) in expressionStages[i])
            {
                await AssertExpressionResultAsync(writer, reader, pendingStops, frameId, expression, expected);
            }

            if (i < expressionStages.Length - 1)
            {
                await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-exec-continue");
            }
        }

        await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-exec-continue");
        await WaitForStoppedNotificationAsync(reader, pendingStops, 10);
        await SendCommandAsync(writer, reader, pendingStops, $"{commandId++}-gdb-exit");
        miProcess.Kill(true);

        async Task<int> RequestFrameIdAsync(TextWriter writerInner, TextReader readerInner, Queue<string> pendingStopsInner, int? preferredLine)
        {
            var resp = await SendCommandAsync(writerInner, readerInner, pendingStopsInner, $"{commandId++}-stack-list-frames");
            if (preferredLine.HasValue)
            {
                foreach (Match frameMatch in Regex.Matches(resp, "\\{[^}]*\\}"))
                {
                    var idMatch = Regex.Match(frameMatch.Value, "id=\\\"(?<id>\\d+)\\\"");
                    var lineMatch = Regex.Match(frameMatch.Value, "line=\\\"(?<line>\\d+)\\\"");
                    if (idMatch.Success && lineMatch.Success && lineMatch.Groups["line"].Value == preferredLine.Value.ToString())
                    {
                        return int.Parse(idMatch.Groups["id"].Value);
                    }
                }
            }

            var match = Regex.Match(resp, "id=\\\"(\\d+)\\\"");
            Assert.True(match.Success, "Failed to find frame id in stack-list-frames response");
            return int.Parse(match.Groups[1].Value);
        }

        async Task AssertExpressionResultAsync(TextWriter writerInner, TextReader readerInner, Queue<string> pendingStopsInner, int frameIdInner, string expression, string expectedValue)
        {
            var resp = await SendCommandAsync(writerInner, readerInner, pendingStopsInner, $"{commandId++}-data-evaluate-expression --expression=\"{expression}\" --frame={frameIdInner}");
            _output.WriteLine($"[DEBUG] Full MI response for expression '{expression}': {resp}");
            if (expression == "a" || expression == "b")
            {
                _output.WriteLine($"[DEBUG] Value of '{expression}' at BREAK1: {ParseValue(resp)}");
            }
            if (expression == "b")
            {
                _output.WriteLine($"[DEBUG] Sending evaluation request for 'b'");
                _output.WriteLine($"[DEBUG] Full MI response for 'b': {resp}");
            }

            Assert.Contains("^done", resp);
            _output.WriteLine($"Expression '{expression}' -> {resp}");
            Assert.Equal(expectedValue, ParseValue(resp));
        }

        async Task<string> SendCommandAsync(TextWriter writerInner, TextReader readerInner, Queue<string> pendingStopsInner, string command)
        {
            await writerInner.WriteLineAsync(command);
            var response = await ReadMiResponseAsync(readerInner, pendingStopsInner, 5);
            Assert.True(response != null, $"MI did not respond to `{command}`");
            return response!;
        }
    }

    private static string ParseValue(string response)
    {
        var match = Regex.Match(response, "value=\\\"(?<value>[^\"]+)\\\"");
        Assert.True(match.Success, "Failed to parse MI value");
        return match.Groups["value"].Value;
    }

    private static async Task<string?> ReadMiResponseAsync(TextReader reader, Queue<string> pendingStops, int timeoutSeconds)
    {
        using var timeoutCts = new CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, TestContext.Current.CancellationToken);
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                string? line = null;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token))
                {
                    readCts.CancelAfter(System.TimeSpan.FromSeconds(1));
                    try
                    {
                        line = await reader.ReadLineAsync(readCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                }
                if (line == null) continue;
                if (line.StartsWith("*stopped"))
                {
                    pendingStops.Enqueue(line);
                    continue;
                }
                if (line.StartsWith("^") || Regex.IsMatch(line, "^\\d+\\^"))
                {
                    return line;
                }
            }
        }
        catch (System.TimeoutException)
        {
        }
        return null;
    }

    private static async Task<string?> WaitForStoppedNotificationAsync(TextReader reader, Queue<string> pendingStops, int timeoutSeconds)
    {
        if (pendingStops.Count > 0)
        {
            return pendingStops.Dequeue();
        }
        using var timeoutCts = new CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, TestContext.Current.CancellationToken);
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                string? line = null;
                using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token))
                {
                    readCts.CancelAfter(System.TimeSpan.FromSeconds(1));
                    try
                    {
                        line = await reader.ReadLineAsync(readCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                }
                if (line != null && line.StartsWith("*stopped"))
                {
                    return line;
                }
            }
        }
        catch (System.TimeoutException)
        {
        }
        return null;
    }
}
