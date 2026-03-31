using System.Diagnostics;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests.Helpers;

public static class DebugAdapterProcessHelper
{
	public static string GetWorkspaceRootPath()
	{
		return Path.GetFullPath(Path.Combine(GitRoot.GetGitRootPath(), "..", ".."));
	}

	public static Process GetDebugAdapterProcess()
	{
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				//FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
				//FileName = @"C:\Users\Matthew\Documents\Git\sharpdbg\artifacts\bin\SharpDbg.Cli\debug\SharpDbg.Cli.exe",
				FileName = Path.JoinFromGitRoot("artifacts", "bin", "SharpDbg.Cli", "debug", OperatingSystem.IsWindows() ? "SharpDbg.Cli.exe" : "SharpDbg.Cli"),
				Arguments = "--interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
				CreateNoWindow = true
			}
		};
		if (File.Exists(process.StartInfo.FileName) is false) throw new FileNotFoundException("SharpDbg executable not found", process.StartInfo.FileName);
		process.Start();
		return process;
	}
	public static DebugProtocolHost GetDebugProtocolHost(Process process, ITestOutputHelper testOutputHelper, TaskCompletionSource? initializedEventTcs = null) =>
		GetDebugProtocolHost(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, testOutputHelper, initializedEventTcs);

	public static DebugProtocolHost GetDebugProtocolHost(Stream inputStream, Stream outputStream, ITestOutputHelper testOutputHelper, TaskCompletionSource? initializedEventTcs = null)
	{
		var debugProtocolHost = new DebugProtocolHost(inputStream, outputStream, false);
		debugProtocolHost.LogMessage += (sender, args) =>
		{
			//testOutputHelper.WriteLine($"Log [DAP Host]: {args.Message}");
		};
		debugProtocolHost.RegisterClientRequestType<HandshakeRequest, HandshakeArguments, HandshakeResponse>(async void (responder) =>
		{
			var signatureResponse = await DebuggerHandshakeSigner.Sign(responder.Arguments.Value);
			responder.SetResponse(new HandshakeResponse(signatureResponse));
		});
		debugProtocolHost.RegisterEventType<InitializedEvent>(@event =>
		{
			initializedEventTcs?.SetResult();
		});
		// debugProtocolHost.RegisterEventType<StoppedEvent>(async void (@event) =>
		// {
		// 	testOutputHelper.WriteLine("Stopped Event");
		// });
		debugProtocolHost.VerifySynchronousOperationAllowed();
		return debugProtocolHost;
	}

	public static InitializeRequest GetInitializeRequest()
	{
		return new InitializeRequest
		{
			ClientID = "vscode",
			ClientName = "Visual Studio Code",
			AdapterID = "coreclr",
			Locale = "en-us",
			LinesStartAt1 = true,
			ColumnsStartAt1 = true,
			PathFormat = InitializeArguments.PathFormatValue.Path,
			SupportsVariableType = true,
			SupportsVariablePaging = true,
			SupportsRunInTerminalRequest = true,
			SupportsHandshakeRequest = true
		};
	}

	public static AttachRequest GetAttachRequest(int processId, bool justMyCode = true, bool stopAtEntry = false)
	{
		return new AttachRequest
		{
			ConfigurationProperties = new Dictionary<string, JToken>
			{
				["name"] = "AttachRequestName",
				["type"] = "coreclr",
				["processId"] = processId,
				["stopAtEntry"] = stopAtEntry,
				["console"] = "internalConsole", // integratedTerminal, externalTerminal, internalConsole
				["justMyCode"] = justMyCode
			}
		};
	}

	public static string GetDebuggableProgramPath()
	{
		var filePath = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", OperatingSystem.IsWindows() ? "DebuggableConsoleApp.exe" : "DebuggableConsoleApp");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("DebuggableConsoleApp executable not found", filePath);
		return filePath;
	}

	public static string GetWpfHotReloadRuntimeStubPath()
	{
		var filePath = Path.JoinFromGitRoot("artifacts", "bin", "WpfHotReload.RuntimeStub", "debug", "WpfHotReload.RuntimeStub.dll");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("WpfHotReload.RuntimeStub assembly not found", filePath);
		return filePath;
	}

	public static string GetWorkspaceSampleProjectPath()
	{
		var filePath = Path.Combine(GetWorkspaceRootPath(), "sample", "sample.csproj");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("Workspace sample project not found", filePath);
		return filePath;
	}

	public static string GetWorkspaceSampleProgramPath()
	{
		var filePath = Path.Combine(GetWorkspaceRootPath(), "sample", "bin", "Debug", "net10.0-windows", "sample.exe");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("Workspace sample executable not found", filePath);
		return filePath;
	}

	public static string GetWorkspaceSampleMainWindowXamlPath()
	{
		var filePath = Path.Combine(GetWorkspaceRootPath(), "sample", "MainWindow.xaml");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("Workspace sample XAML not found", filePath);
		return filePath;
	}

	public static string GetWorkspaceSampleMainWindowCodeBehindPath()
	{
		var filePath = Path.Combine(GetWorkspaceRootPath(), "sample", "MainWindow.xaml.cs");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("Workspace sample code-behind not found", filePath);
		return filePath;
	}

	public static string GetWorkspaceWpfHotReloadRuntimeProjectPath()
	{
		var filePath = Path.Combine(GetWorkspaceRootPath(), "src", "WpfHotReload.Runtime", "WpfHotReload.Runtime.csproj");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("Workspace WpfHotReload.Runtime project not found", filePath);
		return filePath;
	}

	public static string GetWorkspaceWpfHotReloadRuntimePath()
	{
		var filePath = Path.Combine(GetWorkspaceRootPath(), "src", "WpfHotReload.Runtime", "bin", "Debug", "net10.0-windows", "WpfHotReload.Runtime.dll");
		if (File.Exists(filePath) is false) throw new FileNotFoundException("Workspace WpfHotReload.Runtime assembly not found", filePath);
		return filePath;
	}

	public static void EnsureProjectBuilt(string projectPath, ITestOutputHelper testOutputHelper)
	{
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = $"build \"{projectPath}\" -c Debug -nologo",
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(projectPath)
			}
		};

		process.OutputDataReceived += (_, args) =>
		{
			if (string.IsNullOrWhiteSpace(args.Data) is false)
			{
				testOutputHelper.WriteLine(args.Data);
			}
		};
		process.ErrorDataReceived += (_, args) =>
		{
			if (string.IsNullOrWhiteSpace(args.Data) is false)
			{
				testOutputHelper.WriteLine(args.Data);
			}
		};

		if (process.Start() is false)
		{
			throw new InvalidOperationException($"Failed to start build for {projectPath}");
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		process.WaitForExit();

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException($"dotnet build failed for {projectPath} with exit code {process.ExitCode}");
		}
	}

	public static LaunchRequest GetLaunchRequest(bool stopAtEntry = false)
	{
		var programPath = GetDebuggableProgramPath();
		return new LaunchRequest
		{
			ConfigurationProperties = new Dictionary<string, JToken>
			{
				["name"] = "LaunchRequestName",
				["type"] = "coreclr",
				["program"] = programPath,
				["cwd"] = Path.GetDirectoryName(programPath)!,
				["args"] = new JArray(),
				["console"] = "internalConsole",
				["stopAtEntry"] = stopAtEntry
			}
		};
	}

	public static SetBreakpointsRequest GetSetBreakpointsRequest(int? line = null, string? filePath = null)
	{
		line ??= 22;
		filePath ??= Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		var debugFilePath = filePath;
		var debugFileBreakpointLine = line.Value;

		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = debugFilePath },
			Breakpoints = [new SourceBreakpoint { Line = debugFileBreakpointLine }]
		};
		return setBreakpointsRequest;
	}

	public static SetBreakpointsRequest GetSetBreakpointsRequest(int[] lines, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = filePath },
			Breakpoints = lines.Select(line => new SourceBreakpoint { Line = line }).ToList()
		};
		return setBreakpointsRequest;
	}

	public static SetBreakpointsRequest GetSetBreakpointsRequest(List<SharpDbgBreakpointRequest> breakpointRequests, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = filePath },
			Breakpoints = breakpointRequests.Select(s => new SourceBreakpoint
			{
				Line = s.Line,
				Condition = s.Condition,
				HitCondition = s.HitCondition
			}).ToList()
		};
		return setBreakpointsRequest;
	}
}
