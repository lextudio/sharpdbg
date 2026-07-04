using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class StackTraceTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task StackTraceRequest_ReturnsFrames()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;


		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode: false)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [new("all"), new("user-unhandled")] });
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClassWithBclCall.cs");
		debugProtocolHost
			.WithBreakpointsRequest([12], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("ClassWithBclCall.cs");
		stopInfo.line.Should().Be(12);

		List<StackFrame> expectedStackFrames =
		[
			new() { Id = 1, Column = 3, EndColumn = 16,	Line = 12, EndLine = 12, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Selector()",      Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 2, Column = 0, EndColumn =  0, Line =  0, EndLine =  0, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.Fill()",    Source = null },
			new() { Id = 3, Column = 0, EndColumn =  0, Line =  0, EndLine =  0, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.ToArray()", Source = null },
			new() { Id = 4, Column = 3, EndColumn = 65, Line =  7, EndLine =  7, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Test()",          Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 5, Column = 4, EndColumn = 28, Line = 31, EndLine = 31, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main()",                   Source = new Source { Name = "Program.cs",          SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
			// TODO: Return internal frames (thread.ActiveInternalFrames)
			//new() { Id = 1005, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "[Native to Managed Transition]", Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle }
		];

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse, null);
		stackTraceResponse.StackFrames.Count.Should().Be(expectedStackFrames.Count);
		stackTraceResponse.StackFrames.Should().BeEquivalentTo(expectedStackFrames, options => options.WithStrictOrdering().Excluding(s => s.Source.Checksums).Excluding(s => s.Source.VsSourceLinkInfo).Excluding(s => s.InstructionPointerReference));
	}
}
