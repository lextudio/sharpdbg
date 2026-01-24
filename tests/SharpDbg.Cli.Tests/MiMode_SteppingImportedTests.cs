using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiMode_SteppingImportedTests
{
    readonly ITestOutputHelper _output;
    public MiMode_SteppingImportedTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MiMode_StepIn_And_StepOver()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot(new string[] { "test", "mi-integration", "TestAppStepping", "Program.cs" });
        // breakpoint at call to Foo (line 13)
        await writer.WriteLineAsync($"1-break-insert {bpPath}:13");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        // breakpoint at call to Bar (line 16) for step-over test
        await writer.WriteLineAsync($"2-break-insert {bpPath}:16");
        var bpResp2 = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp2: {bpResp2}");
        Assert.Contains("^done", bpResp2);

        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestAppStepping", "debug", "MiIntegrationTestAppStepping" });
        await writer.WriteLineAsync($"3-exec-run --program=\"{appPath}\"");

        var stopped = await WaitForStoppedNotificationAsync(reader, 20);
        Assert.NotNull(stopped);
        _output.WriteLine($"Stopped: {stopped}");

        // step in into Foo
        await writer.WriteLineAsync("4-exec-step");
        var stepStopped = await WaitForStoppedNotificationAsync(reader, 10);
        Assert.NotNull(stepStopped);
        _output.WriteLine($"StepStopped: {stepStopped}");

        // continue and step over Bar
        await writer.WriteLineAsync("5-exec-continue");
        var cont = await WaitForStoppedNotificationAsync(reader, 20);
        // expect to hit next breakpoint at Bar call
        Assert.NotNull(cont);
        _output.WriteLine($"ContinueStopped: {cont}");

        await writer.WriteLineAsync("6-exec-next");
        var nextStopped = await WaitForStoppedNotificationAsync(reader, 10);
        Assert.NotNull(nextStopped);
        _output.WriteLine($"NextStopped: {nextStopped}");

        await writer.WriteLineAsync("7-gdb-exit");
        miProcess.Kill(true);
    }

    private static async Task<string?> ReadMiResponseAsync(TextReader reader, int timeoutSeconds)
    {
        var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                if (line == null) continue;
                if (line.StartsWith("^") || line.StartsWith("*") || line.StartsWith("=") || line.StartsWith("~") || System.Text.RegularExpressions.Regex.IsMatch(line, "^\\d+\\^"))
                {
                    return line;
                }
            }
        }
        catch (System.TimeoutException)
        {
            // swallow and return null
        }
        return null;
    }

    private static async Task<string?> WaitForStoppedNotificationAsync(TextReader reader, int timeoutSeconds)
    {
        var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                if (line != null && line.StartsWith("*stopped")) return line;
            }
        }
        catch (System.TimeoutException)
        {
        }
        return null;
    }
}
