using ccrun;

namespace CCRun.Tests;

// Full parent -> re-exec -> child pipeline. unshare(CLONE_NEWUTS) needs
// CAP_SYS_ADMIN, so every test here is gated on root and skips for a normal
// non-root dev/CI (keeping `dotnet test` green). Run as root with
// `sudo dotnet test` to exercise them.
public class NamespaceIntegrationTests
{
    private static (int code, string err) Run(params string[] args)
    {
        var stderr = new StringWriter();
        int code = Cli.Run(args, new StringWriter(), stderr);
        return (code, stderr.ToString());
    }

    [SkippableFact]
    public void FullPipeline_TrueCommand_ReturnsZero()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");

        // unshare + re-exec + sethostname + spawn, all the way through.
        var (code, err) = Run("run", "true");
        Assert.Equal(0, code);
        Assert.Equal("", err);
    }

    [SkippableFact]
    public void Hostname_AppliedInsideContainer()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");

        // Read the hostname to a file rather than asserting the live hostname:
        // robust to whatever UTS-namespace state the test runner is in.
        string tmp = Path.GetTempFileName();
        try
        {
            var (code, _) = Run("run", "--hostname", "ccrun-test",
                "/bin/sh", "-c", $"hostname > {tmp}");
            Assert.Equal(0, code);
            Assert.Equal("ccrun-test", File.ReadAllText(tmp).Trim());
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
