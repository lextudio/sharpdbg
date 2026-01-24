using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiMode_EvaluateTests
{
    readonly ITestOutputHelper _output;
    public MiMode_EvaluateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MITestEvaluate_BasicExpressions()
    {
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        var bpPath = Path.JoinFromGitRoot(new string[] { "test", "mi-integration", "TestApp", "Program.cs" });
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var bpResp = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"BreakResp: {bpResp}");
        Assert.Contains("^done", bpResp);

        var appPath = Path.JoinFromGitRoot(new string[] { "artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp" });
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        string? stoppedLine = null;
        for (int i = 0; i < 20; i++)
        {
            var l = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            _output.WriteLine($"MI line: {l}");
            if (l != null && l.StartsWith("*stopped"))
            {
                stoppedLine = l;
                break;
            }
        }
        Assert.NotNull(stoppedLine);

        // Query stack frames and find frame id
        await writer.WriteLineAsync("3-stack-list-frames");
        var framesResp = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"FramesResp: {framesResp}");
        Assert.Contains("^done", framesResp);
        var idMatch = System.Text.RegularExpressions.Regex.Match(framesResp ?? string.Empty, "id=\\\"(\\d+)\\\"");
        Assert.True(idMatch.Success, "Failed to find frame id in stack-list-frames response");
        var frameIdStr = idMatch.Groups[1].Value;
        var commandId = 4;

        // Evaluate some expressions in that frame
        var expressions = new (string Expression, string Expected)[]
        {
            ("dec", "12345678901234567890123456"),
            ("longZeroDec", "0.00000000000000000017"),
            ("shortZeroDec", "0.17"),
            ("array1[0]", "10"),
            ("array1[2]", "30"),
            ("array1[4]", "50"),
            ("valueArray[1]", "20"),
            ("1 + 1", "2"),
            ("a + b", "21"),
            ("str1 + str2", "string1string2"),
        };

        foreach (var (expression, expected) in expressions)
        {
            await writer.WriteLineAsync($"{commandId++}-data-evaluate-expression --expression=\"{expression}\" --frame={frameIdStr}");
            var evalResp = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            _output.WriteLine($"EvalResp: {evalResp}");
            Assert.Contains("^done", evalResp);
            Assert.Equal(expected, ParseValue(evalResp ?? string.Empty));
        }

        await writer.WriteLineAsync($"{commandId++}-gdb-exit");
        miProcess.Kill(true);
    }

    private static string ParseValue(string response)
    {
        var match = Regex.Match(response, "value=\\\"(?<value>[^\"]+)\\\"");
        Assert.True(match.Success, "Failed to parse MI value");
        return match.Groups["value"].Value;
    }
}
