# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CCRun is a "Build Your Own Docker" learning project: a lightweight Linux container
runtime in C# / .NET 10, built in 8 phases. **The repo is currently at Phase 3
(chroot filesystem isolation).** `ccrun run <command>` puts the command in a new
UTS namespace so it gets its own hostname, runs it, and passes back its exit code.
With `--rootfs <path>` it also `chroot`s into that root filesystem (then
`chdir("/")`) so the command sees it as `/` and cannot climb above it. Those are
the first two real isolation primitives. There is still no process isolation
(PID namespace), no private mount table (mount namespace, `pivot_root`, private
`/proc`), no user namespace, no resource limits (cgroups), and no image handling
(`pull`, registry client).

Creating a namespace needs `CAP_SYS_ADMIN` and `chroot` needs `CAP_SYS_CHROOT`,
so `ccrun run` requires root/sudo until rootless mode lands in Phase 5. Phase 2
set up the parent/child re-exec architecture that every later phase builds on:
the parent process creates the namespaces, then re-runs ccrun in a hidden
`__child` stage that does the in-namespace setup (`sethostname`, and now the
optional `chroot`; later proc mount, `pivot_root`, …) before launching the user
command. On the chroot path the child hands off with `execvp` (replacing its
process image) rather than `Process.Start`, because after `chroot` the .NET
runtime's own files may sit outside the new root; the no-`--rootfs` path keeps
`Process.Start`. See `docs/code-overview/code-overview.md` for a full walkthrough
of how it works.

Remaining phases add: the PID/mount/user namespaces + `pivot_root` (4), cgroup v2
+ rootless mode (5), and image pull + registry client (7–8).

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
```

## Structure

- `src/CCRun/` — the CLI console app (`net10.0`). `Program.cs` is a thin
  top-level-statement entrypoint that delegates to `Cli.Run`. `Cli.cs` does
  verb dispatch and usage; `ExitCodes.cs` holds named exit codes; `RunOptions.cs`
  parses the arguments to `run` (`--hostname`, `--rootfs`). `Commands/` has one
  class per command: `RunCommand` is the parent/host stage (validates `--rootfs`,
  then `unshare`), and the hidden `ChildCommand` is the re-exec'd `__child` init
  stage (`sethostname`, then optional `chroot` + `chdir("/")` + `execvp`).
  `Native/Libc.cs` holds the libc P/Invoke declarations (`unshare`,
  `sethostname`, `chroot`, `chdir`, `execvp`, `geteuid`, plus `EACCES`/`EPERM`).
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
  full unshare + sethostname + chroot + execvp pipeline. The namespace tests need
  root, so they skip automatically (via `Xunit.SkippableFact`) when `dotnet test`
  runs unprivileged; the chroot tests additionally skip if `alpine-rootfs/` is
  absent.
- `alpine-rootfs/` — Alpine minirootfs, **git-ignored**, the rootfs used by
  `--rootfs` for chroot testing (Phase 3). Recreate via the commands in README.md
  if missing; presence is verified by the `ALPINE_FS_ROOT` marker file and
  `alpine-rootfs/bin/busybox`.
- `docs/code-overview/code-overview.md` — a detailed, educational walkthrough of the whole runtime
  (the two-stage re-exec model, the `run` trace, chroot, `execvp` vs
  `Process.Start`, the libc layer, and testing). Start here to understand *how*
  the code works.

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
