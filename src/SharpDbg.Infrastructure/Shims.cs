using System;
using System.Collections.Generic;
#if !NET10_0_OR_GREATER
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
	internal static class IsExternalInit
	{
	}

	[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
	internal sealed class CompilerFeatureRequiredAttribute : Attribute
	{
		public CompilerFeatureRequiredAttribute(string featureName)
		{
			FeatureName = featureName;
		}

		public string FeatureName { get; }

		public bool IsOptional { get; init; }
	}

	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
	internal sealed class RequiredMemberAttribute : Attribute
	{
	}
}

namespace System.Runtime.InteropServices
{
	internal static class NativeLibrary
	{
		public static IntPtr Load(string libraryPath)
		{
			if (string.IsNullOrWhiteSpace(libraryPath))
			{
				throw new ArgumentException("Library path cannot be null or empty.", nameof(libraryPath));
			}

			var handle = LoadLibrary(libraryPath);
			if (handle == IntPtr.Zero)
			{
				throw new DllNotFoundException(libraryPath);
			}

			return handle;
		}

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibrary(string lpFileName);
	}
}
#endif

namespace SharpDbg.Infrastructure.Debugger
{
	internal static class EnumerableCompat
	{
		public static IEnumerable<(int index, T item)> WithIndex<T>(this IEnumerable<T> source)
		{
			if (source == null)
			{
				throw new ArgumentNullException(nameof(source));
			}

			var index = 0;
			foreach (var item in source)
			{
				yield return (index, item);
				index++;
			}
		}
	}
}
