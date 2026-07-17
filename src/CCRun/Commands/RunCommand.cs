using System.Runtime.InteropServices;

namespace ccrun;

/// <summary>
/// `run` (parent/host stage): parses options, optionally validates the target
/// rootfs, creates the container's namespaces via unshare(2), then re-executes
/// ccrun in the hidden init stage (see <see cref="ReExec"/>) which sets the
/// hostname, optionally chroots into the rootfs and mounts a private /proc, and
/// launches the user command.
///
/// The namespace set depends on --rootfs: a rootfs run gets UTS + mount + PID; a
/// bare run gets UTS only. Requires root (CAP_SYS_ADMIN / CAP_SYS_CHROOT) until
/// rootless mode (Phase 5).
/// </summary>
public static class RunCommand
{
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = RunOptions.Parse(args, stderr);
        if (options is null)
            return ExitCodes.UsageError;

        // Resolve --rootfs to an absolute path and confirm it exists *before*
        // touching namespaces, so a bad path fails early and cleanly. Absolute
        // because the child chroots after inheriting a fresh cwd. (Marker/rootfs
        // validation is deliberately shallow — Phase 8 images have no marker.)
        string? rootfs = null;
        if (options.Rootfs is not null)
        {
            rootfs = Path.GetFullPath(options.Rootfs);
            if (!Directory.Exists(rootfs))
            {
                stderr.WriteLine($"ccrun: rootfs '{options.Rootfs}' does not exist or is not a directory");
                return ExitCodes.RuntimeError;
            }
        }

        // New UTS namespace so the container holds its own hostname without
        // touching the host's (FR-2.1, FR-2.3). Affects this process; the
        // re-exec'd child inherits it.
        //
        // A rootfs run additionally gets the mount namespace (a private mount table,
        // so the /proc we mount never leaks to the host — FR-4.3) and the PID
        // namespace (a fresh process-id space — FR-4.1). Without --rootfs we stay at
        // Phase 2 behaviour: UTS only. That keeps the no-rootfs path on managed
        // Process.Start, which must not become PID 1.
        //
        // CLONE_NEWPID does not move this process into the new namespace — it makes
        // the *next* forked process PID 1. That fork is the __child ReExec launches,
        // which then execs the user command, so the command itself lands as PID 1.
        int flags = Libc.CLONE_NEWUTS;
        string flagNames = "CLONE_NEWUTS";
        if (rootfs is not null)
        {
            flags |= Libc.CLONE_NEWNS | Libc.CLONE_NEWPID;
            flagNames += "|CLONE_NEWNS|CLONE_NEWPID";
        }

        if (Libc.Unshare(flags) != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            stderr.WriteLine($"ccrun: unshare({flagNames}) failed: {Libc.LastErrorMessage()}");
            if (err == Libc.EPERM)
                stderr.WriteLine("hint: ccrun needs elevated privileges for namespaces; " +
                                 "re-run under sudo (rootless mode arrives in Phase 5).");
            return ExitCodes.RuntimeError;
        }

        return ReExec.RunChild(options with { Rootfs = rootfs }, stderr);
    }
}
