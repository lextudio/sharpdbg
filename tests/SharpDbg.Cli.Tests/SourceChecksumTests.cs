using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class SourceChecksumTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task StackFrameSource_CarriesASha256Checksum()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll"), stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost.WithConfigurationDoneRequest();
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();

		var frame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		frame.Source.Should().NotBeNull();
		var checksum = frame.Source!.Checksums.Should().ContainSingle().Subject;
		checksum.Algorithm.Should().Be(ChecksumAlgorithm.SHA256);
		checksum.ChecksumValue.Should().NotBeNullOrEmpty();
	}
}
