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

	[Fact]
	public async Task ApplyXamlText_CustomRequest_UpdatesRealWpfSample_InProc()
	{
		DebugAdapterProcessHelper.EnsureProjectBuilt(DebugAdapterProcessHelper.GetWorkspaceSampleProjectPath(), testOutputHelper);
		DebugAdapterProcessHelper.EnsureProjectBuilt(DebugAdapterProcessHelper.GetWorkspaceWpfHotReloadRuntimeProjectPath(), testOutputHelper);

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

		var programPath = DebugAdapterProcessHelper.GetWorkspaceSampleProgramPath();
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

		var sourcePath = DebugAdapterProcessHelper.GetWorkspaceSampleMainWindowCodeBehindPath();
		var lines = File.ReadAllLines(sourcePath);
		var breakpointLine = Array.FindIndex(lines, line => line.Contains("HotReloadReady();")) + 1;
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

		const string xamlText =
			"<Window x:Class=\"sample.MainWindow\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
			"xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Title=\"Hot Reloaded\"><Grid>" +
			"<TextBlock Text=\"Live update from SharpDbg\" /></Grid></Window>";

		var response = await client.SendRequestAsync("vsCustomMessage", new
		{
			message = new
			{
				sourceId = "wpfHotReload",
				messageCode = 1001,
				parameter1 = DebugAdapterProcessHelper.GetWorkspaceWpfHotReloadRuntimePath(),
				parameter2 = DebugAdapterProcessHelper.GetWorkspaceSampleMainWindowXamlPath(),
				xamlText
			}
		});
		response["success"]?.ToObject<bool>().Should().BeTrue();
		response["body"]?["responseMessage"]?["parameter1"]?.ToObject<bool>().Should().BeTrue();
		response["body"]?["responseMessage"]?["parameter2"]?.ToObject<string>().Should().Be("ok: window updated");

		var disconnectResponse = await client.SendRequestAsync("disconnect", new
		{
			restart = false,
			terminateDebuggee = true,
			suspendDebuggee = false
		});
		disconnectResponse["success"]?.ToObject<bool>().Should().BeTrue();
	}

	[Fact]
	public async Task ApplyXamlText_CustomRequest_UpdatesNestedUserControl_InProc()
	{
		DebugAdapterProcessHelper.EnsureProjectBuilt(DebugAdapterProcessHelper.GetWorkspaceSampleProjectPath(), testOutputHelper);
		DebugAdapterProcessHelper.EnsureProjectBuilt(DebugAdapterProcessHelper.GetWorkspaceWpfHotReloadRuntimeProjectPath(), testOutputHelper);

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

		var programPath = DebugAdapterProcessHelper.GetWorkspaceSampleProgramPath();
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

		var sourcePath = DebugAdapterProcessHelper.GetWorkspaceSampleMainWindowCodeBehindPath();
		var lines = File.ReadAllLines(sourcePath);
		var breakpointLine = Array.FindIndex(lines, line => line.Contains("HotReloadReady();")) + 1;
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
		var threadId = stoppedEvent["body"]?["threadId"]?.ToObject<int>();
		threadId.Should().NotBeNull();
		var frameId = await GetTopFrameIdAsync(client, threadId!.Value);
		var beforePaneHash = await EvaluateExpressionAsync(client, frameId, "GetPaneHashCode()");
		var beforePaneWidth = await EvaluateExpressionAsync(client, frameId, "GetPaneWidth()");
		var beforeTitleHash = await EvaluateExpressionAsync(client, frameId, "GetPaneTitleHashCode()");
		var beforeBodyHash = await EvaluateExpressionAsync(client, frameId, "GetPaneBodyHashCode()");
		var beforeListItemOneHash = await EvaluateExpressionAsync(client, frameId, "GetPaneListItemOneHashCode()");
		var beforeListItemTwoHash = await EvaluateExpressionAsync(client, frameId, "GetPaneListItemTwoHashCode()");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneListSelectedIndex()")).Should().Be("1");
		beforePaneWidth.Should().Be("NaN");

		const string xamlText =
			"<UserControl x:Class=\"sample.SamplePane\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
			"xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Width=\"321\"><Border x:Name=\"PaneBorder\" Padding=\"10\" Background=\"LightGreen\">" +
			"<StackPanel x:Name=\"PaneStack\"><TextBlock x:Name=\"PaneBody\" Text=\"Nested body update\" />" +
			"<TextBlock x:Name=\"PaneInserted\" Text=\"Inserted sibling\" FontStyle=\"Italic\" />" +
			"<TextBlock x:Name=\"PaneTitle\" Text=\"Nested live update\" FontSize=\"20\" />" +
			"<ListBox x:Name=\"PaneList\" SelectedIndex=\"0\"><TextBlock x:Name=\"PaneListItemTwo\" Text=\"Second item updated\" />" +
			"<TextBlock x:Name=\"PaneListItemInserted\" Text=\"Inserted item\" />" +
			"<TextBlock x:Name=\"PaneListItemOne\" Text=\"First item updated\" /></ListBox></StackPanel></Border></UserControl>";

		var response = await client.SendRequestAsync("vsCustomMessage", new
		{
			message = new
			{
				sourceId = "wpfHotReload",
				messageCode = 1001,
				parameter1 = DebugAdapterProcessHelper.GetWorkspaceWpfHotReloadRuntimePath(),
				parameter2 = Path.Combine(DebugAdapterProcessHelper.GetWorkspaceRootPath(), "sample", "SamplePane.xaml"),
				xamlText
			}
		});
		response["success"]?.ToObject<bool>().Should().BeTrue();
		response["body"]?["responseMessage"]?["parameter1"]?.ToObject<bool>().Should().BeTrue();
		response["body"]?["responseMessage"]?["parameter2"]?.ToObject<string>().Should().Be("ok: content control updated");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneHashCode()")).Should().Be(beforePaneHash);
		(await EvaluateExpressionAsync(client, frameId, "GetPaneWidth()")).Should().Be("321");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneTitleHashCode()")).Should().Be(beforeTitleHash);
		(await EvaluateExpressionAsync(client, frameId, "GetPaneBodyHashCode()")).Should().Be(beforeBodyHash);
		(await EvaluateExpressionAsync(client, frameId, "GetPaneTitleText()")).Should().Be("Nested live update");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneBodyText()")).Should().Be("Nested body update");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneListItemOneHashCode()")).Should().Be(beforeListItemOneHash);
		(await EvaluateExpressionAsync(client, frameId, "GetPaneListItemTwoHashCode()")).Should().Be(beforeListItemTwoHash);
		(await EvaluateExpressionAsync(client, frameId, "GetPaneListItemOneText()")).Should().Be("First item updated");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneListItemTwoText()")).Should().Be("Second item updated");
		(await EvaluateExpressionAsync(client, frameId, "GetPaneListSelectedIndex()")).Should().Be("0");

		var disconnectResponse = await client.SendRequestAsync("disconnect", new
		{
			restart = false,
			terminateDebuggee = true,
			suspendDebuggee = false
		});
		disconnectResponse["success"]?.ToObject<bool>().Should().BeTrue();
	}

	private static async Task<int> GetTopFrameIdAsync(RawDapClient client, int threadId)
	{
		var stackTraceResponse = await client.SendRequestAsync("stackTrace", new
		{
			threadId,
			startFrame = 0,
			levels = 1
		});

		stackTraceResponse["success"]?.ToObject<bool>().Should().BeTrue();
		return stackTraceResponse["body"]?["stackFrames"]?.First?["id"]?.ToObject<int>() ?? 0;
	}

	private static async Task<string> EvaluateExpressionAsync(RawDapClient client, int frameId, string expression)
	{
		var evaluateResponse = await client.SendRequestAsync("evaluate", new
		{
			expression,
			frameId,
			context = "repl"
		});

		evaluateResponse["success"]?.ToObject<bool>().Should().BeTrue();
		return evaluateResponse["body"]?["result"]?.ToObject<string>() ?? string.Empty;
	}
}
