using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace SharpDbg.Infrastructure;

public static class DiagnosticClientHelper
{
	// For applications like godot, or a native host that loads the CLR through hostfxr, diagnostics IPC is only available
	// once the host gets around to starting the runtime, which can take arbitrarily long (and is noticeably slower for an
	// x86 host under load). A fixed retry budget left such a runtime parked on "awaiting ResumeStartup" forever, so keep
	// retrying for as long as the debuggee is alive.
	public static async Task DiagnosticClientResumeRuntime(int debuggeeProcessId)
	{
		var diagnosticsClient = new DiagnosticsClient(debuggeeProcessId);
		const int maxDelayMs = 500;
		var delayMs = 50;

		while (true)
		{
			try
			{
				await Task.Delay(delayMs);
				diagnosticsClient.ResumeRuntime();
				return;
			}
			catch (ServerNotAvailableException) when (IsProcessAlive(debuggeeProcessId))
			{
				delayMs = Math.Min(delayMs * 2, maxDelayMs);
			}
		}
	}

	internal static bool IsProcessAlive(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return process.HasExited is false;
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}
}
