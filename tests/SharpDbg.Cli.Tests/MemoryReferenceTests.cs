using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class MemoryReferenceTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task FrameAndVariables_CarryMemoryReferences()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll"), stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(12, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest();
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();

		var frame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		frame.InstructionPointerReference.Should().NotBeNullOrEmpty();

		debugProtocolHost
			.WithScopesRequest(frame.Id, out var scopes)
			.WithVariablesRequest(scopes.Scopes.Single().VariablesReference, out var variables);
		var local = variables.Single(v => v.Name == "myInt");
		local.MemoryReference.Should().NotBeNullOrEmpty();
		local.MemoryReference.Should().StartWith("0x");
	}
}
