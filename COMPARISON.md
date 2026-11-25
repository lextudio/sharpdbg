# Comparison

[Back to the README](README.md)

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
