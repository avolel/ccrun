# Plan — Phase 2: Hostname Isolation (UTS namespace) + re-exec architecture

## Context

CCRun is at Phase 1: `ccrun run <command>` spawns the command directly (no
isolation). Phase 2 (BRD §7 FR-2.1–2.4, Milestone M2) adds the first real
container primitive — a **UTS namespace** so the container gets its own
hostname — and, more importantly, establishes the **parent/child re-execution
architecture** that every later phase (chroot, PID/mount/user namespaces, proc
mount, cgroups) builds on.

The flow becomes:

1. **Parent/host stage** (`ccrun run ...`): parse options, `unshare(CLONE_NEWUTS)`
   to create the namespace, then re-exec ccrun itself in a hidden init stage.
2. **Init stage** (`ccrun __child ...`): runs inside the new namespace, calls
   `sethostname("container")`, then launches the user command — inheriting stdio
   so interactivity (FR-1.3) survives the extra hop, and propagating its exit
   code (FR-1.4).

`unshare(CLONE_NEWUTS)` needs `CAP_SYS_ADMIN`, so Phase 2 requires **root/sudo**
(BRD assumption 5; rootless arrives in Phase 5). Confirmed environment: this host
runs as euid 1000, kernel 6.17, xUnit 2.9.3 (supports dynamic `Assert.Skip*`).

**Decisions (confirmed with user):** add a `--hostname <name>` flag now
(default `container`); when `unshare` fails with `EPERM`, hard-fail with a
"re-run under sudo" hint (no silent unisolated fallback).

## Requirements mapped to design

- **FR-2.1** new UTS namespace → `Libc.unshare(CLONE_NEWUTS)` in the parent stage.
- **FR-2.2** set hostname via `sethostname` P/Invoke → `ChildCommand` in the init stage; value from `--hostname` (default `container`).
- **FR-2.3** host hostname untouched → hostname is only set *after* unshare, inside the new namespace; the host namespace is never written.
- **FR-2.4** parent creates namespace, child sets hostname + execs command → `RunCommand` (parent) → `ReExec` → `ChildCommand` (init), via a hidden `__child` verb.
- **NFR-2** surface errno → every P/Invoke uses `SetLastError`; failures print `Win32Exception(GetLastPInvokeError()).Message`.

## Architecture notes / design choices

- **Config flows parent→child via an env var** (`CCRUN_HOSTNAME`), not argv, so
  the child's argv stays cleanly `__child <command> <args...>` with no re-parsing
  ambiguity about where the command begins.
- **Self-path resolution** for re-exec handles both dev and published modes:
  under `dotnet run` / framework-dependent, `Environment.ProcessPath` is the
  apphost (re-invoked directly) or the `dotnet` muxer (prepend the entry dll);
  a single-file publish is the binary itself (re-invoked directly).
- **Init stage uses `Process.Start`** (consistent with Phase 1, trivial
  exit-code propagation). Replacing the process image with `execvp` is deferred
  to Phase 4, where PID-1 semantics inside the PID namespace make it matter.
- **Multithreading caveat (future):** `unshare(CLONE_NEWUTS)` is safe in a
  multithreaded process, but `CLONE_NEWUSER` (Phase 5) is not — noted now so the
  Phase 5 design accounts for it. No action in Phase 2.

## Files

### New: `src/CCRun/Native/Libc.cs`
```csharp
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
    /// <summary>Flag for <see cref="unshare"/>: new UTS namespace (hostname).</summary>
    public const int CLONE_NEWUTS = 0x04000000;

    /// <summary>errno EPERM — operation not permitted (needs CAP_SYS_ADMIN / root).</summary>
    public const int EPERM = 1;

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int unshare(int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sethostname(string name, nuint len);

    [LibraryImport("libc")]
    internal static partial uint geteuid();

    /// <summary>Human-readable message for the last failed P/Invoke's errno.</summary>
    public static string LastErrorMessage() =>
        new Win32Exception(Marshal.GetLastPInvokeError()).Message;
}
```

### New: `src/CCRun/RunOptions.cs`
```csharp
namespace ccrun;

/// <summary>
/// Parsed form of `ccrun run [options] <command> [args...]`. Options are the
/// leading `--`-prefixed tokens; the first non-option token is the command and
/// everything after it is passed through to the command unchanged.
/// </summary>
public sealed record RunOptions(string Hostname, string Command, IReadOnlyList<string> CommandArgs)
{
    public const string DefaultHostname = "container";

    /// <summary>
    /// Parses run arguments. Returns null and writes a usage/error message to
    /// <paramref name="stderr"/> on invalid input (missing command, unknown
    /// option, or a --hostname without a value).
    /// </summary>
    public static RunOptions? Parse(string[] args, TextWriter stderr)
    {
        string hostname = DefaultHostname;
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }              // explicit end of options
            if (!a.StartsWith("--", StringComparison.Ordinal))
                break;                                   // first positional => command

            if (a == "--hostname")
            {
                if (i + 1 >= args.Length)
                {
                    stderr.WriteLine("ccrun run: --hostname requires a value");
                    return null;
                }
                hostname = args[++i];
            }
            else if (a.StartsWith("--hostname=", StringComparison.Ordinal))
            {
                hostname = a["--hostname=".Length..];
            }
            else
            {
                stderr.WriteLine($"ccrun run: unknown option '{a}'");
                return null;
            }
        }

        if (i >= args.Length)
        {
            stderr.WriteLine("ccrun run: missing command");
            stderr.WriteLine("usage: ccrun run [--hostname <name>] <command> [args...]");
            return null;
        }

        return new RunOptions(hostname, args[i], args[(i + 1)..]);
    }
}
```

### New: `src/CCRun/Container/ProcessRunner.cs`
Extracted, verbatim, from the Phase 1 `RunCommand` spawn block so the direct
spawn and the re-exec'd init stage share one implementation and error contract.
```csharp
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
```

### New: `src/CCRun/Container/ReExec.cs`
```csharp
using System.Diagnostics;
using System.Reflection;

namespace ccrun;

/// <summary>
/// Re-invokes ccrun itself in the hidden <c>__child</c> init stage. The parent
/// creates the namespace(s); this child (inheriting them) performs in-namespace
/// setup (sethostname, later chroot / proc mount) before running the user
/// command. Stdio is inherited so interactivity survives the extra hop.
/// </summary>
internal static class ReExec
{
    /// <summary>Hidden verb marking the re-executed init stage.</summary>
    public const string ChildVerb = "__child";

    /// <summary>Env var carrying the container hostname parent -> child.</summary>
    public const string HostnameEnv = "CCRUN_HOSTNAME";

    public static int RunChild(RunOptions options, TextWriter stderr)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        psi.Environment[HostnameEnv] = options.Hostname;

        // Resolve how to re-invoke *this* program. Published apphost / single-file
        // binaries are invoked directly; under the `dotnet` muxer we must prepend
        // the managed entry dll.
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve Environment.ProcessPath for re-exec");
        psi.FileName = exe;
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Assembly.GetEntryAssembly()?.Location is { Length: > 0 } loc
                ? loc
                : throw new InvalidOperationException("cannot resolve entry assembly for re-exec");
            psi.ArgumentList.Add(dll);
        }

        psi.ArgumentList.Add(ChildVerb);
        psi.ArgumentList.Add(options.Command);
        foreach (var a in options.CommandArgs)
            psi.ArgumentList.Add(a);

        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for re-exec");
        child.WaitForExit();
        return child.ExitCode; // propagate the init stage's (and thus the command's) exit code
    }
}
```

### Rewrite: `src/CCRun/Commands/RunCommand.cs` (parent/host stage)
```csharp
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
    // args = [ [--hostname <name>] command, arg1, arg2, ... ]
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = RunOptions.Parse(args, stderr);
        if (options is null)
            return ExitCodes.UsageError;

        // New UTS namespace so the container holds its own hostname without
        // touching the host's (FR-2.1, FR-2.3). Affects this process; the
        // re-exec'd child inherits it.
        if (Libc.unshare(Libc.CLONE_NEWUTS) != 0)
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
```

### New: `src/CCRun/Commands/ChildCommand.cs` (init stage)
```csharp
using System.Text;

namespace ccrun;

/// <summary>
/// Hidden `__child` init stage, re-executed by <see cref="ReExec"/> inside the
/// namespaces the parent created. Sets the container hostname (FR-2.2) then
/// hands off to the user command. Later phases add chroot, proc mount, etc.
/// here before the hand-off.
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

        if (Libc.sethostname(hostname, (nuint)Encoding.UTF8.GetByteCount(hostname)) != 0)
        {
            stderr.WriteLine($"ccrun: sethostname('{hostname}') failed: {Libc.LastErrorMessage()}");
            return ExitCodes.RuntimeError;
        }

        return ProcessRunner.Run(args[0], args[1..], stderr);
    }
}
```

### Update: `src/CCRun/Cli.cs`
Add the hidden `__child` dispatch case and extend the `run` usage line. Keep
`__child` out of the usage text (internal only).
- Add after the `run` case:
  ```csharp
  case ReExec.ChildVerb:
      return ChildCommand.Execute(args.AsSpan(1).ToArray(), stdout, stderr);
  ```
- In `PrintUsage`, change the run line to:
  ```csharp
  w.WriteLine("  ccrun run [--hostname <name>] <command> [args...]   run a command in a container");
  ```
  and update the trailing note to:
  ```csharp
  w.WriteLine("Phase 2: 'run' isolates the hostname in a new UTS namespace (needs sudo).");
  ```

### Update: `src/CCRun/ExitCodes.cs`
Add a setup-failure code between `UsageError` and `CommandNotExecutable`:
```csharp
    // Container setup failed before the user command ran (namespace / hostname
    // setup, re-exec). Mirrors Docker's 125 "runtime error" convention.
    public const int RuntimeError = 125;
```

### Update: `src/CCRun/CCRun.csproj`
Expose internals to the test project (so `ProcessRunner`, `ReExec`, `Libc` stay
`internal` but testable):
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="CCRun.Tests" />
  </ItemGroup>
```

## Tests

### New: `tests/CCRun.Tests/RunOptionsTests.cs`
Pure parsing (no root, no spawning): default hostname; `--hostname web`;
`--hostname=web`; `--` separator then command; flags **after** the command pass
through untouched (`run echo --hostname` → command `echo`, args `[--hostname]`);
`ls -la` → command `ls`, args `[-la]`; missing command → null + "missing command";
unknown option → null + "unknown option"; `--hostname` with no value → null.

### New: `tests/CCRun.Tests/ProcessRunnerTests.cs`
The Phase 1 exit-code contract, moved here and calling `ProcessRunner.Run`
directly (no namespaces/root): `true`→0, `false`→nonzero, `/bin/sh -c "exit 3"`→3,
missing binary→`CommandNotFound`.

### Rewrite: `tests/CCRun.Tests/RunCommandTests.cs`
Parent-stage behavior through the `Cli.Run` seam:
- `run` (no command) → `UsageError` (parsing fails before any `unshare`).
- `run --bogus true` → `UsageError`.
- **Gated `!IsRoot`** (`Assert.SkipUnless(!IsRoot, ...)`): `run true` →
  `RuntimeError` and stderr contains "sudo" (verifies the EPERM hint path; the
  failed `unshare` is a no-op, so nothing is mutated).

`IsRoot` helper: `Libc.geteuid() == 0` (visible via `InternalsVisibleTo`).

### New: `tests/CCRun.Tests/NamespaceIntegrationTests.cs`
**Gated `IsRoot`** (`Assert.SkipUnless(IsRoot, ...)`), so `dotnet test` stays
green for a normal non-root dev/CI:
- Full pipeline smoke: `run true` → 0 (unshare + re-exec + sethostname + spawn).
- Hostname applied inside the container: `run --hostname ccrun-test /bin/sh -c
  "hostname > <tempfile>"` → tempfile content trimmed == `ccrun-test`. (Robust to
  UTS-ns state in the test process since it reads a file, not the live hostname.)

Host-hostname-unchanged is verified manually (below) rather than asserted, to
avoid mutating the test runner's namespace.

## Verification

1. `dotnet build` — clean compile (confirms the `LibraryImport` source generator
   is happy).
2. `dotnet test` — non-root: parsing, `ProcessRunner`, and EPERM-hint tests pass;
   root-only tests skip. As root (`sudo dotnet test`): the gated tests run.
3. Manual acceptance (build first so sudo runs the apphost directly, avoiding the
   `dotnet run` build subprocess):
   ```sh
   dotnet build
   BIN=src/CCRun/bin/Debug/net10.0/CCRun
   hostname                                  # note the host hostname
   sudo $BIN run /bin/sh -c hostname         # prints: container  (FR-2.2)
   sudo $BIN run --hostname web /bin/sh -c hostname   # prints: web
   hostname                                  # unchanged host hostname (FR-2.3)
   sudo $BIN run /bin/sh -c 'exit 3'; echo $?  # prints: 3  (FR-1.4 through re-exec)
   $BIN run true                             # no sudo: unshare EPERM + sudo hint, exit 125
   ```

## Notes / decisions

- Setup failures (unshare, sethostname, re-exec) return `125`; the user command's
  own exit code still passes through verbatim (FR-1.4) via `ProcessRunner`.
- `--hostname` parses only as a **leading** option; tokens after the command are
  the command's own args, never ccrun options.
- Signal forwarding (Ctrl+C/SIGTERM to the container) is still deferred; revisit
  when PID-1 semantics land in Phase 4.
- Git policy: Claude stages only; the human runs `git commit`.
- CLAUDE.md: csproj stays runtime-agnostic (no pinned RID) — `InternalsVisibleTo`
  does not affect that.
```
