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
/// On the rootfs path this process is PID 1 of a new PID namespace (FR-4.1): the parent
/// passed CLONE_NEWPID when it cloned us, so we were born as the namespace's init. It also
/// passed CLONE_NEWUSER and mapped us to root inside it, which is where the capabilities
/// the steps below need come from — no real root involved (FR-5.1).
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
            // Every failure message below goes out through WriteStderrLine (a raw write(2))
            // rather than the injected TextWriter. Once we chroot, the runtime's own
            // assemblies sit outside the new root and can no longer be loaded on demand —
            // and Console's first real write lazily initializes its terminal support,
            // which pulls in several assemblies (System.Collections, Microsoft.Win32.
            // Primitives, ...). Any of those would fail to load here and kill the process
            // with a FileNotFoundException, losing both the diagnostic and the exit code.
            // Pre-loading them one by one is a losing game against an implementation
            // detail, so the post-chroot paths simply do not use Console at all.
            // (Libc.LastErrorMessage dodges the same trap by using strerror instead of
            // Win32Exception; see Libc.)

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
                WriteStderrLine($"ccrun: cannot make mounts private: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }

            // chroot into the image root, then chdir("/"). The chdir is essential:
            // without it the cwd still references the old root, which is the classic
            // way to escape a chroot (FR-3.1, FR-3.2).
            if (Libc.Chroot(rootfs) != 0)
            {
                WriteStderrLine($"ccrun: chroot('{rootfs}') failed: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }
            if (Libc.Chdir("/") != 0)
            {
                WriteStderrLine($"ccrun: chdir('/') failed: {Libc.LastErrorMessage()}");
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
                WriteStderrLine($"ccrun: cannot mount /proc: {Libc.LastErrorMessage()}");
                return ExitCodes.RuntimeError;
            }

            // Replace this .NET process image with the user command. After chroot the
            // runtime's own files may be outside the new root, so we must not do further
            // managed work (Process.Start). The command inherits our stdio and its exit
            // code becomes this stage's, which ReExec propagates up (FR-1.3, FR-1.4).
            return Exec(args);
        }

        // No rootfs => Phase 2 behavior; Process.Start is safe without a chroot.
        return ProcessRunner.Run(args[0], args[1..], stderr);
    }

    // Writes one line to fd 2 with a raw write(2). Used instead of Console/TextWriter on
    // every path that can run after the chroot, where Console's lazy terminal
    // initialization would try to load assemblies that are no longer reachable. Only
    // CoreLib is touched here, and that is already resident.
    private static unsafe void WriteStderrLine(string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
        fixed (byte* p = bytes)
        {
            nint written = 0;
            while (written < bytes.Length)
            {
                nint n = Libc.Write(2, (IntPtr)(p + written), (nuint)(bytes.Length - written));
                if (n <= 0) return;   // nothing useful left to do about a failing stderr
                written += n;
            }
        }
    }

    // Hands off to the command via execvp(2); only returns if the exec fails.
    private static int Exec(string[] argv)
    {
        string command = argv[0];

        // execvp needs a NULL-terminated argv (argv[0] = program name). A null
        // element marshals to a null pointer via the UTF-8 LibraryImport marshaller.
        var cargv = new string?[argv.Length + 1];
        Array.Copy(argv, cargv, argv.Length);
        cargv[argv.Length] = null;

        Libc.Execvp(command, cargv);

        int err = Marshal.GetLastPInvokeError();
        WriteStderrLine($"ccrun: cannot exec '{command}': {Libc.LastErrorMessage()}");
        // Match ProcessRunner's mapping: EACCES => not executable (126), else not found (127).
        return err == Libc.EACCES ? ExitCodes.CommandNotExecutable : ExitCodes.CommandNotFound;
    }
}
