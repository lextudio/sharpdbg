using System.Text.Json.Nodes;
using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ExpressionEvaluationOptionsTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ShowRawValues_SuppressesDebuggerDisplay()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost.WithInitializeRequest().WaitForInitializedEvent();
		var attach = DebugAdapterProcessHelper.GetAttachRequest(process.Id);
		attach.AdditionalProperties!["expressionEvaluationOptions"] = new JsonObject { ["showRawValues"] = true };
		debugProtocolHost.SendRequestSync(attach);
		debugProtocolHost
			.WithBreakpointsRequest(11, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTrace);
		var response = debugProtocolHost.Evaluate(stackTrace.StackFrames!.First().Id, "_classWithDebugDisplay");

		response.Result.Should().NotContain("IntProperty = 14", "showRawValues suppresses the DebuggerDisplay rendering");
		response.Result.Should().Contain("ClassWithDebugDisplay");
	}
}
