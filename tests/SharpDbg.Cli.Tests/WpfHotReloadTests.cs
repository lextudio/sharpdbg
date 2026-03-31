using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class WpfHotReloadTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ApplyXamlText_CustomRequest_ReturnsHelperResult_InProc()
	{
		var (input, output, adapter) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);
		using var client = new RawDapClient(input, output);

		var initializeResponse = await client.SendRequestAsync("initialize", new
		{
			clientID = "sharpdbg-tests",
			clientName = "SharpDbg Tests",
			adapterID = "coreclr",
			locale = "en-us",
			linesStartAt1 = true,
			columnsStartAt1 = true,
			pathFormat = "path",
			supportsVariableType = true,
			supportsVariablePaging = true,
			supportsRunInTerminalRequest = true,
			supportsHandshakeRequest = true
		});
		initializeResponse["success"]?.ToObject<bool>().Should().BeTrue();

		var initializedEvent = await client.WaitForEventAsync("initialized");
		initializedEvent["event"]?.ToObject<string>().Should().Be("initialized");

		var programPath = DebugAdapterProcessHelper.GetDebuggableProgramPath();
		var launchResponse = await client.SendRequestAsync("launch", new
		{
			name = "LaunchRequestName",
			type = "coreclr",
			program = programPath,
			cwd = Path.GetDirectoryName(programPath)!,
			args = Array.Empty<string>(),
			stopAtEntry = false,
			console = "internalConsole"
		});
		launchResponse["success"]?.ToObject<bool>().Should().BeTrue();

		var sourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
		var lines = File.ReadAllLines(sourcePath);
		var breakpointLine = Array.FindIndex(lines, line => line.Contains("Log2")) + 1;
		breakpointLine.Should().BeGreaterThan(0);

		var setBreakpointsResponse = await client.SendRequestAsync("setBreakpoints", new
		{
			source = new
			{
				path = sourcePath
			},
			breakpoints = new[]
			{
				new
				{
					line = breakpointLine
				}
			}
		});
		setBreakpointsResponse["success"]?.ToObject<bool>().Should().BeTrue();

		var configurationDoneResponse = await client.SendRequestAsync("configurationDone");
		configurationDoneResponse["success"]?.ToObject<bool>().Should().BeTrue();

		var stoppedEvent = await client.WaitForEventAsync("stopped");
		stoppedEvent["body"]?["reason"]?.ToObject<string>().Should().Be("breakpoint");

		const string xamlText = "<Grid><TextBlock Text=\"Hello\" /></Grid>";

		var response = await client.SendRequestAsync("vsCustomMessage", new
		{
			message = new
			{
				sourceId = "wpfHotReload",
				messageCode = 1001,
				parameter1 = DebugAdapterProcessHelper.GetWpfHotReloadRuntimeStubPath(),
				parameter2 = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"),
				xamlText
			}
		});
		response["success"]?.ToObject<bool>().Should().BeTrue();
		response["body"]?["responseMessage"]?["parameter1"]?.ToObject<bool>().Should().BeTrue();
		response["body"]?["responseMessage"]?["parameter2"]?.ToObject<string>().Should().Be($"applied:Program.cs:{xamlText.Length}");

		var disconnectResponse = await client.SendRequestAsync("disconnect", new
		{
			restart = false,
			terminateDebuggee = true,
			suspendDebuggee = false
		});
		disconnectResponse["success"]?.ToObject<bool>().Should().BeTrue();
	}
}
