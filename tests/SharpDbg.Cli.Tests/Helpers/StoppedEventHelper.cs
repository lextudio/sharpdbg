using System.Text.Json.Nodes;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests.Helpers;

public static class StoppedEventHelper
{
	public static (string filePath, int line, int column) ReadStopInfo(this StoppedEvent stoppedEvent)
	{
		var additionalProperties = stoppedEvent.AdditionalProperties;
		if (additionalProperties is null || additionalProperties.Count is 0) throw new InvalidOperationException("StoppedEvent has no AdditionalProperties");
		var filePath = additionalProperties["source"].GetProperty("path").GetString()!;
		var line = additionalProperties["line"].GetInt32();
		var column = additionalProperties["column"].GetInt32();
		return (filePath, line, column);
	}
}
