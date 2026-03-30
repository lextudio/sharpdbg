namespace SharpDbg.Cli.Tests.Helpers;

public static class GitRoot
{
	private static string? _gitRoot;
	public static string GetGitRootPath()
	{
		if (_gitRoot is not null) return _gitRoot;
		var searchRoots = new[]
		{
			Directory.GetCurrentDirectory(),
			AppContext.BaseDirectory
		}.Where(path => string.IsNullOrWhiteSpace(path) is false).Distinct();

		foreach (var root in searchRoots)
		{
			var gitRoot = root;
			while (string.IsNullOrWhiteSpace(gitRoot) is false)
			{
				if (IsGitRoot(gitRoot))
				{
					_gitRoot = gitRoot;
					return _gitRoot;
				}

				gitRoot = Path.GetDirectoryName(gitRoot);
			}
		}

		throw new Exception("Could not find git root");
	}

	private static bool IsGitRoot(string path)
	{
		var gitPath = Path.Combine(path, ".git");
		return Directory.Exists(gitPath) || File.Exists(gitPath);
	}
}

public static class PathExtensions
{
	extension(Path)
	{
		public static string JoinFromGitRoot(params ReadOnlySpan<string?> paths)
		{
			return Path.Join([GitRoot.GetGitRootPath(), ..paths]);
		}
	}
}
