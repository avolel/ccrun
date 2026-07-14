# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CCRun is a "Build Your Own Docker" learning project: a lightweight Linux container
runtime in C# / .NET 10, built in 8 phases. **The repo is currently at Phase 2
(hostname isolation).** `ccrun run <command>` puts the command in a new UTS
namespace so it gets its own hostname, runs it, and passes back its exit code.
That is the first real isolation primitive — there is still no filesystem,
process, or resource isolation (chroot, PID/mount namespaces, cgroups) and no
image handling (`pull`, registry client).

Creating a namespace needs `CAP_SYS_ADMIN`, so `ccrun run` requires root/sudo
until rootless mode lands in Phase 5. Phase 2 also sets up the parent/child
re-exec architecture that every later phase builds on: the parent process creates
the namespaces, then re-runs ccrun in a hidden `__child` stage that does the
in-namespace setup (currently `sethostname`; later chroot, proc mount, …) before
launching the user command.

Remaining phases add: chroot/pivot_root (3), the PID/mount/user namespaces (4),
cgroup v2 + rootless mode (5), and image pull + registry client (7–8).

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
```

## Structure

- `src/CCRun/` — the CLI console app (`net10.0`). `Program.cs` is a thin
  top-level-statement entrypoint that delegates to `Cli.Run`. `Cli.cs` does
  verb dispatch and usage; `ExitCodes.cs` holds named exit codes; `RunOptions.cs`
  parses the arguments to `run`. `Commands/` has one class per command:
  `RunCommand` is the parent/host stage, and the hidden `ChildCommand` is the
  re-exec'd `__child` init stage. `Native/Libc.cs` holds the libc P/Invoke
  declarations (`unshare`, `sethostname`, `geteuid`). `Container/` holds the
  runtime plumbing: `ReExec` re-launches ccrun as its own child, and
  `ProcessRunner` spawns the user command and returns its exit code. Command
  logic takes injectable `TextWriter` stdout/stderr (no `Console` statics) so it
  is unit-testable.
- `tests/CCRun.Tests/` — xUnit tests, references `src/CCRun`. `CliTests` covers
  verb dispatch and usage; `RunOptionsTests` covers argument parsing;
  `ProcessRunnerTests` asserts the child-process exit-code contract;
  `RunCommandTests` covers the parent stage, including the "needs sudo" failure;
  `NamespaceIntegrationTests` exercises the full unshare + sethostname pipeline.
  The namespace tests need root, so they skip automatically (via
  `Xunit.SkippableFact`) when `dotnet test` runs unprivileged.
- `alpine-rootfs/` — Alpine minirootfs, **git-ignored**, used for chroot testing
  from Phase 3. Recreate via the commands in README.md if missing; presence is
  verified by the `ALPINE_FS_ROOT` marker file and `alpine-rootfs/bin/busybox`.

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
