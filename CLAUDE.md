# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CCRun is a "Build Your Own Docker" learning project: a lightweight Linux container
runtime in C# / .NET 10, built in 8 phases. **The repo is currently at Phase 1
(command parsing / `run`).** `ccrun run <command>` launches the command as a
child process and propagates its exit code, but there is no isolation yet —
no namespaces, chroot, cgroups, or image handling (`pull`, registry client).
Later phases add: P/Invoke (2), chroot/pivot_root (3), namespaces (4),
cgroup v2 (5), rootless mode (5), image pull + registry client (7–8).

## Commands

```sh
dotnet build                              # compile the solution
dotnet test                               # run all xUnit tests
dotnet test --filter FullyQualifiedName~CliTests   # run a single test/class
dotnet run --project src/CCRun -- [args]  # run the CLI (no args → usage, exit 1)
dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true  # single-file binary
```

The solution file is `CCRun.slnx` (the .NET 10 XML format), not `.sln`.

## Structure

- `src/CCRun/` — the CLI console app (`net10.0`). `Program.cs` is a thin
  top-level-statement entrypoint that delegates to `Cli.Run`. `Cli.cs` does
  verb dispatch and usage; `ExitCodes.cs` holds named exit codes;
  `Commands/` has one class per command (`RunCommand`, …). Command logic takes
  injectable `TextWriter` stdout/stderr (no `Console` statics) so it is
  unit-testable.
- `tests/CCRun.Tests/` — xUnit tests, references `src/CCRun`. `CliTests`
  covers dispatch/usage; `RunCommandTests` spawns real child processes to
  assert the exit-code contract.
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
