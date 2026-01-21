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
        var breakLine = 25; // int c = tc.b + b; // BREAK1
        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestAppExpression", "debug", "MiIntegrationTestAppExpression" });

        var commandId = 1;
        await SendCommandAsync(writer, reader, $"{commandId++}-break-insert {sourcePath}:{breakLine}");
        await SendCommandAsync(writer, reader, $"{commandId++}-exec-run --program=\"{appPath}\"");

        // Wait for breakpoint to be hit
        Assert.True(await WaitForStoppedNotificationAsync(reader, 30) != null, "Expected to hit breakpoint");
        var frameId = await RequestFrameIdAsync(writer, reader);

        // Test some basic expression evaluations
        var expressions = new (string Expression, string Expected)[]
        {
            ("a", "10"),
            ("b", "11"),
            ("tc.a", "11"),
            ("tc.b", "11"),
            ("str1", "string1"),
            ("Program.Greeting", "hello"),
        };

        foreach (var (expression, expected) in expressions)
        {
            await AssertExpressionResultAsync(writer, reader, frameId, expression, expected);
        }

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
