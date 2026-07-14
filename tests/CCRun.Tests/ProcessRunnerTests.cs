using ccrun;

namespace CCRun.Tests;

// The Phase 1 exit-code contract, exercised directly against ProcessRunner
// (no namespaces, no root). The child inherits the test host's console, so we
// assert on exit codes, not captured child stdout.
public class ProcessRunnerTests
{
    private static int Run(string command, params string[] args) =>
        ProcessRunner.Run(command, args, new StringWriter());

    [Fact]
    public void TrueCommand_ReturnsZero() =>
        Assert.Equal(0, Run("true"));

    [Fact]
    public void FalseCommand_ReturnsNonZero() =>
        Assert.NotEqual(0, Run("false"));

    [Fact]
    public void PropagatesChildExitCode() =>
        Assert.Equal(3, Run("/bin/sh", "-c", "exit 3"));

    [Fact]
    public void MissingBinary_ReturnsCommandNotFound() =>
        Assert.Equal(ExitCodes.CommandNotFound, Run("ccrun-no-such-binary-xyz"));
}
