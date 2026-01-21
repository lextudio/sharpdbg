using System.IO.Pipes;
using SharpDbg.Application;

namespace SharpDbg.Cli.Tests.Helpers;

public static class InMemoryDebugAdapterHelper
{
	public static (AnonymousPipeServerStream input, AnonymousPipeClientStream output, DebugAdapter debugAdapter) GetAdapterStreams(ITestOutputHelper testOutputHelper)
	{
		var stdInServer = new AnonymousPipeServerStream(PipeDirection.Out); // write
		var stdInClient = new AnonymousPipeClientStream(PipeDirection.In, stdInServer.ClientSafePipeHandle); // std in read

		var stdOutServer = new AnonymousPipeServerStream(PipeDirection.Out); // write
		var stdOutClient = new AnonymousPipeClientStream(PipeDirection.In, stdOutServer.ClientSafePipeHandle); // std out read

		var adapter = new DebugAdapter(Log);
		adapter.Initialize(stdInClient, stdOutServer);
		adapter.Protocol.VerifySynchronousOperationAllowed();
		adapter.Protocol.Run();
		// Do not dispose the pipe streams from a background task here.
		// Caller is responsible for disposing the returned streams and adapter.

		return (stdInServer, stdOutClient, adapter);

		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [SharpDbg]: {message}");
		}
	}
}
