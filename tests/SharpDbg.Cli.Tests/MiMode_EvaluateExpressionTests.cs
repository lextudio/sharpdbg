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
        var breakLine1 = 25;
        var breakLine2 = 28;
        var breakLine3 = 53;
        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestAppExpression", "debug", "MiIntegrationTestAppExpression" });

        var commandId = 1;
        await SendCommandAsync(writer, reader, $"{commandId++}-break-insert {sourcePath}:{breakLine1}");
        await SendCommandAsync(writer, reader, $"{commandId++}-break-insert {sourcePath}:{breakLine2}");
        await SendCommandAsync(writer, reader, $"{commandId++}-break-insert {sourcePath}:{breakLine3}");
        await SendCommandAsync(writer, reader, $"{commandId++}-exec-run --program=\"{appPath}\"");

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
            Assert.True(await WaitForStoppedNotificationAsync(reader, 30) != null);
            var frameId = await RequestFrameIdAsync(writer, reader);
            foreach (var (expression, expected) in expressionStages[i])
            {
                await AssertExpressionResultAsync(writer, reader, frameId, expression, expected);
            }

            if (i < expressionStages.Length - 1)
            {
                await SendCommandAsync(writer, reader, $"{commandId++}-exec-continue");
            }
        }

        await SendCommandAsync(writer, reader, $"{commandId++}-exec-continue");
        await WaitForStoppedNotificationAsync(reader, 10);
        await SendCommandAsync(writer, reader, $"{commandId++}-gdb-exit");
        miProcess.Kill(true);

        async Task<int> RequestFrameIdAsync(TextWriter writerInner, TextReader readerInner)
        {
            var resp = await SendCommandAsync(writerInner, readerInner, $"{commandId++}-stack-list-frames");
            var match = Regex.Match(resp, "id=\\\"(\\d+)\\\"");
            Assert.True(match.Success, "Failed to find frame id in stack-list-frames response");
            return int.Parse(match.Groups[1].Value);
        }

        async Task AssertExpressionResultAsync(TextWriter writerInner, TextReader readerInner, int frameIdInner, string expression, string expectedValue)
        {
                var resp = await SendCommandAsync(writerInner, readerInner, $"{commandId++}-data-evaluate-expression --expression=\"{expression}\" --frame={frameIdInner}");
                var miLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sharpdbg_mi_debug_{miProcess.Id}.log");
                if (System.IO.File.Exists(miLogPath))
                {
                    try
                    {
                        var miLog = System.IO.File.ReadAllText(miLogPath);
                        _output.WriteLine($"[DEBUG-MI-FILE] {miLog}");
                    }
                    catch (System.Exception ex)
                    {
                        _output.WriteLine($"[DEBUG] Failed to read MI debug log: {ex.Message}");
                        _output.WriteLine($"[DEBUG] Full MI response for expression '{expression}': {resp}");
                    }
                }
                else
                {
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
                }
            Assert.Contains("^done", resp);
            _output.WriteLine($"Expression '{expression}' -> {resp}");
            Assert.Equal(expectedValue, ParseValue(resp));
        }

        async Task<string> SendCommandAsync(TextWriter writerInner, TextReader readerInner, string command)
        {
            await writerInner.WriteLineAsync(command);
            var response = await ReadMiResponseAsync(readerInner, 5);
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

    private static async Task<string?> ReadMiResponseAsync(TextReader reader, int timeoutSeconds)
    {
        using var timeoutCts = new CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, TestContext.Current.CancellationToken);
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(linkedCts.Token).AsTask().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                if (line == null) continue;
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

    private static async Task<string?> WaitForStoppedNotificationAsync(TextReader reader, int timeoutSeconds)
    {
        using var timeoutCts = new CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, TestContext.Current.CancellationToken);
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(linkedCts.Token).AsTask().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
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
