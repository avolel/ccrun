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
        {
            WriteInterfaceFile("memory.max", bytes.ToString());

            // Deny the container swap as well, so --memory is a hard cap on what it can
            // actually consume. Left at the default "max" the cgroup simply swaps once it
            // hits memory.max: the limit still holds on paper, but a runaway process
            // thrashes indefinitely instead of being killed, which is neither what the
            // user asked for nor observable. The file is absent when the kernel was built
            // without swap accounting, and then there is nothing to do.
            if (File.Exists(System.IO.Path.Combine(_path, "memory.swap.max")))
                WriteInterfaceFile("memory.swap.max", "0");
        }
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
