using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiMode_ImportedFixturesTests
{
    readonly ITestOutputHelper _output;
    public MiMode_ImportedFixturesTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MiMode_TestApp1_Breakpoint_Hits()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot(new string[] { "test", "mi-integration", "TestApp1", "Program.cs" });
        await writer.WriteLineAsync($"1-break-insert {bpPath}:6");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestApp1", "debug", "MiIntegrationTestApp1" });
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        var stopped = await WaitForStoppedNotificationAsync(reader, 20);
        Assert.NotNull(stopped);
        _output.WriteLine($"Stopped: {stopped}");

        await writer.WriteLineAsync("3-gdb-exit");
        miProcess.Kill(true);
    }

    [Fact]
    public async Task MiMode_TestApp2_StepOver_Works()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp2", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:6");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp2", "debug", "MiIntegrationTestApp2");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

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

    private static async Task<string?> ReadMiResponseAsync(TextReader reader, int timeoutSeconds)
    {
        using var timeoutCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, TestContext.Current.CancellationToken);
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(linkedCts.Token).AsTask().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
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
        using var timeoutCts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, TestContext.Current.CancellationToken);
        try
        {
            while (!linkedCts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(linkedCts.Token).AsTask().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                if (line != null && line.StartsWith("*stopped")) return line;
            }
        }
        catch (System.TimeoutException)
        {
        }
        return null;
    }
}
