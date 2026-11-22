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

		var unimplementedResult = host.SendRequest(new SetVariableArguments { VariablesReference = 0, Name = "value", Value = "1" });
		unimplementedResult.Value.Should().BeOfType<DapError>().Which.Message.Should().Be("Request 'setVariable' is not supported.");

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
	}
	private sealed record ApplyDeltasResponse;
}
