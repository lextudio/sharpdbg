using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class RestartTests(ITestOutputHelper testOutputHelper)
{
	private static readonly string ProgramPath = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");
	private static readonly string MyClassFile = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");

	[Fact]
	public async Task Restart_RelaunchesAndStopsAtEntryAgain()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(ProgramPath, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost.WithConfigurationDoneRequest();
		var first = await debugProtocolHost.WaitForStoppedEvent();
		first.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);

		var exception = Record.Exception(() => debugProtocolHost.SendRequestSync(new RestartArguments()));
		exception.Should().BeNull($"restart should relaunch the session; got: {exception?.Message}");

		var second = await debugProtocolHost.WaitForStoppedEvent();
		second.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);
	}

	[Fact]
	public async Task Restart_KeepsTheBreakpointsAndDoesNotEndTheSession()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(ProgramPath, stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(11, MyClassFile)
			.WithConfigurationDoneRequest();
		var first = await debugProtocolHost.WaitForStoppedEvent();
		first.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);

		var exception = Record.Exception(() => debugProtocolHost.SendRequestSync(new RestartArguments()));
		exception.Should().BeNull($"restart should relaunch the session; got: {exception?.Message}");

		// The client does not resend breakpoints for a restart; the relaunched program must still stop at it.
		var second = await debugProtocolHost.WaitForStoppedEvent();
		second.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);

		// Killing the old debuggee must not be reported as the end of the session: a client ends it on terminated.
		debugProtocolHost.ReceivedEvents.OfType<TerminatedEvent>().Should().BeEmpty("a restart keeps the session alive");
		debugProtocolHost.ReceivedEvents.OfType<ExitedEvent>().Should().BeEmpty("the old debuggee's exit belongs to the restart");
	}
}
