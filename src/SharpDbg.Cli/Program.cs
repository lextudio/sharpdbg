using System.Diagnostics;
using System.Threading;
using SharpDbg.Application;

namespace SharpDbg.Cli;

internal static class Program
{
	private static StreamWriter? _logWriter;

	public static int Main(string[] args)
	{
		var (interpreter, serverPort, logPath, requestedHelp) = Arguments.Parse(args);

		if (interpreter is null || requestedHelp)
		{
			Console.WriteLine(HelpText.Text);
			return 0;
		}

		//logPath = @"C:\Users\Matthew\Downloads\sharpdbglogs\log.txt";
		// Setup logging if specified
		if (!string.IsNullOrEmpty(logPath))
		{
			try
			{
				var logDir = Path.GetDirectoryName(logPath);
				if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
				{
					Directory.CreateDirectory(logDir);
				}
				_logWriter = new StreamWriter(logPath, append: true);
				_logWriter.AutoFlush = true;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Failed to open log file: {ex.Message}");
			}
		}

		Log($"Starting SharpDbg - Interpreter: {interpreter}");

		if (interpreter != "vscode")
		{
			Console.Error.WriteLine($"Unsupported interpreter: {interpreter}");
			Console.Error.WriteLine("Currently only --interpreter=vscode is supported");
			return 1;
		}

		try
		{
			// For now, only support stdin/stdout communication
			if (serverPort >= 0)
			{
				Console.Error.WriteLine("TCP server mode not yet implemented");
				return 1;
			}

			// Set up DAP protocol using stdin/stdout

			//Debugger.Launch();
			var inputStream = Console.OpenStandardInput();
			var outputStream = Console.OpenStandardOutput();

			// Create the debug adapter
			var adapter = new DebugAdapter(Log);

			Log("Protocol server starting...");

			// RunAsync handles messages until a 'disconnect' request (while stdin is still open) or
			// the client closes stdin / crashes.
			adapter.RunAsync(inputStream, outputStream).GetAwaiter().GetResult();

			// If the client closed its stream without sending a 'disconnect' request the debugger was never
			// disposed - clean it up now. No-op when a disconnect request was handled.
			adapter.DapAborted_ShutdownDebugger();

			var exitCode = 0;
			Log($"Exiting with code {exitCode}");
			_logWriter?.Flush();
			_logWriter?.Dispose();
			_logWriter = null;

			// The protocol reader thread is a foreground thread blocked reading stdin - returning from Main
			// would leave the process alive for as long as the client keeps its stream open. Exit explicitly.
			Environment.Exit(exitCode);
			return exitCode; // unreachable - keeps the compiler happy
		}
		catch (Exception ex)
		{
			var message = $"SharpDbg fatal error: {ex}";
			Log(message);
			Console.Error.WriteLine(message);
			return 1;
		}
		finally
		{
			_logWriter?.Dispose();
		}
	}

	private static void Log(string message)
	{
		if (_logWriter is not null)
		{
			_logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
		}
	}
}
