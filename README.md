# SharpDbg

An open source, cross platform .NET debugger implementing the VS Code Debug Adapter Protocol (DAP), completely written in C#!

SharpDbg uses [ICorDebugSharp](https://github.com/MattParkerDev/ICorDebugSharp), a managed wrapper around the ICorDebug APIs.

This repository is the **lextudio fork** of [SharpDbg](https://github.com/MattParkerDev/sharpdbg) (`MattParkerDev/sharpdbg`, upstream). It tracks upstream but carries a large functional delta: Edit and Continue / Hot Reload, client-side symbols and decompilation of external code, goto targets, restart, breakpoint locations, loaded sources, single-thread stepping, logpoints, source link, embedded sources, source file maps and more (see [Fork vs. upstream](#fork-vs-upstream)). The fork targets .NET 11; upstream targets .NET 10.

## Usage

SharpDbg is available on NuGet as a .NET Tool: https://www.nuget.org/packages/SharpDbg.Cli

```shell
dotnet tool install --global SharpDbg.Cli 
```
Once installed, run it using:

```shell
sharpdbg --interpreter=vscode
```

### 

The in memory library is also published to NuGet: https://www.nuget.org/packages/SharpDbg


## Comparison

Legend: ✅ supported · ❌ not supported · ⚠️ partial · ❓ unknown / untested.

Columns: **Fork** = this repository (`lextudio/sharpdbg`); **Upstream** = `MattParkerDev/sharpdbg`; **DNCDbg** = [viewizard/dncdbg](https://github.com/viewizard/dncdbg); **netcoredbg** = [Samsung/netcoredbg](https://github.com/Samsung/netcoredbg); **csdbg** = [snikeguo/csdbg](https://github.com/snikeguo/csdbg); **vsdbg** = Microsoft's VS Code .NET debugger (commercial).

### General

| Feature | Fork | Upstream | DNCDbg | netcoredbg | csdbg | vsdbg |
|---|---|---|---|---|---|---|
| License | MIT | MIT | MIT | MIT | MIT | Commercial |
| Implementation | C# | C# | C++ | C++ (+C# symbols) | C# | C# |
| Target runtime | .NET 11 | .NET 10 | .NET (all versions) | .NET Core / .NET 5+ | .NET 10 | .NET Core / .NET 5+ |
| Cross-platform | ✅ | ✅ | ✅ | ⚠️ (no musl) | ❓ (Windows-first) | ⚠️ (no musl arm32) |
| Protocols | DAP | DAP | DAP | DAP + MI/GDB + CLI | DAP + MI/GDB + CLI | DAP |
| Physical memory footprint | ❓ | ❓ | 8.6 MB | 51.8 MB | ❓ | 147.2 MB |
| Attach to running process | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Restart debug session | ✅ | ❌ | ✅ | ❌ | ❓ | ❌ |
| Mixed-mode (interop) debugging | ❌ | ❌ | ❌ | ⚠️ (Linux/Tizen) | ❌ | ❓ |

### Variables and evaluation

| Feature | Fork | Upstream | DNCDbg | netcoredbg | csdbg | vsdbg |
|---|---|---|---|---|---|---|
| Expression evaluation | ✅ | ✅ | ✅ | ✅ | ✅ (arithmetic/comparison/logic) | ✅ |
| `DebuggerBrowsable` | ✅ | ✅ | ✅ | ⚠️ (Never only) | ❓ | ✅ |
| `DebuggerDisplay` | ✅ | ✅ | ✅ | ❌ | ❓ | ✅ |
| `DebuggerTypeProxy` | ✅ | ✅ | ✅ | ❌ | ❓ | ✅ |
| Use `ToString()` for object display | ✅ (allowToString) | ✅ | ✅ | ❌ | ❓ | ✅ |
| Paging for child variables | ✅ | ✅ | ✅ | ❌ | ❓ | ✅ |
| Set variable (`setVariable`) | ✅ (primitives, strings, null in stack scopes) | ❌ | ❓ | ❓ | ✅ | ✅ |
| Evaluation format specifiers | ⚠️ (nq, h, d) | ❓ | ✅ | ❌ | ❓ | ✅ |
| Human-readable async stack traces | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ |

### Breakpoints

| Feature | Fork | Upstream | DNCDbg | netcoredbg | csdbg | vsdbg |
|---|---|---|---|---|---|---|
| Line breakpoints | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Function breakpoints | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Conditional breakpoints | ✅ | ✅ | ✅ | ✅ | ✅ (`if`) | ✅ |
| Hit-count breakpoints | ✅ | ✅ | ✅ | ❌ | ❓ | ✅ |
| Logpoints | ✅ | ❌ | ✅ | ❌ | ❓ | ✅ |
| Column breakpoints | ✅ | ❓ | ✅ | ❌ | ❓ | ❓ |
| Exception breakpoints | ✅ (all / user-unhandled) | ✅ | ✅ (conditions) | ✅ | ✅ | ✅ |
| Breakpoint locations | ✅ | ❌ | ✅ | ❌ | ❓ | ❓ |
| Single-thread execution / stepping | ✅ | ❌ | ✅ | ❌ | ❓ | ❓ |

### Sources and debug info

| Feature | Fork | Upstream | DNCDbg | netcoredbg | csdbg | vsdbg |
|---|---|---|---|---|---|---|
| Embedded PDB | ✅ | ❓ | ✅ | ❌ | ❓ | ✅ |
| Embedded sources | ✅ | ❌ | ✅ | ❌ | ❓ | ✅ |
| Source Link | ✅ | ❌ | ✅ | ❌ | ❓ | ✅ |
| Source file map | ✅ | ❌ | ✅ | ❌ | ❓ | ✅ |
| Loaded sources request | ✅ | ❌ | ✅ | ❌ | ❓ | ✅ |
| Goto targets / Set Next Statement | ✅ | ❌ | ✅ | ❌ | ❓ | ✅ |
| Client symbols / decompile external code | ✅ | ❌ | ❌ | ❌ | ❌ | ❓ |

### Hot Reload / Edit and Continue

| Feature | Fork | Upstream | DNCDbg | netcoredbg | csdbg | vsdbg |
|---|---|---|---|---|---|---|
| Edit and Continue (apply deltas) | ✅ (stopped + running) | ❌ | ❌ | ⚠️ (MI/GDB, Tizen) | ❌ | ❓ |
| Hot Reload while debugging | ✅ | ❌ | ❌ | ⚠️ | ❌ | ❓ |
| Remap active frames after edit | ✅ | ❌ | ❌ | ❌ | ❌ | ❓ |

### Fork vs. upstream

The lextudio fork adds the following on top of upstream `a902133`:

- **Edit and Continue / Hot Reload**: opt-in `enableEditAndContinue` launch/attach flag, the `sharpdbg/applyDeltas` request (metadata + IL + PDB deltas, updated method tokens, line maps), applying deltas while stopped *or* while running, and remapping active frames onto the new method version.
- **Client symbols and decompilation**: `supportsClientSymbols`, the `symbolsNeeded` event and `loadSymbols` request, `decompileExternalCode`, per-session `clientSymbolsTimeoutMs`, so modules without symbols can be decompiled and debugged.
- **Navigation**: `supportsGotoTargetsRequest` (`goto` / `gotoTargets`), `supportsRestartRequest`, `supportsBreakpointLocationsRequest`, `supportsLoadedSourcesRequest`, `supportsSingleThreadExecutionRequests`.
- **Breakpoints**: `supportsLogPoints`.
- **Sources**: embedded source serving (`source` with `sourceReference`), `sourceLink`, `sourceFileMap` on launch/attach.
- **Runtime/async**: runtime-async stepping via `ICorDebugProcess12` async stacks.
- **.NET 11** target (upstream is .NET 10).

## Architecture

SharpDbg is organized into three main components:

```
┌─────────────────────────────────────────────────────────┐
│                    VS Code / DAP Client                 │
└────────────────────────────┬────────────────────────────┘
                             │ DAP (stdin/stdout)
┌────────────────────────────▼────────────────────────────┐
│                    SharpDbg.Cli                         │
│  • Entry point and command-line argument handling       │
│  • DAP protocol client initialization                   │
└────────────────────────────┬────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────┐
│                SharpDbg.Application                     │
│  • DAP protocol implementation                          │
│  • VS Code protocol message handlers                    │
│  • Event communication (stopped, continued, etc.)       │
└────────────────────────────┬────────────────────────────┘
                             │
┌────────────────────────────▼────────────────────────────┐
│              SharpDbg.Infrastructure                    │
│  • Core debugger engine (ManagedDebugger)               │
│  • ICorDebug API wrapper (ICorDebugSharp)               │
│  • Breakpoint and variable management                   │
│  • Expression evaluator (compiler + interpreter)        │
└─────────────────────────────────────────────────────────┘
```

## Building From Source

1. Ensure .NET 11 SDK is installed
2. git clone
3. `dotnet build`
4. Done!

This will produce the debug adapter executable at `artifacts/bin/SharpDbg.Cli/debug/SharpDbg.Cli`, equivalent to `netcoredbg.exe`

## Screenshots

### DebuggerDisplay attribute

<img width="558" height="180" alt="image" src="https://github.com/user-attachments/assets/09dee930-1dbc-495f-88ff-3646273eb98d" />

```cs
	[DebuggerDisplay("Count = {Count}")]
	public class List<T> : IList<T>, IList, IReadOnlyList<T>
```

### DebuggerTypeProxy & DebuggerBrowsable(.RootHidden) attributes

<img width="365" height="173" alt="image" src="https://github.com/user-attachments/assets/037dc6f5-ed51-4b59-a4d3-ce651ceacf2a" />

```cs
	[DebuggerTypeProxy(typeof(ICollectionDebugView<>))]
	public class List<T> : IList<T>, IList, IReadOnlyList<T>
```

## References

- **[ICorDebugSharp](https://github.com/MattParkerDev/ICorDebugSharp)** - Managed wrappers around the ICorDebug API
- **[netcoredbg](https://github.com/Samsung/netcoredbg)** - .NET debugger, written in C++
- **[DNCDbg](https://github.com/viewizard/dncdbg)** - Managed-code .NET debugger with DAP support (NetCoreDbg fork)
- **[csdbg](https://github.com/snikeguo/csdbg)** - C#/.NET 10 reimplementation of netcoredbg
- **[debug-adapter-protocol](https://github.com/microsoft/debug-adapter-protocol)** - Microsoft's VS Code Debug Adapter Protocol
