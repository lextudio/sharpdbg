using System.Runtime.CompilerServices;

namespace RuntimeAsyncOptimizedLib;

// Built optimized: FirstCallsCoreLibAsync's first IL instruction is the call to Environment.ProcessorCount's getter, so a step into it
// leaves this runtime-async frame at IL 0 below a CoreLib frame that is not AsyncHelpers - which is not the thunk of an unawaited call
public static class OptimizedIl0CallClass
{
	public static async Task TestAsync()
	{
		var count = await FirstCallsCoreLibAsync();
		GC.KeepAlive(count);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static async Task<int> FirstCallsCoreLibAsync()
	{
		var count = Environment.ProcessorCount;
		await Task.Yield();
		return count;
	}
}
