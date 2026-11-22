using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models;

namespace SharpDbg.Cli.Tests;

public class EditAndContinueTests(ITestOutputHelper testOutputHelper)
{
	private static string Source(string version, int padding = 0) => """
		using System;
		using System.Diagnostics;
		public static class Target
		{
		    public static string Get()
		    {
		        PADDINGreturn "VERSION";
		    }
		}
		public static class Program
		{
		    public static int Main()
		    {
		        Console.WriteLine("first=" + Target.Get());
		        Debugger.Break();
		        Console.WriteLine("second=" + Target.Get());
		        return 0;
		    }
		}
		""".Replace("VERSION", version).Replace("PADDING", new string('\n', padding));

	// <dotnet root>/shared/Microsoft.NETCore.App/<version>/ -> <dotnet root>/dotnet
	private static string DotnetHost => Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

	[Fact]
	public async Task ApplyDelta_MethodBodyEdit_TakesEffectAfterResume()
	{
		var dir = Path.Combine(Path.GetTempPath(), "sharpdbg-enc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
			var refs = tpa.Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Console.dll" or "netstandard.dll" or "System.Diagnostics.Debug.dll")
				.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
			var sourcePath = Path.Combine(dir, "Program.cs");
			CSharpCompilation Make(string v, int padding = 0) => CSharpCompilation.Create("enc_target",
				[CSharpSyntaxTree.ParseText(SourceText.From(Source(v, padding), Encoding.UTF8), path: sourcePath)], refs,
				new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));

			var v1 = Make("v1");
			var pe = new MemoryStream();
			var pdb = new MemoryStream();
			var pdbPath = Path.Combine(dir, "enc_target.pdb");
			v1.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath)).Success.Should().BeTrue();
			var dll = Path.Combine(dir, "enc_target.dll");
			File.WriteAllBytes(dll, pe.ToArray());
			File.WriteAllBytes(pdbPath, pdb.ToArray());
			File.WriteAllText(Path.Combine(dir, "enc_target.runtimeconfig.json"),
				"""{"runtimeOptions":{"tfm":"net11.0","framework":{"name":"Microsoft.NETCore.App","version":"11.0.0"}}}""");

			var metadata = ModuleMetadata.CreateFromImage(pe.ToArray());
			var reader = metadata.GetMetadataReader();
			var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
			var baseline = EmitBaseline.CreateInitialBaseline(v1, metadata,
				_ => EditAndContinueMethodDebugInformation.Create(ImmutableArray<byte>.Empty, ImmutableArray<byte>.Empty),
				_ => default(StandaloneSignatureHandle), hasPortableDebugInformation: true);
			var v2 = Make("v2", padding: 3);
			var edit = new SemanticEdit(SemanticEditKind.Update,
				v1.GetTypeByMetadataName("Target")!.GetMembers("Get")[0], v2.GetTypeByMetadataName("Target")!.GetMembers("Get")[0]);
			var mdDelta = new MemoryStream();
			var ilDelta = new MemoryStream();
			var pdbDelta = new MemoryStream();
			var diff = v2.EmitDifference(baseline, [edit], _ => false, mdDelta, ilDelta, pdbDelta);
			diff.Success.Should().BeTrue();
			var updatedMethodTokens = diff.UpdatedMethods.Select(method => MetadataTokens.GetToken(method)).ToArray();
			int sourceBreakpointLine;
			using (var symbols = ModuleMetadataReader.TryLoad(dll))
			{
				symbols.Should().NotBeNull();
				var baselineLocation = symbols!.GetSourceLocationForOffset(updatedMethodTokens.Single(), 1);
				sourceBreakpointLine = baselineLocation!.Value.startLine;
				symbols!.TryApplyPdbDelta(pdbDelta.ToArray(), updatedMethodTokens, out var pdbError).Should().BeTrue(pdbError);
				var updatedLocation = symbols.GetSourceLocationForOffset(updatedMethodTokens.Single(), 1);
				updatedLocation.Should().NotBeNull();
				updatedLocation!.Value.startLine.Should().Be(sourceBreakpointLine + 3);
			}

			var output = new StringBuilder();
			var stopped = new TaskCompletionSource<int>();
			var exited = new TaskCompletionSource();
			var debugger = new ManagedDebugger(m => testOutputHelper.WriteLine(m)) { EnableEditAndContinue = true };
			debugger.OnStopped += (tid, _) => stopped.TrySetResult(tid);
			debugger.OnStopped2 += (tid, _, _, _, _, _) => stopped.TrySetResult(tid);
			debugger.OnOutput += (s, _) => { lock (output) output.Append(s); };
			debugger.OnExited += _ => exited.TrySetResult();
			debugger.OnTerminated += () => exited.TrySetResult();
			debugger.Launch(new LaunchInfo
			{
				LaunchRequestConsoleType = LaunchRequestConsoleType.InternalConsole, Cwd = dir,
				Program = DotnetHost, StopAtEntry = false, Arguments = [dll], Env = new Dictionary<string, string>(),
			}, justMyCode: false);
			await debugger.ConfigurationDone();

			(await Task.WhenAny(stopped.Task, Task.Delay(30000))).Should().BeSameAs(stopped.Task);
			var sourceBreakpoint = debugger.SetBreakpoints(sourcePath, [new SharpDbgBreakpointRequest(sourceBreakpointLine)]).Single();
			sourceBreakpoint.Verified.Should().BeTrue();
			var rebindEvents = 0;
			debugger.OnBreakpointChanged += (changed, _) =>
			{
				if (changed.Id == sourceBreakpoint.Id) rebindEvents++;
			};
			var result = debugger.ApplyDelta(mvid, mdDelta.ToArray(), ilDelta.ToArray(), pdbDelta.ToArray(), updatedMethodTokens);
			result.Success.Should().BeTrue(result.Message);
			result.Message.Should().Contain("updated symbols");
			sourceBreakpoint.Verified.Should().BeTrue();
			sourceBreakpoint.Line.Should().Be(sourceBreakpointLine + 3);
			rebindEvents.Should().BeGreaterThan(0);
			debugger.RemoveBreakpoint(sourceBreakpoint.Id).Should().BeTrue();

			debugger.HandleContinueRequest(stopped.Task.Result);
			await Task.WhenAny(exited.Task, Task.Delay(15000));
			try { debugger.Disconnect(true); } catch { }
			string text;
			lock (output) text = output.ToString();
			text.Should().Contain("first=v1").And.Contain("second=v2");
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	/// <summary>Hot Reload under the debugger: the application keeps running and the delta reaches it.</summary>
	[Fact]
	public async Task ApplyDelta_WhileRunning_TakesEffectWithoutAStop()
	{
		const string loop = """
			using System;
			using System.Threading;
			public static class Target { public static string Get() { return "VERSION"; } }
			public static class Program
			{
			    public static int Main()
			    {
			        Console.WriteLine("ready");
			        for (var i = 0; i < 600; i++)
			        {
			            if (Target.Get() == "v2") { Console.WriteLine("saw=v2"); return 0; }
			            Thread.Sleep(50);
			        }
			        return 1;
			    }
			}
			""";
		var dir = Path.Combine(Path.GetTempPath(), "sharpdbg-enc-run-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
			var refs = tpa.Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Console.dll" or "netstandard.dll" or "System.Threading.Thread.dll" or "System.Threading.dll")
				.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
			var sourcePath = Path.Combine(dir, "Program.cs");
			CSharpCompilation Make(string v) => CSharpCompilation.Create("enc_running",
				[CSharpSyntaxTree.ParseText(SourceText.From(loop.Replace("VERSION", v), Encoding.UTF8), path: sourcePath)], refs,
				new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));
			var v1 = Make("v1");
			var pe = new MemoryStream();
			var pdb = new MemoryStream();
			var pdbPath = Path.Combine(dir, "enc_running.pdb");
			v1.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath)).Success.Should().BeTrue();
			var dll = Path.Combine(dir, "enc_running.dll");
			File.WriteAllBytes(dll, pe.ToArray());
			File.WriteAllBytes(pdbPath, pdb.ToArray());
			File.WriteAllText(Path.Combine(dir, "enc_running.runtimeconfig.json"),
				"""{"runtimeOptions":{"tfm":"netMAJOR.0","framework":{"name":"Microsoft.NETCore.App","version":"MAJOR.0.0"}}}"""
					.Replace("MAJOR", Environment.Version.Major.ToString()));
			var metadata = ModuleMetadata.CreateFromImage(pe.ToArray());
			var reader = metadata.GetMetadataReader();
			var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
			var baseline = EmitBaseline.CreateInitialBaseline(v1, metadata,
				_ => EditAndContinueMethodDebugInformation.Create(ImmutableArray<byte>.Empty, ImmutableArray<byte>.Empty),
				_ => default(StandaloneSignatureHandle), hasPortableDebugInformation: true);
			var v2 = Make("v2");
			var edit = new SemanticEdit(SemanticEditKind.Update,
				v1.GetTypeByMetadataName("Target")!.GetMembers("Get")[0], v2.GetTypeByMetadataName("Target")!.GetMembers("Get")[0]);
			var mdDelta = new MemoryStream();
			var ilDelta = new MemoryStream();
			var pdbDelta = new MemoryStream();
			var diff = v2.EmitDifference(baseline, [edit], _ => false, mdDelta, ilDelta, pdbDelta);
			diff.Success.Should().BeTrue();

			var output = new StringBuilder();
			var ready = new TaskCompletionSource();
			var exited = new TaskCompletionSource();
			var stops = 0;
			var debugger = new ManagedDebugger(m => testOutputHelper.WriteLine(m)) { EnableEditAndContinue = true };
			debugger.OnStopped += (_, _) => Interlocked.Increment(ref stops);
			debugger.OnStopped2 += (_, _, _, _, _, _) => Interlocked.Increment(ref stops);
			debugger.OnOutput += (s, _) => { lock (output) { output.Append(s); if (output.ToString().Contains("ready")) ready.TrySetResult(); } };
			debugger.OnExited += _ => exited.TrySetResult();
			debugger.OnTerminated += () => exited.TrySetResult();
			debugger.Launch(new LaunchInfo
			{
				LaunchRequestConsoleType = LaunchRequestConsoleType.InternalConsole, Cwd = dir,
				Program = DotnetHost, StopAtEntry = false, Arguments = [dll], Env = new Dictionary<string, string>(),
			}, justMyCode: false);
			await debugger.ConfigurationDone();
			(await Task.WhenAny(ready.Task, Task.Delay(30000))).Should().BeSameAs(ready.Task);

			var result = debugger.ApplyDelta(mvid, mdDelta.ToArray(), ilDelta.ToArray(), pdbDelta.ToArray(),
				diff.UpdatedMethods.Select(method => MetadataTokens.GetToken(method)).ToArray());
			result.Success.Should().BeTrue(result.Message);

			(await Task.WhenAny(exited.Task, Task.Delay(30000))).Should().BeSameAs(exited.Task);
			try { debugger.Disconnect(true); } catch { }
			string text;
			lock (output) text = output.ToString();
			text.Should().Contain("saw=v2");
			stops.Should().Be(0);
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}
}
