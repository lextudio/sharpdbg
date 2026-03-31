using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace SharpDbg.Cli.Tests.Helpers;

internal sealed class WpfHotReloadApplyXamlTextRequest : DebugRequestWithResponse<WpfHotReloadApplyXamlTextArguments, WpfHotReloadApplyXamlTextResponse>
{
	public WpfHotReloadApplyXamlTextRequest()
		: base("wpfHotReload/applyXamlText")
	{
	}
}

internal sealed class WpfHotReloadApplyXamlTextArguments
{
	public string HelperAssemblyPath { get; set; } = string.Empty;

	public string FilePath { get; set; } = string.Empty;

	public string XamlText { get; set; } = string.Empty;
}

internal sealed class WpfHotReloadApplyXamlTextResponse : ResponseBody
{
	public bool Success { get; set; }

	public string Message { get; set; } = string.Empty;
}
