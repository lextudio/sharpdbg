using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpDbg.Cli.Tests.Helpers;

internal sealed class RawDapClient : IDisposable
{
	private readonly Stream _writeStream;
	private readonly Stream _readStream;
	private readonly StreamWriter _writer;
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _readerTask;
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pendingResponses = new();
	private readonly ConcurrentQueue<JObject> _events = new();
	private readonly SemaphoreSlim _eventSignal = new(0);
	private int _seq;

	public RawDapClient(Stream writeStream, Stream readStream)
	{
		_writeStream = writeStream;
		_readStream = readStream;
		_writer = new StreamWriter(_writeStream, new UTF8Encoding(false), leaveOpen: true)
		{
			AutoFlush = true
		};
		_readerTask = Task.Run(ReadLoopAsync);
	}

	public async Task<JObject> SendRequestAsync(string command, object? arguments = null, TimeSpan? timeout = null)
	{
		var seq = Interlocked.Increment(ref _seq);
		var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingResponses[seq] = tcs;

		var payload = new JObject
		{
			["seq"] = seq,
			["type"] = "request",
			["command"] = command,
			["arguments"] = arguments is null ? new JObject() : JObject.FromObject(arguments)
		};

		var json = payload.ToString(Formatting.None);
		await _writer.WriteAsync($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");

		using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
		await using var _ = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
		return await tcs.Task;
	}

	public async Task<JObject> WaitForEventAsync(string eventName, TimeSpan? timeout = null)
	{
		var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
		using var timeoutCts = new CancellationTokenSource(effectiveTimeout);

		while (true)
		{
			while (_events.TryDequeue(out var queued))
			{
				if (string.Equals((string?)queued["event"], eventName, StringComparison.Ordinal))
				{
					return queued;
				}
			}

			await _eventSignal.WaitAsync(timeoutCts.Token);
		}
	}

	private async Task ReadLoopAsync()
	{
		try
		{
			while (!_cts.IsCancellationRequested)
			{
				var message = await ReadMessageAsync(_cts.Token);
				if (message is null)
				{
					break;
				}

				var messageType = (string?)message["type"];
				if (string.Equals(messageType, "response", StringComparison.Ordinal))
				{
					var requestSeq = (int?)message["request_seq"];
					if (requestSeq is int seq && _pendingResponses.TryRemove(seq, out var tcs))
					{
						tcs.TrySetResult(message);
					}
				}
				else if (string.Equals(messageType, "event", StringComparison.Ordinal))
				{
					_events.Enqueue(message);
					_eventSignal.Release();
				}
				else if (string.Equals(messageType, "request", StringComparison.Ordinal))
				{
					await SendResponseAsync(message, new JObject());
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			foreach (var pending in _pendingResponses.Values)
			{
				pending.TrySetException(ex);
			}
		}
		finally
		{
			foreach (var pending in _pendingResponses.Values)
			{
				pending.TrySetCanceled();
			}
		}
	}

	private async Task<JObject?> ReadMessageAsync(CancellationToken cancellationToken)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		while (true)
		{
			var line = await ReadLineAsync(cancellationToken);
			if (line is null)
			{
				return null;
			}

			if (line.Length == 0)
			{
				break;
			}

			var separatorIndex = line.IndexOf(':');
			if (separatorIndex <= 0)
			{
				continue;
			}

			headers[line[..separatorIndex]] = line[(separatorIndex + 1)..].Trim();
		}

		if (!headers.TryGetValue("Content-Length", out var contentLengthText) ||
			!int.TryParse(contentLengthText, out var contentLength) ||
			contentLength <= 0)
		{
			return null;
		}

		var buffer = new byte[contentLength];
		var read = 0;
		while (read < contentLength)
		{
			var bytesRead = await _readStream.ReadAsync(buffer.AsMemory(read, contentLength - read), cancellationToken);
			if (bytesRead == 0)
			{
				return null;
			}

			read += bytesRead;
		}

		return JObject.Parse(Encoding.UTF8.GetString(buffer));
	}

	private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
	{
		var bytes = new List<byte>();
		while (true)
		{
			var oneByte = new byte[1];
			var read = await _readStream.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken);
			if (read == 0)
			{
				return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
			}

			if (oneByte[0] == '\n')
			{
				if (bytes.Count > 0 && bytes[^1] == '\r')
				{
					bytes.RemoveAt(bytes.Count - 1);
				}

				return Encoding.ASCII.GetString(bytes.ToArray());
			}

			bytes.Add(oneByte[0]);
		}
	}

	private async Task SendResponseAsync(JObject request, JObject body)
	{
		var response = new JObject
		{
			["seq"] = Interlocked.Increment(ref _seq),
			["type"] = "response",
			["request_seq"] = request["seq"]?.Value<int>() ?? 0,
			["command"] = request["command"]?.Value<string>() ?? string.Empty,
			["success"] = true,
			["body"] = body
		};

		var json = response.ToString(Formatting.None);
		await _writer.WriteAsync($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");
	}

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			_readerTask.Wait(TimeSpan.FromSeconds(2));
		}
		catch
		{
		}

		_eventSignal.Dispose();
		_cts.Dispose();
		try
		{
			_writer.Dispose();
		}
		catch (ObjectDisposedException)
		{
		}
	}
}
