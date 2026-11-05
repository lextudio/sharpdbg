using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.IO.Pipes;
using SharpDbg.Application;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.InMemory;

namespace SharpDbg.Cli.Tests;

public class ProtocolTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public void UnsupportedRequests_ReturnErrorsWithoutStoppingDispatcher()
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();
		using var _ = adapter;
		using var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		host.Run();

		// A client always initializes first: the OmniSharp server does not dispatch other requests before that.
		var initializeResponse = host.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		initializeResponse.Should().NotBeNull();

		var unimplementedError = Assert.Throws<ProtocolException>(() =>
			host.SendRequestSync(new SetVariableRequest(0, "value", "1")));
		unimplementedError.Message.Should().Be("Request 'setVariable' is not supported.");

		var unknownError = Assert.Throws<ProtocolException>(() =>
			host.SendRequestSync(new UnknownRequest()));
		// A request nobody registered is reported by the protocol library itself ("Method not found - <name>"), not by us.
		unknownError.Message.Should().Contain("unknownRequest").And.NotContain("Exception").And.NotContain(" at ");

		// The dispatcher is still alive after the errors.
		host.SendRequestSync(new ThreadsRequest()).Should().NotBeNull();
	}

	private sealed class UnknownRequest() : DebugRequestWithResponse<UnknownArguments, UnknownResponse>("unknownRequest");
	private sealed class UnknownArguments;
	private sealed class UnknownResponse : ResponseBody;

	[Fact]
	public async Task Disconnect_IsAnsweredAndEndsTheAdapterWhileTheClientKeepsItsInputOpen()
	{
		// The wire as a real host sees it: the client does not close anything after 'disconnect'. Raw DAP frames are
		// used on purpose: a protocol client cancels a request that is still waiting when it reads the end of the
		// stream, which would hide whether the response was written before the adapter shut down.
		using var stdInServer = new AnonymousPipeServerStream(PipeDirection.Out);
		using var stdInClient = new AnonymousPipeClientStream(PipeDirection.In, stdInServer.ClientSafePipeHandle);
		using var stdOutServer = new AnonymousPipeServerStream(PipeDirection.Out);
		using var stdOutClient = new AnonymousPipeClientStream(PipeDirection.In, stdOutServer.ClientSafePipeHandle);
		using var adapter = new DebugAdapter();
		var shutdownRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		adapter.ShutdownRequested += () => shutdownRaised.TrySetResult();
		var run = Task.Run(() => adapter.RunAsync(stdInClient, stdOutServer));
		var timeout = TimeSpan.FromSeconds(10);

		await WriteFrameAsync(stdInServer, """{"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"test","linesStartAt1":true,"columnsStartAt1":true}}""");
		(await ReadUntilAsync(stdOutClient, "\"request_seq\":1", timeout)).Should().Contain("\"success\":true");

		await WriteFrameAsync(stdInServer, """{"seq":2,"type":"request","command":"disconnect","arguments":{"terminateDebuggee":false}}""");
		var response = await ReadUntilAsync(stdOutClient, "\"request_seq\":2", timeout);
		response.Should().Contain("\"command\":\"disconnect\"").And.Contain("\"success\":true", "the response must be written before the adapter shuts down");

		(await Task.WhenAny(run, Task.Delay(timeout))).Should().BeSameAs(run, "RunAsync must end after 'disconnect' even though stdin is still open");
		await run;
		shutdownRaised.Task.IsCompleted.Should().BeTrue("hosts such as SharpDbg.Cli exit on ShutdownRequested");
	}

	static async Task WriteFrameAsync(Stream stream, string json)
	{
		var body = System.Text.Encoding.UTF8.GetBytes(json);
		await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
		await stream.WriteAsync(body);
		await stream.FlushAsync();
	}

	// Reads from the stream until the accumulated text contains the marker (frames may arrive in pieces and events may precede it).
	static async Task<string> ReadUntilAsync(Stream stream, string marker, TimeSpan timeout)
	{
		using var cancel = new CancellationTokenSource(timeout);
		var text = new System.Text.StringBuilder();
		var buffer = new byte[4096];
		while (!text.ToString().Contains(marker, StringComparison.Ordinal))
		{
			int read = await stream.ReadAsync(buffer, cancel.Token);
			if (read == 0) throw new EndOfStreamException($"The stream ended before '{marker}'. Received: {text}");
			text.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
		}
		return text.ToString();
	}
}
