using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiMode_VariablesTests
{
    readonly ITestOutputHelper _output;
    public MiMode_VariablesTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MITestVariables_BasicVariableReads()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var bpResp = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        string? stoppedLine = null;
        for (int i = 0; i < 20; i++)
        {
            var l = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            _output.WriteLine($"MI line: {l}");
            if (l != null && l.StartsWith("*stopped"))
            {
                stoppedLine = l;
                break;
            }
        }
        Assert.NotNull(stoppedLine);

        // Request stack frames
        await writer.WriteLineAsync("3-stack-list-frames");
        var framesResp = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"FramesResp: {framesResp}");
        Assert.Contains("^done", framesResp);

        // Request variables for frame id
        var idMatch = System.Text.RegularExpressions.Regex.Match(framesResp ?? string.Empty, "id=\\\"(\\d+)\\\"");
        Assert.True(idMatch.Success, "Failed to find frame id in stack-list-frames response");
        var frameIdStr = idMatch.Groups[1].Value;

        await writer.WriteLineAsync($"4-data-evaluate-expression --expression=\"1+2\" --frame={frameIdStr}");
        var evalResp = await reader.ReadLineAsync().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"EvalResp: {evalResp}");
        Assert.Contains("^done", evalResp);

        await writer.WriteLineAsync("5-gdb-exit");
        miProcess.Kill(true);
    }
}
