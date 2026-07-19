# Phase 5 — Rootless Containers (User namespace + UID/GID mapping)

## Context

CCRun is at Phase 4: `run` isolates the hostname (UTS namespace) and, with
`--rootfs`, adds mount + PID namespaces, a `chroot`, and a private `/proc`. Every
one of those operations needs `CAP_SYS_ADMIN` / `CAP_SYS_CHROOT`, so today
`ccrun run` **requires sudo** — the `unshare` fails with `EPERM` otherwise and
prints a "re-run under sudo (rootless mode arrives in Phase 5)" hint.

Phase 5 closes that gap (**FR-5.1–5.4**). The container gets a new **user
namespace** in which the invoking user holds a full capability set. Those
namespace-local capabilities are what authorize the UTS/mount/PID unshares, the
chroot, and the mounts — so **ccrun stops needing root entirely**. We map
container UID/GID `0` to the invoking user's effective UID/GID, so a process
started inside the container (e.g. `sleep`) appears on the host owned by that
unprivileged user, not root — the FR-5.3 acceptance test.

**Scope decisions (confirmed with the user):**

1. **Always-on.** Every `run` creates a `CLONE_NEWUSER` namespace — no `--rootless`
   flag, no euid auto-detection. Matches FR-5.1's unconditional language and the
   existing `unshare --user --map-root-user` testing pattern.
2. **Both paths.** The lightweight no-`--rootfs` path (managed `Process.Start`,
   previously UTS-only) also gains the user namespace, so `ccrun run sleep 10000`
   demonstrates FR-5.3 with the host `sleep` binary and no rootfs required.
3. **Single-UID mapping, no helper process.** Because we map exactly one UID
   (container root → invoking user), the unsharing process may write its **own**
   `/proc/self/{setgroups,uid_map,gid_map}` — the `--map-root-user` special case in
   user_namespaces(7). No fork/handshake or `newuidmap` helper is required.
4. **Relaxed test gating.** Existing root-gated integration tests move to a
   "user-namespaces-available" gate so they run rootless in normal CI. The obsolete
   "needs sudo" test is removed.

**BRD alignment:** README/code-overview currently bundle cgroups into "Phase 5."
Per the BRD, Phase 5 is **user namespace only (FR-5.x)**; cgroups are **Phase 6
(FR-6.x)**. This plan treats them as separate and reconciles the docs.

## How it fits the existing architecture

The two-stage re-exec model is untouched in shape. The one structural change is in
the **parent stage** (`src/CCRun/Commands/RunCommand.cs`): the single `unshare`
becomes **two** calls with map-writing in between.

Why split it? The map files are written with managed `File`/`FileStream` I/O. The
existing landmine (documented at `RunCommand.cs:76-103`) is that after
`unshare(CLONE_NEWPID)` the process can never create a thread again, so no managed
work that might trigger lazy runtime init may run between that unshare and the
child's `Process.Start`. By doing `CLONE_NEWUSER` (+UTS, +mount for a rootfs run)
first, writing the maps, and only **then** unsharing `CLONE_NEWPID`, all the map
I/O stays on the safe side of that line. Combining `CLONE_NEWUSER` with the other
flags in one call is valid — the kernel creates the user namespace first and uses
its new capabilities for the rest (exactly what `unshare --user --uts --mount`
does).

The child stage (`src/CCRun/Commands/ChildCommand.cs`) needs **no code change**:
`sethostname`, `chroot`, and the `/proc` mount now run as namespace-mapped root,
which carries the required capabilities. `src/CCRun/Container/ReExec.cs` and
`src/CCRun/RunOptions.cs` are unchanged (no new flag, no new env var).

## Files to change

### 1. `src/CCRun/Native/Libc.cs`

Add the user-namespace flag and `getegid` (`Geteuid` already exists):

```csharp
/// <summary>Flag for <see cref="Unshare"/>: new user namespace (rootless — remaps UID/GID).</summary>
public const int CLONE_NEWUSER = 0x10000000;
```
```csharp
[LibraryImport("libc", EntryPoint = "getegid")]
internal static partial uint Getegid();
```

No new syscall is needed for the maps — they are ordinary `/proc/self` files.

### 2. `src/CCRun/Commands/RunCommand.cs`

Add `using System.Text;`. Replace the flag-assembly + single-unshare block with
the two-stage version, and update the class doc comment (drop "Requires root …
(Phase 5)").

```csharp
// A user namespace makes ccrun rootless (FR-5.1): the invoking user gets a full
// capability set *inside* the new namespace, which is what authorizes the UTS /
// mount / PID unshares, the chroot and the mounts below — no sudo required.
//
// We unshare in two stages on purpose. CLONE_NEWUSER (plus UTS, plus mount on a
// rootfs run) comes first; then we write this process's uid/gid maps so container
// root maps to the invoking user (FR-5.2). Only afterwards do we unshare the PID
// namespace: writing the maps is managed File I/O, and CLONE_NEWPID permanently
// forbids new threads (see WarmUpProcessSubsystem), so the map writes must land on
// the safe side of that unshare.
int firstFlags = Libc.CLONE_NEWUSER | Libc.CLONE_NEWUTS;
string firstNames = "CLONE_NEWUSER|CLONE_NEWUTS";
if (rootfs is not null)
{
    firstFlags |= Libc.CLONE_NEWNS;
    firstNames += "|CLONE_NEWNS";
}

if (Libc.Unshare(firstFlags) != 0)
{
    int err = Marshal.GetLastPInvokeError();
    stderr.WriteLine($"ccrun: unshare({firstNames}) failed: {Libc.LastErrorMessage()}");
    if (err == Libc.EPERM)
        stderr.WriteLine("hint: creating a user namespace was denied; your kernel may have " +
                         "unprivileged user namespaces disabled. Enable them " +
                         "(sysctl kernel.unprivileged_userns_clone=1) or run ccrun as root.");
    return ExitCodes.RuntimeError;
}

if (!WriteIdMaps(stderr))
    return ExitCodes.RuntimeError;

if (rootfs is not null)
{
    // CLONE_NEWPID makes the *next* forked process (the __child) PID 1.
    WarmUpProcessSubsystem();
    if (Libc.Unshare(Libc.CLONE_NEWPID) != 0)
    {
        stderr.WriteLine($"ccrun: unshare(CLONE_NEWPID) failed: {Libc.LastErrorMessage()}");
        return ExitCodes.RuntimeError;
    }
}

return ReExec.RunChild(options with { Rootfs = rootfs }, stderr);
```

Add the two helpers (keep `WarmUpProcessSubsystem` as-is):

```csharp
/// <summary>
/// Maps container UID/GID 0 to the invoking user's effective UID/GID (FR-5.2) by
/// writing this process's own /proc/self map files. Must run after
/// unshare(CLONE_NEWUSER) and before the PID-namespace unshare. For a single-ID
/// unprivileged mapping, setgroups must be denied before gid_map is written
/// (user_namespaces(7)).
/// </summary>
private static bool WriteIdMaps(TextWriter stderr)
{
    uint uid = Libc.Geteuid();
    uint gid = Libc.Getegid();
    try
    {
        WriteProcFile("/proc/self/setgroups", "deny");
        WriteProcFile("/proc/self/uid_map", $"0 {uid} 1");
        WriteProcFile("/proc/self/gid_map", $"0 {gid} 1");
        return true;
    }
    catch (IOException ex)
    {
        stderr.WriteLine($"ccrun: writing the uid/gid map failed: {ex.Message}");
        return false;
    }
}

// The kernel requires each map to be written in a single write(2); open the
// existing file and write once rather than using File.WriteAllText.
private static void WriteProcFile(string path, string content)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
    byte[] bytes = Encoding.ASCII.GetBytes(content);
    fs.Write(bytes, 0, bytes.Length);
}
```

### 3. `src/CCRun/Cli.cs`

Replace the Phase-4 usage footer:

```csharp
w.WriteLine("Phase 5: 'run' isolates the hostname (UTS ns) and runs rootless in a user namespace,");
w.WriteLine("so container root maps to your user and no sudo is needed. With --rootfs it also chroots");
w.WriteLine("into the root FS and adds PID + mount namespaces with a private /proc.");
w.WriteLine("Requires unprivileged user namespaces enabled (or run as root).");
```

### 4. `tests/CCRun.Tests/RunCommandTests.cs`

Add a `IsUserNsAvailable` gate next to the existing `IsRoot`, and **delete
`Run_AsNonRoot_FailsWithSudoHint`** — non-root now succeeds, so that path no longer
exists. The success path cannot be tested in-process here (an in-process
`unshare(CLONE_NEWUSER)` would permanently mutate the xunit host); it moves
out-of-process to the integration tests. The remaining RunCommandTests all return
before any `unshare` (parse errors, missing-rootfs validation) and stay as-is.

```csharp
internal static bool IsRoot => Libc.Geteuid() == 0;

// ccrun now always creates a user namespace, so the namespace integration tests
// need either root or a kernel that permits unprivileged user namespaces.
internal static bool IsUserNsAvailable => IsRoot || UnprivilegedUsernsEnabled();

private static bool UnprivilegedUsernsEnabled()
{
    const string knob = "/proc/sys/kernel/unprivileged_userns_clone"; // Debian/Ubuntu gate
    if (File.Exists(knob) && File.ReadAllText(knob).Trim() == "0")
        return false;
    const string max = "/proc/sys/user/max_user_namespaces";
    if (File.Exists(max) && int.TryParse(File.ReadAllText(max).Trim(), out int n) && n <= 0)
        return false;
    return true;
}
```

### 5. `tests/CCRun.Tests/NamespaceIntegrationTests.cs`

**Relax gates:** replace every `Skip.IfNot(RunCommandTests.IsRoot, "…")` with
`Skip.IfNot(RunCommandTests.IsUserNsAvailable, "requires root or unprivileged user namespaces")`.
The existing tests (hostname, chroot, PID-1, private `/proc`, host-mount-isolation)
then run rootless — the same situation the README's `unshare --user
--map-root-user dotnet test` already exercises, so they continue to pass.

**Add two rootless tests** (out-of-process, using the existing `Run` helper /
`FindAlpineRootfs`):

- `Rootless_ContainerRootMapsToInvokingUser` (**FR-5.2**) — gate on
  `IsUserNsAvailable` + `FindAlpineRootfs()`; run `run --rootfs <alpine>
  /bin/busybox id -u`; assert exit 0 and stdout trim `== "0"` (inside the container
  we are root).

- `Rootless_HostSeesProcessOwnedByInvokingUser` (**FR-5.3**, the acceptance test) —
  gate on `IsUserNsAvailable` **and** `Skip.If(RunCommandTests.IsRoot, "meaningful
  only for a non-root invoker")` (under sudo, container root maps to real root 0, so
  the assertion would be vacuous). Follows the concurrent-inspect pattern of the
  existing `MountNamespace_…` test: launch `run /bin/sleep <unique-secs>` (no rootfs
  → host sleep) via a hand-built `ProcessStartInfo`, poll `/proc/[0-9]*/cmdline` for
  the process whose command line contains the unique duration, read
  `/proc/<pid>/status`, parse the `Uid:` line, then assert the effective uid equals
  `Libc.Geteuid()` **and** is non-zero. A `finally` kills the process tree.

No `RunOptionsTests` change (no new flag).

## Documentation

- **`CLAUDE.md`:** bump "at Phase 4" → Phase 5; describe the always-on user
  namespace, the single-UID map (root → invoking user), and the two-stage unshare
  ordering as a new landmine (map I/O must precede `CLONE_NEWPID`); drop the
  "requires root/sudo until Phase 5" lines; update the Commands section to show
  `ccrun run …` **without** sudo (note sudo still works and maps root→root); update
  the Structure section for the new `Libc.CLONE_NEWUSER`/`Getegid` and
  `RunCommand`'s `WriteIdMaps`/`WriteProcFile`.
- **`docs/code-overview/code-overview.md`:** bump "Where the project stands" to
  Phase 5; add a `## User namespace / rootless` deep-dive (why namespace-local caps
  remove sudo, the single-UID `--map-root-user` special case, the two-unshare split
  and why); extend the parent-stage walkthrough; add `CLONE_NEWUSER`/`getegid` to
  the libc-layer section; update "How it is tested" (root gate → userns-available
  gate, sudo-hint test removed); rewrite "Where this goes next" so **Phase 6 =
  cgroups** (separated from Phase 5).
- **`README.md`:** header → Phase 5; "Still missing" drops user namespace/rootless;
  Prerequisites note unprivileged user namespaces are now required for default
  (non-root) operation; Build & test — sudo no longer required, and the `unshare
  --user --map-root-user` wrapper is no longer needed (plain `dotnet test` / `$BIN
  run …` work when unprivileged userns is enabled); Roadmap marks Phase 5 done and
  keeps cgroups as Phase 6.

## Verification (end-to-end, no sudo)

```sh
dotnet build
BIN=src/CCRun/bin/Debug/net10.0/CCRun

# rootless: root inside the userns, host untouched, no sudo
$BIN run /bin/sh -c 'id -u; hostname'                     # -> 0  then  container
$BIN run --rootfs alpine-rootfs /bin/busybox id -u        # -> 0
$BIN run --rootfs alpine-rootfs /bin/busybox ps           # only container procs

# FR-5.3 acceptance: long-running proc is owned by *you* on the host, not root
$BIN run /bin/sleep 500 &
ps -o pid,user,cmd -C sleep                               # USER column = your login
kill %1

dotnet test                                               # namespace tests now run without sudo
```

If the first command fails with the user-namespace hint, unprivileged user
namespaces are disabled — enable with `sudo sysctl -w
kernel.unprivileged_userns_clone=1` (or run `ccrun` under sudo, which maps
container root → real root).

## Out of scope (later phases)

- **Cgroup resource limits** — Phase 6 (FR-6.x).
- **Image pull / registry client** — Phase 7 (FR-7.x); **run pulled image** —
  Phase 8 (FR-8.x).
- **Range mapping** via `/etc/subuid` + `newuidmap`/`newgidmap` — we map a single
  UID only, which satisfies FR-5.2/5.3.
- **`pivot_root`** — still deferred; plain `chroot` is retained.

## Git policy

Claude will stage changes and draft a commit message only — the human reviews and
runs `git commit`. Claude never commits to this repository.
