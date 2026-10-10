namespace SharpDbg.Infrastructure.Debugger.Models;

public class LaunchInfo
{
	public required LaunchRequestConsoleType LaunchRequestConsoleType { get; set; }
	public required string? Cwd { get; set; }
	public required string Program { get; set; }
	public required bool StopAtEntry { get; set; }
	public required List<string> Arguments { get; set; }
	public required Dictionary<string, string> Env { get; set; }

	/// <summary>Run Without Debugging: start the process and stream its output, but never attach the
	/// debugger (no breakpoints, stepping or evaluation).</summary>
	public bool NoDebug { get; set; }
}

public class RemoteAttachInfo
{
	public required string Address { get; set; }
	public required int Port { get; set; }
	public required string Platform { get; set; }
	public required bool IsServer { get; set; }
	public required string MscordbiPath { get; set; }
	public required string AssembliesPath { get; set; }
}
