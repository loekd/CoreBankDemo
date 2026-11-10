using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public sealed class GitBranchReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("demo-runner-git-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Read_HeadOnABranch_IsTheBranchNameWithItsSlashes()
    {
        WriteHead(Path.Combine(_root, ".git"), "ref: refs/heads/feature/lock-release\n");

        GitBranchReader.Read(_root).Should().Be("feature/lock-release");
    }

    [Fact]
    public void Read_DetachedHead_IsTheShortCommitId()
    {
        WriteHead(Path.Combine(_root, ".git"), "306167f0c1d2e3f4a5b6c7d8e9f00112233445566\n");

        GitBranchReader.Read(_root).Should().Be("306167f");
    }

    [Fact]
    public void Read_InALinkedWorktree_FollowsTheGitdirPointer()
    {
        var gitDirectory = Path.Combine(_root, "elsewhere", "worktrees", "talk");
        WriteHead(gitDirectory, "ref: refs/heads/main\n");
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktree")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDirectory}\n");

        GitBranchReader.Read(worktree).Should().Be("main");
    }

    [Fact]
    public void Read_OutsideARepository_IsNull()
    {
        GitBranchReader.Read(_root).Should().BeNull();
    }

    private static void WriteHead(string gitDirectory, string content)
    {
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), content);
    }
}
