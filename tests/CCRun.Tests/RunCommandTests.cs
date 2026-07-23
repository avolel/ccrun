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

    /// <summary>
    /// True when cgroup v2 is mounted and some ancestor of our own cgroup will let us
    /// create a child with the memory and cpu controllers — the same search Cgroup.Create
    /// does, which is the honest precondition for the Phase 6 tests. The probe cgroup is
    /// disposed immediately, so nothing is left behind.
    /// </summary>
    internal static bool IsCgroupV2Delegated => s_cgroupV2Delegated.Value;

    // Cached: the probe creates and removes a real directory, and test classes run in
    // parallel, so asking once avoids two probes racing over the same name.
    private static readonly Lazy<bool> s_cgroupV2Delegated = new(() =>
    {
        using var probe = Cgroup.Create(
            new ResourceLimits(1L << 30, 1.0), Environment.ProcessId, TextWriter.Null);
        return probe is not null;
    });

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

    [Fact]
    public void Run_BadMemoryValue_ReturnsUsageError()
    {
        // Limit values are validated at parse time, so a typo is a usage error and no
        // namespace or cgroup is created — reachable on any host.
        var (code, err) = Run("run", "--memory", "bogus", "true");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("invalid --memory value", err);
    }

    [Fact]
    public void Run_BadCpusValue_ReturnsUsageError()
    {
        var (code, err) = Run("run", "--cpus", "0", "true");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("invalid --cpus value", err);
    }
}
