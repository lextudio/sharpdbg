# SharpDbg

An open source, cross platform .NET debugger implementing the VS Code Debug Adapter Protocol (DAP), completely written in C#!

SharpDbg uses [ICorDebugSharp](https://github.com/MattParkerDev/ICorDebugSharp), a managed wrapper around the ICorDebug APIs.

This repository is the **lextudio fork** of [SharpDbg](https://github.com/MattParkerDev/sharpdbg) (`MattParkerDev/sharpdbg`, upstream). It tracks upstream but carries a large functional delta: Edit and Continue / Hot Reload, client-side symbols and decompilation of external code, goto targets, restart, breakpoint locations, loaded sources, single-thread stepping, logpoints, source link, embedded sources, source file maps and more (see [Fork vs. upstream](COMPARISON.md#fork-vs-upstream)). The fork targets .NET 11; upstream targets .NET 10.

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

See [COMPARISON.md](COMPARISON.md) for the detailed feature matrix and the fork-vs-upstream differences.

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
