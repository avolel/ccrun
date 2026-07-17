# Phase 4 — Process Isolation (PID namespace + Mount namespace + private `/proc`)

## Context

CCRun is at **Phase 3**: `ccrun run` puts a command in a new UTS namespace
(hostname isolation) and, with `--rootfs`, `chroot`s into a root filesystem. But
`chroot` only restricts path resolution — inside the container `ps` still sees
every host process, because the container shares the host's PID number space and
its `/proc` mount.

Phase 4 closes that gap (BRD **FR-4.1–4.5**):

- **FR-4.1** run in a new **PID namespace** so the container's process tree starts
  fresh (its first process is PID 1).
- **FR-4.3** run in a new **Mount namespace** so container mounts are invisible to
  the host (`mount | grep proc` on the host must not show the container's proc).
- **FR-4.2 / FR-4.5** mount a **private `/proc`** so `ps` inside shows only
  container processes.
- **FR-4.4** the `/proc` mount is torn down cleanly on exit, including error paths.

**Scope decisions (confirmed with the user):**

1. **Gate the whole Phase 4 stack behind `--rootfs`.** A rootfs run gets
   UTS + mount + PID namespaces + private `/proc`; a bare `run` stays exactly at
   Phase 2 (UTS-only). This mirrors the existing chroot gate, keeps the
   invariant "`--rootfs` = full container, bare `run` = hostname only", and
   avoids the .NET runtime ever becoming PID 1 (the no-rootfs path still uses
   managed `Process.Start`).
2. **Keep `chroot`; do not introduce `pivot_root`.** FR-4.x do not require it;
   `chroot` + `chdir("/")` from Phase 3 already satisfies the filesystem
   requirement. `pivot_root` is deferred (revisit with the user-namespace work).

## How it fits the existing architecture

The parent→child re-exec model already does everything Phase 4 needs; we only add
flags and mount calls, no new structure:

- **PID-namespace ordering is free.** `unshare(CLONE_NEWPID)` does *not* move the
  caller — it makes the *next* forked process PID 1 of the new namespace. The
  parent (`RunCommand`) unshares, then `ReExec.RunChild` does `Process.Start`,
  which forks the `__child`. That child is therefore PID 1, and on the rootfs
  path it hands off with `execvp`, so the **user's command ends up as PID 1** —
  exactly what a private `/proc` wants.
- **Cleanup is automatic (FR-4.4).** The `/proc` mount lives only in the new
  mount namespace. When PID 1 (the exec'd command) exits, the PID namespace
  empties and the mount namespace is destroyed with its `/proc` — on both success
  and error paths. No explicit `umount2` is needed (and none is possible after
  `execvp` replaces the process image anyway). We document this rather than add
  teardown code.
- **No new env-var channel.** Both the parent's unshare flags and the child's
  mount steps key off the same rootfs signal (`options.Rootfs` in the parent,
  the `CCRUN_ROOTFS` env var in the child), so they stay in agreement. `ReExec`
  is unchanged.

## Files to change

### 1. `src/CCRun/Native/Libc.cs` — new constants + `mount(2)`

Add alongside the existing `CLONE_NEWUTS`:

```csharp
public const int CLONE_NEWUTS = 0x04000000;   // new UTS namespace (hostname) — existing
public const int CLONE_NEWNS  = 0x00020000;   // new mount namespace (private mount table)
public const int CLONE_NEWPID = 0x20000000;   // new PID namespace (fresh process-id space)

// mount(2) flags.
public const ulong MS_NOSUID  = 1UL << 1;    // ignore set-uid/set-gid bits under this mount
public const ulong MS_NODEV   = 1UL << 2;    // disallow access to device special files
public const ulong MS_NOEXEC  = 1UL << 3;    // disallow program execution from this mount
public const ulong MS_REC     = 1UL << 14;   // recursive: apply to the whole subtree
public const ulong MS_PRIVATE = 1UL << 18;   // turn off mount/unmount propagation
```

Add the P/Invoke (source-generated, `SetLastError = true`, reuse `LastErrorMessage()`):

```csharp
// mount(2). source/filesystemtype/data may be NULL for some operations — e.g. a
// propagation change passes source="none" and data=NULL. Passing a C# null
// marshals to a null pointer.
[LibraryImport("libc", EntryPoint = "mount", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
internal static partial int Mount(string? source, string target, string? filesystemtype, ulong mountflags, string? data);
```

No `umount2` (cleanup is via namespace teardown, see above).

### 2. `src/CCRun/Commands/RunCommand.cs` — conditional unshare flags

Where it currently calls `Libc.Unshare(Libc.CLONE_NEWUTS)`, build the flag set
from rootfs presence (the resolved `rootfs` local already exists there):

```csharp
// A rootfs container gets the full Phase 4 isolation stack: UTS (hostname),
// mount (a private mount table so our /proc never leaks to the host), and PID
// (a fresh process-id space). Without --rootfs we stay at Phase 2: UTS only.
// CLONE_NEWPID does not move the caller — it makes the *next* forked process
// (the re-exec'd __child that ReExec launches) PID 1 of the new namespace.
int flags = Libc.CLONE_NEWUTS;
if (rootfs is not null)
    flags |= Libc.CLONE_NEWNS | Libc.CLONE_NEWPID;

if (Libc.Unshare(flags) != 0)
{
    // ...existing errno / EPERM sudo-hint handling, unchanged...
}
```

### 3. `src/CCRun/Commands/ChildCommand.cs` — make mounts private, then mount `/proc`

`sethostname` stays first and unconditional. In the `rootfs is not null` branch,
add the make-private mount *before* `chroot` and the `/proc` mount *after*
`chroot`+`chdir("/")`, before the `Exec` hand-off:

```csharp
if (rootfs is not null)
{
    // We are PID 1 inside fresh mount + PID namespaces. Before touching any
    // mounts, mark the whole tree private so nothing we mount (notably /proc)
    // propagates back to the host mount namespace (FR-4.3). MS_REC covers every
    // mount under "/"; MS_PRIVATE turns propagation off. Done on the real host
    // "/" (pre-chroot) so the recursion reaches all inherited mounts.
    if (Libc.Mount("none", "/", null, Libc.MS_REC | Libc.MS_PRIVATE, null) != 0)
    {
        stderr.WriteLine($"ccrun: cannot make mounts private: {Libc.LastErrorMessage()}");
        return ExitCodes.RuntimeError;
    }

    if (Libc.Chroot(rootfs) != 0) { /* ...existing... */ }
    if (Libc.Chdir("/") != 0)    { /* ...existing... */ }

    // Mount a private /proc so tools like `ps` report only this container's
    // processes (FR-4.2/4.5). Because we are PID 1 of a new PID namespace, this
    // procfs instance is scoped to that namespace. nosuid/nodev/noexec are the
    // conventional hardening flags for /proc. The rootfs must contain a /proc
    // directory as the mountpoint (the Alpine minirootfs does).
    if (Libc.Mount("proc", "/proc", "proc", Libc.MS_NOSUID | Libc.MS_NODEV | Libc.MS_NOEXEC, null) != 0)
    {
        stderr.WriteLine($"ccrun: cannot mount /proc: {Libc.LastErrorMessage()}");
        return ExitCodes.RuntimeError;
    }

    return Exec(args, stderr);
}

return ProcessRunner.Run(args[0], args[1..], stderr);   // no-rootfs path unchanged (Phase 2)
```

### 4. `src/CCRun/Cli.cs` — usage text

Update the `PrintUsage` string that currently says "Phase 3: 'run' isolates the
hostname (UTS ns) and, with --rootfs, chroots" to mention that `--rootfs` now
also adds PID + mount namespaces and a private `/proc`.

## Tests — `tests/CCRun.Tests/NamespaceIntegrationTests.cs`

Add two `[SkippableFact]`s next to the existing chroot tests, gated the same way
(`Skip.IfNot(RunCommandTests.IsRoot, ...)` then `Skip.If(rootfs is null, ...)` via
`FindAlpineRootfs()`). Both assert on **exit code** (the execvp path replaces the
process, so its stdout is not captured by the `StringWriter` seam — the existing
tests already rely on exit codes / temp files for this reason):

- **`PidNamespace_ContainerShellIsPidOne`** — proves FR-4.1.
  `run --rootfs <fs> /bin/busybox sh -c '[ "$$" = 1 ]'` → assert exit `0`.
  Under a new PID namespace the exec'd busybox shell is PID 1, so `$$` is `1`.

- **`PrivateProc_OnlyContainerProcessesVisible`** — proves FR-4.2/4.5.
  `run --rootfs <fs> /bin/busybox sh -c 'c=$(ls -d /proc/[0-9]* 2>/dev/null | wc -l); [ "$c" -le 4 ]'`
  → assert exit `0`. With a fresh PID namespace + private `/proc`, only a handful
  of PIDs exist (the shell plus the `ls`/`wc` pipeline); on the host `/proc` would
  list hundreds, so this fails loudly if the private `/proc` mount is missing.

Existing tests are unaffected: the bare-run tests still exercise UTS-only, and the
Phase 3 chroot tests still pass (now with the extra namespaces underneath).
No new package references (`Xunit.SkippableFact 1.5.61` is already present).

## Documentation

- **`CLAUDE.md`** — bump "Phase 3" → "Phase 4" in the "What this is" section;
  describe the new PID + mount namespaces and private `/proc` (gated on
  `--rootfs`), the automatic cleanup via namespace teardown, and update the
  `Native/Libc.cs` / `ChildCommand` descriptions to list `mount`,
  `CLONE_NEWNS`/`CLONE_NEWPID`, and the `MS_*` flags. Update "Remaining phases".
- **`docs/code-overview/code-overview.md`** — update "Where the project stands";
  extend the child-stage walkthrough with the make-private + `/proc` steps; add a
  short "PID namespace, mount namespace, private /proc" subsection (including why
  cleanup needs no `umount2`); reword the "Where this goes next" Phase 4 forecast
  (note `pivot_root` is deferred, not part of this phase).
- **`README.md`** — mark Phase 4 done in the roadmap; add an example, e.g.
  `sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox ps` showing only container
  processes.

## Verification (end-to-end, needs root)

```sh
dotnet build
BIN=src/CCRun/bin/Debug/net10.0/CCRun

# PID namespace: busybox shell is PID 1
sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox sh -c 'echo $$'          # -> 1

# Private /proc: ps shows only container processes (a few lines, no host procs)
sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox ps

# Host isolation (FR-4.3): the container's proc mount is NOT visible on the host.
# In one terminal, keep a container alive:
sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox sh -c 'sleep 30' &
# In another, confirm nothing new shows for our container:
mount | grep -c 'alpine-rootfs/proc'                                         # -> 0

# Regression: Phase 2/3 behavior intact
sudo "$BIN" run /bin/sh -c hostname                                          # -> container
sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox sh -c 'cat /etc/alpine-release'

# Tests (namespace tests run only under sudo; they skip unprivileged)
dotnet test
sudo dotnet test --filter FullyQualifiedName~NamespaceIntegrationTests
```

Prerequisite: `alpine-rootfs/` must be present and must contain a `/proc`
directory (the Alpine minirootfs does). Recreate via the README commands if
missing.

## Out of scope (later phases)

- User namespace / rootless mode (Phase 5, FR-5.x).
- `pivot_root` (deferred; `chroot` retained).
- cgroup resource limits (Phase 6), image pull/run (Phases 7–8).
- Applying PID/mount isolation to the no-rootfs path (intentionally left at
  Phase 2 behavior per the scope decision above).

## Git policy

Per repo policy, Claude will stage changes and draft a commit message only — the
human reviews and runs `git commit`.
