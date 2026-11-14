using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static unsafe class ProfilerFactory
{
    private static readonly Guid ProfilerId = new("9DC623E8-C88F-4FD5-AD99-77E67E1D9631");
    private static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Profiler Profiler = new();
    private static readonly nint Factory = MakeFactory();
    private static int references = 1;
    private const int NoInterface = unchecked((int)0x80004002), Pointer = unchecked((int)0x80004003);

    private static nint MakeFactory()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&Query;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged<nint, nint, Guid*, nint*, int>)&Create;
        table[4] = (nint)(delegate* unmanaged<nint, int, int>)&Lock;
        nint* obj = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        *obj = (nint)table;
        return (nint)obj;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int GetClassObject(Guid* clsid, Guid* iid, nint* result)
    {
        if (clsid == null || iid == null || result == null) return Pointer;
        *result = 0;
        if (*clsid != ProfilerId) return unchecked((int)0x80040111);
        return QueryCore(iid, result);
    }

    private static int QueryCore(Guid* iid, nint* result)
    {
        if (iid == null || result == null) return Pointer;
        *result = 0;
        if (*iid != IUnknown && *iid != IClassFactory) return NoInterface;
        *result = Factory;
        Interlocked.Increment(ref references);
        return 0;
    }
    [UnmanagedCallersOnly] private static int Query(nint self, Guid* iid, nint* result) => QueryCore(iid, result);
    [UnmanagedCallersOnly] private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref references);
    [UnmanagedCallersOnly] private static uint Release(nint self) => (uint)Interlocked.Decrement(ref references);
    [UnmanagedCallersOnly] private static int Lock(nint self, int locked) => 0;
    [UnmanagedCallersOnly] private static int Create(nint self, nint outer, Guid* iid, nint* result)
    {
        if (iid == null || result == null) return Pointer;
        *result = 0;
        if (outer != 0) return unchecked((int)0x80040110);
        nint callback = Profiler.ICorProfilerCallback;
        nint* table = *(nint**)callback;
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)table[0])(callback, iid, result);
    }
}
