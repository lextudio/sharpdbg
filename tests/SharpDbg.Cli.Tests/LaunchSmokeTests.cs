using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class LaunchSmokeTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task LaunchRequest_StopAtEntry_HitsEntryThenBreakpoint_Oop()
	{
		var startSuspended = false;
		var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, extraProcess) =
			TestHelper.GetRunningDebugProtocolHostOop(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(extraProcess);

		var programPath = DebugAdapterProcessHelper.GetDebuggableProgramPath();
		var sourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
		var lines = File.ReadAllLines(sourcePath);
		var bpLine = Array.FindIndex(lines, l => l.Contains("Log2")) + 1;
		if (bpLine == 0) bpLine = 11;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(stopAtEntry: true)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost
			.WithBreakpointsRequest(bpLine, sourcePath)
			.WithConfigurationDoneRequest();

		var entryStoppedEvent = await stoppedEventTcs.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
		stoppedEventTcs.Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		entryStoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);

		debugProtocolHost.WithContinueRequest();

		var breakpointStoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs).WaitAsync(TimeSpan.FromSeconds(15));
		breakpointStoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
		var stopInfo = breakpointStoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(bpLine);
	}
}
