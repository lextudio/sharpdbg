using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiMode_SteppingTests
{
    readonly ITestOutputHelper _output;
    public MiMode_SteppingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MiMode_StepOver_Works()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        // Wait for first stopped (breakpoint)
        var stopped = await WaitForStoppedNotificationAsync(reader, 20);
        Assert.NotNull(stopped);
        _output.WriteLine($"Stopped: {stopped}");

        // Issue next (step over)
        await writer.WriteLineAsync("3-exec-next");
        var nextStopped = await WaitForStoppedNotificationAsync(reader, 10);
        Assert.NotNull(nextStopped);
        _output.WriteLine($"NextStopped: {nextStopped}");

        await writer.WriteLineAsync("4-gdb-exit");
        miProcess.Kill(true);
    }

    [Fact]
    public async Task MiMode_StepIn_Works()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        var stopped = await WaitForStoppedNotificationAsync(reader, 20);
        Assert.NotNull(stopped);
        _output.WriteLine($"Stopped: {stopped}");

        // Issue step (step in)
        await writer.WriteLineAsync("3-exec-step");
        var stepStopped = await WaitForStoppedNotificationAsync(reader, 10);
        Assert.NotNull(stepStopped);
        _output.WriteLine($"StepStopped: {stepStopped}");

        await writer.WriteLineAsync("4-gdb-exit");
        miProcess.Kill(true);
    }

    private static async Task<string?> ReadMiResponseAsync(TextReader reader, int timeoutSeconds)
    {
        var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
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
                var line = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                if (line != null && line.StartsWith("*stopped")) return line;
            }
        }
        catch (System.TimeoutException)
        {
        }
        return null;
    }
}
