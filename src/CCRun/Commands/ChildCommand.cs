using System.Runtime.InteropServices;
using System.Text;

namespace ccrun;

/// <summary>
/// Hidden `__child` init stage, re-executed by <see cref="ReExec"/> inside the
/// namespaces the parent created. Sets the container hostname (FR-2.2), then —
/// if a rootfs was supplied — chroots into it (FR-3.1) before handing off to the
/// user command.
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

            // Replace this .NET process image with the user command. After chroot the
            // runtime's own files may be outside the new root, so we must not do further
            // managed work (Process.Start). The command inherits our stdio and its exit
            // code becomes this stage's, which ReExec propagates up (FR-1.3, FR-1.4).
            return Exec(args, stderr);
        }

        // No rootfs => Phase 2 behavior; Process.Start is safe without a chroot.
        return ProcessRunner.Run(args[0], args[1..], stderr);
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
