using WpfHotReload.Runtime;

namespace DebuggableConsoleApp;

public static class HotReloadBridge
{
	public static string ApplyXamlTextFromBase64(string filePath, string xamlTextBase64)
	{
		return WpfHotReloadAgent.ApplyXamlTextFromBase64(filePath, xamlTextBase64);
	}
}
