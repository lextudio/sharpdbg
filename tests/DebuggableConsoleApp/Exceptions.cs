namespace DebuggableConsoleApp;

public static class Exceptions
{
	public static void Test()
	{
		var test = true;
		try
		{
			if (test)
			{
				throw new InvalidOperationException("Test exception");
			}
		}
		catch (Exception e)
		{
			;
		}
	}
}
