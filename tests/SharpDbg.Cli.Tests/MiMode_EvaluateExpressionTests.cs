// Ported from Samsung/netcoredbg test-suite
// Original tests related to evaluation live across many netcoredbg MI tests (see test-suite/MITestExpression and related folders)
// This test implements the minimal flow: stop the debuggee, obtain a frame id, and call data-evaluate-expression --frame=<id>
using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiModeEvaluateExpressionTests
{
    [Fact]
    public async Task Evaluate_Expression_Works()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
        Console.WriteLine($"MI ready: {ready}");

        // Set a breakpoint in the test app and run it, then evaluate against the stopped frame
        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var bpResp = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
        Console.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        // Run the program using the native host executable (not dotnet CLI)
        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        // Wait for async stopped notification from MI server
        string? stoppedLine = null;
        for (int i = 0; i < 20; i++)
        {
            var l = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
            Console.WriteLine($"MI line: {l}");
            if (l != null && l.StartsWith("*stopped"))
            {
                stoppedLine = l;
                break;
            }
        }
        Assert.NotNull(stoppedLine);

        // Query stack frames and parse the returned frame id
        await writer.WriteLineAsync("3-stack-list-frames");
        var framesResp = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
        Console.WriteLine($"FramesResp: {framesResp}");
        Assert.Contains("^done", framesResp);

        // crude parse to find id="NN"
        var idMatch = System.Text.RegularExpressions.Regex.Match(framesResp ?? string.Empty, "id=\\\"(\\d+)\\\"");
        Assert.True(idMatch.Success, "Failed to find frame id in stack-list-frames response");
        var frameIdStr = idMatch.Groups[1].Value;

        // Evaluate an expression using the real frame id (we'll evaluate something simple like '1+2' to force interpreter path)
        await writer.WriteLineAsync($"4-data-evaluate-expression --expression=\"1+2\" --frame={frameIdStr}");
        var evalResp = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5));
        Console.WriteLine($"EvalResp: {evalResp}");
        Assert.Contains("^done", evalResp);
        await writer.WriteLineAsync("2-gdb-exit");
        miProcess.Kill(true);
    }
}
