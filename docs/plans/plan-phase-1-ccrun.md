# Plan — Phase 1: Run an Arbitrary Command

## Context

CCRun is currently a Phase 0 scaffold: `src/CCRun/Program.cs` only prints usage
and exits 1, and the only test is a placeholder (`ScaffoldTests`). Phase 1
(BRD §7, FR-1.1–1.5, Milestone M1) implements the `run` verb so
`ccrun run <command> [args...]` launches the command as a child process, wires
its stdio to the terminal for live/interactive use, and propagates its exit
code. Invalid usage must produce a helpful message and a nonzero exit.

Isolation is intentionally out of scope here — the parent/child re-execution
architecture is deferred to Phase 2 (BRD FR-2.4). Phase 1 runs the command
directly. The main design goal beyond behaviour is a **testable seam**: today
all logic sits in top-level statements calling `Console` statics, which can't
be unit-tested. We extract dispatch and the run command into public classes
with injectable `TextWriter`s.

## Requirements mapped to design

- **FR-1.1** recognise `ccrun run <command> <args...>` → verb dispatch in `Cli`.
- **FR-1.2** pass args through unchanged → `ProcessStartInfo.ArgumentList`.
- **FR-1.3** live/interactive stdio → `UseShellExecute = false` with **no**
  stream redirection, so the child inherits ccrun's console handles.
- **FR-1.4** propagate exit code → return `process.ExitCode`.
- **FR-1.5** invalid usage → usage message + nonzero exit (`ExitCodes.UsageError`).

Note: on Linux, .NET's `Process` with `UseShellExecute = false` performs `PATH`
resolution for a bare filename, so both `run echo ...` and
`run /bin/busybox sh` work without extra logic.

## Files

### New: `src/CCRun/ExitCodes.cs`
```csharp
namespace CCRun;

/// <summary>Exit codes produced by ccrun itself, distinct from codes
/// propagated from the child command.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int UsageError = 1;
    // Shell conventions for launch failures, reused so scripts/users see
    // familiar values.
    public const int CommandNotExecutable = 126;
    public const int CommandNotFound = 127;
}
```

### New: `src/CCRun/Cli.cs`
```csharp
namespace CCRun;

/// <summary>
/// Top-level command dispatch. Parses the verb and routes to the matching
/// command. Writers are injectable (no Console statics) so dispatch and usage
/// behaviour are unit-testable.
/// </summary>
public static class Cli
{
    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        if (args.Length == 0)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        string verb = args[0];
        switch (verb)
        {
            case "run":
                return RunCommand.Execute(args.AsSpan(1).ToArray(), stdout, stderr);

            case "-h":
            case "--help":
            case "help":
                PrintUsage(stdout);
                return ExitCodes.Ok;

            default:
                stderr.WriteLine($"ccrun: unknown command '{verb}'");
                PrintUsage(stderr);
                return ExitCodes.UsageError;
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("ccrun: a lightweight Linux container runtime");
        w.WriteLine();
        w.WriteLine("usage:");
        w.WriteLine("  ccrun run <command> [args...]   run a command in a container");
        w.WriteLine("  ccrun --help                    show this help");
        w.WriteLine();
        w.WriteLine("Phase 1: 'run' executes the command directly (no isolation yet).");
    }
}
```

### New: `src/CCRun/Commands/RunCommand.cs`
```csharp
using System.ComponentModel;
using System.Diagnostics;

namespace CCRun;

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
```

### Update: `src/CCRun/Program.cs` (replace entire file)
```csharp
// CCRun — a lightweight Linux container runtime (learning project).
// Entrypoint: delegates to Cli so the logic stays unit-testable.
using CCRun;

return Cli.Run(args);
```

### New: `tests/CCRun.Tests/CliTests.cs`
```csharp
namespace CCRun.Tests;

public class CliTests
{
    private static (int code, string outText, string errText) RunCli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void NoArgs_PrintsUsage_AndFails()
    {
        var (code, _, err) = RunCli();
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("usage:", err);
    }

    [Fact]
    public void UnknownVerb_ReportsError_AndFails()
    {
        var (code, _, err) = RunCli("bogus");
        Assert.NotEqual(ExitCodes.Ok, code);
        Assert.Contains("unknown command 'bogus'", err);
    }

    [Fact]
    public void Help_PrintsUsageToStdout_AndSucceeds()
    {
        var (code, outText, _) = RunCli("--help");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("usage:", outText);
    }

    [Fact]
    public void RunWithoutCommand_Fails()
    {
        var (code, _, err) = RunCli("run");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("missing command", err);
    }
}
```

### New: `tests/CCRun.Tests/RunCommandTests.cs`
```csharp
namespace CCRun.Tests;

// Integration tests that spawn real child processes. The child inherits the
// test host's console, so we assert on exit codes (the FR-1.4 contract), not
// on captured child stdout.
public class RunCommandTests
{
    private static int Run(params string[] args) =>
        Cli.Run(args, new StringWriter(), new StringWriter());

    [Fact]
    public void Run_TrueCommand_ReturnsZero() =>
        Assert.Equal(0, Run("run", "true"));

    [Fact]
    public void Run_FalseCommand_ReturnsNonZero() =>
        Assert.NotEqual(0, Run("run", "false"));

    [Fact]
    public void Run_PropagatesChildExitCode() =>
        Assert.Equal(3, Run("run", "/bin/sh", "-c", "exit 3"));

    [Fact]
    public void Run_MissingBinary_ReturnsCommandNotFound() =>
        Assert.Equal(ExitCodes.CommandNotFound, Run("run", "ccrun-no-such-binary-xyz"));
}
```

Keep the existing `ScaffoldTests` placeholder as-is (harmless), or delete it now
that real tests exist — either is fine; recommend deleting for cleanliness.

## Verification

1. `dotnet build` — compiles clean.
2. `dotnet test` — all `CliTests` + `RunCommandTests` pass.
3. Manual acceptance (BRD §10.1):
   - `dotnet run --project src/CCRun run echo Hello Coding Challenges!`
     → prints `Hello Coding Challenges!`, then `echo $?` shows `0`.
   - `dotnet run --project src/CCRun run ls madeupdir`
     → prints the `ls` error, `echo $?` shows nonzero.
   - `dotnet run --project src/CCRun` (no args) and `... run` (no command)
     → usage message, nonzero exit.
   - `dotnet run --project src/CCRun run /bin/sh` → interactive shell (FR-1.3),
     confirming inherited stdin/stdout.

## Notes / decisions

- Usage errors return `1`; launch failures reuse shell conventions (`127`
  not-found, `126` not-executable). Command exit codes pass through verbatim.
- Signal handling (Ctrl+C forwarding) is not addressed in Phase 1; revisit with
  the re-exec architecture in Phase 2.
- Git policy: Claude stages only; the human commits.
