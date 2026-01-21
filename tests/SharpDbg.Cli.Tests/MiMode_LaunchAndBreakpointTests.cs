// Ported from Samsung/netcoredbg test-suite
// Original file: netcoredbg/test-suite/MIExampleTest/Program.cs
// Purpose: MI sequence to test break-insert, exec-run (stop-at-entry), and break-delete
/*
Original netcoredbg/test-suite/MIExampleTest/Program.cs

using System;
using System.IO;

using NetcoreDbgTest;
using NetcoreDbgTest.MI;
using NetcoreDbgTest.Script;

namespace NetcoreDbgTest.Script
{
    class Context
    {
        public void Prepare(string caller_trace)
        {
            Assert.Equal(MIResultClass.Done,
                         MIDebugger.Request("-file-exec-and-symbols " + ControlInfo.CorerunPath).Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);

            Assert.Equal(MIResultClass.Done,
                         MIDebugger.Request("-exec-arguments " + ControlInfo.TargetAssemblyPath).Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);

            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-run").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        bool IsStoppedEvent(MIOutOfBandRecord record)
        {
            if (record.Type != MIOutOfBandRecordType.Async) {
                return false;
            }

            var asyncRecord = (MIAsyncRecord)record;

            if (asyncRecord.Class != MIAsyncRecordClass.Exec ||
                asyncRecord.Output.Class != MIAsyncOutputClass.Stopped) {
                return false;
            }

            return true;
        }

        public void WasEntryPointHit(string caller_trace)
        {
            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];

                if (reason.CString != "entry-point-hit") {
                    return false;
                }

                var frame = (MITuple)output["frame"];
                var func = (MIConst)frame["func"];
                if (func.CString == ControlInfo.TestName + ".Program.Main()") {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter),
                        @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasBreakpointHit(string caller_trace, string bpName)
        {
            var bp = (LineBreakpoint)ControlInfo.Breakpoints[bpName];

            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];

                if (reason.CString != "breakpoint-hit") {
                    return false;
                }

                var frame = (MITuple)output["frame"];
                var fileName = (MIConst)frame["file"];
                var line = ((MIConst)frame["line"]).Int;

                if (fileName.CString == bp.FileName &&
                    line == bp.NumLine) {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter),
                        @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void WasExit(string caller_trace)
        {

            Func<MIOutOfBandRecord, bool> filter = (record) => {
                if (!IsStoppedEvent(record)) {
                    return false;
                }

                var output = ((MIAsyncRecord)record).Output;
                var reason = (MIConst)output["reason"];

                if (reason.CString != "exited") {
                    return false;
                }

                var exitCode = (MIConst)output["exit-code"];

                if (exitCode.CString == "0") {
                    return true;
                }

                return false;
            };

            Assert.True(MIDebugger.IsEventReceived(filter),
                        @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void DebuggerExit(string caller_trace)
        {
            Assert.Equal(MIResultClass.Exit,
                         MIDebugger.Request("-gdb-exit").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void EnableBreakpoint(string caller_trace, string bpName)
        {
            Breakpoint bp = ControlInfo.Breakpoints[bpName];

            Assert.Equal(BreakpointType.Line, bp.Type, @"__FILE__:__LINE__"+"\n"+caller_trace);

            var lbp = (LineBreakpoint)bp;

            Assert.Equal(MIResultClass.Done,
                         MIDebugger.Request("-break-insert -f " + lbp.FileName + ":" + lbp.NumLine).Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public void Continue(string caller_trace)
        {
            Assert.Equal(MIResultClass.Running,
                         MIDebugger.Request("-exec-continue").Class,
                         @"__FILE__:__LINE__"+"\n"+caller_trace);
        }

        public Context(ControlInfo controlInfo, NetcoreDbgTestCore.DebuggerClient debuggerClient)
        {
            ControlInfo = controlInfo;
            MIDebugger = new MIDebugger(debuggerClient);
        }

        ControlInfo ControlInfo;
        MIDebugger MIDebugger;
    }
}

namespace MIExampleTest
{
    class Program
    {
        static void Main(string[] args)
        {
            // first checkpoint (initialization) must provide "init" as id
            Label.Checkpoint("init", "bp_test", (Object context) => {
                Context Context = (Context)context;
                Context.Prepare(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.EnableBreakpoint(@"__FILE__:__LINE__", "bp");
                Context.EnableBreakpoint(@"__FILE__:__LINE__", "bp2");
                Context.Continue(@"__FILE__:__LINE__");
            });

            Console.WriteLine("A breakpoint \"bp\" is set on this line"); Label.Breakpoint("bp");

            Label.Checkpoint("bp_test", "bp2_test", (Object context) => {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp");
                Context.Continue(@"__FILE__:__LINE__");
            });

            MIExampleTest2.Program.testfunc();

            // last checkpoint must provide "finish" as id or empty string ("") as next checkpoint id
            Label.Checkpoint("finish", "", (Object context) => {
                Context Context = (Context)context;
                Context.WasExit(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
        }
    }
}

*/
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SharpDbg.Cli.Tests.Helpers;
using Xunit;

namespace SharpDbg.Cli.Tests;

public class MiModeLaunchAndBreakpointTests
{
    readonly ITestOutputHelper _output;
    public MiModeLaunchAndBreakpointTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MiMode_BreakInsert_Run_Delete_Works()
    {
        // Ensure artifacts built before running tests - the referenced test app is built via ProjectReference
        var miProcess = Helpers.DebugAdapterProcessHelper.GetMiProcess();
        using var writer = miProcess.StandardInput;
        using var reader = miProcess.StandardOutput;

        // Wait for protocol ready line
        var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"MI ready: {ready}");

        // Insert breakpoint
        var bpPath = Path.JoinFromGitRoot("test", "mi-integration", "TestApp", "Program.cs");
        await writer.WriteLineAsync($"1-break-insert {bpPath}:12");
        var resp1 = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Console.WriteLine($"Resp1: {resp1}");
        Assert.Contains("^done", resp1);

        // Run the program using the native host executable (not dotnet CLI)
        var appPath = Path.JoinFromGitRoot("artifacts", "bin", "MiIntegrationTestApp", "debug", "MiIntegrationTestApp");
        await writer.WriteLineAsync($"2-exec-run --program=\"{appPath}\"");

        // Wait for stopped notification indicating entry-point or breakpoint
        string? stoppedLine = null;
        for (int i = 0; i < 20; i++)
        {
            var l = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Console.WriteLine($"MI line: {l}");
            if (l != null && l.StartsWith("*stopped"))
            {
                stoppedLine = l;
                break;
            }
        }
        Assert.NotNull(stoppedLine);

        // Delete breakpoint
        await writer.WriteLineAsync("3-break-delete 1");
        var resp3 = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Console.WriteLine($"Resp3: {resp3}");
        Assert.Contains("^done", resp3);

        // Exit MI
        await writer.WriteLineAsync("4-gdb-exit");
        var resp4 = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(System.TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _output.WriteLine($"Resp4: {resp4}");

        miProcess.Kill(true);
    }
}
