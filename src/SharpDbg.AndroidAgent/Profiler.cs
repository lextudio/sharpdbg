using System;
using System.Runtime.InteropServices;
using System.Threading;
using Silhouette;

internal unsafe class Profiler : CorProfilerCallback2Base
{
    internal static readonly ManualResetEventSlim StartupGate = new(false);
    [DllImport("log", EntryPoint="__android_log_write")]
    internal static extern int Log(int priority, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    protected override HResult QueryInterface(in Guid guid, out nint ptr)
    {
        if (guid == new Guid("00000000-0000-0000-C000-000000000046")) {
            ptr = ICorProfilerCallback;
            nint* table = *(nint**)ptr;
            ((delegate* unmanaged<nint, uint>)table[1])(ptr);
            return HResult.S_OK;
        }
        var status = base.QueryInterface(guid, out ptr);
        if (status.IsOK && ptr != 0) {
            nint* table = *(nint**)ptr;
            ((delegate* unmanaged<nint, uint>)table[1])(ptr);
        }
        return status;
    }

    protected override HResult Initialize(int version)
    {
        Log(4,"NaotProfiler","Silhouette Initialize entered");
        if (Exports.StartCore() != 0) return HResult.E_FAIL;
        bool ready = StartupGate.Wait(TimeSpan.FromSeconds(25));
        Log(4,"NaotProfiler",ready ? "attachment completed; starting app" : "attachment timed out; starting app");
        return HResult.S_OK;
    }
}
