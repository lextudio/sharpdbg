using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

// 🤖
internal sealed class CilExpressionEvaluator(RuntimeAssemblyPrimitiveTypeClasses primitiveTypes, ManagedDebugger debugger)
{
	private readonly CilExpressionCompiler _compiler = new(debugger);
	private readonly CilInterpreter _interpreter = new(debugger, primitiveTypes);

	internal async Task<EvaluationResult> Evaluate(string expression, CompiledExpressionEvaluationContext context)
	{
		try
		{
			expression = SubstitutePseudoVariables(expression, context);
			if (expression == "$exception" && debugger.GetCurrentException(context.ThreadId) is { } currentException)
			{
				if (currentException is not ICorDebugReferenceValue { IsNull: false } reference)
				{
					return new EvaluationResult { Value = currentException };
				}
				using var handles = new EvaluationHandleScope();
				var handle = handles.CreateOwnedHandle(reference);
				return new EvaluationResult { Value = handle, OwnedResultHandle = handles.DetachIfOwned(handle) };
			}
			var compiled = _compiler.TryCompile(expression, context, out var error, out var hasUnresolvedSymbol);
			// LINQ is the usual case of an assembly the expression needs but the debuggee has not loaded yet
			if (compiled is null && hasUnresolvedSymbol && await TryLoadAssemblyAsync("System.Linq", context))
			{
				compiled = _compiler.TryCompile(expression, context, out error);
			}
			if (compiled is null) return new EvaluationResult { Error = $"error: {error}" };
			var delegateMaterializer = new Lazy<DelegateMaterializerAssembly>(() => _compiler.GetDelegateMaterializer(context));
			var interpretationResult = await _interpreter.InterpretAsync(compiled, context, delegateMaterializer);
			return new EvaluationResult { Value = interpretationResult.Value, OwnedResultHandle = interpretationResult.OwnedResultHandle };
		}
		catch (Exception ex)
		{
			return new EvaluationResult { Error = $"error: {ex.Message}" };
		}
	}

	/// <summary>Expands the debugger pseudo-variables <c>$tid</c> and <c>$pid</c> to their numeric
	/// values before the expression is compiled.</summary>
	private string SubstitutePseudoVariables(string expression, CompiledExpressionEvaluationContext context)
	{
		if (expression.Contains("$tid", StringComparison.Ordinal))
			expression = expression.Replace("$tid", context.ThreadId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
		if (expression.Contains("$pid", StringComparison.Ordinal) && debugger.ProcessId is int processId)
			expression = expression.Replace("$pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
		return expression;

	}

	/// <summary>Loads an assembly into the debuggee with Assembly.Load(string), returning whether it was newly loaded.</summary>
	private async Task<bool> TryLoadAssemblyAsync(string assemblyName, CompiledExpressionEvaluationContext context)
	{
		var moduleName = assemblyName + ".dll";
		if (debugger.AllModules.Any(module => module.ModuleName == moduleName)) return false;
		var coreLib = debugger.AllModules.FirstOrDefault(module => module.ModuleName == "System.Private.CoreLib.dll");
		if (coreLib is null) return false;
		var reader = coreLib.MetadataReader.PeMetadataReader;
		var load = reader.TypeDefinitions
			.Select(reader.GetTypeDefinition)
			.Where(type => reader.StringComparer.Equals(type.Namespace, "System.Reflection") && reader.StringComparer.Equals(type.Name, "Assembly"))
			.SelectMany(type => type.GetMethods())
			.FirstOrDefault(handle =>
			{
				var method = reader.GetMethodDefinition(handle);
				return reader.StringComparer.Equals(method.Name, "Load")
					&& method.DecodeSignature(EvaluationMetadataResolver.SignatureNameProvider.Instance, genericContext: null).ParameterTypes is [nameof(PrimitiveTypeCode.String)];
			});
		if (load.IsNil) return false;
		try
		{
			var name = await context.Thread.CreateEval().NewStringAsync(debugger.ProcessRuntimeEventsUntilEvalEvent, debugger.EvalStatus, assemblyName, throwOnException: true)
				?? throw new InvalidOperationException("Failed to create the assembly name");
			await context.Thread.CreateEval().CallParameterizedFunctionAsync(
				debugger.ProcessRuntimeEventsUntilEvalEvent,
				debugger.EvalStatus,
				coreLib.Module.GetFunctionFromToken(MetadataTokens.GetToken(load)),
				0,
				null,
				1,
				[name],
				throwOnException: true);
		}
		catch (ManagedDebugger.EvalException)
		{
			return false;
		}
		return debugger.AllModules.Any(module => module.ModuleName == moduleName);
	}
}

internal sealed class EvaluationResult : IDisposable
{
	private ICorDebugHandleValue? _ownedResultHandle;

	public ICorDebugValue? Value { get; init; }
	public string? Error { get; init; }
	public ICorDebugHandleValue? OwnedResultHandle
	{
		init => _ownedResultHandle = value;
	}

	public void RelinquishResultHandleOwnership()
	{
		_ownedResultHandle = null;
	}

	public void Dispose()
	{
		_ownedResultHandle?.TryDispose();
		_ownedResultHandle = null;
	}
}
