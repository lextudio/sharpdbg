namespace SharpDbg.Cli.Tests.Helpers;

public static class GitRoot
{
	private static string? _gitRoot;
	public static string GetGitRootPath()
	{
		if (_gitRoot is not null) return _gitRoot;
		var currentDirectory = Directory.GetCurrentDirectory();
		var gitRoot = currentDirectory;
		// .git is a directory in a normal clone, but a file holding a gitdir: pointer when this
		// repo is checked out as a submodule or linked worktree - accept both, otherwise the walk
		// sails past the real root and resolves fixture paths against an unrelated outer repo.
		while (!IsGitRoot(gitRoot))
		{
			gitRoot = Path.GetDirectoryName(gitRoot); // parent directory
			if (string.IsNullOrWhiteSpace(gitRoot))
			{
				throw new Exception("Could not find git root");
			}
		}

		_gitRoot = gitRoot;
		return _gitRoot;
	}

	private static bool IsGitRoot(string directory)
	{
		var dotGit = Path.Combine(directory, ".git");
		return Directory.Exists(dotGit) || File.Exists(dotGit);
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
