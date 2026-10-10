using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace SharpDbg.Infrastructure.Debugger;

// 🤖
public partial class ModuleMetadataReader
{
	private sealed record MethodCandidate(
	int Token,
	SequencePoint FirstSp,      // smallest End where EndLine >= line (next-line snapping)
	SequencePoint LastSp,       // largest End where EndLine >= line
	SequencePoint? CoveringSp,  // latest Start where the requested source position is covered
	string DocPath)
	{
		public bool CoversLine => CoveringSp.HasValue;
	}

	public ResolvedBreakpoint? ResolveBreakpoint(string sourceFilePath, int line, int? column = null)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var normalizedPath = NormalizePath(sourceFilePath);

		var documentHandle = reader.Documents.FirstOrDefault(h => PathsMatch(normalizedPath, reader.GetString(reader.GetDocument(h).Name)));

		if (documentHandle.IsNil) return null;

		var candidates = reader.MethodDebugInformation
			.Select(h => CollectCandidate(h, documentHandle, line, column))
			.OfType<MethodCandidate>()
			.ToList();

		var covering = candidates.Where(c => c.CoversLine).ToList();

		if (covering.Count == 0)
			return candidates
				.OrderBy(c => c.FirstSp.Start())
				.Select(c => NewResolvedBreakpoint(c.Token, c.FirstSp, c.DocPath))
				.FirstOrDefault();

		if (covering.Count == 1) return NewResolvedBreakpoint(covering[0].Token, covering[0].CoveringSp!.Value, covering[0].DocPath);

		// Keep only the candidates whose covering SP starts latest — i.e. whose SP most
		// specifically matches the target line from above or at.  This naturally picks the
		// innermost lambda when the enclosing method only covers the line via a large
		// spanning SP (e.g. a delegate-assignment SP that spans the whole lambda body),
		// without needing explicit case analysis.
		var maxCoverStart = covering.Max(c => c.CoveringSp!.Value.Start());
		var primary = covering.Where(c => c.CoveringSp!.Value.Start() == maxCoverStart).ToList();

		if (primary.Count == 1) return NewResolvedBreakpoint(primary[0].Token, primary[0].CoveringSp!.Value, primary[0].DocPath);

		// https://github.com/Samsung/netcoredbg/blob/8b8b22200fecdb1aec5f47af63215462d8c79a4b/src/managed/SymbolReader.cs#L801-L817

		// Only reach here when multiple methods have a covering SP starting at the exact
		// same source position — the same-line lambda case (e.g. items.Select(i => i * 2)).
		// Apply netcoredbg's two-case containment check for that narrow scenario.
		var sorted = primary.OrderBy(c => c.FirstSp.Start()).ToList();
		var outer = sorted[^2];
		var nested = sorted[^1];

		// Case 1: lambda range fully inside outer's first SP → BP is on the call-site line
		if (nested.FirstSp.Start() > outer.FirstSp.Start() &&
			nested.LastSp.End() < outer.FirstSp.End())
			return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);

		// Case 2: outer's first SP ends after nested's -> BP is closer to the lambda body
		if (outer.FirstSp.End() > nested.FirstSp.End())
			return NewResolvedBreakpoint(nested.Token, nested.CoveringSp!.Value, nested.DocPath);

		return NewResolvedBreakpoint(outer.Token, outer.CoveringSp!.Value, outer.DocPath);
	}

	private MethodCandidate? CollectCandidate(MethodDebugInformationHandle handle, DocumentHandle docHandle, int line, int? column)
	{
		var reader = _pdbMetadataReader ?? throw new InvalidOperationException("No symbols are loaded.");
		var info = reader.GetMethodDebugInformation(handle);
		if (info.SequencePointsBlob.IsNil) return null;

		SequencePoint? firstSP = null;
		SequencePoint? lastSP = null;
		SequencePoint? coveringSP = null;
		string? docPath = null;

		foreach (var sp in info.GetSequencePoints())
		{
			if (sp.IsHidden) continue;
			var spDoc = sp.Document.IsNil ? info.Document : sp.Document;
			if (spDoc != docHandle || sp.IsBeforeRequestedPosition(line, column)) continue;

			docPath ??= reader.GetString(reader.GetDocument(spDoc).Name);

			if (firstSP is null || sp.End() < firstSP.Value.End()) firstSP = sp;
			if (lastSP is null || sp.End() > lastSP.Value.End()) lastSP = sp;

			if (sp.CoversRequestedPosition(line, column) && sp.ShouldReplaceCoveringSequencePoint(coveringSP, column))
				coveringSP = sp;
		}

		if (firstSP is null) return null;
		return new MethodCandidate(MetadataTokens.GetToken(handle.ToDefinitionHandle()), firstSP.Value, lastSP!.Value, coveringSP, docPath!);
	}
	private static ResolvedBreakpoint NewResolvedBreakpoint(int token, SequencePoint sp, string docPath) =>
		new(token, sp.Offset, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn, docPath);

	/// <summary>The EmbeddedSource custom debug information kind (ECMA-335 / Roslyn).</summary>
	static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

	/// <summary>The embedded source text for a document, when the PDB carries it (EmbedAllSources).</summary>
	public bool TryGetEmbeddedSource(string sourceFilePath, out string? sourceText)
	{
		sourceText = null;
		var reader = _pdbMetadataReader;
		if (reader is null) return false;
		var normalizedPath = NormalizePath(sourceFilePath);
		var documentHandle = reader.Documents.FirstOrDefault(h => PathsMatch(normalizedPath, reader.GetString(reader.GetDocument(h).Name)));
		if (documentHandle.IsNil) return false;

		foreach (var handle in reader.GetCustomDebugInformation(documentHandle))
		{
			var info = reader.GetCustomDebugInformation(handle);
			if (reader.GetGuid(info.Kind) != EmbeddedSourceKind) continue;
			var blob = reader.GetBlobBytes(info.Value);
			sourceText = DecodeEmbeddedSource(blob);
			return sourceText is not null;
		}
		return false;
	}

	static string? DecodeEmbeddedSource(byte[] blob)
	{
		if (blob.Length < 4) return null;
		var uncompressedSize = BitConverter.ToInt32(blob, 0);
		if (uncompressedSize == 0)
			return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
		try
		{
			using var input = new MemoryStream(blob, 4, blob.Length - 4);
			using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);
			return Encoding.UTF8.GetString(output.ToArray());
		}
		catch
		{
			return null;
		}
	}

	/// <summary>The SourceLink custom debug information kind (on the module definition).</summary>
	static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

	/// <summary>The SourceLink JSON document from the PDB, or null.</summary>
	public string? GetSourceLinkJson()
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
		{
			var info = reader.GetCustomDebugInformation(handle);
			if (reader.GetGuid(info.Kind) != SourceLinkKind) continue;
			return Encoding.UTF8.GetString(reader.GetBlobBytes(info.Value));
		}
		return null;
	}

	/// <summary>The SourceLink URL for a source file, applying the PDB's SourceLink mapping, or null.</summary>
	public string? GetSourceLinkUrl(string sourceFilePath)
	{
		var json = GetSourceLinkJson();
		if (json is null) return null;
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("documents", out var documents)) return null;
			foreach (var property in documents.EnumerateObject())
			{
				var pattern = property.Name;
				var wildcard = pattern.IndexOf('*');
				if (wildcard < 0)
				{
					if (string.Equals(pattern, sourceFilePath, StringComparison.OrdinalIgnoreCase)) return property.Value.GetString();
					continue;
				}
				var prefix = pattern[..wildcard];
				var suffix = pattern[(wildcard + 1)..];
				if (sourceFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && sourceFilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					var middle = sourceFilePath.Substring(prefix.Length, sourceFilePath.Length - prefix.Length - suffix.Length);
					return property.Value.GetString()?.Replace("*", middle);
				}
			}
		}
		catch
		{
			// a malformed SourceLink document simply yields no mapping
		}
		return null;
	}

	/// <summary>Distinct source document paths from the module's PDB, for the loadedSources request.</summary>
	public IReadOnlyList<string> GetDocumentPaths()
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return [];
		var paths = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var handle in reader.Documents)
		{
			var name = reader.GetString(reader.GetDocument(handle).Name);
			if (!string.IsNullOrEmpty(name) && seen.Add(name)) paths.Add(name);
		}
		return paths;
	}

	public IReadOnlyList<(int Line, int Column, int EndLine, int EndColumn)> GetBreakpointLocations(
		string sourceFilePath, int startLine, int? startColumn, int? endLine, int? endColumn)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return [];
		var normalizedPath = NormalizePath(sourceFilePath);
		var documentHandle = reader.Documents.FirstOrDefault(h => PathsMatch(normalizedPath, reader.GetString(reader.GetDocument(h).Name)));
		if (documentHandle.IsNil) return [];

		var results = new List<(int Line, int Column, int EndLine, int EndColumn)>();
		var seen = new HashSet<(int, int, int, int)>();
		foreach (var handle in reader.MethodDebugInformation)
		{
			var info = reader.GetMethodDebugInformation(handle);
			if (info.SequencePointsBlob.IsNil) continue;
			foreach (var sp in info.GetSequencePoints())
			{
				if (sp.IsHidden) continue;
				var spDoc = sp.Document.IsNil ? info.Document : sp.Document;
				if (spDoc != documentHandle) continue;
				if (sp.StartLine < startLine) continue;
				if (endLine is { } lastLine && sp.StartLine > lastLine) continue;
				if (startColumn is { } firstColumn && sp.StartLine == startLine && sp.StartColumn < firstColumn) continue;
				if (endColumn is { } lastColumn && sp.StartLine == (endLine ?? startLine) && sp.EndColumn > lastColumn) continue;
				if (!seen.Add((sp.StartLine, sp.StartColumn, sp.EndLine, sp.EndColumn))) continue;
				results.Add((sp.StartLine, sp.StartColumn, sp.EndLine, sp.EndColumn));
			}
		}
		return results.OrderBy(r => r.Line).ThenBy(r => r.Column).ToList();
	}
}

internal readonly record struct LineCol(int Line, int Column) : IComparable<LineCol>
{
	public int CompareTo(LineCol other)
	{
		var c = Line.CompareTo(other.Line);
		return c != 0 ? c : Column.CompareTo(other.Column);
	}
	public static bool operator <(LineCol a, LineCol b) => a.CompareTo(b) < 0;
	public static bool operator >(LineCol a, LineCol b) => a.CompareTo(b) > 0;
	public static bool operator <=(LineCol a, LineCol b) => a.CompareTo(b) <= 0;
	public static bool operator >=(LineCol a, LineCol b) => a.CompareTo(b) >= 0;
}

file static class LineColExtensions
{
	public static LineCol Start(this SequencePoint sp) => new(sp.StartLine, sp.StartColumn);
	public static LineCol End(this SequencePoint sp) => new(sp.EndLine, sp.EndColumn);
}
file static class SequencePointExtensions
{
	public static bool IsBeforeRequestedPosition(this SequencePoint sp, int requestedLine, int? requestedColumn)
	{
		return requestedColumn is null
			? sp.EndLine < requestedLine
			: sp.End() < new LineCol(requestedLine, requestedColumn.Value);
	}

	public static bool CoversRequestedPosition(this SequencePoint sp, int requestedLine, int? requestedColumn)
	{
		return requestedColumn is null
			? sp.StartLine <= requestedLine
			: new LineCol(requestedLine, requestedColumn.Value) is var requestedPosition && sp.Start() <= requestedPosition && requestedPosition <= sp.End();
	}

	public static bool ShouldReplaceCoveringSequencePoint(this SequencePoint sp, SequencePoint? coveringSp, int? requestedColumn)
	{
		if (coveringSp is null) return true;

		return requestedColumn is null
			? sp.StartLine > coveringSp.Value.StartLine ||
			  (sp.StartLine == coveringSp.Value.StartLine && sp.StartColumn < coveringSp.Value.StartColumn)
			: sp.Start() > coveringSp.Value.Start();
	}
}
