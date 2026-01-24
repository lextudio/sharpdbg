using System.Reflection.Metadata;
using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	private static List<TypeInfo> ParseMethodSignatureWithMetadata(IntPtr ppvSigBlob, int pcbSigBlob)
	{
		var parameters = new List<TypeInfo>();

		unsafe
		{
			// Create a BlobReader from the signature blob
			//var blob = new ReadOnlySpan<byte>((void*)ppvSigBlob, pcbSigBlob);
			var reader = new BlobReader((byte*) ppvSigBlob, pcbSigBlob);

			// Decode the method signature
			var header = reader.ReadSignatureHeader();

			// Read generic parameter count if present
			int genericParamCount = 0;
			if (header.IsGeneric)
			{
				genericParamCount = reader.ReadCompressedInteger();
			}

			// Read parameter count
			int paramCount = reader.ReadCompressedInteger();

			// Read return type (skip it)
			DecodeType(ref reader); // Return type

			// Read each parameter type
			for (int i = 0; i < paramCount; i++)
			{
				var typeInfo = DecodeType(ref reader);
				parameters.Add(typeInfo);
			}
		}

		return parameters;
	}

	private class TypeInfo
	{
		public SignatureTypeCode TypeCode { get; set; }
		public int Token { get; set; } // For class/valuetype
		public TypeInfo? ElementType { get; set; } // For arrays, pointers, etc.
		public List<TypeInfo> GenericArguments { get; set; } // For generic types
	}

	private static TypeInfo DecodeType(ref BlobReader reader)
	{
		var typeInfo = new TypeInfo();
		var typeCode = reader.ReadSignatureTypeCode();
		typeInfo.TypeCode = typeCode;

		switch (typeCode)
		{
			case SignatureTypeCode.Boolean:
			case SignatureTypeCode.Char:
			case SignatureTypeCode.SByte:
			case SignatureTypeCode.Byte:
			case SignatureTypeCode.Int16:
			case SignatureTypeCode.UInt16:
			case SignatureTypeCode.Int32:
			case SignatureTypeCode.UInt32:
			case SignatureTypeCode.Int64:
			case SignatureTypeCode.UInt64:
			case SignatureTypeCode.Single:
			case SignatureTypeCode.Double:
			case SignatureTypeCode.IntPtr:
			case SignatureTypeCode.UIntPtr:
			case SignatureTypeCode.Object:
			case SignatureTypeCode.String:
			case SignatureTypeCode.Void:
				// Simple types - no additional data
				break;

			case SignatureTypeCode.TypeHandle:
				// Class or ValueType - read the token
				typeInfo.Token = reader.ReadCompressedInteger();
				break;

			case SignatureTypeCode.SZArray:
			case SignatureTypeCode.Pointer:
			case SignatureTypeCode.ByReference:
				// Element type follows
				typeInfo.ElementType = DecodeType(ref reader);
				break;

			case SignatureTypeCode.GenericTypeInstance:
				// Generic type:  read base type, then argument count, then arguments
				typeInfo.ElementType = DecodeType(ref reader);
				int genericArgCount = reader.ReadCompressedInteger();
				typeInfo.GenericArguments = new List<TypeInfo>();
				for (int i = 0; i < genericArgCount; i++)
				{
					typeInfo.GenericArguments.Add(DecodeType(ref reader));
				}

				break;

			case SignatureTypeCode.GenericTypeParameter:
			case SignatureTypeCode.GenericMethodParameter:
				// Read parameter index
				typeInfo.Token = reader.ReadCompressedInteger();
				break;

			case SignatureTypeCode.Array:
				// Multi-dimensional array
				typeInfo.ElementType = DecodeType(ref reader);
				int rank = reader.ReadCompressedInteger();
				int numSizes = reader.ReadCompressedInteger();
				for (int i = 0; i < numSizes; i++)
					reader.ReadCompressedInteger(); // sizes
				int numLoBounds = reader.ReadCompressedInteger();
				for (int i = 0; i < numLoBounds; i++)
					reader.ReadCompressedSignedInteger(); // lower bounds
				break;
		}

		return typeInfo;
	}

	private static bool IsTypeMatch(TypeInfo paramType, CorElementType argType, CorDebugValue argValue)
	{
		// Map SignatureTypeCode to CorElementType for comparison
		var expectedCorType = SignatureTypeCodeToCorElementType(paramType.TypeCode);

		if (expectedCorType == argType)
			return true;
		if (argType == CorElementType.ValueType || argType == CorElementType.Class)
		{
			var exactTypeName = GetExactTypeName(argValue);
			if (expectedCorType == CorElementType.R4 && exactTypeName == "System.Single")
				return true;
			if (expectedCorType == CorElementType.R8 && exactTypeName == "System.Double")
				return true;
		}

		// Handle special cases like class types, generic types, etc.
		if (paramType.TypeCode == SignatureTypeCode.TypeHandle)
		{
			// Need to compare actual type tokens or class information
			// You might need to get the class from argValue and compare
			if (argValue.ExactType != null)
			{
				// Compare class tokens if available
				var argClass = argValue.ExactType.Class;
				// Compare paramType.Token with the class token
				// This requires converting the compressed token format
			}
		}

		return false;
	}

	private static CorElementType GetArgElementType(CorDebugValue argValue)
	{
		var argType = argValue.Type;
		if (argType == CorElementType.Class || argType == CorElementType.ValueType)
		{
			var exactTypeName = GetExactTypeName(argValue);
			var mapped = exactTypeName switch
			{
				"System.Boolean" => CorElementType.Boolean,
				"System.Char" => CorElementType.Char,
				"System.SByte" => CorElementType.I1,
				"System.Byte" => CorElementType.U1,
				"System.Int16" => CorElementType.I2,
				"System.UInt16" => CorElementType.U2,
				"System.Int32" => CorElementType.I4,
				"System.UInt32" => CorElementType.U4,
				"System.Int64" => CorElementType.I8,
				"System.UInt64" => CorElementType.U8,
				"System.Single" => CorElementType.R4,
				"System.Double" => CorElementType.R8,
				_ => argType
			};
			return mapped;
		}
		return argType;
	}

	private static int GetMethodParameterMatchScore(CorDebugFunction method, CorDebugValue?[] args)
	{
		try
		{
			var metaDataImport = method.Class.Module.GetMetaDataInterface().MetaDataImport;
			var methodProps = metaDataImport.GetMethodProps(method.Token);
			var parameterTypes = ParseMethodSignatureWithMetadata(methodProps.ppvSigBlob, methodProps.pcbSigBlob);
			if (parameterTypes.Count != args.Length)
				return -1;

			var score = 0;
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i] == null)
					continue;

				var argType = GetArgElementType(args[i]!);
				var paramType = parameterTypes[i];
				var expectedCorType = SignatureTypeCodeToCorElementType(paramType.TypeCode);

				var compat = GetTypeCompatibilityScore(expectedCorType, paramType, argType, args[i]!);
				if (compat < 0)
					return -1;
				score += compat;
			}

			return score;
		}
		catch
		{
			return -1;
		}
	}

	private static int GetTypeCompatibilityScore(CorElementType expectedCorType, TypeInfo paramType, CorElementType argType, CorDebugValue argValue)
	{
		if (expectedCorType == argType)
			return 0;

		if (IsNumericType(expectedCorType) && IsNumericType(argType))
		{
			return GetNumericCompatibilityScore(expectedCorType, argType);
		}

		return IsTypeMatch(paramType, argType, argValue) ? 0 : -1;
	}

	private static int GetNumericCompatibilityScore(CorElementType expected, CorElementType actual)
	{
		if (expected == actual)
			return 0;

		if (IsFloatingType(actual))
		{
			// Do not allow implicit float->integral conversions
			if (IsIntegralType(expected))
				return -1;
			if (expected == CorElementType.R8 && actual == CorElementType.R4)
				return 1;
			return -1;
		}

		if (IsIntegralType(actual))
		{
			if (IsFloatingType(expected))
				return 1;
			if (IsIntegralType(expected))
				return IsWideningIntegral(actual, expected) ? 1 : -1;
		}

		return -1;
	}

	private static bool IsNumericType(CorElementType type) => IsIntegralType(type) || IsFloatingType(type);

	private static bool IsFloatingType(CorElementType type) => type is CorElementType.R4 or CorElementType.R8;

	private static bool IsIntegralType(CorElementType type) => type is CorElementType.I1 or CorElementType.U1 or CorElementType.I2 or CorElementType.U2 or CorElementType.I4 or CorElementType.U4 or CorElementType.I8 or CorElementType.U8 or CorElementType.I or CorElementType.U;

	private static bool IsWideningIntegral(CorElementType from, CorElementType to)
	{
		var fromRank = GetIntegralRank(from);
		var toRank = GetIntegralRank(to);
		return fromRank > 0 && toRank > 0 && fromRank <= toRank;
	}

	private static int GetIntegralRank(CorElementType type)
	{
		return type switch
		{
			CorElementType.I1 or CorElementType.U1 => 1,
			CorElementType.I2 or CorElementType.U2 => 2,
			CorElementType.I4 or CorElementType.U4 => 4,
			CorElementType.I8 or CorElementType.U8 => 8,
			CorElementType.I or CorElementType.U => IntPtr.Size,
			_ => -1
		};
	}

	private static string? GetExactTypeName(CorDebugValue argValue)
	{
		var exactType = argValue.ExactType;
		if (exactType == null)
			return null;
		var metaDataImport = exactType.Class.Module.GetMetaDataInterface().MetaDataImport;
		var typeDefProps = metaDataImport.GetTypeDefProps(exactType.Class.Token);
		return typeDefProps.szTypeDef;
	}

	private static CorElementType SignatureTypeCodeToCorElementType(SignatureTypeCode typeCode)
	{
		return typeCode switch
		{
			SignatureTypeCode.Void => CorElementType.Void,
			SignatureTypeCode.Boolean => CorElementType.Boolean,
			SignatureTypeCode.Char => CorElementType.Char,
			SignatureTypeCode.SByte => CorElementType.I1,
			SignatureTypeCode.Byte => CorElementType.U1,
			SignatureTypeCode.Int16 => CorElementType.I2,
			SignatureTypeCode.UInt16 => CorElementType.U2,
			SignatureTypeCode.Int32 => CorElementType.I4,
			SignatureTypeCode.UInt32 => CorElementType.U4,
			SignatureTypeCode.Int64 => CorElementType.I8,
			SignatureTypeCode.UInt64 => CorElementType.U8,
			SignatureTypeCode.Single => CorElementType.R4,
			SignatureTypeCode.Double => CorElementType.R8,
			SignatureTypeCode.String => CorElementType.String,
			SignatureTypeCode.Pointer => CorElementType.Ptr,
			SignatureTypeCode.ByReference => CorElementType.ByRef,
			SignatureTypeCode.TypeHandle => CorElementType.Class, // or ValueType
			SignatureTypeCode.Object => CorElementType.Object,
			SignatureTypeCode.SZArray => CorElementType.SZArray,
			SignatureTypeCode.Array => CorElementType.Array,
			SignatureTypeCode.IntPtr => CorElementType.I,
			SignatureTypeCode.UIntPtr => CorElementType.U,
			SignatureTypeCode.GenericTypeInstance => CorElementType.GenericInst,
			SignatureTypeCode.GenericTypeParameter => CorElementType.Var,
			SignatureTypeCode.GenericMethodParameter => CorElementType.MVar,
			_ => CorElementType.End
		};
	}
}
