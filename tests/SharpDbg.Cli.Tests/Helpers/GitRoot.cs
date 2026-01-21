using System;
using System.IO;

namespace SharpDbg.Cli.Tests.Helpers;

public static class GitRoot
{
	private static string? _gitRoot;

	public static string GetGitRootPath()
	{
		if (_gitRoot is not null)
			return _gitRoot;

		var currentDirectory = Directory.GetCurrentDirectory();
		var gitRoot = currentDirectory;
		while (!string.IsNullOrEmpty(gitRoot) && !Directory.Exists(System.IO.Path.Combine(gitRoot, ".git")))
		{
			gitRoot = System.IO.Path.GetDirectoryName(gitRoot);
		}

		if (string.IsNullOrWhiteSpace(gitRoot))
			throw new InvalidOperationException("Could not find git root");

		_gitRoot = gitRoot!;
		return _gitRoot;
	}
}

public static class PathExtensions
{
	extension(Path)
	{
		public static string JoinFromGitRoot(params string[] paths)
		{
			if (paths is null || paths.Length == 0)
				return GitRoot.GetGitRootPath();

			var segments = new string[paths.Length + 1];
			segments[0] = GitRoot.GetGitRootPath();
			Array.Copy(paths, 0, segments, 1, paths.Length);
			return System.IO.Path.Combine(segments);
		}
	}
}
