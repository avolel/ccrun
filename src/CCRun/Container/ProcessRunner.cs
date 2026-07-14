using System.ComponentModel;
using System.Diagnostics;

namespace ccrun;

/// <summary>
/// Launches a target command as a child process, inheriting ccrun's stdio for
/// live/interactive IO, and returns its exit code.
/// </summary>
internal static class ProcessRunner
{
    public static int Run(string command, IReadOnlyList<string> args, TextWriter stderr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            // No redirection + UseShellExecute=false => the child inherits
            // ccrun's stdin/stdout/stderr for live, interactive IO (FR-1.3).
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

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
