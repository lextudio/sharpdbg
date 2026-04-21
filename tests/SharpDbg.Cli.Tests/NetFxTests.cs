using System.Diagnostics;
using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class NetFxTests(ITestOutputHelper testOutputHelper)
{
    // artifacts/bin/DebuggableConsoleAppNetFx/debug/DebuggableConsoleAppNetFx.exe (single-target net48)
    private static string NetFxExePath =>
        Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleAppNetFx", "debug", "DebuggableConsoleAppNetFx.exe");

    private static string NetFxProgramCs =>
        Path.JoinFromGitRoot("tests", "DebuggableConsoleAppNetFx", "Program.cs");

    private static string NetFxClassCs =>
        Path.JoinFromGitRoot("tests", "DebuggableConsoleAppNetFx", "NetFxClass.cs");

    [Fact(Timeout = 30000)]
    public async Task SharpDbgCli_NetFx_Launch_HitsBreakpoint()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) =
            TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
        using var _ = adapter;

        await debugProtocolHost
            .WithInitializeRequest()
            .WithLaunchRequest(NetFxExePath, [], stopAtEntry: false, runtimeFlavor: "desktopclr")
            .WaitForInitializedEvent(initializedEventTcs);

        // Line 10: var greeting = cls.GetGreeting(i);
        debugProtocolHost
            .WithBreakpointsRequest(10, NetFxProgramCs)
            .WithConfigurationDoneRequest();

        var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
        var stopInfo = stoppedEvent.ReadStopInfo();

        stopInfo.filePath.Should().EndWith("Program.cs");
        stopInfo.line.Should().Be(10);

        debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
    }

    [Fact(Timeout = 30000)]
    public async Task SharpDbgCli_NetFx_Launch_StopAtEntry_StopsBeforeFirstUserCode()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) =
            TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
        using var _ = adapter;

        await debugProtocolHost
            .WithInitializeRequest()
            .WithLaunchRequest(NetFxExePath, [], stopAtEntry: true, runtimeFlavor: "desktopclr")
            .WaitForInitializedEvent(initializedEventTcs);

        debugProtocolHost.WithConfigurationDoneRequest();

        var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
        var stopInfo = stoppedEvent.ReadStopInfo();

        stopInfo.filePath.Should().NotBeNullOrEmpty();
        stopInfo.line.Should().BeGreaterThan(0);

        debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
    }

    [Fact(Timeout = 30000)]
    public async Task SharpDbgCli_NetFx_Launch_StepIn_EntersCalledMethod()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) =
            TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
        using var _ = adapter;

        await debugProtocolHost
            .WithInitializeRequest()
            .WithLaunchRequest(NetFxExePath, [], stopAtEntry: false, runtimeFlavor: "desktopclr")
            .WaitForInitializedEvent(initializedEventTcs);

        // Line 10: var greeting = cls.GetGreeting(i);
        debugProtocolHost
            .WithBreakpointsRequest(10, NetFxProgramCs)
            .WithConfigurationDoneRequest();

        var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
        var stopInfo = stoppedEvent.ReadStopInfo();
        stopInfo.filePath.Should().EndWith("Program.cs");
        stopInfo.line.Should().Be(10);

        // Desktop CLR may report either the method declaration or the first statement
        // as the initial stoppable sequence point for a StepIn.
        var stoppedEvent2 = await debugProtocolHost
            .WithStepInRequest(stoppedEvent.ThreadId!.Value)
            .WaitForStoppedEvent(stoppedEventTcs);
        var stopInfo2 = stoppedEvent2.ReadStopInfo();
        stopInfo2.filePath.Should().EndWith("NetFxClass.cs");
        stopInfo2.line.Should().BeOneOf(6, 7);

        debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
    }

    [Fact(Timeout = 30000)]
    public async Task SharpDbgCli_NetFx_Launch_ContinueHitsBreakpointAgain()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) =
            TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
        using var _ = adapter;

        await debugProtocolHost
            .WithInitializeRequest()
            .WithLaunchRequest(NetFxExePath, [], stopAtEntry: false, runtimeFlavor: "desktopclr")
            .WaitForInitializedEvent(initializedEventTcs);

        // Line 10: var greeting = cls.GetGreeting(i);
        debugProtocolHost
            .WithBreakpointsRequest(10, NetFxProgramCs)
            .WithConfigurationDoneRequest();

        var firstStop = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
        var firstInfo = firstStop.ReadStopInfo();
        firstInfo.filePath.Should().EndWith("Program.cs");
        firstInfo.line.Should().Be(10);

        // Continue — loop repeats, breakpoint should fire again at same location
        var secondStop = await debugProtocolHost
            .WithContinueRequest()
            .WaitForStoppedEvent(stoppedEventTcs);
        var secondInfo = secondStop.ReadStopInfo();
        secondInfo.filePath.Should().EndWith("Program.cs");
        secondInfo.line.Should().Be(10);

        debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
    }

    [Fact(Timeout = 30000)]
    public async Task SharpDbgCli_NetFx_Attach_HitsBreakpoint()
    {
        if (!OperatingSystem.IsWindows()) return;

        var netFxProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = NetFxExePath,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        netFxProcess.Start();
        using var _p = new ProcessKiller(netFxProcess);

        // Wait for the CLR to fully load before attaching
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) =
            TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
        using var _ = adapter;

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(netFxProcess.Id, runtimeFlavor: "desktopclr")
            .WaitForInitializedEvent(initializedEventTcs);

        // Line 10: var greeting = cls.GetGreeting(i);
        debugProtocolHost
            .WithBreakpointsRequest(10, NetFxProgramCs)
            .WithConfigurationDoneRequest();

        var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
        var stopInfo = stoppedEvent.ReadStopInfo();

        stopInfo.filePath.Should().EndWith("Program.cs");
        stopInfo.line.Should().Be(10);

        debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
    }
}
