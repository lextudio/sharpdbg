using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.Breakpoints;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>The outcome of applying an Edit-and-Continue delta.</summary>
public sealed record DeltaApplyResult(bool Success, string Message, int HResult = 0);

public partial class ManagedDebugger
{
	/// <summary>Opt in to Edit and Continue: user modules are loaded with ENABLE_ENC so deltas can be applied. Set before launch.</summary>
	public bool EnableEditAndContinue { get; set; }

	/// <summary>
	/// Applies an Edit-and-Continue delta (as produced by Roslyn's EmitDifference) to a loaded module through
	/// ICorDebugModule2::ApplyChanges. A running debuggee is synchronized for the update and resumed afterwards
	/// without a stopped event, so Hot Reload works while the application runs under the debugger.
	/// </summary>
	public DeltaApplyResult ApplyDelta(Guid moduleMvid, byte[] metadataDelta, byte[] ilDelta, byte[]? pdbDelta = null, IReadOnlyList<int>? updatedMethodTokens = null)
	{
		if (CorProcess is null) return new DeltaApplyResult(false, "There is no process to apply a delta to");
		if (_run is RunState.Stopped) return ApplyDeltaWhileStopped(moduleMvid, metadataDelta, ilDelta, pdbDelta, updatedMethodTokens);

		// ICorDebugController::Stop/Continue nest: this pair synchronizes the process for ApplyChanges and lets it
		// run on, raising no debugger events, so the client's view (running) stays true throughout.
		if (CorProcess.TryIsRunning(out var isRunning) is not Cor.S_OK || !isRunning)
			return new DeltaApplyResult(false, "The debuggee is neither stopped nor running");
		var stop = CorProcess.TryStop(0);
		if (stop is not Cor.S_OK) return new DeltaApplyResult(false, $"Could not synchronize the debuggee for the update (0x{stop:X8})", stop);
		try
		{
			return ApplyDeltaWhileStopped(moduleMvid, metadataDelta, ilDelta, pdbDelta, updatedMethodTokens);
		}
		finally
		{
			var resume = CorProcess.TryContinue(false);
			if (resume is not Cor.S_OK) _logger?.Invoke($"Error resuming the debuggee after a delta: 0x{resume:X8}");
		}
	}

	private DeltaApplyResult ApplyDeltaWhileStopped(Guid moduleMvid, byte[] metadataDelta, byte[] ilDelta, byte[]? pdbDelta, IReadOnlyList<int>? updatedMethodTokens)
	{
		if (CorProcess is null) return new DeltaApplyResult(false, "There is no process to apply a delta to");

		var module = _modules.Values.FirstOrDefault(m => m.MetadataReader.Mvid == moduleMvid);
		if (module is null) return new DeltaApplyResult(false, $"No loaded module has MVID {moduleMvid}");

		// CordbModule::ApplyChanges dereferences m_pIMImport, which is only created on the first GetMetaDataInterface
		module.Module.GetMetaDataInterface<IMetaDataImport>();
		module.Module.TryGetJITCompilerFlags(out var flags);
		_logger?.Invoke($"Applying delta to {module.ModuleName}: metadata={metadataDelta.Length}B il={ilDelta.Length}B pdb={pdbDelta?.Length ?? 0}B updatedMethods={updatedMethodTokens?.Count ?? 0} jitFlags={flags}");

		var hr = module.Module.TryApplyChanges((uint)metadataDelta.Length, metadataDelta, (uint)ilDelta.Length, ilDelta);
		_logger?.Invoke($"ApplyChanges = 0x{hr:X8}");
		if (hr < 0) return new DeltaApplyResult(false, $"ApplyChanges failed with HRESULT 0x{hr:X8} (jitFlags={flags})", hr);

		if (pdbDelta is not { Length: > 0 } || updatedMethodTokens is not { Count: > 0 })
			return new DeltaApplyResult(true, "Applied", hr);
		if (!module.MetadataReader.TryApplyPdbDelta(pdbDelta, updatedMethodTokens, out var pdbError))
		{
			_logger?.Invoke($"Applied delta, but could not update symbols for {module.ModuleName}: {pdbError}");
			return new DeltaApplyResult(true, $"Applied, but symbol locations were not updated: {pdbError}", hr);
		}

		RebindSourceBreakpoints(module);
		return new DeltaApplyResult(true, "Applied and updated symbols", hr);
	}

	private void RebindSourceBreakpoints(ModuleInfo module)
	{
		foreach (var breakpoint in _breakpointManager.GetAllBreakpoints())
		{
			if (breakpoint.Binding is not BreakpointBinding.SourceBound { ModuleBaseAddress: var baseAddress, CorBreakpoint: var oldBreakpoint } || baseAddress != module.BaseAddress)
				continue;
			breakpoint.Binding = new BreakpointBinding.Unbound("Rebinding after Edit and Continue update.");
			DeactivateIfUnused(oldBreakpoint);
			TryBindBreakpoint(breakpoint);
			OnBreakpointChanged?.Invoke(breakpoint);
		}
	}
}
