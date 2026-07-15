# Plan — Phase 3: Filesystem Isolation (chroot into a root filesystem)

## Context

CCRun is at Phase 2: `ccrun run <command>` unshares a **UTS namespace** in the
parent stage, re-execs itself as the hidden `__child` init stage, sets the
hostname there, and hands off to the user command via `ProcessRunner`
(`Process.Start`). There is still no filesystem isolation.

Phase 3 (BRD §7 FR-3.1–3.3, Milestone M3) adds the first filesystem primitive:
the container **`chroot`s into a root filesystem** (the git-ignored
`alpine-rootfs/` for now, pulled images later) and `chdir("/")`, so the command
sees that tree as `/` and cannot traverse above it. The re-exec architecture from
Phase 2 is exactly where this slots in — the `__child` stage does the `chroot`
before launching the command, mirroring how it already does `sethostname`.

`chroot(2)` needs `CAP_SYS_CHROOT` (root), like Phase 2's `unshare`; rootless
arrives in Phase 5. `alpine-rootfs/` is present on disk with the `ALPINE_FS_ROOT`
marker and a real `bin/busybox`.

**Decisions (confirmed with user):**
1. **Opt-in `--rootfs <path>`.** `chroot` happens only when `--rootfs` is given;
   without it, `run` keeps Phase 2 behavior (UTS-only, no fs isolation). This is
   backward-compatible and maps cleanly to Phase 8, where an image name will
   supply the rootfs. The acceptance invocation becomes
   `ccrun run --rootfs alpine-rootfs /bin/busybox sh`.
2. **`execvp(2)` on the chroot path.** After `chroot`, the .NET runtime's own
   files may sit outside the new root, so further managed work (`Process.Start`)
   can fail non-deterministically (lazy assembly/JIT loads, single-file
   extraction dir). On the chroot path the child therefore **replaces its process
   image** with the command via `execvp`. The non-chroot path keeps
   `ProcessRunner`/`Process.Start` unchanged and unit-testable. This pulls the
   Phase-2-deferred `execvp` work forward — Phase 4 (PID 1 semantics) wanted it
   anyway.

**Scope boundary:** Phase 3 is plain `chroot` only. The **mount namespace**
(`CLONE_NEWNS`), private `/proc`, and `pivot_root` belong to Phase 4 — `unshare`
stays `CLONE_NEWUTS` only here, keeping the phases cleanly separated.

## Requirements mapped to design

- **FR-3.1** chroot into the root + `chdir("/")` → `Libc.Chroot` then
  `Libc.Chdir("/")` in `ChildCommand`, after `sethostname`, before hand-off.
- **FR-3.2** cannot escape the root; `ALPINE_FS_ROOT` visible → `chdir("/")` after
  `chroot` closes the classic cwd-escape; verified by root-gated integration
  tests (`cd ..` from `/` stays put; marker file present).
- **FR-3.3** BusyBox shell via `ccrun run --rootfs alpine-rootfs /bin/busybox sh`
  → the `execvp` hand-off runs the in-rootfs busybox with inherited stdio.
- **NFR-2** surface errno → `Chroot`/`Chdir`/`Execvp` all use `SetLastError`;
  failures print `Libc.LastErrorMessage()`.
- **NFR-3** clean failure paths → each step checks its return and bails with
  `RuntimeError` before the command runs.

## Architecture notes / design choices

- **Rootfs flows parent→child via an env var** (`CCRUN_ROOTFS`), consistent with
  `CCRUN_HOSTNAME`. The parent resolves the user path to an **absolute** path
  (`Path.GetFullPath`) and validates it exists *before* `unshare`, so a bad path
  fails early with a clear message and no namespace is created.
- **Validation lives in the parent** (`RunCommand`), not the parser: `RunOptions`
  stays pure/filesystem-free and unit-testable. Only existence + directory-ness
  is checked — **not** the `ALPINE_FS_ROOT` marker, which is Alpine-specific;
  Phase 8 chroots into pulled images that won't have it.
- **Two hand-off models, each justified:** no-rootfs → `Process.Start` is safe
  (no chroot) and keeps the cheap non-root `ProcessRunnerTests`; with-rootfs →
  `execvp` because `Process.Start` after `chroot` is unsafe. The command's exit
  code becomes the `__child` process's exit code, which `ReExec.RunChild`'s
  `WaitForExit`/`ExitCode` already propagates upward unchanged (FR-1.4).
- **`execvp` argv is NULL-terminated**, `argv[0]` = program name. Built as a
  `string?[]` with a trailing `null`; the `LibraryImport` UTF-8 marshaller turns
  a null element into a null pointer. (If the source generator rejects the string
  array, fall back to manual `Marshal`-based argv construction — not expected.)
- **Stdio survives** `execvp`: the `__child` already inherits ccrun's
  stdin/stdout/stderr (no redirection), and `execvp` keeps open fds, so the
  command gets the terminal (FR-1.3). Use **absolute** command paths under
  `--rootfs`; a bare name would hit `execvp`'s `PATH` search inside the new root.

## Files

### Update: `src/CCRun/Native/Libc.cs`
Add `EACCES` and the `chroot` / `chdir` / `execvp` imports (same
`[LibraryImport]` + `SetLastError` pattern as the existing calls).
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
    /// <summary>Flag for <see cref="Unshare"/>: new UTS namespace (hostname).</summary>
    public const int CLONE_NEWUTS = 0x04000000;

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
```

### Update: `src/CCRun/RunOptions.cs`
Add a nullable `Rootfs` field (null ⇒ no chroot) and `--rootfs` parsing in both
`--rootfs X` and `--rootfs=X` forms, mirroring `--hostname`. Parsing stays pure —
no path resolution or existence checks here.
```csharp
namespace ccrun;

/// <summary>
/// Parsed form of `ccrun run [options] <command> [args...]`. Options are the
/// leading `--`-prefixed tokens; the first non-option token is the command and
/// everything after it is passed through to the command unchanged.
/// </summary>
public sealed record RunOptions(
    string Hostname,
    string? Rootfs,
    string Command,
    IReadOnlyList<string> CommandArgs)
{
    public const string DefaultHostname = "container";

    /// <summary>
    /// Parses run arguments. Returns null and writes a usage/error message to
    /// <paramref name="stderr"/> on invalid input (missing command, unknown
    /// option, or a --hostname/--rootfs without a value).
    /// </summary>
    public static RunOptions? Parse(string[] args, TextWriter stderr)
    {
        string hostname = DefaultHostname;
        string? rootfs = null;
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
            else if (a == "--rootfs")
            {
                if (i + 1 >= args.Length)
                {
                    stderr.WriteLine("ccrun run: --rootfs requires a value");
                    return null;
                }
                rootfs = args[++i];
            }
            else if (a.StartsWith("--rootfs=", StringComparison.Ordinal))
            {
                rootfs = a["--rootfs=".Length..];
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
            stderr.WriteLine("usage: ccrun run [--hostname <name>] [--rootfs <path>] <command> [args...]");
            return null;
        }

        return new RunOptions(hostname, rootfs, args[i], args[(i + 1)..]);
    }
}
```

### Update: `src/CCRun/Commands/RunCommand.cs` (parent/host stage)
Resolve + validate `--rootfs` before `unshare`; pass the absolute path down via a
`RunOptions` copy. `unshare` stays `CLONE_NEWUTS` only.
```csharp
using System.Runtime.InteropServices;

namespace ccrun;

/// <summary>
/// `run` (parent/host stage): parses options, optionally validates the target
/// rootfs, creates the container's UTS namespace via unshare(2), then re-executes
/// ccrun in the hidden init stage (see <see cref="ReExec"/>) which sets the
/// hostname, optionally chroots into the rootfs, and launches the user command.
/// Requires root (CAP_SYS_ADMIN / CAP_SYS_CHROOT) until rootless mode (Phase 5).
/// </summary>
public static class RunCommand
{
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = RunOptions.Parse(args, stderr);
        if (options is null)
            return ExitCodes.UsageError;

        // Resolve --rootfs to an absolute path and confirm it exists *before*
        // touching namespaces, so a bad path fails early and cleanly. Absolute
        // because the child chroots after inheriting a fresh cwd. (Marker/rootfs
        // validation is deliberately shallow — Phase 8 images have no marker.)
        string? rootfs = null;
        if (options.Rootfs is not null)
        {
            rootfs = Path.GetFullPath(options.Rootfs);
            if (!Directory.Exists(rootfs))
            {
                stderr.WriteLine($"ccrun: rootfs '{options.Rootfs}' does not exist or is not a directory");
                return ExitCodes.RuntimeError;
            }
        }

        // New UTS namespace so the container holds its own hostname without
        // touching the host's (FR-2.1, FR-2.3). The re-exec'd child inherits it.
        if (Libc.Unshare(Libc.CLONE_NEWUTS) != 0)
        {
            int err = Marshal.GetLastPInvokeError();
            stderr.WriteLine($"ccrun: unshare(CLONE_NEWUTS) failed: {Libc.LastErrorMessage()}");
            if (err == Libc.EPERM)
                stderr.WriteLine("hint: ccrun needs elevated privileges for namespaces; " +
                                 "re-run under sudo (rootless mode arrives in Phase 5).");
            return ExitCodes.RuntimeError;
        }

        return ReExec.RunChild(options with { Rootfs = rootfs }, stderr);
    }
}
```

### Update: `src/CCRun/Container/ReExec.cs`
Add the `CCRUN_ROOTFS` env var and set it when a rootfs is present.
```csharp
using System.Diagnostics;
using System.Reflection;

namespace ccrun;

/// <summary>
/// Re-invokes ccrun itself in the hidden <c>__child</c> init stage. The parent
/// creates the namespace(s); this child (inheriting them) performs in-namespace
/// setup (sethostname, chroot, later proc mount) before running the user command.
/// Stdio is inherited so interactivity survives the extra hop.
/// </summary>
internal static class ReExec
{
    /// <summary>Hidden verb marking the re-executed init stage.</summary>
    public const string ChildVerb = "__child";

    /// <summary>Env var carrying the container hostname parent -> child.</summary>
    public const string HostnameEnv = "CCRUN_HOSTNAME";

    /// <summary>Env var carrying the absolute rootfs path parent -> child (unset => no chroot).</summary>
    public const string RootfsEnv = "CCRUN_ROOTFS";

    public static int RunChild(RunOptions options, TextWriter stderr)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        psi.Environment[HostnameEnv] = options.Hostname;
        if (options.Rootfs is not null)
            psi.Environment[RootfsEnv] = options.Rootfs;

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

### Update: `src/CCRun/Commands/ChildCommand.cs` (init stage)
Read `CCRUN_ROOTFS`; if set, `chroot` + `chdir("/")` then `execvp` the command
(replacing the process image); otherwise keep the Phase 2 `ProcessRunner` path.
```csharp
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
```

### Update: `src/CCRun/Cli.cs`
Usage text only — add `[--rootfs <path>]` and bump the phase note. Dispatch is
unchanged (`__child` already routes).
- Change the run usage line to:
  ```csharp
  w.WriteLine("  ccrun run [--hostname <name>] [--rootfs <path>] <command> [args...]   run a command in a container");
  ```
- Change the trailing note to:
  ```csharp
  w.WriteLine("Phase 3: 'run' isolates the hostname (UTS ns) and, with --rootfs, chroots into a root FS (needs sudo).");
  ```

### Unchanged: `src/CCRun/ExitCodes.cs`, `src/CCRun/Program.cs`, `src/CCRun/Container/ProcessRunner.cs`, `src/CCRun/CCRun.csproj`
No changes. Phase 3 reuses `RuntimeError` (125), `CommandNotExecutable` (126),
`CommandNotFound` (127). `ProcessRunner` still serves the no-rootfs path.

## Tests

### Update: `tests/CCRun.Tests/RunOptionsTests.cs`
Existing tests still compile (property names `Hostname`/`Command`/`CommandArgs`
unchanged; nobody calls the constructor directly). Add pure-parse facts:
- `RootfsOption_SpaceSeparated`: `--rootfs /r true` → `Rootfs == "/r"`, command `true`.
- `RootfsOption_EqualsForm`: `--rootfs=/r true` → `Rootfs == "/r"`.
- `DefaultRootfs_IsNull_WhenNoOption`: `true` → `Rootfs is null`.
- `RootfsAndHostname_Combined`: `--hostname web --rootfs /r /bin/sh` → both set, command `/bin/sh`.
- `RootfsWithoutValue_ReturnsNull_WithMessage`: `--rootfs` → null + "--rootfs requires a value".
- `RootfsAfterCommand_PassesThrough`: `echo --rootfs /r` → command `echo`, args `["--rootfs","/r"]`, `Rootfs is null`.

### Update: `tests/CCRun.Tests/RunCommandTests.cs`
Add a **non-root** fact (validation runs before `unshare`, so it's deterministic
regardless of privilege):
```csharp
[Fact]
public void Run_MissingRootfs_ReturnsRuntimeError()
{
    // Rootfs is validated before any unshare, so this is reachable without root.
    var (code, err) = Run("run", "--rootfs", "/no/such/rootfs/xyz", "true");
    Assert.Equal(ExitCodes.RuntimeError, code);
    Assert.Contains("does not exist", err);
}
```

### Update: `tests/CCRun.Tests/NamespaceIntegrationTests.cs`
Add root-gated chroot tests, also gated on `alpine-rootfs` being present (so they
skip on a machine without the rootfs). Add a helper that walks up from the test
assembly location to find the repo's `alpine-rootfs/ALPINE_FS_ROOT`:
```csharp
// Repo-root-relative alpine rootfs, located by walking up from the test binary
// until the ALPINE_FS_ROOT marker is found. Null if not present (tests skip).
private static string? FindAlpineRootfs()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "alpine-rootfs");
        if (File.Exists(Path.Combine(candidate, "ALPINE_FS_ROOT")) &&
            File.Exists(Path.Combine(candidate, "bin", "busybox")))
            return candidate;
    }
    return null;
}
```
Tests (each `[SkippableFact]`, `Skip.IfNot(RunCommandTests.IsRoot, ...)` then
`Skip.If(FindAlpineRootfs() is null, "alpine-rootfs not present")`):
- `Chroot_LandsInRootfs_MarkerVisible` (FR-3.2/3.3): `run --rootfs <alpine>
  /bin/busybox sh -c "[ -f /ALPINE_FS_ROOT ]"` → exit 0. Proves the new root is
  the Alpine tree, reached via an in-rootfs busybox.
- `Chroot_CannotEscapeAboveRoot` (FR-3.2): `... /bin/busybox sh -c "cd .. && [ -f
  /ALPINE_FS_ROOT ]"` → exit 0. `cd ..` from `/` stays at the root; the marker is
  still there.
- `Chroot_MissingCommandInRootfs_ReturnsNotFound`: `run --rootfs <alpine>
  /no/such/bin` → `ExitCodes.CommandNotFound` (exercises the `execvp` failure
  mapping end-to-end).

The existing `FullPipeline_TrueCommand_ReturnsZero` continues to cover the
no-rootfs (`Process.Start`) path.

## Verification

1. `dotnet build` — clean compile (confirms the new `LibraryImport` decls,
   including the `string?[]` argv marshalling, satisfy the source generator).
2. `dotnet test` — non-root: parse tests + the missing-rootfs `RuntimeError` test
   pass; all root-gated tests skip. `sudo dotnet test`: the chroot integration
   tests run (they also self-skip if `alpine-rootfs` is absent).
3. Manual acceptance (build first so sudo runs the apphost directly, not a
   build-as-root under `dotnet run`):
   ```sh
   dotnet build
   BIN=src/CCRun/bin/Debug/net10.0/CCRun

   # FR-3.3 + FR-3.1: busybox shell inside the Alpine root
   sudo $BIN run --rootfs alpine-rootfs /bin/busybox sh -c 'echo in:; ls / ; cat /etc/alpine-release'

   # FR-3.2: marker visible, cannot escape above /
   sudo $BIN run --rootfs alpine-rootfs /bin/busybox sh -c 'ls -a / | grep ALPINE_FS_ROOT; cd .. ; pwd'
   # expect: ALPINE_FS_ROOT listed, pwd prints /

   # Interactive shell (FR-1.3 through chroot + execvp)
   sudo $BIN run --rootfs alpine-rootfs /bin/busybox sh   # type `hostname`, `ls /`, `exit`

   # Exit-code passthrough across chroot + execvp (FR-1.4)
   sudo $BIN run --rootfs alpine-rootfs /bin/busybox sh -c 'exit 3'; echo $?   # 3

   # Bad rootfs fails early, before any namespace work (no sudo needed)
   $BIN run --rootfs /no/such/dir true; echo $?   # "does not exist" + 125

   # Phase 2 path unchanged: no --rootfs => UTS-only, no chroot
   sudo $BIN run /bin/sh -c hostname   # prints: container
   ```

## Notes / decisions

- **Phase boundary:** no mount namespace / private `/proc` / `pivot_root` here —
  those are Phase 4. `unshare` stays `CLONE_NEWUTS` only.
- **execvp pulled forward** from the Phase 2 "deferred to Phase 4" note, because
  `chroot` makes post-chroot managed work unsafe. Only the chroot path uses it;
  the no-rootfs path keeps `Process.Start`/`ProcessRunner` and its non-root unit
  tests.
- **Rootfs validation is shallow** (exists + is a directory), not marker-based,
  so it carries forward to pulled images in Phase 8.
- **Use absolute command paths** with `--rootfs`; a bare name relies on `execvp`
  searching `PATH` inside the new root.
- Docs (README.md / CLAUDE.md "currently at Phase N") update is a follow-up commit
  after the code lands, per the repo's existing "update docs" commit pattern — not
  included in this change set unless you want it bundled.
- **Git policy:** Claude stages only; the human runs `git commit`.
