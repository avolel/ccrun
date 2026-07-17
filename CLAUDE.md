# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CCRun is a "Build Your Own Docker" learning project: a lightweight Linux container
runtime in C# / .NET 10, built in 8 phases. **The repo is currently at Phase 4
(process isolation).** `ccrun run <command>` puts the command in a new UTS
namespace so it gets its own hostname, runs it, and passes back its exit code.

**How much isolation you get is gated on `--rootfs`**, and that split is
deliberate:

- **With `--rootfs <path>`** — the full stack. The command runs in UTS + mount +
  PID namespaces, `chroot`s into the given root filesystem (then `chdir("/")`) so
  it sees that as `/` and cannot climb above it, and gets a private `/proc` so
  `ps` shows only container processes. It ends up as PID 1 of its own namespace.
- **Without `--rootfs`** — Phase 2 behaviour only: a UTS namespace (hostname), and
  the command is spawned with managed `Process.Start`. It deliberately stays here
  because that path must not become PID 1 of a PID namespace.

Still missing: user namespace, resource limits (cgroups), and image handling
(`pull`, registry client). `pivot_root` is deferred — Phase 4 kept the plain
`chroot`, since the FR-4.x requirements do not need the stronger boundary.

Creating namespaces needs `CAP_SYS_ADMIN`, `chroot` needs `CAP_SYS_CHROOT`, and
mounting needs `CAP_SYS_ADMIN`, so `ccrun run` requires root/sudo until rootless
mode lands in Phase 5. Phase 2 set up the parent/child re-exec architecture that
every later phase builds on: the parent process creates the namespaces, then
re-runs ccrun in a hidden `__child` stage that does the in-namespace setup
(`sethostname`, then the optional make-private mount + `chroot` + `/proc` mount)
before launching the user command.

Two non-obvious mechanics worth knowing before editing:

- **`unshare(CLONE_NEWPID)` does not move the caller.** It makes the *next* forked
  process PID 1. That fork is the `__child` the parent re-execs, so the user's
  command lands as PID 1 — which is exactly what a private `/proc` wants.
- **The `/proc` mount needs no teardown.** It lives only in the container's mount
  namespace, which the kernel destroys once the PID namespace empties, on success
  and error paths alike. Explicit unmounting would be impossible after `execvp`
  replaces the process image anyway.

On the chroot path the child hands off with `execvp` (replacing its process image)
rather than `Process.Start`, because after `chroot` the .NET runtime's own files
may sit outside the new root; the no-`--rootfs` path keeps `Process.Start`. See
`docs/code-overview/code-overview.md` for a full walkthrough of how it works.

Remaining phases add: user namespace + cgroup v2 + rootless mode (5), and image
pull + registry client (7–8).

## Commands

```sh
dotnet build                              # compile the solution
dotnet test                               # run all xUnit tests
dotnet test --filter FullyQualifiedName~CliTests   # run a single test/class
dotnet run --project src/CCRun -- [args]  # run the CLI (no args → usage, exit 1)
dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true  # single-file binary
```

The solution file is `CCRun.slnx` (the .NET 10 XML format), not `.sln`.

`ccrun run` now creates a namespace, which needs root. To try it, build first
and run the produced binary under sudo — `sudo dotnet run` would trigger a build
as root and clutter the output:

```sh
dotnet build
sudo src/CCRun/bin/Debug/net10.0/CCRun run /bin/sh -c hostname   # prints: container
# chroot into the Alpine rootfs and run its in-tree busybox:
sudo src/CCRun/bin/Debug/net10.0/CCRun run --rootfs alpine-rootfs /bin/busybox sh -c 'cat /etc/alpine-release'
# Phase 4: the command is PID 1 and sees only its own processes:
sudo src/CCRun/bin/Debug/net10.0/CCRun run --rootfs alpine-rootfs /bin/busybox sh -c 'echo $$'   # prints: 1
sudo src/CCRun/bin/Debug/net10.0/CCRun run --rootfs alpine-rootfs /bin/busybox ps               # only container procs
```

## Structure

- `src/CCRun/` — the CLI console app (`net10.0`). `Program.cs` is a thin
  top-level-statement entrypoint that delegates to `Cli.Run`. `Cli.cs` does
  verb dispatch and usage; `ExitCodes.cs` holds named exit codes; `RunOptions.cs`
  parses the arguments to `run` (`--hostname`, `--rootfs`). `Commands/` has one
  class per command: `RunCommand` is the parent/host stage (validates `--rootfs`,
  then `unshare`s UTS, plus mount + PID when a rootfs was given), and the hidden
  `ChildCommand` is the re-exec'd `__child` init stage (`sethostname`, then on the
  rootfs path: recursive make-private mount → `chroot` → `chdir("/")` → mount
  `/proc` → `execvp`). `Native/Libc.cs` holds the libc P/Invoke declarations
  (`unshare`, `sethostname`, `chroot`, `chdir`, `mount`, `execvp`, `geteuid`) and
  the constants they need (`CLONE_NEWUTS`/`CLONE_NEWNS`/`CLONE_NEWPID`, the
  `MS_NOSUID`/`MS_NODEV`/`MS_NOEXEC`/`MS_REC`/`MS_PRIVATE` mount flags, plus
  `EACCES`/`EPERM`).
  `Container/` holds the runtime plumbing: `ReExec` re-launches ccrun as its own
  child (passing hostname/rootfs down via the `CCRUN_HOSTNAME`/`CCRUN_ROOTFS` env
  vars), and `ProcessRunner` spawns the user command on the no-`--rootfs` path and
  returns its exit code. Command logic takes injectable `TextWriter` stdout/stderr
  (no `Console` statics) so it is unit-testable.
- `tests/CCRun.Tests/` — xUnit tests, references `src/CCRun`. `CliTests` covers
  verb dispatch and usage; `RunOptionsTests` covers argument parsing;
  `ProcessRunnerTests` asserts the child-process exit-code contract;
  `RunCommandTests` covers the parent stage, including the "needs sudo" failure
  and the missing-`--rootfs` error; `NamespaceIntegrationTests` exercises the
  full unshare + sethostname + chroot + `/proc` + execvp pipeline, including the
  PID-1 and private-`/proc` assertions. Those two assert on exit codes rather than
  output, because `execvp` replaces the process image and its stdout never reaches
  the `StringWriter` seam. The namespace tests need
  root, so they skip automatically (via `Xunit.SkippableFact`) when `dotnet test`
  runs unprivileged; the chroot tests additionally skip if `alpine-rootfs/` is
  absent.
- `alpine-rootfs/` — Alpine minirootfs, **git-ignored**, the rootfs used by
  `--rootfs` for chroot testing. Recreate via the commands in README.md if
  missing; presence is verified by the `ALPINE_FS_ROOT` marker file and
  `alpine-rootfs/bin/busybox`. It must contain a `/proc` directory to serve as the
  mountpoint for the private `/proc` (the stock minirootfs does).
- `docs/code-overview/code-overview.md` — a detailed, educational walkthrough of the whole runtime
  (the two-stage re-exec model, the `run` trace, chroot, the PID/mount namespaces
  and private `/proc`, `execvp` vs `Process.Start`, the libc layer, and testing).
  Start here to understand *how* the code works.

## Conventions / constraints

- **Target Linux + cgroup v2 only.** Later phases use Linux-specific syscalls via
  P/Invoke; `AllowUnsafeBlocks` is already enabled in the csproj for this.
- Keep `src/CCRun/CCRun.csproj` **runtime-agnostic** (no hard-pinned
  `RuntimeIdentifier`) so it can cross-compile to arm64 — select the RID at
  `publish` time instead.
- `InvariantGlobalization` is on for lean self-contained builds.

## Git Policy

**Claude is NEVER allowed to commit to this repository.**

Claude may stage files and draft a commit message, but must stop there. The human reviews the staged changes and runs `git commit` manually.

## Working agreement
When proposing a plan or a change set, always list every new/updated file and include the full code to be added/changed, so it can be reviewed before implementation.

All prose — code comments, README updates, CLAUDE.md updates, and any other documentation — must be written in plain English aimed at a mid- to senior-level software engineer. Explain the *why* and any non-obvious behaviour; skip explanations of language basics or things the code already states plainly.
