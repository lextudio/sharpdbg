using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

// Production logging uses Android logcat, unavailable in the host-side transport test.
internal static class Profiler { internal static void Log(int priority, string tag, string message) { } }

public sealed class AndroidDapServerTests
{
    [Fact]
    public async Task SessionRequiresTokenAndReleasesStartupWhenAttachFails()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        int factoryCalls = 0, readyCalls = 0;
        var server = new AndroidDapServer(accepted.GetStream(), "secret", Path.GetTempPath(),
            () => { factoryCalls++; throw new InvalidOperationException("factory failed"); }, () => readyCalls++);
        Task running = server.RunAsync();
        var response = await Request(stream, 1, "initialize", new JsonObject());
        Assert.True((bool?)response["success"]);
        response = await Request(stream, 2, "threads", new JsonObject());
        Assert.False((bool?)response["success"]);
        Assert.Equal("Attach with a valid session token first.", (string?)response["message"]);
        response = await Request(stream, 3, "attach", new JsonObject { ["sessionToken"] = "错误" });
        Assert.False((bool?)response["success"]);
        Assert.Equal("Invalid debug session token.", (string?)response["message"]);
        Assert.Equal(0, factoryCalls);
        response = await Request(stream, 4, "attach", new JsonObject { ["sessionToken"] = "secret" });
        Assert.True((bool?)response["success"]);
        response = await Request(stream, 5, "configurationDone", null);
        Assert.False((bool?)response["success"]);
        Assert.Contains("factory failed", (string?)response["message"]);
        // The client sees our message, not the library's "Internal Error - <exception.ToString()>" with a call stack.
        Assert.Equal("factory failed", (string?)response["message"]);
        Assert.DoesNotContain("Internal Error", response.ToJsonString());
        Assert.DoesNotContain("   at ", response.ToJsonString());
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, readyCalls);
        client.Close();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(readyCalls >= 1);
    }

    [Fact]
    public async Task Initialize_SendsInitializedEventAfterTheResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var server = new AndroidDapServer(accepted.GetStream(), "secret", Path.GetTempPath(), () => throw new InvalidOperationException("not attached"), () => { });
        Task running = server.RunAsync();

        var response = await Request(stream, 1, "initialize", new JsonObject());
        Assert.True((bool?)response["success"]);
        // DAP requires the initialized event after the initialize response; the IDE waits for it before
        // sending setBreakpoints/configurationDone.
        var initialized = await Read(stream);
        Assert.Equal("event", (string?)initialized["type"]);
        Assert.Equal("initialized", (string?)initialized["event"]);

        client.Close();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SessionRejectsMissingToken(string? token)
    {
        Assert.Throws<ArgumentException>(() => new AndroidDapServer(Stream.Null, token!, Path.GetTempPath(), () => throw new InvalidOperationException(), () => { }));
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, 4)]
    public async Task InitializeAdvertisesExceptionFiltersAndHonoursClientLineBase(bool oneBased, int line)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var server = new AndroidDapServer(accepted.GetStream(), "secret", Path.GetTempPath(), () => throw new InvalidOperationException("not attached"), () => { });
        Task running = server.RunAsync();
        var initialize = await Request(stream, 1, "initialize", new JsonObject { ["linesStartAt1"] = oneBased, ["columnsStartAt1"] = oneBased });
        Assert.True((bool?)initialize["success"]);
        var filters = initialize["body"]!["exceptionBreakpointFilters"]!.AsArray().Select(f => (string?)f!["filter"]).ToArray();
        Assert.Equal(["all", "user-unhandled"], filters);
        Assert.True((bool?)(await Request(stream, 2, "attach", new JsonObject { ["sessionToken"] = "secret" }))["success"]);
        // The debugger is 1-based internally. A 0-based client sends and expects back line - 1.
        var response = await Request(stream, 3, "setBreakpoints", new JsonObject {
            ["source"] = new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "BaseCheck.cs") },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = line }) });
        Assert.True((bool?)response["success"]);
        Assert.Equal(line, (int?)response["body"]!["breakpoints"]![0]!["line"]);
        client.Close();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    static async Task<JsonObject> Request(Stream stream, int sequence, string command, JsonObject? args, List<JsonObject>? events = null)
    {
        var message = new JsonObject { ["type"] = "request", ["seq"] = sequence, ["command"] = command };
        if (args is not null) message["arguments"] = args;
        byte[] data = Encoding.UTF8.GetBytes(message.ToJsonString());
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {data.Length}\r\n\r\n"));
        await stream.WriteAsync(data);
        // The server may interleave events (for example "initialized") with the response; skip them.
        events ??= [];
        while (true)
        {
            var received = await Read(stream);
            if ((string?)received["type"] == "response" && (int?)received["request_seq"] == sequence) return received;
            events.Add(received);
        }
    }
    static async Task<JsonObject> Read(Stream stream)
    {
        List<byte> header = [];
        byte[] one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one);
            header.Add(one[0]);
            int n = header.Count;
            if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
        }
        string line = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0];
        byte[] data = new byte[int.Parse(line.AsSpan("Content-Length:".Length).Trim())];
        await stream.ReadExactlyAsync(data);
        return (JsonObject)JsonNode.Parse(data)!;
    }
}
