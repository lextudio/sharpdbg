using System.Diagnostics;

namespace SharpDbg.Cli.Tests.Helpers;

public static class DebuggableProcessHelper
{
	public static Process StartDebuggableProcess(bool startSuspended = false, string appName = "DebuggableConsoleApp")
	{
		var filePath = Path.JoinFromGitRoot("artifacts", "bin", appName, "debug", OperatingSystem.IsWindows() ? appName + ".exe" : appName);
		if (File.Exists(filePath) is false) throw new FileNotFoundException("DebuggableConsoleApp executable not found", filePath);
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = filePath,
				RedirectStandardInput = false,
				RedirectStandardOutput = false,
				UseShellExecute = false,
				CreateNoWindow = false
			}
		};
		if (startSuspended) process.StartInfo.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";

		// Rider/Resharper Test runner adds these env vars to the launched test process when debugging the test, which would get inherited by the debuggee process that we launch.
		// This can cause behaviour changes in tests, e.g. the stack frames that are returned (caused by COMPLUS_ReadyToRun, seems like some LINQ is shipped R2R)
		// Remove them.
		List<string> envVarsToRemove = ["COMPLUS_FORCEENC", "COMPLUS_ReadyToRun", "COMPLUS_ZapDisable", "DOTNET_GCConserveMemory", "DOTNET_GCHeapCount", "DOTNET_GCNoAffinitize", "DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_TieredPGO", "DOTNET_gcServer", "_NO_DEBUG_HEAP"];
		foreach (var envVar in envVarsToRemove) process.StartInfo.Environment.Remove(envVar);

		process.Start();
		return process;
	}
}
