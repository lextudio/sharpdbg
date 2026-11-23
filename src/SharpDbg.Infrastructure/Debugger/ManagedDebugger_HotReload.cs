using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.Breakpoints;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>The outcome of applying an Edit-and-Continue delta.</summary>
public sealed record DeltaApplyResult(bool Success, string Message, int HResult = 0);

/// <summary>
/// Lines an edit left untouched in one document: <see cref="LineCount"/> lines starting at <see cref="OldStartLine"/>
/// in the text the running code was compiled from are the same lines starting at <see cref="NewStartLine"/> in the
/// edited text (1-based). Only the producer of a delta has both texts, so it supplies these.
/// </summary>
public sealed record UnchangedLineRange(int OldStartLine, int NewStartLine, int LineCount);

/// <summary>The unchanged lines of one document, keyed by the path its PDB records.</summary>
public sealed record SourceLineMap(string DocumentPath, IReadOnlyList<UnchangedLineRange> UnchangedLines);

public partial class ManagedDebugger
{
	/// <summary>Opt in to Edit and Continue: user modules are loaded with ENABLE_ENC so deltas can be applied. Set before launch.</summary>
	public bool EnableEditAndContinue { get; set; }

	/// <summary>
	/// How a frame still running an older version of an updated method moves to the new one: the sequence points of
	/// the version it runs, those of the version the delta created, and the lines the edit kept. Keyed by module base
	/// address and method token; a later delta to the same method replaces it.
	/// </summary>
	private sealed record ActiveFrameRemapPlan(uint FromVersion, uint ToVersion,
		IReadOnlyList<ModuleMetadataReader.SequencePointInfo> OldPoints, IReadOnlyList<ModuleMetadataReader.SequencePointInfo> NewPoints,
		IReadOnlyList<SourceLineMap> LineMaps);

	private readonly Dictionary<(CORDB_ADDRESS Module, int Token), ActiveFrameRemapPlan> _remapPlans = new();

	/// <summary>
	/// Applies an Edit-and-Continue delta (as produced by Roslyn's EmitDifference) to a loaded module through
	/// ICorDebugModule2::ApplyChanges. A running debuggee is synchronized for the update and resumed afterwards
	/// without a stopped event, so Hot Reload works while the application runs under the debugger.
	/// With <paramref name="lineMaps"/>, a frame that is executing an updated method moves to the new version at the
	/// next statement the edit did not touch (<see cref="HandleFunctionRemapOpportunity"/>); without them it finishes
	/// the code it started with, and only later calls run the new version.
	/// </summary>
	public DeltaApplyResult ApplyDelta(Guid moduleMvid, byte[] metadataDelta, byte[] ilDelta, byte[]? pdbDelta = null,
		IReadOnlyList<int>? updatedMethodTokens = null, IReadOnlyList<SourceLineMap>? lineMaps = null)
	{
		if (CorProcess is null) return new DeltaApplyResult(false, "There is no process to apply a delta to");
		if (_run is RunState.Stopped) return ApplyDeltaWhileStopped(moduleMvid, metadataDelta, ilDelta, pdbDelta, updatedMethodTokens, lineMaps);

		// ICorDebugController::Stop/Continue nest: this pair synchronizes the process for ApplyChanges and lets it
		// run on, raising no debugger events, so the client's view (running) stays true throughout.
		if (CorProcess.TryIsRunning(out var isRunning) is not Cor.S_OK || !isRunning)
			return new DeltaApplyResult(false, "The debuggee is neither stopped nor running");
		var stop = CorProcess.TryStop(0);
		if (stop is not Cor.S_OK) return new DeltaApplyResult(false, $"Could not synchronize the debuggee for the update (0x{stop:X8})", stop);
		try
		{
			return ApplyDeltaWhileStopped(moduleMvid, metadataDelta, ilDelta, pdbDelta, updatedMethodTokens, lineMaps);
		}
		finally
		{
			var resume = CorProcess.TryContinue(false);
			if (resume is not Cor.S_OK) _logger?.Invoke($"Error resuming the debuggee after a delta: 0x{resume:X8}");
		}
	}

	private DeltaApplyResult ApplyDeltaWhileStopped(Guid moduleMvid, byte[] metadataDelta, byte[] ilDelta, byte[]? pdbDelta,
		IReadOnlyList<int>? updatedMethodTokens, IReadOnlyList<SourceLineMap>? lineMaps)
	{
		if (CorProcess is null) return new DeltaApplyResult(false, "There is no process to apply a delta to");

		var module = _modules.Values.FirstOrDefault(m => m.MetadataReader.Mvid == moduleMvid);
		if (module is null) return new DeltaApplyResult(false, $"No loaded module has MVID {moduleMvid}");

		// CordbModule::ApplyChanges dereferences m_pIMImport, which is only created on the first GetMetaDataInterface
		module.Module.GetMetaDataInterface<IMetaDataImport>();
		module.Module.TryGetJITCompilerFlags(out var flags);
		_logger?.Invoke($"Applying delta to {module.ModuleName}: metadata={metadataDelta.Length}B il={ilDelta.Length}B pdb={pdbDelta?.Length ?? 0}B updatedMethods={updatedMethodTokens?.Count ?? 0} lineMaps={lineMaps?.Count ?? 0} jitFlags={flags}");

		// The version each updated method runs now, and its sequence points, before the delta replaces both.
		var before = new Dictionary<int, (uint Version, IReadOnlyList<ModuleMetadataReader.SequencePointInfo> Points)>();
		if (lineMaps is { Count: > 0 } && updatedMethodTokens is { Count: > 0 })
			foreach (var token in updatedMethodTokens.Distinct())
				if (TryGetCurrentVersion(module, token, out var version))
					before[token] = (version, module.MetadataReader.GetSequencePoints(token));

		var hr = module.Module.TryApplyChanges((uint)metadataDelta.Length, metadataDelta, (uint)ilDelta.Length, ilDelta);
		_logger?.Invoke($"ApplyChanges = 0x{hr:X8}");
		if (hr < 0) return new DeltaApplyResult(false, $"ApplyChanges failed with HRESULT 0x{hr:X8} (jitFlags={flags})", hr);

		// Whatever plan an updated method had was for the version it no longer is.
		if (updatedMethodTokens is not null)
			foreach (var token in updatedMethodTokens) _remapPlans.Remove((module.BaseAddress, token));

		if (pdbDelta is not { Length: > 0 } || updatedMethodTokens is not { Count: > 0 })
			return new DeltaApplyResult(true, "Applied", hr);
		if (!module.MetadataReader.TryApplyPdbDelta(pdbDelta, updatedMethodTokens, out var pdbError))
		{
			_logger?.Invoke($"Applied delta, but could not update symbols for {module.ModuleName}: {pdbError}");
			return new DeltaApplyResult(true, $"Applied, but symbol locations were not updated: {pdbError}", hr);
		}

		foreach (var (token, old) in before)
		{
			if (!TryGetCurrentVersion(module, token, out var newVersion) || newVersion == old.Version) continue;
			_remapPlans[(module.BaseAddress, token)] = new ActiveFrameRemapPlan(old.Version, newVersion, old.Points,
				module.MetadataReader.GetSequencePoints(token), lineMaps!);
		}

		RebindSourceBreakpoints(module);
		return new DeltaApplyResult(true, "Applied and updated symbols", hr);
	}

	private static bool TryGetCurrentVersion(ModuleInfo module, int token, out uint version)
	{
		version = 0;
		try
		{
			return module.Module.GetFunctionFromToken((mdMethodDef)token).TryGetCurrentVersionNumber(out version) is Cor.S_OK;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// The runtime offers a frame that runs an older version of an updated method the move to the new version at each
	/// sequence point it reaches. Accept it only where the statement is provably the same in both versions (see
	/// <see cref="MapActiveStatementOffset"/>); otherwise the frame keeps running the old version, which is safe.
	/// </summary>
	private void HandleFunctionRemapOpportunity(FunctionRemapOpportunityCorDebugManagedCallbackEventArgs e)
	{
		try
		{
			var oldFunction = e.OldFunction;
			if (oldFunction.TryGetToken(out var token) is not Cor.S_OK
				|| oldFunction.TryGetVersionNumber(out var oldVersion) is not Cor.S_OK
				|| e.NewFunction.TryGetVersionNumber(out var newVersion) is not Cor.S_OK)
				return;
			var module = _modules.Values.FirstOrDefault(m => m.BaseAddress == oldFunction.Module.BaseAddress);
			if (module is null || !_remapPlans.TryGetValue((module.BaseAddress, (int)(uint)token), out var plan)) return;
			if (plan.FromVersion != oldVersion || plan.ToVersion != newVersion) return;
			var newOffset = MapActiveStatementOffset(plan.OldPoints, plan.NewPoints, plan.LineMaps, (int)e.OldILOffset);
			if (newOffset is null) return;
			if (e.Thread.ActiveFrame is not ICorDebugILFrame frame) return;
			var hr = frame.TryRemapFunction((uint)newOffset.Value);
			_logger?.Invoke($"RemapFunction 0x{(uint)token:X8} v{oldVersion}@{e.OldILOffset} -> v{newVersion}@{newOffset} = 0x{hr:X8}");
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Active-frame remap skipped: {ex.Message}");
		}
		finally
		{
			CorProcess?.Continue(false);
		}
	}

	/// <summary>
	/// The IL offset in the new version where a frame stopped at <paramref name="oldIlOffset"/> in the old version may
	/// continue, or null when that cannot be shown to be the same statement: the offset must start a visible sequence
	/// point, every line of that statement must be one the edit left unchanged, and the new version must have a visible
	/// sequence point with exactly the mapped span. Remapping elsewhere would skip or repeat code.
	/// </summary>
	public static int? MapActiveStatementOffset(IReadOnlyList<ModuleMetadataReader.SequencePointInfo> oldPoints,
		IReadOnlyList<ModuleMetadataReader.SequencePointInfo> newPoints, IReadOnlyList<SourceLineMap> lineMaps, int oldIlOffset)
	{
		ModuleMetadataReader.SequencePointInfo? found = null;
		foreach (var point in oldPoints)
			if (point.Offset == oldIlOffset && !point.IsHidden) { found = point; break; }
		if (found is not { } old) return null;
		var map = lineMaps.FirstOrDefault(m => string.Equals(m.DocumentPath, old.DocumentPath, StringComparison.OrdinalIgnoreCase));
		if (map is null) return null;
		var range = map.UnchangedLines.FirstOrDefault(r => old.StartLine >= r.OldStartLine && old.EndLine < r.OldStartLine + r.LineCount);
		if (range is null) return null;
		var shift = range.NewStartLine - range.OldStartLine;
		foreach (var candidate in newPoints)
		{
			if (candidate.IsHidden) continue;
			if (string.Equals(candidate.DocumentPath, old.DocumentPath, StringComparison.OrdinalIgnoreCase)
				&& candidate.StartLine == old.StartLine + shift && candidate.EndLine == old.EndLine + shift
				&& candidate.StartColumn == old.StartColumn && candidate.EndColumn == old.EndColumn)
				return candidate.Offset;
		}
		return null;
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
