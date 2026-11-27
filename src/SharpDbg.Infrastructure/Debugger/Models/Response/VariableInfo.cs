using SharpDbg.Infrastructure.Debugger.PresentationHintModels;

namespace SharpDbg.Infrastructure.Debugger.Models.Response;

public class VariableInfo
{
	public required string Name { get; set; }
	public required string Value { get; set; }
	public required string? Type { get; set; }
	public required int VariablesReference { get; set; }
	/// <summary>The value's memory address, for the DAP variable's memoryReference.</summary>
	public string? MemoryReference { get; set; }
	public VariablePresentationHint? PresentationHint { get; set; }
}
