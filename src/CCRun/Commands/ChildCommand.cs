using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ccrun;

/// <summary>
/// Hidden `__child` init stage, re-executed by <see cref="ReExec"/> inside the
/// namespaces the parent created. Sets the container hostname (FR-2.2), then —
/// if a rootfs was supplied — makes the mount tree private (FR-4.3), chroots into
/// the rootfs (FR-3.1) and mounts a private /proc (FR-4.2) before handing off to
/// the user command.
///
/// On the rootfs path this process is PID 1 of a new PID namespace (FR-4.1): the
/// parent's unshare(CLONE_NEWPID) took effect on the fork that produced us.
/// </summary>
public static class ChildCommand
{
    // args = [command, arg1, arg2, ...]
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("ccrun: internal error: __child requires a command");
            return ExitCodes.RuntimeError;
        }

        string hostname = Environment.GetEnvironmentVariable(ReExec.HostnameEnv)
            ?? RunOptions.DefaultHostname;

        if (Libc.Sethostname(hostname, (nuint)Encoding.UTF8.GetByteCount(hostname)) != 0)
        {
            stderr.WriteLine($"ccrun: sethostname('{hostname}') failed: {Libc.LastErrorMessage()}");
            return ExitCodes.RuntimeError;
        }

        string? rootfs = Environment.GetEnvironmentVariable(ReExec.RootfsEnv);
        if (rootfs is not null)
        {
            // Everything below runs after chroot, where the .NET runtime's own assemblies
            // sit outside the new root and can no longer be loaded on demand. Console
            // initializes itself lazily on its first real write, pulling in
            // Microsoft.Win32.Primitives — so a failure message from any step below would
            // try to load it from an unreachable path and die with a FileNotFoundException,
            // losing the diagnostic and the exit code both. Get that assembly resident now,
            // while the runtime directory is still reachable. (Libc.LastErrorMessage dodges
            // the same trap by using strerror instead of Win32Exception; see Libc.)
            PreloadConsoleWriteDependencies();

            // We are PID 1 of a fresh PID namespace, inside a fresh mount namespace.
            // Before touching any mount, mark the inherited tree private so nothing we
            // mount below (notably /proc) propagates back into the host's mount
            // namespace (FR-4.3). A new mount namespace starts as a *copy* of the
            // host's, and on most distros "/" is shared, so without this our mounts
            // would still be visible on the host. MS_REC reaches every inherited mount;
            // MS_PRIVATE turns propagation off. Done against the real "/" (pre-chroot)
            // so the recursion covers the whole tree.
            if (Libc.Mount("none", "/", null, Libc.MS_REC | Libc.MS_PRIVATE, null) != 0)
            {
                stderr.WriteLine($"ccrun: cannot make mounts private: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }

            // chroot into the image root, then chdir("/"). The chdir is essential:
            // without it the cwd still references the old root, which is the classic
            // way to escape a chroot (FR-3.1, FR-3.2).
            if (Libc.Chroot(rootfs) != 0)
            {
                stderr.WriteLine($"ccrun: chroot('{rootfs}') failed: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }
            if (Libc.Chdir("/") != 0)
            {
                stderr.WriteLine($"ccrun: chdir('/') failed: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }

            // Mount a procfs so `ps` and friends report only this container's processes
            // (FR-4.2, FR-4.5). procfs reports the PID namespace of the process that
            // mounts it, and we are PID 1 of the new one, so this instance is scoped to
            // the container. nosuid/nodev/noexec are the conventional hardening flags.
            // The rootfs must already contain /proc as a mountpoint (Alpine's does).
            //
            // No teardown is needed (FR-4.4): this mount exists only in our mount
            // namespace, which the kernel destroys once the PID namespace empties — on
            // success and on every error path alike. Explicit unmounting would be
            // impossible anyway once execvp replaces this process image.
            if (Libc.Mount("proc", "/proc", "proc", Libc.MS_NOSUID | Libc.MS_NODEV | Libc.MS_NOEXEC, null) != 0)
            {
                stderr.WriteLine($"ccrun: cannot mount /proc: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }

            // Replace this .NET process image with the user command. After chroot the
            // runtime's own files may be outside the new root, so we must not do further
            // managed work (Process.Start). The command inherits our stdio and its exit
            // code becomes this stage's, which ReExec propagates up (FR-1.3, FR-1.4).
            return Exec(args, stderr);
        }

        // No rootfs => Phase 2 behavior; Process.Start is safe without a chroot.
        return ProcessRunner.Run(args[0], args[1..], stderr);
    }

    // Pre-loads the assembly that Console's first-write terminal initialization needs, by
    // constructing a throwaway instance of a type that lives in it (Win32Exception is in
    // Microsoft.Win32.Primitives). Triggering that initialization directly would mean
    // actually writing bytes into the container's output, so we settle for making sure
    // the assembly is already resident by the time the first write happens.
    private static void PreloadConsoleWriteDependencies()
    {
        _ = new Win32Exception(0);
    }

    // Hands off to the command via execvp(2); only returns if the exec fails.
    private static int Exec(string[] argv, TextWriter stderr)
    {
        string command = argv[0];

        // execvp needs a NULL-terminated argv (argv[0] = program name). A null
        // element marshals to a null pointer via the UTF-8 LibraryImport marshaller.
        var cargv = new string?[argv.Length + 1];
        Array.Copy(argv, cargv, argv.Length);
        cargv[argv.Length] = null;

        Libc.Execvp(command, cargv);

        int err = Marshal.GetLastPInvokeError();
        stderr.WriteLine($"ccrun: cannot exec '{command}': {Libc.LastErrorMessage()}");
        // Match ProcessRunner's mapping: EACCES => not executable (126), else not found (127).
        return err == Libc.EACCES ? ExitCodes.CommandNotExecutable : ExitCodes.CommandNotFound;
    }
}
