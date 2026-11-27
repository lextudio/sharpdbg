using AwesomeAssertions;
using SharpDbg.DapProtocol;
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

		var unimplementedResult = host.SendRequest(new StepBackArguments { ThreadId = 1 });
		unimplementedResult.Value.Should().BeOfType<DapError>().Which.Message.Should().Be("Request 'stepBack' is not supported.");

		var unknownResult = host.SendRequest(new UnknownArguments());
		unknownResult.Value.Should().BeOfType<DapError>().Which.Message.Should().Be("Request 'unknownRequest' is not supported.");

		var initializeResponse = host.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		initializeResponse.Should().NotBeNull();
	}

	[Fact]
	public void ApplyDeltasRequest_ReportsValidationAndEngineFailuresAsDapErrors()
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams();
		using var _ = adapter;
		using var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		host.Run();
		var invalidMvid = host.SendRequest(new ApplyDeltasArguments { Mvid = "not-a-guid", Metadata = "AQ==", Il = "Ag==" });
		invalidMvid.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("valid mvid");

		var missingMetadata = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Il = "Ag==" });
		missingMetadata.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("needs metadata");

		var invalidIl = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "not-base64" });
		invalidIl.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("invalid il");

		var invalidUpdatedMethod = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "Ag==", UpdatedMethodTokens = [0x02000001] });
		invalidUpdatedMethod.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("invalid updatedMethodTokens");

		var invalidLineMaps = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "Ag==",
			LineMaps = new[] { new { documentPath = "/src/Program.cs", unchangedLines = new[] { new { oldStartLine = 0, newStartLine = 1, lineCount = 1 } } } } });
		invalidLineMaps.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("invalid lineMaps");
		var lineMapsNotAnArray = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "Ag==", LineMaps = "x" });
		lineMapsNotAnArray.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("invalid lineMaps");

		// Well-formed lineMaps pass validation: the request then fails on the missing debuggee, not on the argument.
		var wellFormedLineMaps = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "Ag==",
			LineMaps = new[] { new { documentPath = "/src/Program.cs", unchangedLines = new[] { new { oldStartLine = 3, newStartLine = 5, lineCount = 2 } } } } });
		wellFormedLineMaps.Value.Should().BeOfType<DapError>().Which.Message.Should().NotContain("lineMaps");

		var notOptedIn = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "Ag==" });
		notOptedIn.Value.Should().BeOfType<DapError>().Which.Message.Should().Contain("enableEditAndContinue");

		var launch = DebugAdapterProcessHelper.GetLaunchRequest("not-started.dll", stopAtEntry: true);
		launch.AdditionalProperties!["enableEditAndContinue"] = true;
		host.SendRequestSync(launch);
		var noDebuggee = host.SendRequest(new ApplyDeltasArguments { Mvid = Guid.NewGuid().ToString("D"), Metadata = "AQ==", Il = "Ag==" });
		noDebuggee.Value.Should().BeOfType<DapError>();
	}

	private sealed record UnknownArguments : IDapRequest<UnknownResponse>
	{
		public string Command => "unknownRequest";
	}
	private sealed record UnknownResponse;
	private sealed record ApplyDeltasArguments : IDapRequest<ApplyDeltasResponse>
	{
		public string Command => "sharpdbg/applyDeltas";
		public string Mvid { get; init; } = "";
		public string Metadata { get; init; } = "";
		public string Il { get; init; } = "";
		public int[]? UpdatedMethodTokens { get; init; }
		public object? LineMaps { get; init; }
	}
	private sealed record ApplyDeltasResponse;
}
