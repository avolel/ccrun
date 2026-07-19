using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ccrun;

/// <summary>
/// Creates the container's namespaces and re-invokes ccrun itself in the hidden
/// <c>__child</c> init stage inside them. That child performs the in-namespace setup
/// (sethostname, and on a rootfs run chroot + /proc mount) before running the user
/// command. Stdio is inherited, so interactivity survives the extra hop.
/// </summary>
/// <remarks>
/// <para>
/// The namespaces are created by <c>clone3</c> — as part of making the child process —
/// rather than by the parent calling <c>unshare</c> and letting an ordinary
/// <c>Process.Start</c> inherit the result. That is forced on us by one line of
/// unshare(2): <c>CLONE_NEWUSER</c> requires the caller to be single-threaded. The .NET
/// runtime always has more than one thread (the finalizer thread alone is enough), so a
/// managed process can never unshare a user namespace — the call fails with EINVAL.
/// Creating a *new process* with the flag has no such restriction, because the new
/// process starts with exactly one thread by definition. Go has the same problem and runc
/// solves it the same way.
/// </para>
/// <para>
/// Doing it this way also removes a subtlety: with <c>CLONE_NEWPID</c> passed at clone
/// time, the cloned child *is* PID 1 rather than merely being the fork that becomes it.
/// </para>
/// <para>
/// The handshake exists because of how capabilities behave across <c>execve</c>. A brand
/// new user namespace has empty id maps, so until they are written every id in it resolves
/// to the overflow uid (nobody), and exec'ing as a non-root uid with no file capabilities
/// clears the permitted capability set — the child would arrive at its chroot and mount
/// calls with no CAP_SYS_ADMIN. So the child blocks on a pipe, the parent writes the maps
/// on its behalf, and only then does the child exec, now as root-in-namespace, which keeps
/// its capabilities.
/// </para>
/// </remarks>
internal static class ReExec
{
    /// <summary>Hidden verb marking the re-executed init stage.</summary>
    public const string ChildVerb = "__child";

    /// <summary>Env var carrying the container hostname parent -> child.</summary>
    public const string HostnameEnv = "CCRUN_HOSTNAME";

    /// <summary>Env var carrying the absolute rootfs path parent -> child (unset => no chroot).</summary>
    public const string RootfsEnv = "CCRUN_ROOTFS";

    // Everything the cloned child needs, staged in static fields and native memory before
    // the clone. The child may not allocate (see RunAsClonedChild), so nothing here can be
    // computed on its side of the call.
    private static int s_childReadFd;
    private static int s_childWriteFd;
    private static IntPtr s_execPath;
    private static IntPtr s_execArgv;
    private static IntPtr s_execEnvp;

    /// <summary>
    /// Clones a child into fresh namespaces, maps container root onto the invoking user,
    /// and returns the exit status of the container once it finishes.
    /// </summary>
    public static int RunChild(RunOptions options, ulong namespaceFlags, TextWriter stderr)
    {
        StageExecArguments(options);

        // O_CLOEXEC means both ends vanish by themselves when the child execs, so the child
        // never has to close anything — one less call on a code path that must stay minimal.
        var fds = new int[2];
        if (Libc.Pipe2(fds, Libc.O_CLOEXEC) != 0)
        {
            stderr.WriteLine($"ccrun: pipe2 failed: {Libc.LastErrorMessage()}");
            return ExitCodes.RuntimeError;
        }
        s_childReadFd = fds[0];
        s_childWriteFd = fds[1];

        PrepareClonedChildPath();

        int pid = Clone(namespaceFlags, stderr);
        if (pid < 0)
        {
            Libc.Close(s_childReadFd);
            Libc.Close(s_childWriteFd);
            return ExitCodes.RuntimeError;
        }
        if (pid == 0)
            RunAsClonedChild(); // never returns

        Libc.Close(s_childReadFd);
        bool mapped = WriteIdMaps(pid, stderr);
        if (mapped)
        {
            // Any byte will do; the child only cares that the read completes.
            unsafe
            {
                byte go = 1;
                Libc.Write(s_childWriteFd, (IntPtr)(&go), 1);
            }
        }

        // Closing unblocks the child even when the maps failed, so that case surfaces as
        // the child exiting rather than as a hang.
        Libc.Close(s_childWriteFd);

        // Reap unconditionally. Even on the failure path the child exists and must be
        // waited for, or we would exit leaving a zombie behind.
        int code = WaitForExitCode(pid, stderr);
        return mapped ? code : ExitCodes.RuntimeError;
    }

    /// <summary>
    /// Issues clone3(2). Returns the child pid in the parent, 0 in the child, -1 on error.
    /// </summary>
    private static int Clone(ulong namespaceFlags, TextWriter stderr)
    {
        // struct clone_args, version 0: flags at offset 0 and exit_signal at 32 are the
        // only fields we need; the rest (pidfd, tid pointers, stack, tls) stay zero, which
        // gives fork-like semantics — the child shares no address space and inherits our
        // stack copy-on-write. exit_signal = SIGCHLD is what makes the child waitpid-able.
        IntPtr args = Marshal.AllocHGlobal((int)Libc.CLONE_ARGS_SIZE_VER0);
        try
        {
            for (int off = 0; off < (int)Libc.CLONE_ARGS_SIZE_VER0; off += 8)
                Marshal.WriteInt64(args, off, 0);
            Marshal.WriteInt64(args, 0, (long)namespaceFlags);
            Marshal.WriteInt64(args, 32, (long)Libc.SIGCHLD);

            long ret = Libc.Syscall(Libc.SYS_clone3, args, Libc.CLONE_ARGS_SIZE_VER0);
            if (ret >= 0)
                return (int)ret;

            int err = Marshal.GetLastPInvokeError();
            stderr.WriteLine($"ccrun: clone3 failed: {Libc.LastErrorMessage()}");
            if (err == Libc.EPERM)
                stderr.WriteLine("hint: creating a user namespace was denied; this kernel may have " +
                                 "unprivileged user namespaces disabled. Enable them with " +
                                 "'sysctl -w kernel.unprivileged_userns_clone=1', or run ccrun as root.");
            else if (err == Libc.ENOSYS)
                stderr.WriteLine("hint: this kernel has no clone3 syscall; ccrun needs Linux 5.3 or newer.");
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(args);
        }
    }

    /// <summary>
    /// The cloned child: wait for the parent's go-ahead, then become the init stage.
    /// Never returns.
    /// </summary>
    /// <remarks>
    /// This runs in a process cloned out of a multithreaded CLR, so only the cloning thread
    /// exists here and any runtime state another thread was mutating at clone time is
    /// frozen mid-flight with nobody left to finish it. Touching the allocator, the JIT,
    /// Console — or even doing a normal P/Invoke GC transition — can therefore deadlock or
    /// trip a runtime assertion that ends in abort(). The rules for this method are:
    ///
    /// <list type="bullet">
    /// <item>no allocation and no managed I/O;</item>
    /// <item>nothing but libc calls on pointers staged before the clone;</item>
    /// <item>every one of those imports carries [SuppressGCTransition], so the calls touch
    /// no runtime state at all;</item>
    /// <item>everything it reaches is compiled in advance by PrepareClonedChildPath.</item>
    /// </list>
    ///
    /// Two calls is the whole budget. The pipe's descriptors are O_CLOEXEC, so the exec
    /// cleans them up and no close is needed here.
    /// </remarks>
    private static void RunAsClonedChild()
    {
        unsafe
        {
            byte go = 0;
            if (Libc.Read(s_childReadFd, (IntPtr)(&go), 1) != 1)
                Libc.Exit(ExitCodes.RuntimeError); // parent died or failed to write the maps
        }

        // Replaces this process image; we are mapped as root in the new user namespace, so
        // the capabilities the init stage needs survive the exec.
        Libc.Execve(s_execPath, s_execArgv, s_execEnvp);
        Libc.Exit(ExitCodes.RuntimeError); // only reachable if exec'ing ccrun itself failed
    }

    /// <summary>
    /// Forces the child's code path to be compiled, and its libc stubs resolved, while we
    /// are still safely in the parent — see the remarks on <see cref="RunAsClonedChild"/>.
    /// The calls below are chosen to fail harmlessly: they resolve the symbol and generate
    /// the interop stub without doing anything.
    /// </summary>
    private static void PrepareClonedChildPath()
    {
        RuntimeHelpers.PrepareMethod(
            typeof(ReExec).GetMethod(nameof(RunAsClonedChild), BindingFlags.NonPublic | BindingFlags.Static)!
                .MethodHandle);

        Libc.Read(-1, IntPtr.Zero, 0);                      // EBADF
        Libc.Execve(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // EFAULT
    }

    /// <summary>
    /// Maps container UID/GID 0 onto the invoking user's effective UID/GID (FR-5.2), so the
    /// command is root inside the container while every process it starts appears on the
    /// host owned by the ordinary user who ran ccrun (FR-5.3).
    /// </summary>
    /// <remarks>
    /// Writing another process's maps normally requires CAP_SETUID in the parent namespace,
    /// which we do not have. user_namespaces(7) carves out the case we use: a process may
    /// write a map consisting of a single id when that id is its own effective one. Mapping
    /// yourself grants no authority you did not already hold, so it is allowed unprivileged.
    /// That is the whole reason ccrun needs neither root nor a setuid newuidmap helper.
    ///
    /// The setgroups write is the price of that exception: an unprivileged writer must
    /// permanently disable setgroups(2) in the namespace before gid_map may be written, so
    /// the namespace cannot be used to shed a supplementary group and reach a file that
    /// group was denying. The order matters — "deny" is rejected once gid_map exists.
    /// </remarks>
    private static bool WriteIdMaps(int pid, TextWriter stderr)
    {
        uint uid = Libc.Geteuid();
        uint gid = Libc.Getegid();
        try
        {
            WriteProcFile($"/proc/{pid}/setgroups", "deny");
            // "<id inside the namespace> <id outside it> <count>": container root is us.
            WriteProcFile($"/proc/{pid}/uid_map", $"0 {uid} 1");
            WriteProcFile($"/proc/{pid}/gid_map", $"0 {gid} 1");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"ccrun: writing the uid/gid map failed: {ex.Message}");
            return false;
        }
    }

    // The kernel parses each of these files from a single write(2) and rejects partial or
    // repeated writes, so open the existing file and write the payload in one call.
    // (File.WriteAllText would additionally try to truncate, which they do not support.)
    private static void WriteProcFile(string path, string content)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.Write(System.Text.Encoding.ASCII.GetBytes(content));
    }

    /// <summary>Reaps the container and translates its wait status into our exit code.</summary>
    private static int WaitForExitCode(int pid, TextWriter stderr)
    {
        if (Libc.Waitpid(pid, out int status, 0) < 0)
        {
            stderr.WriteLine($"ccrun: waitpid failed: {Libc.LastErrorMessage()}");
            return ExitCodes.RuntimeError;
        }

        // Decode the wait status by hand: the W* macros are C macros, so there is nothing
        // to P/Invoke. Low 7 bits zero => exited normally, code in the next 8 bits.
        if ((status & 0x7f) == 0)
            return (status >> 8) & 0xff;

        // Otherwise it was killed by a signal; report it the way a shell does.
        return 128 + (status & 0x7f);
    }

    /// <summary>
    /// Works out how to re-invoke this program and marshals the exec arguments into native
    /// memory, so that the cloned child has nothing left to compute. Not freed: the parent
    /// lives exactly as long as the container.
    /// </summary>
    private static void StageExecArguments(RunOptions options)
    {
        // Published apphost / single-file binaries are invoked directly; under the `dotnet`
        // muxer we must prepend the managed entry dll.
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve Environment.ProcessPath for re-exec");

        var argv = new List<string> { exe };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Assembly.GetEntryAssembly()?.Location is { Length: > 0 } loc
                ? loc
                : throw new InvalidOperationException("cannot resolve entry assembly for re-exec");
            argv.Add(dll);
        }

        argv.Add(ChildVerb);
        argv.Add(options.Command);
        argv.AddRange(options.CommandArgs);

        var envp = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            if (key != HostnameEnv && key != RootfsEnv)
                envp.Add($"{key}={e.Value}");
        }
        envp.Add($"{HostnameEnv}={options.Hostname}");
        if (options.Rootfs is not null)
            envp.Add($"{RootfsEnv}={options.Rootfs}");

        s_execPath = Marshal.StringToHGlobalAnsi(exe);
        s_execArgv = AllocStringArray(argv);
        s_execEnvp = AllocStringArray(envp);
    }

    /// <summary>Builds a NULL-terminated char*[] in native memory.</summary>
    private static IntPtr AllocStringArray(List<string> items)
    {
        IntPtr block = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        for (int i = 0; i < items.Count; i++)
            Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.StringToHGlobalAnsi(items[i]));
        Marshal.WriteIntPtr(block, items.Count * IntPtr.Size, IntPtr.Zero);
        return block;
    }
}
