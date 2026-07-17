using System.Diagnostics;
using ccrun;

namespace CCRun.Tests;

// Full parent -> re-exec -> child pipeline, exercised end to end against the real
// CCRun binary. Creating namespaces needs CAP_SYS_ADMIN, so every test here is
// gated on root and skips for a normal non-root dev/CI (keeping `dotnet test`
// green). Run them with `sudo dotnet test`, or without sudo inside an
// unprivileged user namespace:
//
//     unshare --user --map-root-user dotnet test
//
// These run ccrun **out of process** rather than through the Cli.Run seam the
// other test classes use, and that is not incidental. `run` calls unshare(2),
// which permanently mutates the calling process — in-process, that would be the
// xunit test host. A UTS namespace is survivable, but unshare(CLONE_NEWPID) is
// not: it leaves the caller unable to create threads, and once the new
// namespace's PID 1 exits, every later fork(2) from that process fails with
// ENOMEM. One in-process rootfs test would wedge the test host and fail every
// test that ran after it. Spawning a fresh process per run keeps each test's
// namespace mutations to itself.
//
// Running out of process also means stdout is a real inherited fd, so these can
// assert on what the container actually printed — even across the execvp hand-off,
// which replaces the process image and would defeat an in-process StringWriter.
public class NamespaceIntegrationTests
{
    private static (int code, string stdout, string stderr) Run(params string[] args)
    {
        // CCRun.dll sits next to the test assembly via the project reference. Launch it
        // through the muxer; ReExec handles being re-invoked under `dotnet` itself.
        string dll = Path.Combine(AppContext.BaseDirectory, "CCRun.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ccrun");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }

    [SkippableFact]
    public void FullPipeline_TrueCommand_ReturnsZero()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");

        // unshare + re-exec + sethostname + spawn, all the way through.
        var (code, _, err) = Run("run", "true");
        Assert.Equal(0, code);
        Assert.Equal("", err);
    }

    [SkippableFact]
    public void Hostname_AppliedInsideContainer()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWUTS)");

        var (code, stdout, _) = Run("run", "--hostname", "ccrun-test", "/bin/sh", "-c", "hostname");
        Assert.Equal(0, code);
        Assert.Equal("ccrun-test", stdout.Trim());
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
        var (code, _, _) = Run("run", "--rootfs", rootfs!,
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
        var (code, _, _) = Run("run", "--rootfs", rootfs!,
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
        var (code, _, _) = Run("run", "--rootfs", rootfs!, "/no/such/bin");
        Assert.Equal(ExitCodes.CommandNotFound, code);
    }

    [SkippableFact]
    public void PidNamespace_ContainerShellIsPidOne()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWPID)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // In a fresh PID namespace the exec'd shell is the namespace's init, so $$ is 1
        // (FR-4.1). Without CLONE_NEWPID it would be some arbitrary host PID.
        var (code, stdout, _) = Run("run", "--rootfs", rootfs!,
            "/bin/busybox", "sh", "-c", "echo $$");
        Assert.Equal(0, code);
        Assert.Equal("1", stdout.Trim());
    }

    [SkippableFact]
    public void PrivateProc_OnlyContainerProcessesVisible()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWPID)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // A private procfs over a fresh PID namespace lists only our own handful of
        // PIDs — the shell plus the ls/wc pipeline (FR-4.2, FR-4.5). The host's /proc
        // has hundreds of entries, and a missing /proc mount would yield zero, so
        // requiring a small non-zero count catches both failure modes.
        var (code, stdout, _) = Run("run", "--rootfs", rootfs!,
            "/bin/busybox", "sh", "-c", "ls -d /proc/[0-9]* | wc -l");
        Assert.Equal(0, code);
        int pidCount = int.Parse(stdout.Trim());
        Assert.InRange(pidCount, 1, 4);
    }

    [SkippableFact]
    public void MountNamespace_ContainerProcMountNotVisibleOnHost()
    {
        Skip.IfNot(RunCommandTests.IsRoot, "requires root for unshare(CLONE_NEWNS)");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // FR-4.3: the container's /proc must not appear in the host's mount table. This
        // has to be checked while a container is *live*, so the container signals
        // readiness by creating a marker (it has already mounted /proc by the time it
        // execs) and then idles while we read the host's mounts.
        string marker = Path.Combine(rootfs!, "ccrun-mount-test-ready");
        File.Delete(marker);

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "CCRun.dll"), "run", "--rootfs", rootfs!,
                     "/bin/busybox", "sh", "-c", "touch /ccrun-mount-test-ready && sleep 10",
                 })
            psi.ArgumentList.Add(a);

        using var container = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ccrun");
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(marker) && !container.HasExited && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.True(File.Exists(marker), "container never signalled readiness");

            string hostMounts = File.ReadAllText("/proc/self/mounts");
            Assert.DoesNotContain(Path.Combine(rootfs!, "proc"), hostMounts);
        }
        finally
        {
            if (!container.HasExited)
                container.Kill(entireProcessTree: true);
            container.WaitForExit();
            File.Delete(marker);
        }
    }
}
