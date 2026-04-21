# .NET Framework Support

SharpDbg supports debugging .NET Framework 4.x applications in addition to modern .NET (CoreCLR) applications. This document describes how it works, how to configure it, and its known limitations.

## Overview

Desktop CLR debugging uses a different initialization path from CoreCLR. Instead of `DbgShim`'s `CreateProcessForLaunch` + `RegisterForRuntimeStartup`, it uses:

1. **CLR MetaHost** (`CLRCreateInstance`) to locate the installed .NET Framework runtime
2. **`ICLRRuntimeInfo`** to get the runtime version from the target executable's PE metadata
3. **`ICorDebug`** via `CorDebug.CreateProcess` to launch and immediately attach to the process

This approach is Windows-only because the CLR MetaHost COM interfaces (`mscoree.dll`) are only available on Windows.

## Configuration

`runtimeFlavor` must be set explicitly. There is no auto-detection — you choose the target runtime.

| Value | When to use |
|---|---|
| `"desktopclr"` | Debugging a .NET Framework 4.x `.exe` target |
| `"coreclr"` | Debugging a modern .NET application (default when omitted) |

### Launch

```json
{
  "type": "coreclr",
  "request": "launch",
  "name": "Debug .NET Framework App",
  "program": "C:/path/to/MyApp.exe",
  "args": [],
  "stopAtEntry": false,
  "runtimeFlavor": "desktopclr"
}
```

### Attach

```json
{
  "type": "coreclr",
  "request": "attach",
  "name": "Attach to .NET Framework App",
  "processId": 12345,
  "runtimeFlavor": "desktopclr"
}
```

## How it works internally

```
DebugAdapter.HandleLaunchRequest
  └─ ManagedDebugger.Launch(... runtimeFlavor)
       └─ ManagedDebugger.PerformLaunch() [called from ConfigurationDone]
            ├─ [coreclr]    DbgShim.CreateProcessForLaunch → RegisterForRuntimeStartup → DebugActiveProcess
            └─ [desktopclr] ClrDebugExtensions.CreateDesktopCorDebug → CorDebug.CreateProcess

DebugAdapter.HandleAttachRequest
  └─ ManagedDebugger.Attach(... runtimeFlavor)
       └─ ManagedDebugger.PerformAttach() [called from ConfigurationDone]
            ├─ [coreclr]    DbgShim.RegisterForRuntimeStartup → DebugActiveProcess
            └─ [desktopclr] ClrDebugExtensions.AttachToDesktopClr → DebugActiveProcess
```

### `ClrDebugExtensions.CreateDesktopCorDebug` (launch)

```csharp
var metaHost = Extensions.CLRCreateInstance().CLRMetaHost;
var runtimeVersion = metaHost.GetVersionFromFile(executablePath);
var runtime = Extensions.GetRuntime(metaHost, runtimeVersion);
return Extensions.GetInterface(runtime).CorDebug;
```

Reads the required CLR version directly from the target `.exe`'s PE header — no hard-coding version strings.

### `ClrDebugExtensions.AttachToDesktopClr` (attach)

```csharp
var enumResult = dbgshim.EnumerateCLRs(pid);
var versionStr = dbgshim.CreateVersionStringFromModule(pid, enumResult.Items[0].Path);
return dbgshim.CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0, versionStr);
```

Enumerates the CLR already loaded in the running process — synchronous, no polling needed.

### Process creation (launch)

`CorDebug.CreateProcess` is used instead of `CreateProcessForLaunch`. The process launches with `DEBUG_NO_SPECIAL_OPTIONS` and the debugger attaches immediately. There is no `DOTNET_DefaultDiagnosticPortSuspend` mechanism involved.

## Shims and compatibility layer

To compile the SharpDbg libraries against `net48`, a `Shims.cs` file provides polyfills for APIs only available in modern .NET:

| Shim | Replaces |
|---|---|
| `IsExternalInit` | Enables C# 9 `init`-only setters |
| `CompilerFeatureRequiredAttribute` | Required by `record` types |
| `RequiredMemberAttribute` | Required by `required` member syntax |
| `NativeLibrary.Load` | Calls `LoadLibrary` (kernel32) on .NET Framework |
| `EnumerableCompat.WithIndex<T>()` | Replaces .NET 6+ `Index` on `IEnumerable<T>` |

These shims are compiled only when `TargetFramework` is `net48` via `#if !NET10_0_OR_GREATER`.

## Limitations

### Platform

- **Windows only.** Desktop CLR debugging relies on `mscoree.dll` (CLR MetaHost) and `ICorDebug` COM interfaces that are not available on Linux or macOS.

### Target framework

- Tested against **.NET Framework 4.8** (`net48`). Earlier versions (4.0, 4.5, 4.6, 4.7) may work but are not verified.
- .NET Framework 3.5 and below are not supported (they use an older `ICorDebug` interface version).

### Async debugging

- Async state machine stepping in .NET Framework apps uses the older compiler-generated state machine pattern. The async stepper may not correctly handle all async/await scenarios in `net48` targets.

### Expression evaluation

- The expression evaluator uses Roslyn to compile expressions. Roslyn targets the host runtime (net10.0), so expression evaluation works for language features common to both runtimes, but may fail for .NET Framework-specific APIs or runtime behavior differences.

### `NativeLibrary` shim

- The `NativeLibrary.Load` shim calls `LoadLibrary` via P/Invoke. This means `dbgshim.dll` must be locatable on the PATH or via the standard `DbgShimResolver` search paths when running SharpDbg itself on .NET Framework.

## Testing

Windows-only tests for Desktop CLR debugging live in [tests/SharpDbg.Cli.Tests/NetFxTests.cs](../tests/SharpDbg.Cli.Tests/NetFxTests.cs). Each test is guarded with `if (!OperatingSystem.IsWindows()) return;` and has a 30-second timeout to prevent hangs.

The test target is [tests/DebuggableConsoleAppNetFx/](../tests/DebuggableConsoleAppNetFx/), a `net48` console app built as a project dependency so it is always up to date before tests run.

### Test coverage

| Test | What it verifies |
|---|---|
| `SharpDbgCli_NetFx_Launch_HitsBreakpoint` | Launch with `runtimeFlavor: "desktopclr"` — breakpoint fires at the correct source location |
| `SharpDbgCli_NetFx_Launch_StopAtEntry_StopsBeforeFirstUserCode` | `stopAtEntry: true` stops before user code executes |
| `SharpDbgCli_NetFx_Launch_StepIn_EntersCalledMethod` | Step-in from a call site lands inside the called method in the correct source file |
| `SharpDbgCli_NetFx_Launch_ContinueHitsBreakpointAgain` | Continuing from a breakpoint in a loop hits the same breakpoint on the next iteration |
| `SharpDbgCli_NetFx_Attach_HitsBreakpoint` | Attach with `runtimeFlavor: "desktopclr"` to a running process — breakpoint fires |
