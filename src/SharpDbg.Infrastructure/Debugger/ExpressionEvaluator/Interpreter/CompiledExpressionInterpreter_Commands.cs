using System.Text;
using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	private Task IdentifierName(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var identifier = command.Argument as string ?? "";
		identifier = ReplaceInternalNames(identifier, true);

		evalStack.AddFirst(new EvalStackEntry
		{
			Identifiers = [identifier],
			Editable = true
		});

		return Task.CompletedTask;
	}

	private async Task GenericName(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var argCount = command.Arguments[1] as int? ?? 0;
		var name = command.Arguments[0] as string ?? "";

		var genericTypes = new List<CorDebugType?>();
		var generics = new StringBuilder(">");
		genericTypes.Capacity = argCount;

		for (int i = 0; i < argCount; i++)
		{
			var value = await GetFrontStackEntryValue(evalStack);
			CorDebugType? type = value?.ExactType;

			generics.Insert(0, "," + type?.GetType().Name ?? "");
			genericTypes.Add(type);
			evalStack.RemoveFirst();
		}

		generics.Remove(0, 1);
		name += "<" + generics;

		evalStack.AddFirst(new EvalStackEntry
		{
			Identifiers = new List<string> { name },
			GenericTypeCache = genericTypes,
			Editable = true
		});
	}

	private async Task InvocationExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var argCount = command.Argument as int? ?? 0;

		if (argCount < 0)
			throw new ArgumentException("Invalid argument count");

		var args = new CorDebugValue?[argCount];
		for (var i = argCount - 1; i >= 0; i--)
		{
			args[i] = await GetFrontStackEntryValue(evalStack);
			evalStack.RemoveFirst();
		}

		var entry = evalStack.First.Value;
		if (entry.PreventBinding)
			return;

		if (entry.Identifiers.Count == 0)
			throw new InvalidOperationException("No method name provided");

		var methodNameGenerics = entry.Identifiers.Last();
		entry.Identifiers.RemoveAt(entry.Identifiers.Count - 1);

		var methodName = methodNameGenerics;
		var pos = methodName.IndexOf('`');
		if (pos >= 0)
			methodName = methodName.Substring(0, pos);

		bool idsEmpty = false;
		bool isInstance = true;

		CorDebugValue? objValue;
		CorDebugType? objType;

		if (entry.CorDebugValue == null && entry.Identifiers.Count == 0)
		{
			idsEmpty = true;
			// We don't know if this is a static or instance method, but it's fine to add "this", as if the method is not
			// found as an instance method, it will continue and search for static methods
			entry.Identifiers.Add("this");
			objValue = await GetFrontStackEntryValue(evalStack);
			var isStaticMethod = objValue == null;
			objType = objValue?.ExactType;

			if (!isStaticMethod)
			{
				//entry.Identifiers.Add("this");
			}
			else
			{
				throw new NotImplementedException("I don't think this is ever hit?");
				var ilFrame = _debugger.GetFrameForThreadIdAndStackDepth(_context.ThreadId, _context.StackDepth);
				var corDebugFunction = ilFrame.Function;
				var module = corDebugFunction.Class.Module;
				var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
				var methodProps = metaDataImport!.GetMethodProps(corDebugFunction.Token);
				var declaringTypeDef = methodProps.pClass;
				var typeProps = metaDataImport!.GetTypeDefProps(declaringTypeDef);
				var className = typeProps.szTypeDef;
				entry.Identifiers.AddRange(className.Split('.'));
			}
		}

		objValue = await GetFrontStackEntryValue(evalStack);

		if (objValue != null)
		{
			var elemType = objValue.UnwrapDebugValue().Type;

			if (_runtimeAssemblyPrimitiveTypeClasses.CorElementToValueClassMap.TryGetValue(elemType, out var boxedClass))
			{
				var size = objValue.Size;
				var data = objValue.UnwrapDebugValue() is CorDebugGenericValue genValue
					? genValue.GetValueAsBytes()
					: null;

				if (data != null)
				{
					objValue = await CreateValueType(boxedClass, data);
				}
			}

			objType = objValue.ExactType;
		}
		else
		{
			objType = await GetFrontStackEntryType(evalStack);
		}

		if (objType == null && objValue == null) throw new InvalidOperationException("Could not resolve target type for method invocation");

		CorDebugFunction? function = null;
		bool? searchStatic = objValue == null;
		if (objType != null && IsStaticClass(objType))
		{
			searchStatic = true;
		}

		if (objType != null)
		{
			function = await FindMethodOnType(objType, methodName, args, searchStatic.Value, idsEmpty);
			if (function == null && searchStatic.Value == false && objValue != null)
			{
				function = await FindMethodOnType(objType, methodName, args, true, idsEmpty);
			}
		}

		if (function == null)
		{
			throw new InvalidOperationException($"Method '{methodName}' with {args.Length} parameters not found");
		}

		var methodProps2 = function.Class.Module.GetMetaDataInterface().MetaDataImport!.GetMethodProps(function.Token);
		isInstance = methodProps2.pdwAttr.IsMdStatic() is false;

		var parameterTypes = ParseMethodSignatureWithMetadata(methodProps2.ppvSigBlob, methodProps2.pcbSigBlob);
		if (parameterTypes.Count == args.Length)
		{
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == null)
					continue;
				var expectedType = SignatureTypeCodeToCorElementType(parameterTypes[i].TypeCode);
				if (expectedType == CorElementType.End)
					continue;
				var argType = GetArgElementType(args[i]!);
				if (expectedType != argType)
				{
					var converted = await TryConvertNumericValue(args[i]!, argType, expectedType);
					if (converted != null)
					{
						args[i] = converted;
					}
				}
			}
		}

		var typeArgsCount = entry.GenericTypeCache?.Count ?? 0;
		var realArgsCount = args.Length + (isInstance ? 1 : 0);
		var typeArgs = new List<ICorDebugType>(typeArgsCount);
		var valueArgs = new List<ICorDebugValue>(realArgsCount);

		if (isInstance)
		{
			valueArgs.Add(objValue!.Raw);
		}

		foreach (var arg in args)
		{
			valueArgs.Add(arg!.Raw);
		}

		if (objType != null)
		{
			var typeParamsEnum = objType.EnumerateTypeParameters();
			foreach (var typeParam in typeParamsEnum)
			{
				typeArgs.Add(typeParam.Raw);
			}
		}

		if (entry.GenericTypeCache != null)
		{
			for (int i = entry.GenericTypeCache.Count - 1; i >= 0; i--)
			{
				if (entry.GenericTypeCache[i] != null)
				{
					typeArgs.Add(entry.GenericTypeCache[i]!.Raw);
				}
			}
		}

		entry.ResetEntry();
		var eval = _context.Thread.CreateEval();
		var result = await eval.CallParameterizedFunctionAsync(
			_debuggerManagedCallback,
			function,
			typeArgs.Count,
			typeArgs.Count > 0 ? typeArgs.ToArray() : null,
			valueArgs.Count,
			valueArgs.ToArray());

		if (result == null && _runtimeAssemblyPrimitiveTypeClasses.CorVoidClass != null)
		{
			entry.CorDebugValue = await CreateValueType(_runtimeAssemblyPrimitiveTypeClasses.CorVoidClass, null);
		}
		else
		{
			entry.CorDebugValue = result;
		}
	}

	// TODO: Refactor - this doesn't belong in this class
	public static async Task<CorDebugFunction?> FindMethodOnType(
		CorDebugType type,
		string methodName,
		CorDebugValue?[] args,
		bool searchStatic,
		bool idsEmpty)
	{
		var typeClass = type.Class;
		var module = typeClass.Module;
		var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
		var classToken = typeClass.Token;
		CorDebugFunction? bestMethod = null;
		var bestScore = int.MaxValue;

		var methods = metaDataImport!.EnumMethods(classToken);
		foreach (var methodToken in methods)
		{
			var methodProps = metaDataImport!.GetMethodProps(methodToken);

			if (methodProps.szMethod != methodName)
				continue;

			var isStatic = methodProps.pdwAttr.IsMdStatic();

			if ((searchStatic && !isStatic) || (!searchStatic && isStatic && !idsEmpty))
				continue;

			var method = module.GetFunctionFromToken(methodToken);

			if (IsMethodParameterMatch(method, args))
			{
				return method;
			}

			var score = GetMethodParameterMatchScore(method, args);
			if (score >= 0)
			{
				if (bestMethod == null || score < bestScore)
				{
					bestMethod = method;
					bestScore = score;
				}
			}
		}

		if (bestMethod != null)
			return bestMethod;

		var baseType = type.Base;
		while (baseType != null)
		{
			var baseMethod = await FindMethodOnType(baseType, methodName, args, searchStatic, idsEmpty);
			if (baseMethod != null)
				return baseMethod;

			baseType = baseType.Base;
		}

		return null;
	}

	private static bool IsMethodParameterMatch(CorDebugFunction method, CorDebugValue?[] args)
	{
		var metaDataImport = method. Class.Module.GetMetaDataInterface().MetaDataImport;

		// Get the method signature blob
		var methodProps = metaDataImport.GetMethodProps(method.Token);

		// Parse the signature using System.Reflection.Metadata
		var parameterTypes = ParseMethodSignatureWithMetadata(methodProps.ppvSigBlob, methodProps.pcbSigBlob);

		// Compare parameter count
		if (parameterTypes.Count != args.Length)
			return false;

		// Compare each parameter type
		for (var i = 0; i < args.Length; i++)
		{
			if (args[i] == null)
				continue;

			var argType = GetArgElementType(args[i]);

			if (!IsTypeMatch(parameterTypes[i], argType, args[i]))
				return false;
		}

		return true;
	}

	private async Task ElementAccessExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var indexCount = command.Argument as int? ?? 0;

		var indexes = new List<uint>();
		for (int i = indexCount - 1; i >= 0; i--)
		{
			var indexValue = await GetFrontStackEntryValue(evalStack);
			indexes.Insert(0, await GetElementIndex(indexValue!));
			evalStack.RemoveFirst();
		}

		var entry = evalStack.First.Value;
		if (entry.PreventBinding)
			return;

		var objValue = await GetFrontStackEntryValue(evalStack);
		var realValue = await GetRealValueWithType(objValue!);
		var elemType = realValue.Type;

		if (elemType == CorElementType.SZArray || elemType == CorElementType.Array)
		{
			try
			{
				CorDebugArrayValue? arrayValue = null;
				if (realValue is CorDebugArrayValue av) arrayValue = av;
				else if (realValue is CorDebugReferenceValue rv && !rv.IsNull)
				{
					var deref = rv.Dereference();
					if (deref is CorDebugArrayValue av2) arrayValue = av2;
				}

				if (arrayValue == null)
				{
					throw new InvalidOperationException("Failed to resolve array for element access");
				}

				var rank = arrayValue.Rank;
				if (rank > 1) throw new NotImplementedException("Multidimensional array indexing not yet supported");

				// Get element (GetElement expects 1-based first parameter)
				var idxs = new int[indexes.Count];
				for (int i = 0; i < indexes.Count; i++) idxs[i] = (int)indexes[i];
				var element = arrayValue.GetElement(1, idxs);
				evalStack.First.Value.CorDebugValue = element;
				return;
			}
			catch (Exception ex)
			{
				throw;
			}
		}
		else
		{
			throw new NotImplementedException("Indexer access not yet fully implemented");
		}
	}

	private static bool IsStaticClass(CorDebugType type)
	{
		try
		{
			var metaDataImport = type.Class.Module.GetMetaDataInterface().MetaDataImport;
			var typeDefProps = metaDataImport.GetTypeDefProps(type.Class.Token);
			var flags = typeDefProps.pdwTypeDefFlags;
			return (flags & CorTypeAttr.tdAbstract) != 0 && (flags & CorTypeAttr.tdSealed) != 0;
		}
		catch
		{
			return false;
		}
	}

	private async Task NumericLiteralExpression(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var typeArg = command.Arguments[0] as ePredefinedType? ?? ePredefinedType.IntKeyword;
		var value = command.Arguments[1];

		var elemType = typeArg switch
		{
			ePredefinedType.DoubleKeyword => CorElementType.R8,
			ePredefinedType.FloatKeyword => CorElementType.R4,
			ePredefinedType.IntKeyword => CorElementType.I4,
			ePredefinedType.UIntKeyword => CorElementType.U4,
			ePredefinedType.LongKeyword => CorElementType.I8,
			ePredefinedType.ULongKeyword => CorElementType.U8,
			ePredefinedType.ShortKeyword => CorElementType.I2,
			ePredefinedType.UShortKeyword => CorElementType.U2,
			ePredefinedType.SByteKeyword => CorElementType.I1,
			ePredefinedType.ByteKeyword => CorElementType.U1,
			ePredefinedType.CharKeyword => CorElementType.Char,
			ePredefinedType.DecimalKeyword => CorElementType.ValueType,
			_ => throw new ArgumentException($"Unsupported numeric literal type: {typeArg}")
		};

		byte[]? data = null;
		if (value != null)
		{
			data = value switch
			{
				double d => BitConverter.GetBytes(d),
				float f => BitConverter.GetBytes(f),
				int i => BitConverter.GetBytes(i),
				uint ui => BitConverter.GetBytes(ui),
				long l => BitConverter.GetBytes(l),
				ulong ul => BitConverter.GetBytes(ul),
				short s => BitConverter.GetBytes(s),
				ushort us => BitConverter.GetBytes(us),
				sbyte sb => new[] { (byte)sb },
				byte b => new[] { b },
				char c => BitConverter.GetBytes(c),
				_ => throw new ArgumentException($"Unsupported numeric literal value type: {value.GetType()}")
			};
		}

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = elemType == CorElementType.ValueType && typeArg == ePredefinedType.DecimalKeyword
				? await CreateValueType(_runtimeAssemblyPrimitiveTypeClasses.CorDecimalClass!, data)
				: await CreatePrimitiveValue(elemType, data)
		});
	}

	private async Task StringLiteralExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var str = command.Argument as string ?? "";
		str = ReplaceInternalNames(str, true);

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = await CreateString(str)
		});
	}

	private async Task InterpolatedStringExpression(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var componentCount = command.Argument as int? ?? 0;

		if (componentCount < 0)
			throw new ArgumentException("Invalid component count for interpolated string");

		var stringBuilder = new StringBuilder();

		var components = new CorDebugValue?[componentCount];
		// Retrieve components in reverse order
		for (var i = componentCount - 1; i >= 0; i--)
		{
			components[i] = await GetFrontStackEntryValue(evalStack);
			evalStack.RemoveFirst();
		}

		foreach (var value in components)
		{
			var unwrapped = value.UnwrapDebugValue();
			if (unwrapped == null || unwrapped is CorDebugReferenceValue { IsNull: true })
			{
				stringBuilder.Append("null");
			}
			else if (unwrapped is CorDebugStringValue stringValue)
			{
				stringBuilder.Append(stringValue.GetStringWithoutBug(stringValue.Length + 1));
			}
			else
			{
				var toStringResult = await GetToStringResult(value);
				stringBuilder.Append(toStringResult);
			}
		}

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = await CreateString(stringBuilder.ToString())
		});
	}

	private async Task<string> GetToStringResult(CorDebugValue value)
	{
		var unwrappedValue = value.UnwrapDebugValue();
		if (_runtimeAssemblyPrimitiveTypeClasses.CorElementToValueClassMap.TryGetValue(unwrappedValue.Type, out var boxedClass))
		{
			var data = unwrappedValue is CorDebugGenericValue genValue
				? genValue.GetValueAsBytes()
				: null;

			if (data != null)
			{
				value = await CreateValueType(boxedClass, data);
			}
		}
		var corDebugFunction = await FindMethodOnType(value.ExactType, "ToString", [], false, true);
		if (corDebugFunction is null) throw new InvalidOperationException("ToString method not found");
		var eval = _context.Thread.CreateEval();
		var result = await eval.CallParameterlessInstanceMethodAsync(_debuggerManagedCallback, corDebugFunction, value);
		var unwrappedResult = result!.UnwrapDebugValue();
		if (unwrappedResult is not CorDebugStringValue stringValue) throw new InvalidOperationException("ToString did not return a string");

		var stringResult = stringValue.GetStringWithoutBug(stringValue.Length + 1);
		return stringResult;
	}

	private async Task CharacterLiteralExpression(TwoOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var value = command.Arguments[1];
		var data = value is char c ? BitConverter.GetBytes(c) : null;

		evalStack.AddFirst(new EvalStackEntry
		{
			Literal = true,
			CorDebugValue = await CreatePrimitiveValue(CorElementType.Char, data)
		});
	}

	private async Task PredefinedType(OneOperandCommand command, LinkedList<EvalStackEntry> evalStack)
	{
		var typeArg = command.Argument as ePredefinedType? ?? ePredefinedType.IntKeyword;

		var elemType = typeArg switch
		{
			ePredefinedType.BoolKeyword => CorElementType.Boolean,
			ePredefinedType.ByteKeyword => CorElementType.U1,
			ePredefinedType.CharKeyword => CorElementType.Char,
			ePredefinedType.DoubleKeyword => CorElementType.R8,
			ePredefinedType.FloatKeyword => CorElementType.R4,
			ePredefinedType.IntKeyword => CorElementType.I4,
			ePredefinedType.LongKeyword => CorElementType.I8,
			ePredefinedType.SByteKeyword => CorElementType.I1,
			ePredefinedType.ShortKeyword => CorElementType.I2,
			ePredefinedType.StringKeyword => CorElementType.String,
			ePredefinedType.UShortKeyword => CorElementType.U2,
			ePredefinedType.UIntKeyword => CorElementType.U4,
			ePredefinedType.ULongKeyword => CorElementType.U8,
			ePredefinedType.DecimalKeyword => CorElementType.ValueType,
			_ => throw new ArgumentException($"Unsupported predefined type: {typeArg}")
		};

		evalStack.AddFirst(new EvalStackEntry
		{
			CorDebugValue = elemType == CorElementType.ValueType && typeArg == ePredefinedType.DecimalKeyword
				? await CreateValueType(_runtimeAssemblyPrimitiveTypeClasses.CorDecimalClass!, null)
				: elemType == CorElementType.String
					? await CreateString("")
					: await CreatePrimitiveValue(elemType, null)
		});
	}

	private Task SimpleMemberAccessExpression(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		if (evalStack.Count < 2)
			throw new InvalidOperationException("Stack underflow in SimpleMemberAccessExpression");

		var identifier = evalStack.First.Value.Identifiers.FirstOrDefault() ?? "";
		var genericTypes = evalStack.First.Value.GenericTypeCache;
		evalStack.RemoveFirst();

		if (!evalStack.First.Value.PreventBinding)
		{
			evalStack.First.Value.Identifiers.Add(identifier);
			evalStack.First.Value.GenericTypeCache = genericTypes;
		}

		return Task.CompletedTask;
	}

	private Task QualifiedName(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		return SimpleMemberAccessExpression(command, evalStack);
	}

	private async Task MemberBindingExpression(CommandBase command, LinkedList<EvalStackEntry> evalStack)
	{
		if (evalStack.Count < 2)
			throw new InvalidOperationException("Stack underflow in MemberBindingExpression");

		var identifier = evalStack.First.Value.Identifiers.FirstOrDefault() ?? "";
		evalStack.RemoveFirst();

		var entry = evalStack.First.Value;
		if (entry.PreventBinding)
			return;

		var value = await GetFrontStackEntryValue(evalStack, true);
		entry.CorDebugValue = value;
		entry.Identifiers.Clear();

		if (value is CorDebugReferenceValue refValue && !refValue.IsNull)
		{
			entry.Identifiers.Add(identifier);
		}
		else
		{
			entry.PreventBinding = true;
		}
	}

	private async Task SizeOfExpression(LinkedList<EvalStackEntry> evalStack)
	{
		var entry = evalStack.First.Value;
		var size = 0;

		if (entry.CorDebugValue != null)
		{
			var elemType = entry.CorDebugValue.Type;
			if (elemType == CorElementType.Class)
			{
				var unwrapped = entry.CorDebugValue.UnwrapDebugValue();
				size = unwrapped.Size;
			}
			else
			{
				size = entry.CorDebugValue.Size;
			}
		}
		else
		{
			throw new NotImplementedException("SizeOf for types not yet fully implemented");
		}

		entry.ResetEntry();
		entry.CorDebugValue = await CreatePrimitiveValue(CorElementType.U4, BitConverter.GetBytes((uint)size));
	}

	private async Task CoalesceExpression(LinkedList<EvalStackEntry> evalStack)
	{
		var rightEntry = evalStack.First.Value;
		var rightValue = await GetFrontStackEntryValue(evalStack);
		var realRight = await GetRealValueWithType(rightValue!);
		evalStack.RemoveFirst();

		var leftEntry = evalStack.First.Value;
		var leftValue = await GetFrontStackEntryValue(evalStack);
		var realLeft = await GetRealValueWithType(leftValue!);

		var rightType = realRight.Type;
		var leftType = realLeft.Type;

		if ((rightType == CorElementType.String && leftType == CorElementType.String) ||
			(rightType == CorElementType.Class && leftType == CorElementType.Class))
		{
			if (leftValue is CorDebugReferenceValue refValue && refValue.IsNull)
			{
				evalStack.RemoveFirst();
				evalStack.AddFirst(rightEntry);
			}
		}
		else if (TryGetNullableValue(realLeft, out var hasValue, out var underlyingValue))
		{
			if (!hasValue)
			{
				evalStack.RemoveFirst();
				evalStack.AddFirst(rightEntry);
			}
			else if (underlyingValue != null)
			{
				leftEntry.CorDebugValue = underlyingValue;
				leftEntry.Identifiers.Clear();
				leftEntry.PreventBinding = true;
			}
		}
		else
		{
			throw new ArgumentException("Operator ?? cannot be applied to operands of these types");
		}
	}

	private static bool TryGetNullableValue(CorDebugValue value, out bool hasValue, out CorDebugValue? underlyingValue)
	{
		hasValue = false;
		underlyingValue = null;
		try
		{
			if (value is not CorDebugObjectValue objectValue)
				return false;

			var metaDataImport = objectValue.Class.Module.GetMetaDataInterface().MetaDataImport;
			var typeProps = metaDataImport.GetTypeDefProps(objectValue.Class.Token);
			if (!typeProps.szTypeDef.StartsWith("System.Nullable`", StringComparison.Ordinal))
				return false;

			var hasValueFieldDef = metaDataImport.FindField(objectValue.Class.Token, "hasValue", 0, 0);
			var valueFieldDef = metaDataImport.FindField(objectValue.Class.Token, "value", 0, 0);

			var hasValueDebugValue = objectValue.GetFieldValue(objectValue.Class.Raw, hasValueFieldDef);
			if (!TryReadBoolean(hasValueDebugValue, out hasValue))
				return false;
			if (!hasValue)
				return true;

			underlyingValue = objectValue.GetFieldValue(objectValue.Class.Raw, valueFieldDef);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryReadBoolean(CorDebugValue value, out bool result)
	{
		result = false;
		var unwrapped = value.UnwrapDebugValue();
		if (unwrapped is CorDebugGenericValue genValue && genValue.Type == CorElementType.Boolean)
		{
			var data = genValue.GetValueAsBytes();
			result = data.Length > 0 && data[0] != 0;
			return true;
		}
		return false;
	}

	private async Task<CorDebugValue?> TryConvertNumericValue(CorDebugValue value, CorElementType fromType, CorElementType toType)
	{
		if (!IsNumericType(fromType) || !IsNumericType(toType))
			return null;

		var unwrapped = value.UnwrapDebugValue();
		if (unwrapped is not CorDebugGenericValue genValue)
			return null;

		var data = genValue.GetValueAsBytes();
		double numericValue;
		try
		{
			numericValue = fromType switch
			{
				CorElementType.I1 => unchecked((sbyte)data[0]),
				CorElementType.U1 => data[0],
				CorElementType.I2 => BitConverter.ToInt16(data, 0),
				CorElementType.U2 => BitConverter.ToUInt16(data, 0),
				CorElementType.I4 => BitConverter.ToInt32(data, 0),
				CorElementType.U4 => BitConverter.ToUInt32(data, 0),
				CorElementType.I8 => BitConverter.ToInt64(data, 0),
				CorElementType.U8 => BitConverter.ToUInt64(data, 0),
				CorElementType.R4 => BitConverter.ToSingle(data, 0),
				CorElementType.R8 => BitConverter.ToDouble(data, 0),
				_ => throw new ArgumentOutOfRangeException(nameof(fromType))
			};
		}
		catch
		{
			return null;
		}

		byte[]? outData = toType switch
		{
			CorElementType.I4 => BitConverter.GetBytes((int)numericValue),
			CorElementType.U4 => BitConverter.GetBytes((uint)numericValue),
			CorElementType.I8 => BitConverter.GetBytes((long)numericValue),
			CorElementType.U8 => BitConverter.GetBytes((ulong)numericValue),
			CorElementType.R4 => BitConverter.GetBytes((float)numericValue),
			CorElementType.R8 => BitConverter.GetBytes(numericValue),
			CorElementType.I2 => BitConverter.GetBytes((short)numericValue),
			CorElementType.U2 => BitConverter.GetBytes((ushort)numericValue),
			CorElementType.I1 => new[] { (byte)(sbyte)numericValue },
			CorElementType.U1 => new[] { (byte)numericValue },
			_ => null
		};

		if (outData == null)
			return null;

		return await CreatePrimitiveValue(toType, outData);
	}
}
