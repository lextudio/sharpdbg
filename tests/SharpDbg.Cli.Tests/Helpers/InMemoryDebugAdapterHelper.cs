using System.IO.Pipes;
using SharpDbg.Application;

namespace SharpDbg.Cli.Tests.Helpers;

public sealed class RunningInProcAdapter : IDisposable
{
	public AnonymousPipeServerStream StdInServer { get; }
	public AnonymousPipeClientStream StdInClient { get; }
	public AnonymousPipeServerStream StdOutServer { get; }
	public AnonymousPipeClientStream StdOutClient { get; }
	public DebugAdapter Adapter { get; }

	internal RunningInProcAdapter(AnonymousPipeServerStream stdInServer, AnonymousPipeClientStream stdInClient, AnonymousPipeServerStream stdOutServer, AnonymousPipeClientStream stdOutClient, DebugAdapter adapter)
	{
		StdInServer = stdInServer;
		StdInClient = stdInClient;
		StdOutServer = stdOutServer;
		StdOutClient = stdOutClient;
		Adapter = adapter;
	}

	public void Dispose()
	{
		try
		{
			// Closing the server-side write end signals EOF to the adapter's reader
			StdInServer.Dispose();
			// Also close the adapter-side client stream to be sure the reader unblocks
			StdInClient.Dispose();
			StdOutServer.Dispose();
			StdOutClient.Dispose();

			// Wait for the adapter protocol reader to finish (it should exit after input closed)
			try { Adapter.Protocol.WaitForReader(); } catch { }

			// Dispose adapter if it implements IDisposable
			(Adapter as IDisposable)?.Dispose();
		}
		catch { }
	}
}

public static class InMemoryDebugAdapterHelper
{
	public static RunningInProcAdapter GetAdapterStreams(ITestOutputHelper testOutputHelper)
	{
		var stdInServer = new AnonymousPipeServerStream(PipeDirection.Out); // write
		var stdInClient = new AnonymousPipeClientStream(PipeDirection.In, stdInServer.ClientSafePipeHandle); // std in read

		var stdOutServer = new AnonymousPipeServerStream(PipeDirection.Out); // write
		var stdOutClient = new AnonymousPipeClientStream(PipeDirection.In, stdOutServer.ClientSafePipeHandle); // std out read

		var adapter = new DebugAdapter(Log);
		adapter.Initialize(stdInClient, stdOutServer);
		adapter.Protocol.VerifySynchronousOperationAllowed();
		adapter.Protocol.Run();

		return new RunningInProcAdapter(stdInServer, stdInClient, stdOutServer, stdOutClient, adapter);

		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [SharpDbg]: {message}");
		}
	}
}
