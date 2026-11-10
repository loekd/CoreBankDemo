namespace CoreBankDemo.DemoRunner.Infrastructure;

/// <summary>
/// Reads which branch a checkout is on straight from its <c>HEAD</c> file, so the console can name
/// it in its title without spawning <c>git</c>.
/// </summary>
public static class GitBranchReader
{
    private const string BranchPrefix = "ref: refs/heads/";
    private const string GitdirPrefix = "gitdir:";
    private const int ShortCommitIdLength = 7;

    /// <summary>
    /// The branch checked out at <paramref name="repositoryRoot"/>, the short commit id when HEAD
    /// is detached, or <see langword="null"/> when the directory is not a readable checkout.
    /// </summary>
    public static string? Read(string repositoryRoot)
    {
        try
        {
            var gitPath = Path.Combine(repositoryRoot, ".git");
            if (File.Exists(gitPath))
            {
                // A linked worktree's .git is a file pointing at its real git directory.
                var pointer = File.ReadAllText(gitPath).Trim();
                if (!pointer.StartsWith(GitdirPrefix, StringComparison.Ordinal))
                {
                    return null;
                }

                gitPath = Path.GetFullPath(pointer[GitdirPrefix.Length..].Trim(), repositoryRoot);
            }

            var headPath = Path.Combine(gitPath, "HEAD");
            if (!File.Exists(headPath))
            {
                return null;
            }

            var head = File.ReadAllText(headPath).Trim();
            if (head.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                return head[BranchPrefix.Length..];
            }

            return head.Length > ShortCommitIdLength ? head[..ShortCommitIdLength] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The title is decoration; an unreadable checkout must never stop the console.
            return null;
        }
    }
}
