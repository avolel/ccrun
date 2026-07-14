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

    /// <summary>errno EPERM — operation not permitted (needs CAP_SYS_ADMIN / root).</summary>
    public const int EPERM = 1;

    [LibraryImport("libc", EntryPoint = "unshare", SetLastError = true)]
    internal static partial int Unshare(int flags);

    [LibraryImport("libc", EntryPoint = "sethostname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Sethostname(string name, nuint len);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    internal static partial uint Geteuid();

    /// <summary>Human-readable message for the last failed P/Invoke's errno.</summary>
    public static string LastErrorMessage() =>
        new Win32Exception(Marshal.GetLastPInvokeError()).Message;
}
