using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class NoDebugTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task NoDebug_StreamsOutputWithoutAttaching()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll"), stopAtEntry: true, justMyCode: true, noDebug: true)
			.WaitForInitializedEvent();
		debugProtocolHost.WithConfigurationDoneRequest();

		var output = await debugProtocolHost.WaitForEvent<OutputEvent>(e => e.Output.Contains("DebuggableConsoleApp is running"));
		output.Output.Should().Contain("DebuggableConsoleApp is running");
		debugProtocolHost.ReceivedEvents.OfType<StoppedEvent>().Should().BeEmpty("Run Without Debugging never attaches");
	}
}
