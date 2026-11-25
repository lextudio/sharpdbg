using System.Collections.Immutable;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Breakpoints;

/// <summary>
/// Whether a breakpoint is bound to code in the debuggee. The breakpoint is reported to the client as verified only when it is bound
/// </summary>
/// <remarks>
/// Every breakpoint starts <see cref="Unbound"/>. It becomes <see cref="SourceBound"/> or <see cref="FunctionBound"/> when a
/// loaded module has code for it, and goes back to <see cref="Unbound"/> when the module(s) it is bound in unload.
/// A <see cref="Rejected"/> breakpoint never changes state.
/// </remarks>
public closed record BreakpointBinding
{
	/// <summary>Not bound to code yet. Binding is tried again whenever a module with symbols loads</summary>
	public sealed record Unbound(string Message) : BreakpointBinding;

	/// <summary>Can never be bound, e.g. its condition is not supported. Never tried again</summary>
	public sealed record Rejected(string Message) : BreakpointBinding;

	/// <summary>A source breakpoint bound to the location the PDB resolved its line to</summary>
	public sealed record SourceBound(ICorDebugFunctionBreakpoint CorBreakpoint, CORDB_ADDRESS ModuleBaseAddress, ModuleMetadataReader.ResolvedBreakpoint Resolved) : BreakpointBinding
	{
		/// <summary>Runtime breakpoints in other methods that cover the same source line, for a document whose code reaches the PDB through #line (XAML).
		/// <see cref="CorBreakpoint"/> stays the one the reported location belongs to.</summary>
		public ImmutableArray<AlsoBoundLocation> AlsoBound { get; init; } = ImmutableArray<AlsoBoundLocation>.Empty;
	}

	/// <summary>One more location of a <see cref="SourceBound"/> breakpoint: its runtime breakpoint and where it sits, so a breakpoint on the same location can share it</summary>
	public sealed record AlsoBoundLocation(ICorDebugFunctionBreakpoint CorBreakpoint, int MethodToken, int ILOffset);

	/// <summary>A function breakpoint bound to every matching method in the loaded modules. Always has at least one binding</summary>
	public sealed record FunctionBound(ImmutableArray<FunctionBreakpointBinding> Bindings) : BreakpointBinding
	{
		public ImmutableArray<FunctionBreakpointBinding> Bindings { get; } = Bindings.IsDefaultOrEmpty
			? throw new ArgumentException("A bound function breakpoint must have at least one binding", nameof(Bindings))
			: Bindings;
	}
}
