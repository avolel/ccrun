using ccrun;

namespace CCRun.Tests;

// Parent/host-stage behaviour through the Cli.Run seam. Cases that would need a
// real UTS namespace (which requires CAP_SYS_ADMIN) live in
// NamespaceIntegrationTests; here we cover the paths that fail before unshare
// or via the EPERM hint, so this class stays green for a non-root dev/CI.
public class RunCommandTests
{
    // euid 0 => running as root; unshare(CLONE_NEWUTS) would succeed.
    internal static bool IsRoot => Libc.Geteuid() == 0;

    private static (int code, string err) Run(params string[] args)
    {
        var stderr = new StringWriter();
        int code = Cli.Run(args, new StringWriter(), stderr);
        return (code, stderr.ToString());
    }

    [Fact]
    public void Run_NoCommand_ReturnsUsageError()
    {
        // Parsing fails before any unshare is attempted.
        var (code, err) = Run("run");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("missing command", err);
    }

    [Fact]
    public void Run_UnknownOption_ReturnsUsageError()
    {
        var (code, err) = Run("run", "--bogus", "true");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void Run_MissingRootfs_ReturnsRuntimeError()
    {
        // Rootfs is validated before any unshare, so this is reachable without root.
        var (code, err) = Run("run", "--rootfs", "/no/such/rootfs/xyz", "true");
        Assert.Equal(ExitCodes.RuntimeError, code);
        Assert.Contains("does not exist", err);
    }

    [SkippableFact]
    public void Run_AsNonRoot_FailsWithSudoHint()
    {
        Skip.If(IsRoot, "requires a non-root euid to hit the EPERM hint path");

        // The failed unshare is a no-op, so nothing on the host is mutated.
        var (code, err) = Run("run", "true");
        Assert.Equal(ExitCodes.RuntimeError, code);
        Assert.Contains("sudo", err);
    }
}
