using System.Diagnostics;
using ccrun;

namespace CCRun.Tests;

// Full parent -> re-exec -> child pipeline, exercised end to end against the real
// CCRun binary. Since Phase 5 ccrun creates a user namespace first and does the rest
// with the capabilities that grants, so plain `dotnet test` exercises all of this
// unprivileged. The gate is therefore only "can this machine create a user
// namespace" — root also qualifies — and tests skip on kernels where unprivileged
// user namespaces are switched off.
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
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");

        // unshare + re-exec + sethostname + spawn, all the way through.
        var (code, _, err) = Run("run", "true");
        Assert.Equal(0, code);
        Assert.Equal("", err);
    }

    [SkippableFact]
    public void Hostname_AppliedInsideContainer()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");

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
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
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
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
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
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // Exercises the execvp failure mapping end-to-end.
        var (code, _, _) = Run("run", "--rootfs", rootfs!, "/no/such/bin");
        Assert.Equal(ExitCodes.CommandNotFound, code);
    }

    [SkippableFact]
    public void PidNamespace_ContainerShellIsPidOne()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
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
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
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
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
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

    [SkippableFact]
    public void Rootless_ContainerRootMapsToInvokingUser()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
        string? rootfs = FindAlpineRootfs();
        Skip.If(rootfs is null, "alpine-rootfs not present");

        // FR-5.2: inside the user namespace the uid map makes us uid 0, whoever ran
        // ccrun. Before the map is written the kernel reports the overflow uid (65534),
        // so this also proves the map was applied and not merely attempted.
        var (code, stdout, _) = Run("run", "--rootfs", rootfs!, "/bin/busybox", "id", "-u");
        Assert.Equal(0, code);
        Assert.Equal("0", stdout.Trim());
    }

    [SkippableFact]
    public void Rootless_HostSeesProcessOwnedByInvokingUser()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
        Skip.If(RunCommandTests.IsRoot, "meaningful only for a non-root invoker");

        // FR-5.3, the acceptance test: a process that is root *inside* the container must
        // appear on the host owned by the unprivileged user who launched it. That can only
        // be observed while the container is alive, so start a long sleep, find it on the
        // host by its distinctive duration, and read the owner the kernel reports.
        //
        // No --rootfs here on purpose: this path uses the host's /bin/sleep and the
        // lightweight UTS+user namespace setup, which is all FR-5.3 needs.
        string marker = (DateTime.UtcNow.Ticks % 100000 + 100000).ToString();

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "CCRun.dll"), "run", "/bin/sleep", marker,
                 })
            psi.ArgumentList.Add(a);

        using var container = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ccrun");
        try
        {
            int? pid = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (pid is null && !container.HasExited && DateTime.UtcNow < deadline)
            {
                pid = FindHostPidByCmdline(marker);
                if (pid is null)
                    Thread.Sleep(50);
            }
            Assert.True(pid is not null, "container sleep never appeared in the host's /proc");

            // /proc/<pid>/status "Uid:" is real/effective/saved/fs; the effective uid is
            // the second field. The host resolves it outside the namespace, which is the
            // whole point — the container's root is our own uid out here.
            string status = File.ReadAllText($"/proc/{pid}/status");
            string uidLine = status.Split('\n').First(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            uint effectiveUid = uint.Parse(uidLine.Split('\t', StringSplitOptions.RemoveEmptyEntries)[2]);

            Assert.Equal(Libc.Geteuid(), effectiveUid);
            Assert.NotEqual(0u, effectiveUid);
        }
        finally
        {
            if (!container.HasExited)
                container.Kill(entireProcessTree: true);
            container.WaitForExit();
        }
    }

    // ---- Phase 6: cgroup v2 resource limits -------------------------------------------
    //
    // These read the container's cgroup from the *host* side. That is deliberate: the
    // container has no cgroup namespace and no /sys mount of its own, so the honest place
    // to observe the limits is /sys/fs/cgroup on the host. The container's own
    // /proc/self/cgroup tells us which directory to look in — without --rootfs it sees the
    // host's /proc, so the path it reports is the real one.

    private static Process StartContainer(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CCRun.dll"));
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to start ccrun");
    }

    // "0::/user.slice/.../ccrun-1234" -> "/sys/fs/cgroup/user.slice/.../ccrun-1234".
    private static string CgroupDirectoryOf(string procSelfCgroup)
    {
        string line = procSelfCgroup.Split('\n').First(l => l.StartsWith("0::", StringComparison.Ordinal));
        return "/sys/fs/cgroup" + line[3..].Trim();
    }

    [SkippableFact]
    public void Cgroup_ContainerIsInItsOwnCgroup_WithTheRequestedLimits()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
        Skip.IfNot(RunCommandTests.IsCgroupV2Delegated, "requires a delegated cgroup v2 subtree with cpu+memory");

        // FR-6.1/6.2/6.3: the container runs in a ccrun-owned cgroup carrying the limits.
        // The limits must already be in place by the time the command runs, so the command
        // reports its own cgroup and then idles while we inspect that directory.
        string report = Path.Combine(Path.GetTempPath(), $"ccrun-cgroup-{Guid.NewGuid():N}");
        string? dir = null;
        using var container = StartContainer(
            "run", "--memory", "128m", "--cpus", "0.5",
            "/bin/sh", "-c", $"cat /proc/self/cgroup > {report}; sleep 30");
        try
        {
            // Poll for a *non-empty* file: `cat > file` creates it before it writes, so
            // existence alone would race us into reading nothing.
            string reported = "";
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (reported.Length == 0 && !container.HasExited && DateTime.UtcNow < deadline)
            {
                reported = File.Exists(report) ? File.ReadAllText(report) : "";
                if (reported.Length == 0)
                    Thread.Sleep(50);
            }
            Assert.True(reported.Length > 0, "container never reported its cgroup");

            dir = CgroupDirectoryOf(reported);
            Assert.Matches(@"/ccrun-\d+$", dir);
            Assert.Equal("134217728", File.ReadAllText(Path.Combine(dir, "memory.max")).Trim());
            Assert.Equal("50000 100000", File.ReadAllText(Path.Combine(dir, "cpu.max")).Trim());
        }
        finally
        {
            if (!container.HasExited)
                container.Kill(entireProcessTree: true);
            container.WaitForExit();
            File.Delete(report);

            // The kill above is a SIGKILL, so ccrun's own cleanup never got to run and the
            // (now empty) directory survives — see Cgroup_RemovedAfterContainerExits for
            // the case ccrun is responsible for. Tidy up after ourselves rather than
            // leaving one behind on every test run. rmdir fails while the kernel is still
            // tearing the container's processes down, hence the short retry.
            for (int attempt = 0; dir is not null && Directory.Exists(dir) && attempt < 20; attempt++)
            {
                try { Directory.Delete(dir); }
                catch (IOException) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { break; }
            }
        }
    }

    [SkippableFact]
    public void Cgroup_RemovedAfterContainerExits()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
        Skip.IfNot(RunCommandTests.IsCgroupV2Delegated, "requires a delegated cgroup v2 subtree with cpu+memory");

        // FR-6.5: the cgroup lives exactly as long as the container. The command prints the
        // directory it was placed in and exits; by the time ccrun returns, its own reap has
        // happened and the rmdir with it.
        var (code, stdout, _) = Run("run", "--memory", "128m", "/bin/sh", "-c", "cat /proc/self/cgroup");
        Assert.Equal(0, code);

        string dir = CgroupDirectoryOf(stdout);
        Assert.Matches(@"/ccrun-\d+$", dir);
        Assert.False(Directory.Exists(dir), $"cgroup {dir} was left behind");
    }

    [SkippableFact]
    public void Cgroup_MemoryLimitIsEnforced_ProcessKilled()
    {
        Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces");
        Skip.IfNot(RunCommandTests.IsCgroupV2Delegated, "requires a delegated cgroup v2 subtree with cpu+memory");

        // FR-6.2, the acceptance test: a container that keeps allocating past its cap is
        // killed rather than allowed to eat the host. A shell variable is grown a megabyte
        // at a time, which allocates in the container's own address space and so charges
        // to its cgroup.
        using var container = StartContainer(
            "run", "--memory", "16m",
            "/bin/sh", "-c", "x=\"\"; while :; do x=\"$x$(head -c 1000000 /dev/zero | tr '\\0' a)\"; done");
        try
        {
            // Bounded: if the kernel lets the container swap instead of killing it (swap
            // accounting compiled out, so ccrun's memory.swap.max=0 never took), this would
            // otherwise thrash forever. That is a host property, not a ccrun bug, so skip.
            Skip.IfNot(container.WaitForExit(120_000), "container was not OOM-killed (no swap accounting on this host?)");

            // 128 + SIGKILL, straight out of WaitForExitCode's signal branch.
            Assert.Equal(137, container.ExitCode);
        }
        finally
        {
            if (!container.HasExited)
                container.Kill(entireProcessTree: true);
            container.WaitForExit();
        }
    }

    // Scans the host's /proc for a process whose command line contains `needle`. Entries
    // race with process exit, so unreadable ones are skipped rather than thrown on.
    private static int? FindHostPidByCmdline(string needle)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid))
                continue;
            try
            {
                if (File.ReadAllText($"{dir}/cmdline").Replace('\0', ' ').Contains(needle))
                    return pid;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
