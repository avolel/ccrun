using System.Runtime.InteropServices;

namespace ccrun;

/// <summary>
/// `run` (parent/host stage): parses options, optionally validates the target
/// rootfs, creates the container's UTS namespace via unshare(2), then re-executes
/// ccrun in the hidden init stage (see <see cref="ReExec"/>) which sets the
/// hostname, optionally chroots into the rootfs, and launches the user command.
/// Requires root (CAP_SYS_ADMIN / CAP_SYS_CHROOT) until rootless mode (Phase 5).
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
        if (Libc.Unshare(Libc.CLONE_NEWUTS) != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            stderr.WriteLine($"ccrun: unshare(CLONE_NEWUTS) failed: {Libc.LastErrorMessage()}");
            if (err == Libc.EPERM)
                stderr.WriteLine("hint: ccrun needs elevated privileges for namespaces; " +
                                 "re-run under sudo (rootless mode arrives in Phase 5).");
            return ExitCodes.RuntimeError;
        }

        return ReExec.RunChild(options with { Rootfs = rootfs }, stderr);
    }
}
