using AwesomeAssertions;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using SharpDbg.Application.Protocol;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;
using SharpDbg.Infrastructure.Debugger.Decompilation;

namespace SharpDbg.Cli.Tests;

/// <summary>
/// <c>decompileExternalCode: false</c>: the adapter never decompiles by itself. A module without a PDB raises
/// <c>symbolsNeeded</c> and gets symbols only from the client's <c>loadSymbols</c> answer.
/// </summary>
public class ClientSymbolsTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ClientDeclinesSymbols_TheAdapterDoesNotDecompileByItself()
	{
		var (host, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = host;
		var stoppedEvent = await StopInsideBclCallAsync(host, process);
		host.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTrace, null);
		var linqFrame = stackTrace.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));
		linqFrame.IsResolved.Should().BeFalse();

		// The request waits for the client, so it runs while the test answers
		var resolve = Task.Run(() => host.SendRequestSync(new ResolveStackFrameArguments { StackFrameId = linqFrame.Id }));
		var needed = await host.WaitForEvent<SymbolsNeededEvent>(e => e.ModuleName.StartsWith("System.Linq"));
		needed.ModulePath.Should().EndWith("System.Linq.dll");
		Guid.TryParse(needed.Mvid, out var parsedMvid).Should().BeTrue();

		host.SendRequestSync(new LoadSymbolsArguments { Mvid = needed.Mvid, PdbPath = null }).Accepted.Should().BeTrue();

		var response = await resolve.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
		response.StackFrame.IsResolved.Should().BeFalse();
		response.StackFrame.Source.Should().BeNull();
		response.StackFrame.DecompiledSourceInfo.Should().BeNull();
	}

	[Fact]
	public async Task ClientSuppliesSymbols_TheFrameIsResolvedFromThem()
	{
		var (host, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = host;
		var stoppedEvent = await StopInsideBclCallAsync(host, process);
		host.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTrace, null);
		var linqFrame = stackTrace.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));

		var resolve = Task.Run(() => host.SendRequestSync(new ResolveStackFrameArguments { StackFrameId = linqFrame.Id }));
		var needed = await host.WaitForEvent<SymbolsNeededEvent>(e => e.ModuleName.StartsWith("System.Linq"));

		var pdbPath = Path.Combine(Path.GetTempPath(), $"client-symbols-{Guid.NewGuid():N}.pdb");
		try
		{
			WriteDecompiledPdb(needed.ModulePath, pdbPath);
			host.SendRequestSync(new LoadSymbolsArguments { Mvid = needed.Mvid, PdbPath = pdbPath }).Accepted.Should().BeTrue();

			var response = await resolve.WaitAsync(TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);
			response.StackFrame.IsResolved.Should().BeTrue();
			response.StackFrame.Source!.Path.Should().EndWith(".cs");
			response.StackFrame.Line.Should().BeGreaterThan(0);
		}
		finally
		{
			File.Delete(pdbPath);
		}
	}

	[Fact]
	public async Task SymbolsProvidedUpFront_AreUsedWithoutAskingTheClient()
	{
		var (host, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = host;
		var stoppedEvent = await StopInsideBclCallAsync(host, process);
		var linq = host.ReceivedEvents.OfType<ModuleEvent>().Select(e => e.Module).First(m => m.Name == "System.Linq.dll");
		var pdbPath = Path.Combine(Path.GetTempPath(), $"client-symbols-{Guid.NewGuid():N}.pdb");
		try
		{
			WriteDecompiledPdb(linq.Path!, pdbPath);
			host.SendRequestSync(new LoadSymbolsArguments { Mvid = ReadMvid(linq.Path!).ToString(), PdbPath = pdbPath }).Accepted.Should().BeTrue();

			host.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTrace, null);
			var linqFrame = stackTrace.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));
			var response = host.SendRequestSync(new ResolveStackFrameArguments { StackFrameId = linqFrame.Id });

			response.StackFrame.IsResolved.Should().BeTrue();
			response.StackFrame.Source!.Path.Should().EndWith(".cs");
			host.ReceivedEvents.OfType<SymbolsNeededEvent>().Should().BeEmpty("the client had already provided the symbols");
		}
		finally
		{
			File.Delete(pdbPath);
		}
	}

	[Fact]
	public async Task NoAnswer_IsGivenUpOnAfterTheTimeout()
	{
		var (host, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = host;
		var stoppedEvent = await StopInsideBclCallAsync(host, process, clientSymbolsTimeoutMs: 1500);
		host.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTrace, null);
		var linqFrame = stackTrace.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var response = await Task.Run(() => host.SendRequestSync(new ResolveStackFrameArguments { StackFrameId = linqFrame.Id }), TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

		watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(45), "the default timeout is 180 s");
		response.StackFrame.IsResolved.Should().BeFalse();
		host.ReceivedEvents.OfType<SymbolsNeededEvent>().Should().ContainSingle(e => e.ModuleName.StartsWith("System.Linq"));
	}

	[Fact]
	public async Task Disconnect_ReleasesAThreadWaitingForSymbols()
	{
		var (host, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		var stoppedEvent = await StopInsideBclCallAsync(host, process);
		host.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTrace, null);
		var linqFrame = stackTrace.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));

		var resolve = Task.Run(() => host.SendRequest(new ResolveStackFrameArguments { StackFrameId = linqFrame.Id }), TestContext.Current.CancellationToken);
		await host.WaitForEvent<SymbolsNeededEvent>(e => e.ModuleName.StartsWith("System.Linq"));

		// The waiting request holds the request lock; the default timeout is 180 s
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var disconnect = Task.Run(() => host.SendRequest(new DisconnectArguments { TerminateDebuggee = true }), TestContext.Current.CancellationToken);
		var both = Task.WhenAll(resolve, disconnect);
		await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken));
		both.IsCompleted.Should().BeTrue("disconnect must not wait for the symbols timeout (180 s)");
		watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(45));
		host.Connection.Stop();
	}

	[Fact]
	public async Task StepIntoExternalCode_WaitsForTheClientsSymbolsAndStopsInTheDecompiledSource()
	{
		var (host, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = host;
		// Line 7 calls Enumerable.Range: stepping in leaves user code for System.Linq, which has no PDB
		var stoppedEvent = await StopInsideBclCallAsync(host, process, line: 7);
		host.WithStepInRequest(stoppedEvent.ThreadId!.Value);
		var needed = await host.WaitForEvent<SymbolsNeededEvent>(e => e.ModuleName.StartsWith("System.Linq"));

		var pdbPath = Path.Combine(Path.GetTempPath(), $"client-symbols-{Guid.NewGuid():N}.pdb");
		try
		{
			WriteDecompiledPdb(needed.ModulePath, pdbPath);
			host.SendRequestSync(new LoadSymbolsArguments { Mvid = needed.Mvid, PdbPath = pdbPath }).Accepted.Should().BeTrue();

			var stepped = await host.WaitForStoppedEvent();
			var stopInfo = stepped.ReadStopInfo();
			stopInfo.filePath.Should().EndWith(".cs");
			stopInfo.line.Should().BeGreaterThan(0);
		}
		finally
		{
			File.Delete(pdbPath);
		}
	}

	private static Guid ReadMvid(string assemblyPath)
	{
		using var stream = File.OpenRead(assemblyPath);
		using var peReader = new System.Reflection.PortableExecutable.PEReader(stream);
		var reader = peReader.GetMetadataReader();
		return reader.GetGuid(reader.GetModuleDefinition().Mvid);
	}

	private static async Task<StoppedEvent> StopInsideBclCallAsync(DebugProtocolHost host, System.Diagnostics.Process process, int? clientSymbolsTimeoutMs = null, int line = 12)
	{
		await host
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode: false, decompileExternalCode: false, clientSymbolsTimeoutMs: clientSymbolsTimeoutMs)
			.WaitForInitializedEvent();
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClassWithBclCall.cs");
		host
			.WithBreakpointsRequest([line], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, true);
		return await host.WaitForStoppedEvent();
	}

	private static void WriteDecompiledPdb(string assemblyPath, string pdbPath)
	{
		using var file = new PEFile(assemblyPath, System.Reflection.PortableExecutable.PEStreamOptions.PrefetchEntireImage);
		var settings = new DecompilerSettings();
		var resolver = new UniversalAssemblyResolver(assemblyPath, throwOnError: false, file.DetectTargetFrameworkId());
		var typeSystem = new DecompilerTypeSystem(file, resolver, settings);
		using var stream = File.Create(pdbPath);
		new PortablePdbWriter2 { NoLogo = true }.WritePdb(file, typeSystem, settings, stream);
	}
}
