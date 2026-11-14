using SharpDbg.Infrastructure.Debugger.PresentationHintModels;

namespace SharpDbg.Infrastructure.Debugger.Models.Response;

public class VariableInfo
{
	public required string Name { get; set; }
	public required string Value { get; set; }
	public required string? Type { get; set; }
	public required int VariablesReference { get; set; }
	public VariablePresentationHint? PresentationHint { get; set; }

	/// <summary>The memory address of the value, for the DAP memoryReference.</summary>
	public string? MemoryReference { get; set; }
}
