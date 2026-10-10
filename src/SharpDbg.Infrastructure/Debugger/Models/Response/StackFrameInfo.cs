namespace SharpDbg.Infrastructure.Debugger.Models.Response;

public class StackFrameInfo
{
	public required int Id { get; set; }
	public required string Name { get; set; }
	public required int Line { get; set; }
	public required int? EndLine { get; set; }
	public required int Column { get; set; }
	public required int? EndColumn { get; set; }
	public required string? Source { get; set; }
	public required bool IsUserCode { get; set; }
	public required bool IsResolved { get; set; }
	public required DecompiledSourceInfo? DecompiledSourceInfo { get; set; }

	/// <summary>Identifies the current instruction (IL offset within the method), for the DAP
	/// instructionPointerReference.</summary>
	public string? InstructionPointerReference { get; set; }

	/// <summary>A non-zero sourceReference when the source is served from embedded PDB data rather
	/// than a file on disk.</summary>
	public int SourceReference { get; set; }
}
