using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ICorDebugSharp;

internal static unsafe class Exports
{
	[StructLayout(LayoutKind.Sequential)]
	private struct DlInfo { public nint Name, Base, SymbolName, SymbolAddress; }
	[DllImport("libdl.so")] private static extern nint dlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
	[DllImport("libdl.so")] private static extern nint dlsym(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
	[DllImport("libdl.so")] private static extern int dladdr(nint address, out DlInfo info);
	[DllImport("libc.so")] private static extern uint geteuid();

	internal static int StartCore()
	{
		try
		{
			var package = Environment.GetEnvironmentVariable("OD_ANDROID_DEBUG_PACKAGE");
			if (string.IsNullOrEmpty(package)) throw new InvalidOperationException("Missing Android debugger package name.");
			var userId = geteuid() / 100000;
			var configPath = Path.Combine("/data/user", userId.ToString(System.Globalization.CultureInfo.InvariantCulture), package, "files", "sharpdbg", "session");
			var configuration = File.ReadAllLines(configPath);
			if (configuration.Length != 3) throw new InvalidDataException("Invalid Android debugger configuration.");
			var portText = configuration[0];
			var token = configuration[1];
			var modules = configuration[2];
			if (!int.TryParse(portText, out int port) || port is < 1 or > 65535 || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(modules))
				throw new InvalidOperationException("Missing Android debugger port, session token or module directory.");
			new Thread(() => Serve(port, token, modules)) { IsBackground = true, Name = "Android DAP" }.Start();
			return 0;
		}
		catch (Exception ex)
		{
			Profiler.Log(6, "SharpDbg", ex.Message);
			return -1;
		}
	}

	private static void Serve(int port, string token, string modules)
	{
		TcpListener? listener = null;
		try
		{
			listener = new TcpListener(IPAddress.Loopback, port);
			listener.Start();
			using var client = listener.AcceptTcpClient();
			using var stream = client.GetStream();
			new AndroidDapServer(stream, token, modules, CreateDebugInterface, () => Profiler.StartupGate.Set()).RunAsync().GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			Profiler.Log(6, "SharpDbg", ex.ToString());
		}
		finally
		{
			Profiler.StartupGate.Set();
			listener?.Stop();
		}
	}

	private static ICorDebug CreateDebugInterface()
	{
		const int now = 2, noLoad = 4;
		nint core = dlopen("libcoreclr.so", now | noLoad);
		nint symbol = core == 0 ? 0 : dlsym(core, "coreclr_initialize");
		if (symbol == 0 || dladdr(symbol, out var info) == 0)
			throw new InvalidOperationException("The target CoreCLR is not loaded.");
		nint dbi = dlopen("libmscordbi.so", now);
		if (dbi == 0) throw new InvalidOperationException("Could not load the target debugging library.");
		nint entry = dlsym(dbi, "DllMain"), create = dlsym(dbi, "CoreCLRCreateCordbObject3");
		if (entry == 0 || create == 0) throw new InvalidOperationException("The target debugging library is missing required exports.");
		if (((delegate* unmanaged<nint, uint, nint, int>)entry)(0, 1, 0) == 0)
			throw new InvalidOperationException("Could not initialize the target debugging library.");
		nint result = 0;
		int hr = ((delegate* unmanaged<int, uint, nint, nint, nint, nint*, int>)create)(3, (uint)Environment.ProcessId, 0, 0, info.Base, &result);
		Marshal.ThrowExceptionForHR(hr);
		return ComInterfaceMarshaller<ICorDebug>.ConvertToManaged((void*)result)
			?? throw new InvalidOperationException("The debugging library returned no ICorDebug interface.");
	}
}
