using System.Text.Json.Serialization;
using SharpDbg.DapProtocol;

namespace SharpDbg.Application.Protocol;

/// <summary>
/// SharpDbg-specific request: the client's answer to a <see cref="SymbolsNeededEvent"/>, or symbols it provides up front, for the
/// module with the given MVID. Handled outside the request lock, because the thread that asked for the symbols holds it while it
/// waits for this answer. An empty <see cref="PdbPath"/> says the client has no symbols for the module.
/// </summary>
/// <remarks>
/// Only meaningful with <c>decompileExternalCode: false</c> on launch/attach: the adapter then never decompiles by itself, and a
/// module without a PDB gets symbols only from here.
/// </remarks>
public sealed record LoadSymbolsArguments : IDapRequest<LoadSymbolsResponse>, IDapNamedMessage
{
	public const string CommandName = "loadSymbols";
	static string IDapNamedMessage.MessageName => CommandName;
	string IDapRequest.Command => CommandName;

	[JsonPropertyName("mvid")]
	public string Mvid { get; init; } = "";

	[JsonPropertyName("pdbPath")]
	public string? PdbPath { get; init; }
}

public sealed record LoadSymbolsResponse
{
	[JsonPropertyName("accepted")]
	public bool Accepted { get; init; }
}

/// <summary>
/// SharpDbg-specific event: a module without a PDB needs symbols and the adapter is waiting for the client's
/// <see cref="LoadSymbolsArguments"/> (or for the symbols timeout to pass).
/// </summary>
public sealed record SymbolsNeededEvent : IDapEvent, IDapNamedMessage
{
	public const string EventName = "symbolsNeeded";
	static string IDapNamedMessage.MessageName => EventName;
	string IDapEvent.Event => EventName;

	[JsonPropertyName("moduleName")]
	public string ModuleName { get; init; } = "";

	[JsonPropertyName("modulePath")]
	public string ModulePath { get; init; } = "";

	[JsonPropertyName("mvid")]
	public string Mvid { get; init; } = "";
}
