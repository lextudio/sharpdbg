using System.Text;

namespace WpfHotReload.Runtime;

public static class WpfHotReloadAgent
{
	public static string ApplyXamlTextFromBase64(string filePath, string xamlTextBase64)
	{
		var xamlText = Encoding.UTF8.GetString(Convert.FromBase64String(xamlTextBase64));
		return $"applied:{Path.GetFileName(filePath)}:{xamlText.Length}";
	}
}
