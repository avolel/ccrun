using System.ComponentModel;
using System.Diagnostics;

namespace ccrun;

/// <summary>
/// Phase 1 `run`: launches the requested command as a child process, wiring
/// its stdio to ccrun's own terminal and propagating its exit code. Isolation
/// (namespaces, chroot, cgroups) arrives in later phases.
/// </summary>
public static class RunCommand
{
    // args = [command, arg1, arg2, ...]
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("ccrun run: missing command");
            stderr.WriteLine("usage: ccrun run <command> [args...]");
            return ExitCodes.UsageError;
        }

        string command = args[0];

        var psi = new ProcessStartInfo
        {
            FileName = command,
            // No redirection + UseShellExecute=false => the child inherits
            // ccrun's stdin/stdout/stderr for live, interactive IO (FR-1.3).
            UseShellExecute = false,
        };
        
        for (int i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            process.WaitForExit();
            return process.ExitCode; // FR-1.4
        }
        catch (Win32Exception ex)
        {
            // ENOENT (2) => not found; EACCES (13) => not executable.
            stderr.WriteLine($"ccrun: cannot run '{command}': {ex.Message}");
            return ex.NativeErrorCode == 13
                ? ExitCodes.CommandNotExecutable
                : ExitCodes.CommandNotFound;
        }
    }
}
