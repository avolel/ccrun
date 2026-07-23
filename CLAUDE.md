# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CCRun is a "Build Your Own Docker" learning project: a lightweight Linux container
runtime in C# / .NET 10, built in 8 phases. **The repo is currently at Phase 6
(resource limits).** `ccrun run <command>` puts the command in a new user +
UTS namespace so it gets its own hostname and runs as root inside the container
without the invoker being root outside, runs it, and passes back its exit code.

**How much isolation you get is gated on `--rootfs`**, and that split is
deliberate:

- **With `--rootfs <path>`** — the full stack. The command runs in user + UTS +
  mount + PID namespaces, `chroot`s into the given root filesystem (then
  `chdir("/")`) so it sees that as `/` and cannot climb above it, and gets a
  private `/proc` so `ps` shows only container processes. It ends up as PID 1 of
  its own namespace.
- **Without `--rootfs`** — Phase 2 behaviour plus the user namespace: hostname
  isolation and rootless operation, with the command spawned via managed
  `Process.Start`. It deliberately stays here because that path must not become
  PID 1 of a PID namespace.

Orthogonally to `--rootfs`, **`--memory` and `--cpus` (Phase 6)** put the container
in a cgroup v2 directory of its own carrying those limits. Requesting neither skips
the whole mechanism, so an unlimited run behaves exactly as it did in Phase 5.

Still missing: image handling (`pull`, registry client). `pivot_root` is deferred —
Phase 4 kept the plain `chroot`, since the FR-4.x/5.x requirements do not need the
stronger boundary.

Namespaces and mounts need `CAP_SYS_ADMIN` and `chroot` needs `CAP_SYS_CHROOT`,
neither of which an ordinary user has. Phase 5 gets them the only way an
unprivileged process can: it creates a **user namespace** first, in which the
caller holds a full capability set, and performs everything else with those
namespace-local capabilities. `ccrun run` therefore **no longer needs sudo** (it
still works under sudo, where container root maps to real root). Container UID/GID
0 is mapped to the invoking user's, so container processes appear on the host
owned by that unprivileged user.

Phase 2 set up the parent/child architecture that every later phase builds on: the
parent creates the namespaces and launches a hidden `__child` stage that does the
in-namespace setup (`sethostname`, then the optional make-private mount + `chroot`
+ `/proc` mount) before launching the user command.

Non-obvious mechanics worth knowing before editing — the last three are landmines
that will hang or abort the runtime if disturbed:

- **A .NET process can never `unshare` a user namespace.** `unshare(2)` requires a
  single-threaded caller for `CLONE_NEWUSER`, and the CLR always runs extra threads
  (the finalizer thread alone is enough), so the call fails with `EINVAL`. Phase 5
  therefore creates *all* the namespaces with **`clone3`** at child-creation time
  instead of unsharing them in the parent — a new process starts single-threaded by
  definition, so the restriction does not apply. Go/runc hit the same wall and use
  the same fix. Do not try to reintroduce `unshare` here.
- **Passing `CLONE_NEWPID` to `clone3` makes the cloned child itself PID 1** (unlike
  `unshare(CLONE_NEWPID)`, which only affects the *next* fork). The child then
  `execvp`s the user command in place, so the command is PID 1 — which is exactly
  what a private `/proc` wants.
- **The `/proc` mount needs no teardown.** It lives only in the container's mount
  namespace, which the kernel destroys once the PID namespace empties, on success
  and error paths alike. Explicit unmounting would be impossible after `execvp`
  replaces the process image anyway.
- **The cloned child may not touch the managed runtime.** It is cloned out of a
  multithreaded CLR, so it has one thread and any runtime state another thread was
  mid-way through mutating is frozen with nobody to finish it; allocating, JITting,
  or writing to `Console` there can deadlock. **Even a normal P/Invoke is unsafe**:
  its GC transition can observe a suspension that can never complete, and the
  runtime answers that with `abort()` — an intermittent, load-dependent crash that
  surfaces as the container dying of a signal. So `ReExec.RunAsClonedChild` is
  limited to two libc calls on pointers staged *before* the clone, both imports
  carry `[SuppressGCTransition]`, `PrepareClonedChildPath` pre-compiles the method
  and pre-resolves its stubs, and the pipe is `O_CLOEXEC` so no `close` is needed.
  Anything added to that method must keep all of those rules.
- **The cgroup work has exactly one legal place: the parent, between `clone3` and
  the go-byte.** Three constraints force it. The limits must be in force before the
  user command runs, and the child is parked on the pipe there; the cloned child
  cannot write the files itself (no managed I/O — see above); and after `chroot` it
  cannot reach `/sys/fs/cgroup` at all. The pid written to `cgroup.procs` is the
  child's **host-side** pid — `cgroup.procs` knows nothing about the container's PID
  namespace. Cleanup hangs off the existing `waitpid`, because `rmdir` on a cgroup
  only succeeds once it is empty.
- **The UID/GID maps must be written by the parent, before the child execs.** A new
  user namespace starts with empty maps, in which every id is the overflow uid
  (nobody), and `execve` clears the permitted capability set for a non-root uid with
  no file capabilities — so a child that exec'd first would reach `chroot`/`mount`
  with no `CAP_SYS_ADMIN`. Hence the pipe handshake in `ReExec`: child blocks, parent
  writes `/proc/<pid>/{setgroups,uid_map,gid_map}`, child then execs as
  root-in-namespace and keeps its capabilities. `setgroups`=`deny` must precede
  `gid_map` or the write is rejected.
- **After `chroot`, the runtime's assemblies are unreachable, so error paths must
  load nothing.** `Libc.LastErrorMessage` uses `strerror` rather than
  `Win32Exception` (which lives in a lazily-loaded assembly), and `ChildCommand`
  forces that assembly resident before the chroot (`PreloadConsoleWriteDependencies`) because
  Console's first write needs it. Without both, a failing `execvp` died with a
  `FileNotFoundException` instead of reporting the error and exiting 127.

On the chroot path the child hands off with `execvp` (replacing its process image)
rather than `Process.Start`, because after `chroot` the .NET runtime's own files
may sit outside the new root; the no-`--rootfs` path keeps `Process.Start`. See
`docs/code-overview/code-overview.md` for a full walkthrough of how it works.

Cgroups are the one part that cannot be pinned to a fixed path: `/sys/fs/cgroup` is
not writable by an unprivileged user, so `Cgroup.Create` walks up from our own
cgroup and uses the first ancestor that both accepts a `mkdir` and yields the
controller files we need (`memory.max`, `cpu.max`) — i.e. the subtree systemd
delegated to the user session. Probing for the interface files is the delegation
test; do not replace it with a fixed `/sys/fs/cgroup/ccrun/` path, which would
require root and regress Phase 5. `--memory` also writes `memory.swap.max=0`, or the
cgroup swaps instead of OOM-killing and the cap never visibly bites.

Remaining phases add: image pull + registry client (7–8).

## Commands

```sh
dotnet build                              # compile the solution
dotnet test                               # run all xUnit tests
dotnet test --filter FullyQualifiedName~CliTests   # run a single test/class
dotnet run --project src/CCRun -- [args]  # run the CLI (no args → usage, exit 1)
dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true  # single-file binary
```

The solution file is `CCRun.slnx` (the .NET 10 XML format), not `.sln`.

Since Phase 5 `ccrun run` needs **no sudo** — it creates a user namespace and works
from the capabilities that grants. Build first, then run the produced binary
(building separately keeps build output out of the container's):

```sh
dotnet build
BIN=src/CCRun/bin/Debug/net10.0/CCRun
$BIN run /bin/sh -c hostname          # prints: container
$BIN run /bin/sh -c 'id -u'           # prints: 0 — root inside the user namespace
# chroot into the Alpine rootfs and run its in-tree busybox:
$BIN run --rootfs alpine-rootfs /bin/busybox sh -c 'cat /etc/alpine-release'
# the command is PID 1 and sees only its own processes:
$BIN run --rootfs alpine-rootfs /bin/busybox sh -c 'echo $$'   # prints: 1
$BIN run --rootfs alpine-rootfs /bin/busybox ps                # only container procs
# rootless proof: the host shows the process owned by *you*, not root
$BIN run /bin/sleep 500 & ps -o pid,user,cmd -C sleep; kill %1
# resource limits: the container's cgroup path, then the cap biting (exit 137)
$BIN run --memory 128m --cpus 0.5 /bin/sh -c 'cat /proc/self/cgroup'
$BIN run --memory 16m /bin/sh -c 'x=""; while :; do x="$x$(head -c 1000000 /dev/zero | tr "\0" a)"; done'
```

Note `--rootfs` paths resolve against the **current directory**, so run from the
repo root (or pass an absolute path).

This requires unprivileged user namespaces to be enabled (`sysctl
kernel.unprivileged_userns_clone` → 1; `/proc/sys/user/max_user_namespaces` > 0).
Running under sudo still works and needs neither, but then container root maps to
real root. The `unshare --user --map-root-user` wrapper earlier phases needed is
now redundant — ccrun does that itself — and `dotnet test` runs the whole suite
unprivileged.

## Structure

- `src/CCRun/` — the CLI console app (`net10.0`). `Program.cs` is a thin
  top-level-statement entrypoint that delegates to `Cli.Run`. `Cli.cs` does
  verb dispatch and usage; `ExitCodes.cs` holds named exit codes; `RunOptions.cs`
  parses the arguments to `run` (`--hostname`, `--rootfs`, `--memory`, `--cpus`) and
  `ResourceLimits.cs` holds the parsed limits, already converted to cgroup v2 units
  (nullable throughout — `Limits.Any == false` is what skips the cgroup entirely).
  `Commands/` has one
  class per command: `RunCommand` is the parent/host stage (validates `--rootfs`,
  picks the namespace flag set — user + UTS always, plus mount + PID when a rootfs
  was given — and delegates to `ReExec`), and the hidden `ChildCommand` is the
  `__child` init stage (`sethostname`, then on the rootfs path: recursive
  make-private mount → `chroot` → `chdir("/")` → mount `/proc` → `execvp`).
  `Native/Libc.cs` holds the libc P/Invoke declarations (`syscall` for `clone3`,
  `sethostname`, `chroot`, `chdir`, `mount`, `execve`, `execvp`, `pipe`, `read`,
  `write`, `close`, `waitpid`, `_exit`, `geteuid`, `getegid`, `strerror`) and the
  constants they need (`CLONE_NEWUSER`/`CLONE_NEWUTS`/`CLONE_NEWNS`/`CLONE_NEWPID`,
  `SYS_clone3`/`CLONE_ARGS_SIZE_VER0`/`SIGCHLD`, the
  `MS_NOSUID`/`MS_NODEV`/`MS_NOEXEC`/`MS_REC`/`MS_PRIVATE` mount flags, plus
  `EACCES`/`EPERM`/`EINVAL`/`ENOSYS`).
  `Container/` holds the runtime plumbing: **`ReExec` is the heart of Phase 5** —
  it stages the exec arguments in native memory, `clone3`s a child into the
  namespaces, writes that child's UID/GID maps (`WriteIdMaps`) while it blocks on a
  pipe, then reaps it and translates the wait status into an exit code. Hostname and
  rootfs still travel to the child via the `CCRUN_HOSTNAME`/`CCRUN_ROOTFS` env vars.
  `Cgroup` is Phase 6: it finds a writable delegated cgroup v2 subtree, creates the
  container's own directory in it, writes `memory.max`/`memory.swap.max`/`cpu.max`,
  admits the child via `cgroup.procs`, and `rmdir`s it on `Dispose` — all driven from
  the same post-`clone3` window in `ReExec.RunChild`, so nothing crosses the exec.
  `ProcessRunner` spawns the user command on the no-`--rootfs` path and returns its
  exit code. Command logic takes injectable `TextWriter` stdout/stderr (no `Console`
  statics) so it is unit-testable.
- `tests/CCRun.Tests/` — xUnit tests, references `src/CCRun`. `CliTests` covers
  verb dispatch and usage; `RunOptionsTests` covers argument parsing;
  `ProcessRunnerTests` asserts the child-process exit-code contract;
  `ResourceLimitsTests` tables the `--memory`/`--cpus` conversions and rejections;
  `RunCommandTests` covers the parent stage's pre-namespace paths (argument errors,
  the missing-`--rootfs` error, malformed limit values) and hosts the `IsRoot` /
  `IsUserNsAvailable` / `IsCgroupV2Delegated` gates;
  `NamespaceIntegrationTests` exercises the full clone3 + sethostname + chroot +
  `/proc` + execvp pipeline, including the PID-1, private-`/proc`,
  host-mount-isolation, the two Phase 5 rootless assertions (container root is
  uid 0 inside; the host sees the process owned by the invoking user), and the three
  Phase 6 cgroup ones (the container is in `ccrun-<pid>` with the requested
  `memory.max`/`cpu.max`; the directory is gone after it exits; a container over its
  memory cap exits 137). They are
  gated on `IsUserNsAvailable` — root *or* unprivileged user namespaces — so a
  normal `dotnet test` runs them; they skip (via `Xunit.SkippableFact`) only where
  user namespaces are disabled. The chroot tests additionally skip if
  `alpine-rootfs/` is absent, and the cgroup tests if no delegated subtree exists
  (`IsCgroupV2Delegated` probes with the very same search `Cgroup.Create` performs).
  **`NamespaceIntegrationTests` must spawn the ccrun binary out-of-process** (it
  does, via `dotnet <testdir>/CCRun.dll`), never `Cli.Run` in-process like the other
  classes. Running out-of-process keeps each container's process tree, `clone3`
  child and wait status clear of the xunit host, and makes the container's real
  stdout assertable across the `execvp` hand-off, which would defeat an in-process
  `StringWriter`.
- `alpine-rootfs/` — Alpine minirootfs, **git-ignored**, the rootfs used by
  `--rootfs` for chroot testing. Recreate via the commands in README.md if
  missing; presence is verified by the `ALPINE_FS_ROOT` marker file and
  `alpine-rootfs/bin/busybox`. It must contain a `/proc` directory to serve as the
  mountpoint for the private `/proc` (the stock minirootfs does).
- `docs/code-overview/code-overview.md` — a detailed, educational walkthrough of the whole runtime
  (the two-stage re-exec model, the `run` trace, chroot, the PID/mount namespaces
  and private `/proc`, the cgroup and its delegated-subtree search, `execvp` vs
  `Process.Start`, the libc layer, and testing).
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
