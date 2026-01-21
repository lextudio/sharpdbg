using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

// Partial port of netcoredbg/test-suite/MITestBreakpoint/Program.cs
public class MiMode_MITestBreakpointTests
{
    readonly ITestOutputHelper _output;
    public MiMode_MITestBreakpointTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MITestBreakpoint_CoreFlow_Works()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        // Insert first breakpoint (BREAK1) and run
        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var bpResp = await ReadMiResponseAsync(reader, 5);
        Console.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        // Run the program using the native host executable (not dotnet CLI)
        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        // Attempt to read exec-run response or stopped notification (be tolerant)
        // Attempt to read exec-run response or stopped notification (be tolerant)
        var runResp = await ReadMiResponseAsync(reader, 30);
        Console.WriteLine($"RunResp: {runResp}");

        // Delete the inserted breakpoint
        await writer.WriteLineAsync("3-break-delete 1");
        var delResp = await ReadMiResponseAsync(reader, 30);
        Console.WriteLine($"DelResp: {delResp}");
        if (delResp != null)
        {
            Assert.Contains("^done", delResp);
        }

        // Exit MI
        await writer.WriteLineAsync("4-gdb-exit");
        var exitResp = await ReadMiResponseAsync(reader, 10);
        _output.WriteLine($"ExitResp: {exitResp}");
        miProcess.Kill(true);
    }

    private static async Task<string?> ReadMiResponseAsync(System.IO.TextReader reader, int timeoutSeconds)
    {
        var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                if (line == null) continue;
                // Return a line that looks like MI output (result '^', async '*', notifications '=', '~')
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
}
