# Phase 6 — Resource Limits (cgroup v2 memory + CPU)

## Context

CCRun is at Phase 5: `ccrun run` creates user + UTS namespaces (plus mount + PID
with `--rootfs`), maps container root to the invoking user, and needs no sudo.
What it cannot do is bound what the container consumes — a container can take all
the memory and CPU on the box.

Phase 6 closes that (**FR-6.1–6.5**). Each `run` gets its own cgroup v2 directory,
the requested memory and CPU limits are written to that cgroup's controller files,
the container process is put into it **before** the user command executes, and the
directory is removed after the container exits.

**Scope decisions (confirmed with the user):**

1. **`--memory` and `--cpus` only.** Exactly FR-6.2/6.3. No `--pids-limit` —
   easy to add later, but out of scope here.
2. **Delegated-subtree placement.** The cgroup is created under the nearest
   *writable* ancestor of our own cgroup that has the required controllers in its
   `cgroup.subtree_control`. This is what keeps the no-sudo property Phase 5 just
   won: on a systemd host the user session's `user@<uid>.service` slice already
   has `cpu memory pids` delegated (verified on this machine). A fixed
   `/sys/fs/cgroup/ccrun/` path would have required root and regressed Phase 5.
3. **No limits requested ⇒ no cgroup.** If neither flag is passed, the whole
   mechanism is skipped, so the common case stays exactly as it is today. FR-6.4's
   "sensible documented defaults" is read as "unlimited by default" — matching
   Docker, and the honest choice for a runtime that cannot know the host's size.

The BRD's cgroup v1 fallback (§9.4) is deliberately **not** implemented:
`CLAUDE.md` pins the project to "Linux + cgroup v2 only". A v1-only host gets a
clear error, not a silent no-op.

## How it fits the existing architecture

The two-stage re-exec model is untouched. All cgroup work happens in the **parent
stage**, in the window `ReExec.RunChild` already opens between `clone3` returning
and the go-byte that unblocks the child:

```
clone3  →  WriteIdMaps(pid)  →  [NEW: cgroup create + limits + cgroup.procs]  →  go-byte  →  child execs
```

That placement is forced, and for three independent reasons:

- The limits must be in force **before** the user command runs (FR-6.5), and the
  child is parked on the pipe read until we say go.
- `RunAsClonedChild` may not allocate, JIT, or do managed I/O (see
  `ReExec.cs` — "two calls is the whole budget"), so it cannot write cgroup files
  itself.
- After `chroot`, `ChildCommand` cannot reach `/sys/fs/cgroup` at all.

So the parent does it, with the child's **host-side** PID, using the same
`WriteProcFile` helper `WriteIdMaps` already uses — cgroup interface files have
the same single-write, no-truncate semantics as `uid_map`.

Cleanup (FR-6.5) hangs off the existing `WaitForExitCode` call: once the child is
reaped the cgroup is empty and `rmdir` succeeds.

## Files to change

### 1. `src/CCRun/ResourceLimits.cs` (new)

Pure value parsing, so it is unit-testable with no privileges. Both properties are
nullable — `null` means "not requested", which is what makes the whole cgroup step
optional.

```csharp
namespace ccrun;

/// <summary>
/// The resource caps requested on the command line, already converted into the
/// units cgroup v2 wants. Null means the user did not ask for that limit, in which
/// case no cgroup is created at all.
/// </summary>
public sealed record ResourceLimits(long? MemoryBytes, double? Cpus)
{
    public bool Any => MemoryBytes is not null || Cpus is not null;

    /// <summary>The cpu.max period, in microseconds. The kernel default; there is
    /// no reason to deviate, and keeping it fixed makes the quota easy to read.</summary>
    public const long CpuPeriodMicros = 100_000;

    /// <summary>cpu.max is written as "&lt;quota&gt; &lt;period&gt;": the cgroup may
    /// consume quota microseconds of CPU time in every period. 1.5 CPUs is
    /// therefore "150000 100000" — quota is allowed to exceed period, which is how
    /// a limit above one core is expressed.</summary>
    public string CpuMaxValue =>
        $"{(long)(Cpus!.Value * CpuPeriodMicros)} {CpuPeriodMicros}";

    /// <summary>
    /// Parses a memory size with an optional b/k/m/g suffix (case-insensitive,
    /// binary multiples, as Docker uses them). Returns false on anything that is
    /// not a positive size.
    /// </summary>
    public static bool TryParseMemory(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        long multiplier = 1;
        ReadOnlySpan<char> digits = text;
        char suffix = char.ToLowerInvariant(text[^1]);
        if (!char.IsAsciiDigit(suffix))
        {
            multiplier = suffix switch
            {
                'b' => 1,
                'k' => 1024L,
                'm' => 1024L * 1024,
                'g' => 1024L * 1024 * 1024,
                _ => 0,
            };
            if (multiplier == 0)
                return false;
            digits = text.AsSpan(0, text.Length - 1);
        }

        if (!long.TryParse(digits, out long value) || value <= 0)
            return false;
        // Reject sizes that would overflow rather than silently wrapping to a
        // nonsense (possibly negative) limit.
        if (value > long.MaxValue / multiplier)
            return false;

        bytes = value * multiplier;
        return true;
    }

    /// <summary>Parses a CPU count such as "0.5" or "2". Must be positive.</summary>
    public static bool TryParseCpus(string text, out double cpus) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out cpus)
        && cpus > 0
        && double.IsFinite(cpus);
}
```

Note `CultureInfo.InvariantCulture`: `InvariantGlobalization` is on, but being
explicit here documents that `--cpus 0.5` is not locale-dependent.

### 2. `src/CCRun/RunOptions.cs`

Add `Memory`/`Cpus` handling to the existing `if/else if` chain, following the
established two-branch-per-option shape (`--opt value` and `--opt=value`) and the
existing error-message wording. Validation happens here so a bad value is a usage
error, consistent with how the other options behave.

Record signature becomes:

```csharp
public sealed record RunOptions(
    string Hostname,
    string? Rootfs,
    ResourceLimits Limits,
    string Command,
    IReadOnlyList<string> CommandArgs)
```

New locals and branches inside `Parse`:

```csharp
        long? memoryBytes = null;
        double? cpus = null;
```

```csharp
            else if (a == "--memory" || a.StartsWith("--memory=", StringComparison.Ordinal))
            {
                if (!TryTakeValue(args, ref i, "--memory", stderr, out string? value))
                    return null;
                if (!ResourceLimits.TryParseMemory(value, out long bytes))
                {
                    stderr.WriteLine($"ccrun run: invalid --memory value '{value}' (expected a positive size such as 512m or 1g)");
                    return null;
                }
                memoryBytes = bytes;
            }
            else if (a == "--cpus" || a.StartsWith("--cpus=", StringComparison.Ordinal))
            {
                if (!TryTakeValue(args, ref i, "--cpus", stderr, out string? value))
                    return null;
                if (!ResourceLimits.TryParseCpus(value, out double parsed))
                {
                    stderr.WriteLine($"ccrun run: invalid --cpus value '{value}' (expected a positive number such as 0.5 or 2)");
                    return null;
                }
                cpus = parsed;
            }
```

With a small helper that collapses the `--opt value` / `--opt=value` duplication
that the file currently repeats per option (the existing `--hostname`/`--rootfs`
branches get refactored onto it too, so the chain stays uniform rather than
growing a second style):

```csharp
    /// <summary>
    /// Reads the value of the option at args[i], accepting both `--opt value` and
    /// `--opt=value`. Advances i past a consumed separate value. Reports the
    /// missing-value error itself and returns false.
    /// </summary>
    private static bool TryTakeValue(
        string[] args, ref int i, string name, TextWriter stderr, out string value)
    {
        string a = args[i];
        string inlinePrefix = name + "=";
        if (a.StartsWith(inlinePrefix, StringComparison.Ordinal))
        {
            value = a[inlinePrefix.Length..];
            return true;
        }
        if (i + 1 >= args.Length)
        {
            stderr.WriteLine($"ccrun run: {name} requires a value");
            value = "";
            return false;
        }
        value = args[++i];
        return true;
    }
```

The usage line in `Parse` and the one in `Cli.PrintUsage` both become:

```
usage: ccrun run [--hostname <name>] [--rootfs <path>] [--memory <size>] [--cpus <n>] <command> [args...]
```

Construction at the end of `Parse`:

```csharp
        return new RunOptions(
            hostname, rootfs, new ResourceLimits(memoryBytes, cpus), args[i], args[(i + 1)..]);
```

### 3. `src/CCRun/Container/Cgroup.cs` (new)

The one genuinely new subsystem. It owns finding a usable parent cgroup, creating
the container's own directory, writing the limits, admitting the child, and
removing the directory afterwards.

```csharp
namespace ccrun;

/// <summary>
/// A cgroup v2 directory created for one container, holding its memory and CPU
/// limits. Lifetime is the container's: created before the user command starts,
/// removed once it has been reaped.
///
/// The interesting part is <see cref="Create"/>'s search for a parent directory.
/// We cannot assume /sys/fs/cgroup itself is writable — it is not, for an
/// unprivileged user, and Phase 5 exists precisely so ccrun does not need root.
/// What an unprivileged user does typically have on a systemd host is a
/// *delegated* subtree for their login session (user.slice/user-&lt;uid&gt;.slice/
/// user@&lt;uid&gt;.service), inside which they may freely create cgroups. So we start
/// at our own cgroup and walk towards the root looking for the first ancestor we
/// can actually create a child under.
/// </summary>
internal sealed class Cgroup : IDisposable
{
    private const string Mount = "/sys/fs/cgroup";

    private readonly string _path;
    private bool _removed;

    private Cgroup(string path) => _path = path;

    /// <summary>The absolute path of the container's cgroup directory.</summary>
    public string Path => _path;

    /// <summary>
    /// Creates the container's cgroup and writes the requested limits. Returns null
    /// and explains why on stderr if no usable location exists — the caller treats
    /// that as a setup failure, because silently running without the limits the
    /// user asked for would be worse than not running at all.
    /// </summary>
    public static Cgroup? Create(ResourceLimits limits, int pid, TextWriter stderr)
    {
        if (!Directory.Exists(Mount) || !File.Exists(System.IO.Path.Combine(Mount, "cgroup.controllers")))
        {
            stderr.WriteLine(
                "ccrun: --memory/--cpus need cgroup v2 mounted at /sys/fs/cgroup (this host looks like cgroup v1)");
            return null;
        }

        // The interface files whose presence proves the parent actually delegated
        // the controllers we need. Checking for the files beats parsing the
        // parent's cgroup.subtree_control: it is the same question asked of the
        // kernel directly, after the fact.
        var required = new List<string>();
        if (limits.MemoryBytes is not null) required.Add("memory.max");
        if (limits.Cpus is not null) required.Add("cpu.max");

        string name = $"ccrun-{pid}";
        foreach (string parent in CandidateParents())
        {
            string path = System.IO.Path.Combine(parent, name);
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // not writable by us — try the next ancestor up
            }

            if (!required.All(f => File.Exists(System.IO.Path.Combine(path, f))))
            {
                // The directory exists but the controller is not enabled in this
                // parent's subtree_control, so its interface files are absent and
                // the limit would be unenforceable. Undo and keep looking.
                TryRemove(path);
                continue;
            }

            var cgroup = new Cgroup(path);
            try
            {
                cgroup.ApplyLimits(limits);
                return cgroup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"ccrun: writing the cgroup limits failed: {ex.Message}");
                cgroup.Dispose();
                return null;
            }
        }

        stderr.WriteLine(
            "ccrun: no writable cgroup v2 subtree with the memory/cpu controllers is available. " +
            "On a systemd host, enable delegation for your user session " +
            "(e.g. 'systemctl --user set-property ... Delegate=yes', or check that " +
            "/sys/fs/cgroup/user.slice/user-$(id -u).slice/user@$(id -u).service/cgroup.subtree_control " +
            "lists 'cpu memory'), or run ccrun as root.");
        return null;
    }

    /// <summary>
    /// Our own cgroup directory and every ancestor of it, nearest first. The
    /// nearest is usually unusable — a leaf holding our own process has no
    /// controllers delegated below it, and cgroup v2's "no internal processes"
    /// rule stops it from gaining any while it holds tasks — but the presence
    /// check in <see cref="Create"/> filters those out rather than us trying to
    /// predict the layout.
    /// </summary>
    private static IEnumerable<string> CandidateParents()
    {
        // /proc/self/cgroup on a v2 host is a single line, "0::/<path>".
        string relative = "/";
        try
        {
            foreach (string line in File.ReadAllLines("/proc/self/cgroup"))
            {
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    relative = line[3..];
                    break;
                }
            }
        }
        catch (IOException)
        {
            // Fall through with "/" — the mount root is still worth a try (it is
            // what a root-owned run will use).
        }

        string current = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Mount, relative.TrimStart('/')));
        while (current.StartsWith(Mount, StringComparison.Ordinal))
        {
            yield return current;
            if (current == Mount)
                yield break;
            current = System.IO.Path.GetDirectoryName(current) ?? Mount;
        }
    }

    private void ApplyLimits(ResourceLimits limits)
    {
        if (limits.MemoryBytes is long bytes)
            WriteInterfaceFile("memory.max", bytes.ToString());
        if (limits.Cpus is not null)
            WriteInterfaceFile("cpu.max", limits.CpuMaxValue);
    }

    /// <summary>
    /// Moves a process into this cgroup by writing its PID to cgroup.procs. The
    /// limits above are already in place, so the process is capped from the moment
    /// it is admitted — which is why the caller does this before releasing the
    /// child (FR-6.5).
    /// </summary>
    public bool TryAddProcess(int pid, TextWriter stderr)
    {
        try
        {
            WriteInterfaceFile("cgroup.procs", pid.ToString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"ccrun: moving the container into its cgroup failed: {ex.Message}");
            return false;
        }
    }

    // cgroup interface files behave like the /proc map files ReExec writes: they
    // are parsed from a single write(2) and do not support truncation, so open the
    // existing file and write the whole payload in one call.
    private void WriteInterfaceFile(string name, string content)
    {
        using var fs = new FileStream(
            System.IO.Path.Combine(_path, name), FileMode.Open, FileAccess.Write);
        fs.Write(System.Text.Encoding.ASCII.GetBytes(content));
    }

    /// <summary>
    /// Removes the cgroup directory (FR-6.5). Safe to call more than once, and
    /// safe to call on an error path. rmdir only succeeds once the cgroup is
    /// empty, so the caller must have reaped the container first; a container that
    /// left descendants behind can keep it busy, and in that case we give up
    /// quietly rather than fail the run over a stray empty directory.
    /// </summary>
    public void Dispose()
    {
        if (_removed)
            return;
        _removed = true;
        TryRemove(_path);
    }

    private static void TryRemove(string path)
    {
        try
        {
            // Directory.Delete, not recursive: cgroup directories are synthetic and
            // their interface files cannot be unlinked, so a recursive delete would
            // throw. rmdir on the directory itself is the supported operation.
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Busy or already gone. Nothing actionable for the user.
        }
    }
}
```

### 4. `src/CCRun/Container/ReExec.cs`

One insertion in `RunChild`, in the existing post-clone window, plus a `finally`
for cleanup. The failure path must keep falling through to
`Libc.Close(s_childWriteFd)` and `WaitForExitCode` — otherwise a cgroup error
would leave the child blocked on the pipe forever and then zombified.

Replace the body from `Libc.Close(s_childReadFd);` through the `return` with:

```csharp
    Libc.Close(s_childReadFd);

    bool ready = WriteIdMaps(pid, stderr);

    // Create the cgroup and admit the child while it is still parked on the pipe,
    // so the limits are in force before it execs the user command (FR-6.5). The
    // child's host-side PID is what goes into cgroup.procs: the container's own
    // PID-namespace numbering means nothing to the cgroup filesystem.
    Cgroup? cgroup = null;
    if (ready && options.Limits.Any)
    {
        cgroup = Cgroup.Create(options.Limits, pid, stderr);
        ready = cgroup is not null && cgroup.TryAddProcess(pid, stderr);
    }

    try
    {
        if (ready)
        {
            // Any byte will do; the child only cares that the read completes.
            unsafe
            {
                byte go = 1;
                Libc.Write(s_childWriteFd, (IntPtr)(&go), 1);
            }
        }

        // Closing unblocks the child even when setup failed, so that case surfaces
        // as the child exiting rather than as a hang.
        Libc.Close(s_childWriteFd);

        // Reap unconditionally. Even on the failure path the child exists and must
        // be waited for, or we would exit leaving a zombie behind.
        int code = WaitForExitCode(pid, stderr);
        return ready ? code : ExitCodes.RuntimeError;
    }
    finally
    {
        // Only removable once the container has been reaped and the cgroup is
        // empty, which the waitpid above guarantees.
        cgroup?.Dispose();
    }
```

No new env var and no `ChildCommand` change: the cgroup is a property of the
process the parent already holds a PID for, so nothing needs to cross the exec.

### 5. `src/CCRun/Commands/RunCommand.cs`

No change. `options` already flows through to `ReExec.RunChild` and now carries
`Limits`; parse-time validation means there is nothing to pre-flight here the way
`--rootfs` needs.

### 6. `src/CCRun/Cli.cs`

Update the usage line (above) and the trailing prose to mention Phase 6:

```csharp
        w.WriteLine("Phase 6: 'run' isolates the hostname (UTS ns), runs rootless in a user namespace,");
        w.WriteLine("and can cap memory and CPU with --memory/--cpus via a cgroup v2 subtree.");
```

## Tests

### `tests/CCRun.Tests/ResourceLimitsTests.cs` (new)

Table-style unit tests, no privileges needed: `"512m"` → 536870912, `"1g"`,
`"1024"` (bare bytes), `"2k"`; rejects `""`, `"0"`, `"-1"`, `"abc"`, `"5x"`,
`"9223372036854775807g"` (overflow). `TryParseCpus`: `"0.5"`, `"2"`, `"1.5"`
accepted; `"0"`, `"-1"`, `"abc"`, `"NaN"` rejected. `CpuMaxValue` for 0.5 is
`"50000 100000"` and for 2 is `"200000 100000"`.

### `tests/CCRun.Tests/RunOptionsTests.cs` (extend)

Following the existing one-test-per-form convention: `--memory 512m` and
`--memory=512m` both land in `Limits.MemoryBytes`; same for `--cpus`; a missing
value produces `"--memory requires a value"`; a bad value produces
`"invalid --memory value"`; no flags leaves `Limits.Any` false. Also one test that
the refactored `TryTakeValue` did not change `--hostname`/`--rootfs` behaviour
(the existing tests already cover this — they must keep passing untouched).

### `tests/CCRun.Tests/RunCommandTests.cs` (extend)

Add a gate next to the existing `IsRoot`/`IsUserNsAvailable` ones, so the
integration tests skip cleanly on a host without delegation rather than failing:

```csharp
    /// <summary>
    /// True when cgroup v2 is mounted and some ancestor of our own cgroup will let
    /// us create a child with the memory and cpu controllers — the same search
    /// Cgroup.Create does, which is the honest precondition for the Phase 6 tests.
    /// </summary>
    internal static bool IsCgroupV2Delegated
    {
        get
        {
            using var probe = Cgroup.Create(
                new ResourceLimits(1L << 30, 1.0), Environment.ProcessId, TextWriter.Null);
            return probe is not null;
        }
    }
```

(`Cgroup` is `internal`; `InternalsVisibleTo CCRun.Tests` is already set in the
csproj, so no visibility change is needed.)

### `tests/CCRun.Tests/NamespaceIntegrationTests.cs` (extend)

Three tests, all out-of-process via the existing `Run(...)` helper, all gated on
`Skip.IfNot(RunCommandTests.IsUserNsAvailable, ...)` +
`Skip.IfNot(RunCommandTests.IsCgroupV2Delegated, ...)`:

1. **Membership** — reuse the live-container pattern from
   `MountNamespace_ContainerProcMountNotVisibleOnHost`: start
   `ccrun run --memory 128m /bin/sh -c 'touch <marker>; sleep 10'`, poll for the
   marker, then use the existing `FindHostPidByCmdline` to locate the container on
   the host and assert `/proc/<hostpid>/cgroup` ends with `/ccrun-<hostpid>`.
   Kill the tree in a `finally`.
2. **Values** — while that container is live, read `memory.max` from the same
   cgroup directory and assert `134217728`; with `--cpus 0.5`, assert `cpu.max` is
   `50000 100000`. Also assert the directory is **gone** after the process exits,
   which is the FR-6.5 cleanup check.
3. **OOM kill** — `ccrun run --memory 16m /bin/sh -c '...'` with a shell loop that
   grows a variable until it is killed; assert the exit code is `137`
   (128 + SIGKILL, which is exactly what `WaitForExitCode`'s signal branch
   produces). Give it a generous process timeout; if the host has swap enabled for
   the cgroup this can be slow, so the test also accepts a nonzero-and-not-125
   exit as evidence the limit bit, rather than asserting 137 alone.

A fourth, cheap, ungated test: `--memory 512m` on a host **without** delegation is
not reachable in tests, but the parse-error path is —
`ccrun run --memory bogus /bin/true` exits `1` with `invalid --memory value`.

## Documentation

- **`README.md`** — add `--memory`/`--cpus` to the usage synopsis; move cgroup v2
  from "Prerequisites" prose into a short "Resource limits" section explaining the
  delegation requirement and how to check it; update the Roadmap line to "Phases
  7–8 forthcoming".
- **`docs/code-overview/code-overview.md`** — new section "Resource limits and the
  cgroup" after "PID namespace, mount namespace, and a private `/proc`", covering:
  why the work lives in the parent's post-clone window, the delegated-subtree
  search and why `/sys/fs/cgroup` is not writable, why controller-file presence is
  the delegation test, and why cleanup can only happen after `waitpid`. Update
  "Where this goes next" to Phase 7.
- **`CLAUDE.md`** — update "What this is" to Phase 6; add the cgroup ordering
  constraint (limits + `cgroup.procs` must be written between `clone3` and the
  go-byte, in the parent, because the cloned child cannot do managed I/O and the
  chroot'd child cannot reach `/sys/fs/cgroup`) to the landmines list; add
  `Container/Cgroup.cs` and `ResourceLimits.cs` to Structure.
- **`docs/plans/plan-phase-6-ccrun.md`** — commit this plan alongside the others.

## Verification (end-to-end, no sudo)

```sh
dotnet build && dotnet test
BIN=src/CCRun/bin/Debug/net10.0/CCRun

# delegation check — expect 'cpu' and 'memory' in the output
cat /sys/fs/cgroup/user.slice/user-$(id -u).slice/user@$(id -u).service/cgroup.subtree_control

# limits are visible from inside the container
$BIN run --rootfs alpine-rootfs --memory 128m --cpus 0.5 \
    /bin/busybox sh -c 'cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/cpu.max' 2>/dev/null \
    || echo "(no /sys mount inside the container yet — use the host-side check below)"

# host-side: cgroup exists with the right values while the container runs, and is
# removed afterwards
$BIN run --memory 128m --cpus 0.5 /bin/sleep 30 &
sleep 1
CG=$(cat /proc/$(pgrep -f 'sleep 30' | head -1)/cgroup)
echo "$CG"                                   # .../ccrun-<pid>
D=/sys/fs/cgroup${CG#0::}
cat "$D/memory.max"                          # 134217728
cat "$D/cpu.max"                             # 50000 100000
kill %1; wait
test -d "$D" && echo "LEAKED" || echo "cleaned up"

# the memory cap actually bites: killed by SIGKILL => exit 137
$BIN run --memory 16m /bin/sh -c 'x=""; while :; do x="$x$(head -c 1000000 /dev/zero | tr "\0" a)"; done'
echo $?                                      # 137

# unaffected paths still work
$BIN run /bin/sh -c hostname                 # container
$BIN run --rootfs alpine-rootfs /bin/busybox sh -c 'echo $$'   # 1
$BIN run --memory bogus /bin/true; echo $?   # 1, with 'invalid --memory value'
```

## Implementation notes (deviations from the plan above, as built)

- **`memory.swap.max` is set to 0 alongside `memory.max`.** Not in the plan, and
  found while verifying the OOM test: on a host with swap the cgroup happily swaps
  once it reaches `memory.max`, so a runaway container thrashes for minutes instead
  of being killed. The cap held on paper but nothing observable happened. Writing
  `memory.swap.max=0` makes `--memory` a hard cap, and the OOM kill then lands in
  ~0.1s. The write is skipped when the file is absent (kernel built without swap
  accounting).
- **The OOM test asserts 137 but skips rather than hangs** if the container is still
  alive after 120s, which is what a host without swap accounting would produce. That
  is a host property, not a ccrun bug.
- **`RunOptions` grew a `Matches` helper** next to `TryTakeValue`, so the option
  chain reads `if (Matches(a, "--memory"))` rather than repeating the
  `a == x || a.StartsWith(x + "=")` test inline.
- **SIGKILLing ccrun leaks an empty cgroup directory**, since `finally` never runs.
  Unfixable in-process, and the reason real runtimes have a supervising shim; the
  membership test (which SIGKILLs the container it started) therefore removes the
  directory itself rather than leaving one behind on every `dotnet test`.
- **The integration tests get the cgroup path from the container itself**
  (`cat /proc/self/cgroup`) rather than via `FindHostPidByCmdline`. Without
  `--rootfs` the container sees the host's `/proc`, so it reports the real path —
  which is both simpler and free of the ambiguity that the parent ccrun process's
  command line also contains the marker text.

## Out of scope (later phases)

- `--pids-limit` / the pids controller — trivially addable, not FR-6.x.
- cgroup v1 support — the project targets v2 only.
- Mounting the container's own cgroup at `/sys/fs/cgroup` inside the container
  (needs a cgroup namespace); the host-side checks above cover FR-6.6's intent.
- Image pull and registry client — Phases 7–8.

## Git policy

Per `CLAUDE.md`, Claude will not commit. Changes will be staged with a drafted
commit message for human review.
