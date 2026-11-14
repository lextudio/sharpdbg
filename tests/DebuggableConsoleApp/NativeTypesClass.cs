namespace DebuggableConsoleApp;

public class NativeTypesClass
{
	public nint NativeIntField = 5;
	public nuint NativeUIntField = 6;
	[System.ThreadStatic]
	public static int ThreadStaticField = 7;

	public void Test()
	{
		var marker = 1;
		;
	}
}
