using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.InMemory;
using StackFrame = SharpDbg.DapProtocol.StackFrame;

namespace SharpDbg.Cli.Tests;

/// <summary>
/// Feature-conformance tests for DAP capabilities and expression-evaluation constructs that a
/// modern .NET debug adapter is expected to provide. A failing test means SharpDbg does not provide
/// that feature (yet); the pass/fail result is the answer to "does SharpDbg have this too?".
/// </summary>
public class DebuggerFeatureConformanceTests(ITestOutputHelper testOutputHelper)
{
	static readonly string VariablesFile = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "VariablesClass.cs");
	static readonly string NativeTypesFile = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "NativeTypesClass.cs");
	static readonly string MyClassFile = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
	static readonly string EmbeddedSourceFile = Path.JoinFromGitRoot("tests", "EmbeddedSourceApp", "Program.cs");

	// -------------------------------------------------------------------------------------------
	// DAP capabilities
	// -------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("SupportsGotoTargetsRequest")]
	[InlineData("SupportsSingleThreadExecutionRequests")]
	[InlineData("SupportsExceptionFilterOptions")]
	[InlineData("SupportsLoadedSourcesRequest")]
	[InlineData("SupportsBreakpointLocationsRequest")]
	[InlineData("SupportsRestartRequest")]
	public void Initialize_AdvertisesCapability(string capability)
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();
		using var _ = adapter;
		using var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		host.Run();
		var response = host.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());

		var property = typeof(Capabilities).GetProperty(capability);
		property.Should().NotBeNull($"Capabilities should expose the '{capability}' capability");
		((bool?)property!.GetValue(response)).Should().BeTrue($"a modern debug adapter advertises '{capability}'");
	}

	// -------------------------------------------------------------------------------------------
	// DAP request support
	// -------------------------------------------------------------------------------------------

	[Fact]
	public async Task GotoTargets_IsSupported()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		var response = session.Host.SendRequestSync(new GotoTargetsArguments { Source = new Source { Path = VariablesFile }, Line = 113 });
		response.Targets.Should().NotBeNullOrEmpty("gotoTargets should list the statement positions on the line");
	}

	[Fact]
	public async Task Goto_IsSupported()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		var targets = session.Host.SendRequestSync(new GotoTargetsArguments { Source = new Source { Path = VariablesFile }, Line = 113 });
		targets.Targets.Should().NotBeNullOrEmpty();

		var ex = Record.Exception(() => session.Host.SendRequestSync(new GotoArguments { ThreadId = session.ThreadId, TargetId = targets.Targets[0].Id }));
		ex.Should().BeNull($"goto should move the instruction pointer; got: {ex?.Message}");

		var stopped = await session.Host.WaitForStoppedEvent();
		stopped.Reason.Should().Be(StoppedEvent.ReasonValue.Goto);
	}

	[Fact]
	public void LoadedSources_IsSupported() => WithInitializedHost(host =>
		AssertSupported(host, "loadedSources", new LoadedSourcesArguments()));

	[Fact]
	public void BreakpointLocations_IsSupported() => WithInitializedHost(host =>
		AssertSupported(host, "breakpointLocations", new BreakpointLocationsArguments { Source = new Source { Path = MyClassFile }, Line = 11 }));

	[Fact]
	public async Task Restart_IsSupported()
	{
		var (host, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		host.WithConfigurationDoneRequest();
		var first = await host.WaitForStoppedEvent();
		first.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);

		var ex = Record.Exception(() => host.SendRequestSync(new RestartArguments()));
		ex.Should().BeNull($"restart should relaunch the session; got: {ex?.Message}");

		// The restarted session stops at entry again.
		var second = await host.WaitForStoppedEvent();
		second.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);
	}

	[Fact]
	public async Task Restart_KeepsTheBreakpoints()
	{
		var (host, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		host
			.WithBreakpointsRequest(11, MyClassFile)
			.WithConfigurationDoneRequest();
		var first = await host.WaitForStoppedEvent();
		first.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);

		var ex = Record.Exception(() => host.SendRequestSync(new RestartArguments()));
		ex.Should().BeNull($"restart should relaunch the session; got: {ex?.Message}");

		// The client does not send the breakpoints again for a restart; the relaunched program must still stop at it
		// instead of running to completion.
		var second = await host.WaitForStoppedEvent();
		second.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);

		// Killing the old debuggee must not be reported as the end of the session: a client ends it on `terminated`.
		host.ReceivedEvents.OfType<TerminatedEvent>().Should().BeEmpty("a restart keeps the session alive");
		host.ReceivedEvents.OfType<ExitedEvent>().Should().BeEmpty("the old debuggee's exit belongs to the restart, not to the session");
	}

	[Fact]
	public void Source_IsSupported() => WithInitializedHost(host =>
		AssertSupported(host, "source", new SourceArguments { Source = new Source { Path = MyClassFile }, SourceReference = 1 }));

	[Fact]
	public async Task Modules_IsSupported()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		var response = session.Host.SendRequestSync(new ModulesArguments());
		response.Modules.Should().NotBeNullOrEmpty("the loaded modules should be listed");
		response.Modules.Should().Contain(m => (m.Path ?? string.Empty).EndsWith("DebuggableConsoleApp.dll", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Logpoint_LogsAndContinues()
	{
		var startSuspended = true;
		var (host, adapter, debuggee) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var adapterScope = adapter;
		using var killer = new ProcessKiller(debuggee);
		using var hostScope = host;

		await host.WithInitializeRequest().WithAttachRequest(debuggee.Id).WaitForInitializedEvent();
		host
			.WithBreakpointsRequest(VariablesFile, [new SharpDbgBreakpointRequest(149, LogMessage: "logpoint {localInt}")])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(debuggee.Id, startSuspended);

		// A logpoint writes its interpolated message and continues, instead of stopping.
		var output = await host.WaitForEvent<OutputEvent>(e => (e.Output ?? string.Empty).Contains("logpoint", StringComparison.Ordinal));
		output.Output.Should().Contain("logpoint 3");
	}

	[Fact]
	public async Task LoadedSourceEvent_IsRaisedForUserSources()
	{
		var startSuspended = true;
		var (host, adapter, debuggee) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var adapterScope = adapter;
		using var killer = new ProcessKiller(debuggee);
		using var hostScope = host;

		await host.WithInitializeRequest().WithAttachRequest(debuggee.Id).WaitForInitializedEvent();
		host.WithConfigurationDoneRequest().WithOptionalResumeRuntime(debuggee.Id, startSuspended);

		var loaded = await host.WaitForEvent<LoadedSourceEvent>(e => (e.Source?.Path ?? string.Empty).EndsWith("VariablesClass.cs", StringComparison.Ordinal));
		loaded.Reason.Should().Be(LoadedSourceEvent.ReasonValue.New);
	}

	[Fact]
	public async Task Attach_WithOptions_StillHitsBreakpoint()
	{
		var startSuspended = true;
		var (host, adapter, debuggee) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var adapterScope = adapter;
		using var killer = new ProcessKiller(debuggee);
		using var hostScope = host;

		await host.WithInitializeRequest().WaitForInitializedEvent();
		host.SendRequestSync(AttachWithOptions(debuggee.Id, new()
		{
			["stopAtEntry"] = false,
			["suppressJITOptimizations"] = true,
			["enableStepFiltering"] = true,
			["sourceFileMap"] = new Dictionary<string, string> { ["/original/"] = "/mapped/" },
			["expressionEvaluationOptions"] = new Dictionary<string, bool> { ["allowImplicitFuncEval"] = false }
		}));
		host.WithBreakpointsRequest(11, MyClassFile).WithConfigurationDoneRequest().WithOptionalResumeRuntime(debuggee.Id, startSuspended);

		var stopped = await host.WaitForStoppedEvent();
		stopped.ThreadId.Should().NotBeNull();
	}

	[Fact]
	public async Task Evaluate_ShowRawValues_SuppressesDebuggerDisplay()
	{
		var startSuspended = true;
		var (host, adapter, debuggee) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var adapterScope = adapter;
		using var killer = new ProcessKiller(debuggee);
		using var hostScope = host;

		await host.WithInitializeRequest().WaitForInitializedEvent();
		host.SendRequestSync(AttachWithOptions(debuggee.Id, new()
		{
			["expressionEvaluationOptions"] = new Dictionary<string, bool> { ["showRawValues"] = true }
		}));
		host.WithBreakpointsRequest(11, MyClassFile).WithConfigurationDoneRequest().WithOptionalResumeRuntime(debuggee.Id, startSuspended);
		var stopped = await host.WaitForStoppedEvent();
		host.WithStackTraceRequest(stopped.ThreadId!.Value, out var trace);

		host.WithEvaluateRequest(trace.StackFrames!.First().Id, "_classWithDebugDisplay", out var response);
		response.Result.Should().NotContain("IntProperty = 14", "showRawValues suppresses the DebuggerDisplay rendering");
		response.Result.Should().Contain("ClassWithDebugDisplay");
	}

	[Fact]
	public async Task Variables_CarryMemoryReference()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		session.Host.WithScopesRequest(session.Frame.Id, out var scopes);
		session.Host.WithVariablesRequest(scopes.Scopes.Single().VariablesReference, out var variables);
		variables.Should().Contain(v => !string.IsNullOrEmpty(v.MemoryReference), "variables should carry a memory reference");
	}

	[Fact]
	public async Task SourceReference_ServesEmbeddedSource()
	{
		using var session = await StopInAsync(EmbeddedSourceFile, 16, "EmbeddedSourceApp");
		session.Frame.Source.Should().NotBeNull();
		session.Frame.Source!.SourceReference.Should().BeGreaterThan(0, "the debuggee PDB embeds its sources (EmbedAllSources)");

		var source = session.Host.SendRequestSync(new SourceArguments { SourceReference = session.Frame.Source.SourceReference!.Value });
		source.Content.Should().Contain("embeddedMarker");
	}

	[Fact]
	public async Task SourceLink_MapsSourceToRepositoryUrl()
	{
		using var session = await StopInAsync(EmbeddedSourceFile, 16, "EmbeddedSourceApp");
		var response = session.Host.SendRequestSync(new SourceLinkArguments { Path = EmbeddedSourceFile });
		response.Url.Should().NotBeNullOrEmpty("the debuggee PDB carries a SourceLink document");
		response.Url.Should().Contain("lextudio/sharpdbg", "SourceLink maps to the repository URL (case-insensitive on GitHub)");
		response.Url.Should().EndWith("Program.cs");
	}

	[Fact]
	public async Task Evaluate_PseudoVariables()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "$tid").Should().Be(session.ThreadId.ToString());
		int.Parse(Eval(session, "$pid")).Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task Evaluate_NativeIntLiteralFields()
	{
		using var session = await StopInAsync(NativeTypesFile, 12);
		Eval(session, "NativeIntField").Should().Be("5");
		Eval(session, "NativeUIntField").Should().Be("6");
	}

	[Fact]
	public async Task Evaluate_ThreadStaticField()
	{
		using var session = await StopInAsync(NativeTypesFile, 12);
		Eval(session, "DebuggableConsoleApp.NativeTypesClass.ThreadStaticField").Should().Be("7");
	}

	[Fact]
	public async Task StackFrame_PresentationHint_IsNullForUserCode()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		session.Frame.PresentationHint.Should().BeNull("a user-code frame should not be de-emphasized");
	}

	[Fact]
	public async Task Evaluate_UsingAwareTypeResolution()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "Math.Abs(-5)").Should().Be("5", "using-directive-aware type resolution should find System.Math");
	}

	[Fact]
	public async Task Evaluate_NullAssignment()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "StringField = null").Should().NotContain("error");
		Eval(session, "StringField").Should().Be("null");
	}

	[Fact]
	public async Task StackFrame_InstructionPointerReference_IsSet()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		session.Frame.InstructionPointerReference.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Variables_Paging_ReturnsRequestedWindow()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		session.Host.WithScopesRequest(session.Frame.Id, out var scopes);
		var reference = scopes.Scopes.Single().VariablesReference;
		var all = session.Host.SendRequestSync(new VariablesArguments { VariablesReference = reference }).Variables;
		all.Count.Should().BeGreaterThan(1);

		var paged = session.Host.SendRequestSync(new VariablesArguments { VariablesReference = reference, Start = 1, Count = 1 }).Variables;
		paged.Should().HaveCount(1);
		paged[0].Name.Should().Be(all[1].Name);
	}

	// -------------------------------------------------------------------------------------------
	// DAP source metadata
	// -------------------------------------------------------------------------------------------

	[Fact]
	public async Task StackFrameSource_CarriesChecksums()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		session.Frame.Source.Should().NotBeNull();
		session.Frame.Source!.Checksums.Should().NotBeNullOrEmpty(
			"a modern debug adapter returns source checksums so a breakpoint can be matched by content, not just by path");
	}

	// -------------------------------------------------------------------------------------------
	// Expression evaluation
	// -------------------------------------------------------------------------------------------

	[Fact]
	public async Task Evaluate_UnsignedRightShift()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localInt >>> 1").Should().Be("1", "the '>>>' unsigned right shift operator should be supported");
	}

	[Fact]
	public async Task Evaluate_NullableHasValue()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localNullableInt.HasValue").Should().Be("true");
		Eval(session, "localNullableIntNull.HasValue").Should().Be("false");
	}

	[Fact]
	public async Task Evaluate_NullableValue()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localNullableInt.Value").Should().Be("123");
	}

	[Fact]
	public async Task Evaluate_StringMemberAccess()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localString.Length").Should().Be("5", "member access on string values should be supported");
	}

	[Fact]
	public async Task Evaluate_ArrayMemberAccess()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localArray.Length").Should().Be("3", "member access on array values should be supported");
	}

	[Fact]
	public async Task Evaluate_GuidFormatting()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localGuid").Should().Be("27de5b68-af24-4e59-a785-dde52e2ea7af", "System.Guid should be formatted as a human-readable string");
	}

	[Fact]
	public async Task Evaluate_ObjectCreation()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "new TestClass(\"created\").Name").Should().Be("\"created\"", "plain object creation expressions (new T(...)) should be supported");
	}

	[Fact]
	public async Task Evaluate_NintCast()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "(nint)5").Should().Be("5", "nint/nuint predefined type values should be supported");
	}

	[Fact]
	public async Task Evaluate_FormatSpecifier()
	{
		using var session = await StopInAsync(VariablesFile, 149);
		Eval(session, "localString,nq").Should().Be("hello", "the ac/h/nq/raw/hidden format specifiers should be supported");
	}

	[Fact]
	public async Task Evaluate_DebuggerDisplay()
	{
		using var session = await StopInAsync(MyClassFile, 11);
		Eval(session, "_classWithDebugDisplay").Should().Be("IntProperty = 14");
	}

	// -------------------------------------------------------------------------------------------
	// Run Without Debugging (noDebug)
	// -------------------------------------------------------------------------------------------

	[Fact]
	public async Task NoDebug_Launch_RunsToCompletionWithoutStopping()
	{
		var (host, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var adapterScope = adapter;
		using var hostScope = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "TopLevelStatementsConsoleApp", "debug", "TopLevelStatementsConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true, noDebug: true)
			.WaitForInitializedEvent();
		host.WithConfigurationDoneRequest();

		// Run Without Debugging: the process runs to completion and reports its exit code, with no stop.
		var exited = await host.WaitForEvent<ExitedEvent>(timeout: TimeSpan.FromSeconds(60));
		exited.ExitCode.Should().Be(0);
	}

	// -------------------------------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------------------------------

	void WithInitializedHost(Action<DebugProtocolHost> action)
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();
		using var _ = adapter;
		using var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		host.Run();
		host.WithInitializeRequest();
		action(host);
	}

	// Overloads that keep each request's concrete type so SendRequestSync can infer its response.
	static void AssertSupported(DebugProtocolHost host, string requestType, LoadedSourcesArguments request) =>
		AssertNoUnsupportedReply(host, requestType, () => host.SendRequestSync(request));
	static void AssertSupported(DebugProtocolHost host, string requestType, BreakpointLocationsArguments request) =>
		AssertNoUnsupportedReply(host, requestType, () => host.SendRequestSync(request));
	static void AssertSupported(DebugProtocolHost host, string requestType, SourceArguments request) =>
		AssertNoUnsupportedReply(host, requestType, () => host.SendRequestSync(request));

	static void AssertNoUnsupportedReply(DebugProtocolHost host, string requestType, Action send)
	{
		var ex = Record.Exception(send);
		ex.Should().BeNull($"a modern debug adapter supports the '{requestType}' request; SharpDbg replied: {ex?.Message}");
	}

	static string Eval(Session session, string expression)
	{
		EvaluateResponse? response = null;
		var ex = Record.Exception(() => session.Host.WithEvaluateRequest(session.Frame.Id, expression, out response));
		if (ex is not null)
			throw new InvalidOperationException($"evaluating '{expression}' failed: {ex.Message}", ex);
		return response!.Result;
	}

	static AttachArguments AttachWithOptions(int processId, Dictionary<string, object?> options)
	{
		var request = DebugAdapterProcessHelper.GetAttachRequest(processId);
		foreach (var (key, value) in options)
			request.AdditionalProperties![key] = JsonSerializer.SerializeToElement(value);
		return request;
	}

	async Task<Session> StopInAsync(string file, int line, string appName = "DebuggableConsoleApp")
	{
		var startSuspended = true;
		var (host, adapter, debuggee) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended, appName);
		try
		{
			await host
				.WithInitializeRequest()
				.WithAttachRequest(debuggee.Id)
				.WaitForInitializedEvent();
			host
				.WithBreakpointsRequest(line, file)
				.WithConfigurationDoneRequest()
				.WithOptionalResumeRuntime(debuggee.Id, startSuspended);
			var stopped = await host.WaitForStoppedEvent();
			host.WithStackTraceRequest(stopped.ThreadId!.Value, out var stackTraceResponse);
			return new Session(host, adapter, debuggee, stopped.ThreadId!.Value, stackTraceResponse.StackFrames!.First());
		}
		catch
		{
			// No Session owns these yet: without this a failed setup leaves a suspended debuggee behind, whose
			// foreground thread then keeps the whole test host from exiting. Each step on its own, so that a dead
			// adapter (a disconnect that throws) neither skips the rest nor replaces the failure being rethrown.
			try { host.Dispose(); } catch { /* the setup failure is the one to report */ }
			try { new ProcessKiller(debuggee).Dispose(); } catch { /* ProcessKiller already ignores a failed kill */ }
			try { adapter.Dispose(); } catch { /* the setup failure is the one to report */ }
			throw;
		}
	}

	sealed class Session(DebugProtocolHost host, IDisposable adapter, Process debuggee, int threadId, StackFrame frame) : IDisposable
	{
		readonly ProcessKiller killer = new(debuggee);
		public DebugProtocolHost Host { get; } = host;
		public int ThreadId { get; } = threadId;
		public StackFrame Frame { get; } = frame;
		public void Dispose()
		{
			// Disconnect while the debuggee is still alive, then kill it (the existing tests dispose
			// the host before the ProcessKiller for the same reason: DisconnectRequest times out
			// against a process that is already gone).
			Host.Dispose();
			killer.Dispose();
			adapter.Dispose();
		}
	}
}

/// <summary>A SharpDbg-specific request: the SourceLink URL for a source path.</summary>
public sealed record SourceLinkArguments : IDapRequest<SourceLinkResponse>, IDapNamedMessage
{
	public const string CommandName = "sourceLink";
	static string IDapNamedMessage.MessageName => CommandName;
	string IDapRequest.Command => CommandName;

	[JsonPropertyName("path")]
	public string Path { get; init; } = "";
}

public sealed record SourceLinkResponse
{
	[JsonPropertyName("url")]
	public string? Url { get; init; }
}
