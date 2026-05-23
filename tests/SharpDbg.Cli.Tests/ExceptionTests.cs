using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ExceptionTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_Exception_VariablesHasExceptionScope()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [new("all"), new ("user-unhandled")]});
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Exceptions.cs");
		debugProtocolHost
			.WithBreakpointsRequest([17], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Exceptions.cs");
		stopInfo.line.Should().Be(12); // Where the exception is thrown

		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

		List<Variable> expectedVariables =
		[
			new() { Name = "$exception",  EvaluateName = "$exception",  Value = $$"""{System.InvalidOperationException: Test exception{{"\r\n"}}   at DebuggableConsoleApp.Exceptions.Test() in {{breakpointedFilePath}}:line 12}""", Type = "System.InvalidOperationException", VariablesReference = 1},
			new() { Name = "test", EvaluateName = "test", Value = "true",  Type = "bool" },
		];
		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(2);
		variables.Should().BeEquivalentTo(expectedVariables, options => options.Excluding(s => s.MemoryReference).Excluding(s => s.PresentationHint));
	}
}
