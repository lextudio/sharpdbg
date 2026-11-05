using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json;

namespace SharpDbg.Application.Protocol;

/// <summary>A SharpDbg-specific request: the SourceLink URL for a source path, computed from the
/// loaded module's SourceLink document. Not part of the DAP specification.</summary>
public class SourceLinkRequest : DebugRequestWithResponse<SourceLinkArguments, SourceLinkResponse>
{
	public const string RequestType = "sourceLink";

	public SourceLinkRequest() : base(RequestType) { }

	public SourceLinkRequest(string path) : base(RequestType)
	{
		Args.Path = path;
	}
}

public class SourceLinkArguments : DebugRequestArguments
{
	[JsonProperty("path")]
	public string Path { get; set; } = null!;
}

public class SourceLinkResponse : ResponseBody
{
	[JsonProperty("url")]
	public string? Url { get; set; }
}
