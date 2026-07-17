using System.ComponentModel;
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

    /// <summary>errno EPERM — operation not permitted (needs CAP_SYS_ADMIN / root).</summary>
    public const int EPERM = 1;

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

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint Geteuid();

    /// <summary>Human-readable message for the last failed P/Invoke's errno.</summary>
    public static string LastErrorMessage() =>
        new Win32Exception(Marshal.GetLastPInvokeError()).Message;
}
