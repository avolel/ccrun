using ccrun;

namespace CCRun.Tests;

// Integration tests that spawn real child processes. The child inherits the
// test host's console, so we assert on exit codes (the FR-1.4 contract), not
// on captured child stdout.
public class RunCommandTests
{
    private static int Run(params string[] args) =>
        Cli.Run(args, new StringWriter(), new StringWriter());

    [Fact]
    public void Run_TrueCommand_ReturnsZero() =>
        Assert.Equal(0, Run("run", "true"));

    [Fact]
    public void Run_FalseCommand_ReturnsNonZero() =>
        Assert.NotEqual(0, Run("run", "false"));

    [Fact]
    public void Run_PropagatesChildExitCode() =>
        Assert.Equal(3, Run("run", "/bin/sh", "-c", "exit 3"));

    [Fact]
    public void Run_MissingBinary_ReturnsCommandNotFound() =>
        Assert.Equal(ExitCodes.CommandNotFound, Run("run", "ccrun-no-such-binary-xyz"));
}
