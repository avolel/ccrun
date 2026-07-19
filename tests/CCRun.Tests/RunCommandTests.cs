using ccrun;

namespace CCRun.Tests;

// Parent/host-stage behaviour through the Cli.Run seam. Anything that actually
// unshares lives in NamespaceIntegrationTests and runs out of process: unshare(2)
// mutates its caller, which in-process would be the xunit test host. What is left
// here is the paths that return *before* any unshare — argument parsing and rootfs
// validation — so this class needs no privileges at all.
public class RunCommandTests
{
    // euid 0 => running as root (e.g. under sudo).
    internal static bool IsRoot => Libc.Geteuid() == 0;

    // ccrun now always creates a user namespace, so the integration tests need either
    // root or a kernel that lets unprivileged users create one. Both knobs below are
    // opt-out gates: Debian/Ubuntu ship the unprivileged_userns_clone toggle, and
    // max_user_namespaces caps how many a user may own (0 disables them outright).
    internal static bool IsUserNsAvailable => IsRoot || UnprivilegedUsernsEnabled();

    private static bool UnprivilegedUsernsEnabled()
    {
        const string knob = "/proc/sys/kernel/unprivileged_userns_clone";
        if (File.Exists(knob) && File.ReadAllText(knob).Trim() == "0")
            return false;

        const string max = "/proc/sys/user/max_user_namespaces";
        if (File.Exists(max) && int.TryParse(File.ReadAllText(max).Trim(), out int n) && n <= 0)
            return false;

        return true;
    }

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
}
