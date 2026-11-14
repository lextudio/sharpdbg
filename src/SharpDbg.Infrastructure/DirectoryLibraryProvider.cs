using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure;

/// Hands dbgshim mscordbi and the DAC from a fixed directory instead of the target runtime's own directory
[GeneratedComClass]
public sealed partial class DirectoryLibraryProvider(string directory) : ICLRDebuggingLibraryProvider3
{
	public int TryProvideWindowsLibrary(string pwszFileName, string pwszRuntimeModule, LIBRARY_PROVIDER_INDEX_TYPE indexType, uint dwTimestamp, uint dwSizeOfImage, out IntPtr phModule)
	{
		phModule = IntPtr.Zero;
		return unchecked((int)0x80004001); // E_NOTIMPL
	}

	public int TryProvideUnixLibrary(string pwszFileName, string pwszRuntimeModule, LIBRARY_PROVIDER_INDEX_TYPE indexType, ref byte pbBuildId, int iBuildIdSize, out IntPtr ppResolvedModulePath)
	{
		var path = Path.Combine(directory, Path.GetFileName(pwszFileName));
		if (!File.Exists(path))
		{
			ppResolvedModulePath = IntPtr.Zero;
			return unchecked((int)0x80004005); // E_FAIL
		}
		// dbgshim frees the returned path
		ppResolvedModulePath = Marshal.StringToCoTaskMemUni(path);
		return 0;
	}
}
