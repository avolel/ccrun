using System.Runtime.InteropServices;

namespace ccrun;

/// <summary>
/// Source-generated P/Invoke into libc for the Linux isolation primitives.
/// Every call sets errno (SetLastError); use <see cref="LastErrorMessage"/> to
/// turn a failed call into a diagnosable message (NFR-2).
/// </summary>
internal static partial class Libc
{
    /// <summary>Flag for <see cref="Unshare"/>: new UTS namespace (hostname).</summary>
    public const int CLONE_NEWUTS = 0x04000000;

    /// <summary>
    /// Flag for <see cref="Unshare"/>: new user namespace. The caller gets a full set of
    /// capabilities *inside* the new namespace, which is what lets an unprivileged user
    /// perform the other unshares, the chroot and the mounts (FR-5.1).
    /// </summary>
    public const int CLONE_NEWUSER = 0x10000000;

    /// <summary>Flag for <see cref="Unshare"/>: new mount namespace (private mount table).</summary>
    public const int CLONE_NEWNS = 0x00020000;

    /// <summary>
    /// Flag for <see cref="Unshare"/>: new PID namespace (fresh process-id space).
    /// Note this does not move the caller — the *next* forked process becomes PID 1.
    /// </summary>
    public const int CLONE_NEWPID = 0x20000000;

    /// <summary>mount(2): ignore set-uid/set-gid bits under this mount.</summary>
    public const ulong MS_NOSUID = 1UL << 1;

    /// <summary>mount(2): disallow access to device special files under this mount.</summary>
    public const ulong MS_NODEV = 1UL << 2;

    /// <summary>mount(2): disallow program execution from this mount.</summary>
    public const ulong MS_NOEXEC = 1UL << 3;

    /// <summary>mount(2): apply the operation recursively to the whole subtree.</summary>
    public const ulong MS_REC = 1UL << 14;

    /// <summary>mount(2): turn off mount/unmount propagation to and from peers.</summary>
    public const ulong MS_PRIVATE = 1UL << 18;

    /// <summary>
    /// clone3(2) syscall number. Unlike clone(2), whose number differs per architecture
    /// (and whose argument order differs too), clone3 is 435 everywhere — which keeps
    /// the x64/arm64 story simple.
    /// </summary>
    public const long SYS_clone3 = 435;

    /// <summary>
    /// Size of the original <c>struct clone_args</c> (CLONE_ARGS_SIZE_VER0). The kernel
    /// takes the struct size as an argument and uses it to tell which version the caller
    /// was built against, so passing the smallest one works on every kernel that has
    /// clone3 at all.
    /// </summary>
    public const nuint CLONE_ARGS_SIZE_VER0 = 64;

    /// <summary>Signal delivered to the parent when the cloned child exits; makes it waitable.</summary>
    public const ulong SIGCHLD = 17;

    /// <summary>errno EPERM — operation not permitted (needs CAP_SYS_ADMIN / root).</summary>
    public const int EPERM = 1;

    /// <summary>errno EINVAL — invalid argument.</summary>
    public const int EINVAL = 22;

    /// <summary>errno ENOSYS — syscall not implemented by this kernel.</summary>
    public const int ENOSYS = 38;

    /// <summary>errno EACCES — permission denied (e.g. target not executable).</summary>
    public const int EACCES = 13;

    [LibraryImport("libc", EntryPoint = "unshare", SetLastError = true)]
    internal static partial int Unshare(int flags);

    [LibraryImport("libc", EntryPoint = "sethostname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Sethostname(string name, nuint len);

    /// <summary>Change the process root directory to <paramref name="path"/> (FR-3.1). Needs CAP_SYS_CHROOT.</summary>
    [LibraryImport("libc", EntryPoint = "chroot", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Chroot(string path);

    [LibraryImport("libc", EntryPoint = "chdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Chdir(string path);

    /// <summary>
    /// mount(2). source/filesystemtype/data are meaningless for some operations — a
    /// propagation change, for instance, passes source="none" and data=NULL. A C# null
    /// marshals to a NULL pointer.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "mount", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Mount(string? source, string target, string? filesystemtype, ulong mountflags, string? data);

    /// <summary>
    /// Replace the current process image with <paramref name="file"/>. Searches PATH for
    /// a bare name; <paramref name="argv"/> must be NULL-terminated (argv[0] = program name).
    /// Only returns on failure.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "execvp", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Execvp(string file, string?[] argv);

    /// <summary>
    /// Raw syscall(2) escape hatch, used for clone3 — glibc exposes no wrapper for it.
    /// Declared with fixed arguments rather than varargs, which is safe here because the
    /// System V x86-64 and AArch64 calling conventions pass these integer arguments in the
    /// same registers either way.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    internal static partial long Syscall(long number, IntPtr arg1, nuint arg2);

    /// <summary>open(2)/pipe2(2) flag: close this descriptor automatically on exec.</summary>
    public const int O_CLOEXEC = 0x80000;

    /// <summary>
    /// pipe2(2): fds[0] is the read end, fds[1] the write end. Preferred over pipe(2)
    /// because it can set O_CLOEXEC atomically, which saves the cloned child from having
    /// to close anything by hand — see <see cref="ReExec"/> on why its code must stay
    /// minimal.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    internal static partial int Pipe2([Out] int[] fds, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static partial nint Write(int fd, IntPtr buf, nuint count);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    // The three imports below are the *only* calls the cloned child makes, and they carry
    // [SuppressGCTransition] for that reason. Normally a P/Invoke brackets the native call
    // with a transition that moves the thread out of and back into cooperative GC mode,
    // touching runtime state the child is in no position to touch: it holds one thread out
    // of a multithreaded CLR, so if a garbage collection was being coordinated at the
    // moment of the clone, the child inherits a suspension that can never be resolved,
    // because the threads that would resolve it do not exist in it. The runtime detects the
    // impossible state and calls abort(). Suppressing the transition makes these compile to
    // bare native calls that touch no runtime state at all, which is what a post-clone
    // child needs. The usual caveat for the attribute — the callee must be short and must
    // not block — is knowingly waived for Read: it blocks by design, and it is safe here
    // precisely because the child is single-threaded and no GC can be pending on it.

    /// <summary>read(2). Child-path only; see the note above on [SuppressGCTransition].</summary>
    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [SuppressGCTransition]
    internal static partial nint Read(int fd, IntPtr buf, nuint count);

    /// <summary>
    /// execve(2) taking already-marshalled native pointers. The pointer-based signature is
    /// deliberate: this runs in a freshly cloned child where allocating (which string
    /// marshalling would do) is unsafe — see <see cref="ReExec"/>.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    [SuppressGCTransition]
    internal static partial int Execve(IntPtr path, IntPtr argv, IntPtr envp);

    /// <summary>
    /// _exit(2): terminates immediately without running atexit handlers or flushing
    /// stdio. The abrupt variant is the point — a cloned child must not run the .NET
    /// runtime's shutdown path.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "_exit")]
    [SuppressGCTransition]
    internal static partial void Exit(int status);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int Waitpid(int pid, out int status, int options);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint Geteuid();

    [LibraryImport("libc", EntryPoint = "getegid")]
    internal static partial uint Getegid();

    /// <summary>Returns libc's static message buffer for <paramref name="errnum"/>.</summary>
    [LibraryImport("libc", EntryPoint = "strerror")]
    private static partial IntPtr Strerror(int errnum);

    /// <summary>
    /// Human-readable message for the last failed P/Invoke's errno.
    /// </summary>
    /// <remarks>
    /// This goes through libc's strerror rather than the more idiomatic
    /// <c>new Win32Exception(errno).Message</c> on purpose. Win32Exception lives in
    /// Microsoft.Win32.Primitives, which the runtime loads lazily on first use — and
    /// the child stage's failure paths run *after* chroot, where the runtime's own
    /// assemblies are no longer reachable under the new root. Constructing one there
    /// killed the process with a FileNotFoundException instead of reporting the error
    /// (and returning the right exit code). strerror is a plain libc call that needs
    /// nothing loaded, so the diagnostics survive the chroot.
    /// </remarks>
    public static string LastErrorMessage()
    {
        int err = Marshal.GetLastPInvokeError();
        IntPtr msg = Strerror(err);
        return msg == IntPtr.Zero ? $"errno {err}" : Marshal.PtrToStringUTF8(msg) ?? $"errno {err}";
    }
}
