using System.Runtime.InteropServices;
using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using ICorDebugSharp;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure;

namespace SharpDbg.Cli.Tests;

/// The attach options an iOS simulator target needs: mscordbi/DAC from another directory, and a target started stopped
public class AttachOptionsTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task Attach_WithDebuggerLibraryDirectory_LoadsTheDebuggerThroughTheProvider()
	{
		// The library provider answers Unix library requests only
		Assert.SkipWhen(OperatingSystem.IsWindows(), "debuggerLibraryDirectory serves Unix libraries only");
		// The adapter runs in this test host, so every test shares one process: name the runtime's own directory,
		// since a second copy of mscordbi from another directory in this process deadlocks the tests that follow.
		// That the provider hands out its own directory is DirectoryLibraryProvider_* below.
		var libraries = RuntimeEnvironment.GetRuntimeDirectory();
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		debugProtocolHost.WithInitializeRequest();
		debugProtocolHost.SendRequestSync(AttachRequest(p2.Id, new() { ["debuggerLibraryDirectory"] = libraries }));
		await debugProtocolHost.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost
			.WithBreakpointsRequest(22, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended: true);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
	}

	[Fact]
	public void DirectoryLibraryProvider_ResolvesFilesInItsDirectory()
	{
		var directory = Directory.CreateTempSubdirectory("sharpdbg-provider-");
		try
		{
			File.WriteAllText(Path.Combine(directory.FullName, "libmscordbi.dylib"), "");
			var provider = new DirectoryLibraryProvider(directory.FullName);
			byte buildId = 0;

			// dbgshim asks by file name with the target runtime's own directory; the provider answers from its directory
			provider.TryProvideUnixLibrary("/target/runtime/libmscordbi.dylib", "/target/runtime/libcoreclr.dylib",
				LIBRARY_PROVIDER_INDEX_TYPE.Runtime, ref buildId, 0, out var resolved).Should().Be(0);
			try
			{
				Marshal.PtrToStringUni(resolved).Should().Be(Path.Combine(directory.FullName, "libmscordbi.dylib"));
			}
			finally
			{
				Marshal.FreeCoTaskMem(resolved);
			}

			provider.TryProvideUnixLibrary("libmscordaccore.dylib", "/target/runtime/libcoreclr.dylib",
				LIBRARY_PROVIDER_INDEX_TYPE.Runtime, ref buildId, 0, out var missing).Should().Be(unchecked((int)0x80004005)); // E_FAIL
			missing.Should().Be(IntPtr.Zero);
			provider.TryProvideWindowsLibrary("mscordbi.dll", "coreclr.dll", LIBRARY_PROVIDER_INDEX_TYPE.Runtime, 0, 0, out var windows)
				.Should().Be(unchecked((int)0x80004001)); // E_NOTIMPL
			windows.Should().Be(IntPtr.Zero);
		}
		finally
		{
			directory.Delete(true);
		}
	}

	[Fact]
	public void Attach_WithDebuggerLibraryDirectory_IsRejectedOnWindows()
	{
		Assert.SkipUnless(OperatingSystem.IsWindows(), "covered by the Unix attach test");
		var (debugProtocolHost, _, _, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: false);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		debugProtocolHost.WithInitializeRequest();
		var error = Assert.Throws<ProtocolException>(() =>
			debugProtocolHost.SendRequestSync(AttachRequest(p2.Id, new() { ["debuggerLibraryDirectory"] = Path.GetTempPath() })));
		error.Message.Should().Contain("not supported on Windows");
	}

	[Fact]
	public async Task Attach_ThatFailsAfterTheRequest_IsReportedAndTerminatesTheSession()
	{
		Assert.SkipWhen(OperatingSystem.IsWindows(), "debuggerLibraryDirectory serves Unix libraries only");
		// An existing directory without mscordbi: the request is accepted, the runtime-startup registration then fails
		var empty = Directory.CreateTempSubdirectory("sharpdbg-no-dbi-");
		try
		{
			var (debugProtocolHost, initializedEventTcs, _, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
			using var _ = adapter;
			using var __ = new ProcessKiller(p2);
			using var ___ = debugProtocolHost;
			var failureOutput = new TaskCompletionSource<OutputEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
			var terminated = new TaskCompletionSource<TerminatedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
			debugProtocolHost.RegisterEventType<OutputEvent>(e => { if ((e.Output ?? string.Empty).Contains("failed", StringComparison.Ordinal)) failureOutput.TrySetResult(e); });
			debugProtocolHost.RegisterEventType<TerminatedEvent>(e => terminated.TrySetResult(e));

			debugProtocolHost.WithInitializeRequest();
			debugProtocolHost.SendRequestSync(AttachRequest(p2.Id, new() { ["debuggerLibraryDirectory"] = empty.FullName }));
			await debugProtocolHost.WaitForInitializedEvent(initializedEventTcs);
			// The failure comes when the runtime starts and dbgshim asks the provider for mscordbi
			debugProtocolHost
				.WithConfigurationDoneRequest()
				.WithOptionalResumeRuntime(p2.Id, startSuspended: true);

			var output = await failureOutput.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
			output.Output.Should().Contain($"Attach to process {p2.Id} failed");
			output.Category.Should().Be(OutputEvent.CategoryValue.Stderr);
			await terminated.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
		}
		finally
		{
			empty.Delete(true);
		}
	}

	[Fact]
	public void Attach_WithMissingDebuggerLibraryDirectory_FailsTheRequest()
	{
		Assert.SkipWhen(OperatingSystem.IsWindows(), "the option is rejected on Windows before the directory is checked");
		var (debugProtocolHost, _, _, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: false);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		debugProtocolHost.WithInitializeRequest();
		var missing = Path.Combine(Path.GetTempPath(), "sharpdbg-no-such-dir-" + Guid.NewGuid().ToString("N"));
		var error = Assert.Throws<ProtocolException>(() =>
			debugProtocolHost.SendRequestSync(AttachRequest(p2.Id, new() { ["debuggerLibraryDirectory"] = missing })));
		error.Message.Should().Contain("debuggerLibraryDirectory does not exist");
	}

	[Fact]
	public async Task Attach_WithContinueStoppedProcess_ContinuesAStoppedTarget()
	{
		Assert.SkipUnless(OperatingSystem.IsMacOS(), "continueStoppedProcess is macOS only");
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;
		// Stop it once its runtime is parked at startup with its diagnostic socket open (resumed below), the way
		// simctl launch --wait-for-debugger leaves a process stopped by SIGSTOP
		await WaitForDiagnosticSocketAsync(p2.Id);
		kill(p2.Id, SIGSTOP).Should().Be(0);

		debugProtocolHost.WithInitializeRequest();
		debugProtocolHost.SendRequestSync(AttachRequest(p2.Id, new() { ["continueStoppedProcess"] = true }));
		await debugProtocolHost.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost
			.WithBreakpointsRequest(22, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended: true);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
	}

	[Fact]
	public void Attach_WithContinueStoppedProcess_IsRejectedOffMacOS()
	{
		Assert.SkipWhen(OperatingSystem.IsMacOS(), "covered by the macOS test");
		var (debugProtocolHost, _, _, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: false);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		debugProtocolHost.WithInitializeRequest();
		var error = Assert.Throws<ProtocolException>(() =>
			debugProtocolHost.SendRequestSync(AttachRequest(p2.Id, new() { ["continueStoppedProcess"] = true })));
		error.Message.Should().Contain("macOS only");
	}

	static async Task WaitForDiagnosticSocketAsync(int processId)
	{
		for (var attempt = 0; attempt < 100; attempt++)
		{
			if (Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{processId}-*-socket").Length > 0) return;
			await Task.Delay(100, TestContext.Current.CancellationToken);
		}
		throw new TimeoutException($"Process {processId} never opened its diagnostic socket");
	}

	static AttachRequest AttachRequest(int processId, Dictionary<string, JToken> options)
	{
		var request = DebugAdapterProcessHelper.GetAttachRequest(processId);
		foreach (var (key, value) in options)
			request.ConfigurationProperties[key] = value;
		return request;
	}

	const int SIGSTOP = 17; // macOS

	[DllImport("libc", SetLastError = true)]
	static extern int kill(int pid, int signal);
}
