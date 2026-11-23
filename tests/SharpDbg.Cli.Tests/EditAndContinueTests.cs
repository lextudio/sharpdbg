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

	/// <summary>
	/// E3b, active frame without line maps: the edited method is on the stack (stopped inside it) when the delta applies.
	/// Without the client's <c>lineMaps</c> SharpDbg does not remap (a <c>FunctionRemapOpportunity</c> callback is only
	/// continued; the remapping case is <see cref="ApplyDelta_WithLineMaps_RemapsTheActiveFrameAtAnUnchangedStatement"/>), so the
	/// frame must finish on the code it started with and the next call must run the new code, without hanging the
	/// debuggee or reporting a stop of its own.
	/// </summary>
	[Fact]
	public async Task ApplyDelta_ToAnActiveFrame_FinishesTheOldCodeAndCallsTheNew()
	{
		const string target = """
			using System;
			using System.Diagnostics;
			public static class Target
			{
			    public static string Get(int n)
			    {
			        var before = "VERSION";
			        if (n == 1) Debugger.Break();
			        return before + "|VERSION";
			    }
			}
			public static class Program
			{
			    public static int Main()
			    {
			        Console.WriteLine("first=" + Target.Get(1));
			        Console.WriteLine("second=" + Target.Get(2));
			        return 0;
			    }
			}
			""";
		var dir = Path.Combine(Path.GetTempPath(), "sharpdbg-enc-active-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
			var refs = tpa.Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Console.dll" or "netstandard.dll" or "System.Diagnostics.Debug.dll")
				.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
			var sourcePath = Path.Combine(dir, "Program.cs");
			CSharpCompilation Make(string v) => CSharpCompilation.Create("enc_active",
				[CSharpSyntaxTree.ParseText(SourceText.From(target.Replace("VERSION", v), Encoding.UTF8), path: sourcePath)], refs,
				new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));
			var v1 = Make("v1");
			var pe = new MemoryStream();
			var pdb = new MemoryStream();
			var pdbPath = Path.Combine(dir, "enc_active.pdb");
			v1.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath)).Success.Should().BeTrue();
			var dll = Path.Combine(dir, "enc_active.dll");
			File.WriteAllBytes(dll, pe.ToArray());
			File.WriteAllBytes(pdbPath, pdb.ToArray());
			File.WriteAllText(Path.Combine(dir, "enc_active.runtimeconfig.json"),
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
			var log = new StringBuilder();
			var stopped = new TaskCompletionSource<int>();
			var exited = new TaskCompletionSource();
			var stops = 0;
			var debugger = new ManagedDebugger(m => { testOutputHelper.WriteLine(m); lock (log) log.AppendLine(m); }) { EnableEditAndContinue = true };
			debugger.OnStopped += (tid, _) => { Interlocked.Increment(ref stops); stopped.TrySetResult(tid); };
			debugger.OnStopped2 += (tid, _, _, _, _, _) => { Interlocked.Increment(ref stops); stopped.TrySetResult(tid); };
			debugger.OnOutput += (s, _) => { lock (output) output.Append(s); };
			debugger.OnExited += _ => exited.TrySetResult();
			debugger.OnTerminated += () => exited.TrySetResult();
			debugger.Launch(new LaunchInfo
			{
				LaunchRequestConsoleType = LaunchRequestConsoleType.InternalConsole, Cwd = dir,
				Program = DotnetHost, StopAtEntry = false, Arguments = [dll], Env = new Dictionary<string, string>(),
			}, justMyCode: false);
			await debugger.ConfigurationDone();

			// Stopped at Debugger.Break() inside Get: the edited method is the active frame.
			(await Task.WhenAny(stopped.Task, Task.Delay(30000))).Should().BeSameAs(stopped.Task);
			var result = debugger.ApplyDelta(mvid, mdDelta.ToArray(), ilDelta.ToArray(), pdbDelta.ToArray(),
				diff.UpdatedMethods.Select(method => MetadataTokens.GetToken(method)).ToArray());
			result.Success.Should().BeTrue(result.Message);

			debugger.HandleContinueRequest(stopped.Task.Result);
			(await Task.WhenAny(exited.Task, Task.Delay(30000))).Should().BeSameAs(exited.Task, "the debuggee must not hang after an active-frame edit");
			try { debugger.Disconnect(true); } catch { }
			string text;
			lock (output) text = output.ToString();
			text.Should().Contain("first=v1|v1", "the active frame finishes on the code it started with");
			text.Should().Contain("second=v2|v2", "a new call runs the updated method");
			stops.Should().Be(1, "only the Debugger.Break() stop; the remap callback is not reported as a stop");
			// The runtime offers the old frame a remap at each sequence point it reaches (ICorDebugManagedCallback2,
			// 4 times here); without it this test would not have exercised an active frame at all.
			string events;
			lock (log) events = log.ToString();
			events.Should().Contain("Event: FunctionRemapOpportunity", "the edited method must have been an active frame");
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	/// <summary>
	/// E3b, active-frame remap: with the unchanged lines of the edit, a frame stopped inside the updated method moves to
	/// the new version at the next statement the edit did not touch. Two lines inserted above shift every statement, and
	/// a statement after the stop changes; the frame computes its first part with the old code and the rest with the new.
	/// </summary>
	[Fact]
	public async Task ApplyDelta_WithLineMaps_RemapsTheActiveFrameAtAnUnchangedStatement()
	{
		const string target = """
			using System;
			using System.Diagnostics;
			public static class Target
			{
			    public static string Get(int n)
			    {
			PADDING        var before = "VERSION";
			        if (n == 1) Debugger.Break();
			        var mid = before.Length;
			        var after = "VERSION";
			        return before + "|" + after + "|" + mid;
			    }
			}
			public static class Program
			{
			    public static int Main()
			    {
			        Console.WriteLine("first=" + Target.Get(1));
			        Console.WriteLine("second=" + Target.Get(2));
			        return 0;
			    }
			}
			""";
		static string Text(string version, int padding) => target.Replace("VERSION", version).Replace("PADDING", string.Concat(Enumerable.Repeat("\n", padding)));
		var dir = Path.Combine(Path.GetTempPath(), "sharpdbg-enc-remap-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
			var refs = tpa.Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Console.dll" or "netstandard.dll" or "System.Diagnostics.Debug.dll")
				.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
			var sourcePath = Path.Combine(dir, "Program.cs");
			CSharpCompilation Make(string text) => CSharpCompilation.Create("enc_remap",
				[CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), path: sourcePath)], refs,
				new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));
			var oldText = Text("v1", 0);
			var newText = Text("v2", 2);
			var v1 = Make(oldText);
			var pe = new MemoryStream();
			var pdb = new MemoryStream();
			var pdbPath = Path.Combine(dir, "enc_remap.pdb");
			v1.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdbPath)).Success.Should().BeTrue();
			var dll = Path.Combine(dir, "enc_remap.dll");
			File.WriteAllBytes(dll, pe.ToArray());
			File.WriteAllBytes(pdbPath, pdb.ToArray());
			File.WriteAllText(Path.Combine(dir, "enc_remap.runtimeconfig.json"),
				"""{"runtimeOptions":{"tfm":"netMAJOR.0","framework":{"name":"Microsoft.NETCore.App","version":"MAJOR.0.0"}}}"""
					.Replace("MAJOR", Environment.Version.Major.ToString()));
			var metadata = ModuleMetadata.CreateFromImage(pe.ToArray());
			var reader = metadata.GetMetadataReader();
			var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
			var baseline = EmitBaseline.CreateInitialBaseline(v1, metadata,
				_ => EditAndContinueMethodDebugInformation.Create(ImmutableArray<byte>.Empty, ImmutableArray<byte>.Empty),
				_ => default(StandaloneSignatureHandle), hasPortableDebugInformation: true);
			var v2 = Make(newText);
			var edit = new SemanticEdit(SemanticEditKind.Update,
				v1.GetTypeByMetadataName("Target")!.GetMembers("Get")[0], v2.GetTypeByMetadataName("Target")!.GetMembers("Get")[0]);
			var mdDelta = new MemoryStream();
			var ilDelta = new MemoryStream();
			var pdbDelta = new MemoryStream();
			var diff = v2.EmitDifference(baseline, [edit], _ => false, mdDelta, ilDelta, pdbDelta);
			diff.Success.Should().BeTrue();
			var lineMaps = new[] { new SourceLineMap(sourcePath, UnchangedLines(oldText, newText)) };

			var output = new StringBuilder();
			var log = new StringBuilder();
			var stopped = new TaskCompletionSource<int>();
			var exited = new TaskCompletionSource();
			var debugger = new ManagedDebugger(m => { testOutputHelper.WriteLine(m); lock (log) log.AppendLine(m); }) { EnableEditAndContinue = true };
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
			var result = debugger.ApplyDelta(mvid, mdDelta.ToArray(), ilDelta.ToArray(), pdbDelta.ToArray(),
				diff.UpdatedMethods.Select(method => MetadataTokens.GetToken(method)).ToArray(), lineMaps);
			result.Success.Should().BeTrue(result.Message);

			debugger.HandleContinueRequest(stopped.Task.Result);
			(await Task.WhenAny(exited.Task, Task.Delay(30000))).Should().BeSameAs(exited.Task, "the debuggee must not hang after a remap");
			try { debugger.Disconnect(true); } catch { }
			string text;
			lock (output) text = output.ToString();
			string events;
			lock (log) events = log.ToString();
			events.Should().Contain("RemapFunction", "the frame must have been remapped");
			text.Should().Contain("first=v1|v2|2", "before ran on the old code, after on the new, and the local survived the remap");
			text.Should().Contain("second=v2|v2|2");
		}
		finally
		{
			try { Directory.Delete(dir, true); } catch { }
		}
	}

	/// <summary>Unchanged line ranges between two texts (1-based), from a longest common subsequence of their lines.</summary>
	private static UnchangedLineRange[] UnchangedLines(string oldText, string newText)
	{
		var a = oldText.Split('\n');
		var b = newText.Split('\n');
		var lcs = new int[a.Length + 1, b.Length + 1];
		for (var i = a.Length - 1; i >= 0; i--)
			for (var j = b.Length - 1; j >= 0; j--)
				lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
		var ranges = new List<UnchangedLineRange>();
		for (int i = 0, j = 0; i < a.Length && j < b.Length;)
		{
			if (a[i] == b[j])
			{
				if (ranges.Count > 0 && ranges[^1] is var last && last.OldStartLine + last.LineCount == i + 1 && last.NewStartLine + last.LineCount == j + 1)
					ranges[^1] = last with { LineCount = last.LineCount + 1 };
				else
					ranges.Add(new UnchangedLineRange(i + 1, j + 1, 1));
				i++; j++;
			}
			else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
			else j++;
		}
		return ranges.ToArray();
	}
}
