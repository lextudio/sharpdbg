using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class JustMyCodeTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task JustMyCode_StackTraceRequest_ReturnsNonUserSourceFramesAsExternalCode()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode: true)
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
			new() { Id = 1000, Column = 3, EndColumn = 16,	  Line = 12, EndLine =   12, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Selector(int x) Line 12", ModuleId = 1001, Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 1001, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "[External Code]", Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle },
			new() { Id = 1002, Column = 3, EndColumn = 65,    Line =  7, EndLine =    7, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Test() Line 7",           ModuleId = 1001, Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 1003, Column = 4, EndColumn = 28,    Line = 31, EndLine =   31, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args) Line 31",      ModuleId = 1001, Source = new Source { Name = "Program.cs",          SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
			new() { Id = 1004, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "[External Code]", Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle }
		];

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse, null);
		stackTraceResponse.StackFrames.Should().BeEquivalentTo(expectedStackFrames, options => options.Excluding(s => s.Source.Checksums).Excluding(s => s.Source.VsSourceLinkInfo).Excluding(s => s.InstructionPointerReference));
		;
	}

	[Fact]
	public async Task JustMyCodeDisabled_StackTraceRequest_ReturnsNonUserSourceFrames()
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
			new() { Id = 1000, Column = 3, EndColumn = 16,	  Line = 12, EndLine =   12, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Selector(int x) Line 12",                                                     ModuleId = 1001, Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 1001, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.Fill(System.Span<int> results, int start, System.Func<int, int> func)", ModuleId = 1009, Source = null },
			new() { Id = 1002, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.ToArray()",                                                             ModuleId = 1009, Source = null },
			new() { Id = 1003, Column = 3, EndColumn = 65,    Line =  7, EndLine =    7, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Test() Line 7",                                                               ModuleId = 1001, Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 1004, Column = 4, EndColumn = 28,    Line = 31, EndLine =   31, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args) Line 31",                                                          ModuleId = 1001, Source = new Source { Name = "Program.cs",          SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
			new() { Id = 1005, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "[Native to Managed Transition]", Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle }
		];

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse, null);
		stackTraceResponse.StackFrames.Count.Should().Be(expectedStackFrames.Count);
		stackTraceResponse.StackFrames.Should().BeEquivalentTo(expectedStackFrames, options => options.Excluding(s => s.Source.Checksums).Excluding(s => s.Source.VsSourceLinkInfo).Excluding(s => s.InstructionPointerReference));
	}
}
