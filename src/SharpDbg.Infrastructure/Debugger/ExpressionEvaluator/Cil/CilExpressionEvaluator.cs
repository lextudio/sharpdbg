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
			var compiled = _compiler.TryCompile(expression, context, out var error);
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
