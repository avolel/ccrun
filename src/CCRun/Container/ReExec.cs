using System.Diagnostics;
using System.Reflection;

namespace ccrun;

/// <summary>
/// Re-invokes ccrun itself in the hidden <c>__child</c> init stage. The parent
/// creates the namespace(s); this child (inheriting them) performs in-namespace
/// setup (sethostname, later chroot / proc mount) before running the user
/// command. Stdio is inherited so interactivity survives the extra hop.
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
