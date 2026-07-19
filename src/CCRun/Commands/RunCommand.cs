namespace ccrun;

/// <summary>
/// `run` (parent/host stage): parses options, optionally validates the target rootfs,
/// then hands off to <see cref="ReExec"/>, which creates the container's namespaces and
/// launches the hidden init stage inside them. That stage sets the hostname, optionally
/// chroots into the rootfs and mounts a private /proc, and launches the user command.
///
/// Every run gets a user namespace, which is what makes ccrun rootless (FR-5.1). The rest
/// of the set depends on --rootfs: a rootfs run adds mount + PID on top of UTS; a bare run
/// gets UTS only.
/// </summary>
public static class RunCommand
{
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = RunOptions.Parse(args, stderr);
        if (options is null)
            return ExitCodes.UsageError;

        // Resolve --rootfs to an absolute path and confirm it exists *before* creating any
        // namespace, so a bad path fails early and cleanly. Absolute because the child
        // chroots after inheriting a fresh cwd. (Marker/rootfs validation is deliberately
        // shallow — Phase 8 images have no marker.)
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

        // The user namespace comes first in every run (FR-5.1). Creating one is the single
        // namespace operation the kernel permits an unprivileged process, and inside it the
        // caller holds a full capability set — including CAP_SYS_ADMIN and CAP_SYS_CHROOT.
        // Those namespace-local capabilities are what authorize everything else here: the
        // other namespaces, the child's chroot, and the /proc mount. Hence no sudo.
        //
        // The UTS namespace gives the container its own hostname without touching the
        // host's (FR-2.1, FR-2.3). A rootfs run additionally gets the mount namespace (a
        // private mount table, so the /proc we mount never leaks to the host — FR-4.3) and
        // the PID namespace (a fresh process-id space in which the container's command is
        // PID 1 — FR-4.1). Without --rootfs we stay at Phase 2 behaviour: UTS only. That
        // keeps the no-rootfs path on managed Process.Start, which must not become PID 1.
        ulong flags = (ulong)Libc.CLONE_NEWUSER | (ulong)Libc.CLONE_NEWUTS;
        if (rootfs is not null)
            flags |= (ulong)Libc.CLONE_NEWNS | (ulong)Libc.CLONE_NEWPID;

        return ReExec.RunChild(options with { Rootfs = rootfs }, flags, stderr);
    }
}
