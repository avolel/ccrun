using System.Runtime.InteropServices;

namespace ccrun;

/// <summary>
/// Phase 2 `run` (parent/host stage): parses options, creates the container's
/// UTS namespace via unshare(2), then re-executes ccrun in the hidden init
/// stage (see <see cref="ReExec"/>) which sets the hostname and launches the
/// user command. Requires CAP_SYS_ADMIN (root) until rootless mode (Phase 5).
/// </summary>
public static class RunCommand
{
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = RunOptions.Parse(args, stderr);
        if (options is null)
            return ExitCodes.UsageError;

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

        return ReExec.RunChild(options, stderr);
    }
}
