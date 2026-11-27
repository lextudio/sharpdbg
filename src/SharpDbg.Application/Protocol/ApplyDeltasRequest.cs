using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDbg.DapProtocol;

namespace SharpDbg.Application.Protocol;

/// <summary>
/// SharpDbg-specific request that applies an Edit-and-Continue delta (metadata, IL and optional PDB) to a loaded
/// module. The client produces the delta, e.g. with Roslyn's EmitDifference; the request needs
/// enableEditAndContinue on the launch or attach.
/// </summary>
public sealed record ApplyDeltasArguments : IDapRequest<ApplyDeltasResponse>, IDapNamedMessage
{
	public const string CommandName = "sharpdbg/applyDeltas";
	static string IDapNamedMessage.MessageName => CommandName;
	string IDapRequest.Command => CommandName;

	[JsonPropertyName("mvid")]
	public required string Mvid { get; init; }

	[JsonPropertyName("metadata")]
	public required string Metadata { get; init; }

	[JsonPropertyName("il")]
	public required string Il { get; init; }

	[JsonPropertyName("pdb")]
	public string? Pdb { get; init; }

	[JsonPropertyName("updatedMethodTokens")]
	public IReadOnlyList<int>? UpdatedMethodTokens { get; init; }

	/// <summary>Per document, the lines the edit left unchanged, so an active frame can be remapped.</summary>
	[JsonPropertyName("lineMaps")]
	public JsonElement? LineMaps { get; init; }
}

public sealed record ApplyDeltasResponse
{
	[JsonPropertyName("applied")]
	public bool Applied { get; init; }

	[JsonPropertyName("message")]
	public required string Message { get; init; }

	[JsonPropertyName("hresult")]
	public int HResult { get; init; }
}
