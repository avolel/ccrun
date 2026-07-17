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

    // Repo-root-relative alpine rootfs, located by walking up from the test binary
    // until the ALPINE_FS_ROOT marker is found. Null if not present (tests skip).
    private static string? FindAlpineRootfs()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "alpine-rootfs");
            if (File.Exists(Path.Combine(candidate, "ALPINE_FS_ROOT")) &&
                File.Exists(Path.Combine(candidate, "bin", "busybox")))
                return candidate;
        }
        return null;
    }

    [SkippableFact]
    public void Chroot_LandsInRootfs_MarkerVisible()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // The marker is only reachable at / if the new root is the Alpine tree,
        // reached via an in-rootfs busybox (FR-3.2/3.3).
        var (code, _) = Run("run", "--rootfs", rootfs!,
            "/bin/busybox", "sh", "-c", "[ -f /ALPINE_FS_ROOT ]");
        Assert.Equal(0, code);
    }

    [SkippableFact]
    public void Chroot_CannotEscapeAboveRoot()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // `cd ..` from / stays at the root; the marker is still there (FR-3.2).
        var (code, _) = Run("run", "--rootfs", rootfs!,
            "/bin/busybox", "sh", "-c", "cd .. && [ -f /ALPINE_FS_ROOT ]");
        Assert.Equal(0, code);
    }

    [SkippableFact]
    public void Chroot_MissingCommandInRootfs_ReturnsNotFound()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // Exercises the execvp failure mapping end-to-end.
        var (code, _) = Run("run", "--rootfs", rootfs!, "/no/such/bin");
        Assert.Equal(ExitCodes.CommandNotFound, code);
    }

    // The two Phase 4 tests below assert on exit codes rather than output: the
    // rootfs path hands off with execvp, which replaces the process image, so the
    // command's stdout never reaches our StringWriter seam.

    [SkippableFact]
    public void PidNamespace_ContainerShellIsPidOne()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWPID)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // In a fresh PID namespace the exec'd shell is the namespace's init, so $$ is 1
        // (FR-4.1). Without CLONE_NEWPID it would be some arbitrary host PID.
        var (code, _) = Run("run", "--rootfs", rootfs!,
            "/bin/busybox", "sh", "-c", "[ \"$$\" = 1 ]");
        Assert.Equal(0, code);
    }

    [SkippableFact]
    public void PrivateProc_OnlyContainerProcessesVisible()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWPID)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // A private procfs over a fresh PID namespace lists only our own handful of
        // PIDs — the shell plus the ls/wc pipeline (FR-4.2, FR-4.5). A host /proc, or
        // no /proc at all, would count hundreds or zero respectively, so the bounds
        // catch both failure modes.
        var (code, _) = Run("run", "--rootfs", rootfs!,
            "/bin/busybox", "sh", "-c",
            "c=$(ls -d /proc/[0-9]* 2>/dev/null | wc -l); [ \"$c\" -ge 1 ] && [ \"$c\" -le 4 ]");
        Assert.Equal(0, code);
    }
}
