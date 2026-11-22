using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>The outcome of applying an Edit-and-Continue delta.</summary>
public sealed record DeltaApplyResult(bool Success, string Message, int HResult = 0);

public partial class ManagedDebugger
{
	/// <summary>Opt in to Edit and Continue: user modules are loaded with ENABLE_ENC so deltas can be applied. Set before launch.</summary>
	public bool EnableEditAndContinue { get; set; }

	/// <summary>
	/// Applies an Edit-and-Continue delta (as produced by Roslyn's EmitDifference) to a loaded module through
	/// ICorDebugModule2::ApplyChanges. The debuggee must be stopped.
	/// </summary>
	/// <remarks>
	/// Spike scope: no PDB merge, no breakpoint rebinding, no EditAndContinueRemap handling. See
	/// OpenDevelop doc/technotes/sharpdbg-enc.md.
	/// </remarks>
	public DeltaApplyResult ApplyDelta(Guid moduleMvid, byte[] metadataDelta, byte[] ilDelta)
	{
		if (CorProcess is null) return new DeltaApplyResult(false, "There is no process to apply a delta to");
		if (_run is not RunState.Stopped) return new DeltaApplyResult(false, "The debuggee must be stopped to apply a delta");

		var module = _modules.Values.FirstOrDefault(m => m.MetadataReader.Mvid == moduleMvid);
		if (module is null) return new DeltaApplyResult(false, $"No loaded module has MVID {moduleMvid}");

		// CordbModule::ApplyChanges dereferences m_pIMImport, which is only created on the first GetMetaDataInterface
		module.Module.GetMetaDataInterface<IMetaDataImport>();
		module.Module.TryGetJITCompilerFlags(out var flags);
		_logger?.Invoke($"Applying delta to {module.ModuleName}: metadata={metadataDelta.Length}B il={ilDelta.Length}B jitFlags={flags}");

		var hr = module.Module.TryApplyChanges((uint)metadataDelta.Length, metadataDelta, (uint)ilDelta.Length, ilDelta);
		_logger?.Invoke($"ApplyChanges = 0x{hr:X8}");
		return hr >= 0
			? new DeltaApplyResult(true, "Applied", hr)
			: new DeltaApplyResult(false, $"ApplyChanges failed with HRESULT 0x{hr:X8} (jitFlags={flags})", hr);
	}
}
