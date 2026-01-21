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
        
        // Set breakpoint at Console.WriteLine in Main (line 10) - a line we know will be hit
        await writer.WriteLineAsync($"1-break-insert {bpPath}:10");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestAppStepping", "debug", "MiIntegrationTestAppStepping" });
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        var stopped = await WaitForStoppedNotificationAsync(reader, 20);
        Assert.NotNull(stopped);
        Assert.Contains("line=\"10\"", stopped);
        _output.WriteLine($"Stopped at breakpoint: {stopped}");

        // Step to next line (should go to line 13 - the Foo() call)
        await writer.WriteLineAsync("3-exec-next");
        var nextStopped = await WaitForStoppedNotificationAsync(reader, 10);
        Assert.NotNull(nextStopped);
        _output.WriteLine($"After step-next: {nextStopped}");

        // Step into Foo() 
        await writer.WriteLineAsync("4-exec-step");
        var stepStopped = await WaitForStoppedNotificationAsync(reader, 10);
        Assert.NotNull(stepStopped);
        // Should now be inside Foo (around line 24-25)
        _output.WriteLine($"After step-in: {stepStopped}");
        // Verify we're at line 24 (inside Foo method)
        Assert.Contains("line=\"24\"", stepStopped);

        await writer.WriteLineAsync("5-gdb-exit");
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
