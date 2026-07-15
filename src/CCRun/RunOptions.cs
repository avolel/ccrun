namespace ccrun;

/// <summary>
/// Parsed form of `ccrun run [options] <command> [args...]`. Options are the
/// leading `--`-prefixed tokens; the first non-option token is the command and
/// everything after it is passed through to the command unchanged.
/// </summary>
public sealed record RunOptions(
    string Hostname,
    string? Rootfs,
    string Command,
    IReadOnlyList<string> CommandArgs)
{
    public const string DefaultHostname = "container";

    /// <summary>
    /// Parses run arguments. Returns null and writes a usage/error message to
    /// <paramref name="stderr"/> on invalid input (missing command, unknown
    /// option, or a --hostname/--rootfs without a value).
    /// </summary>
    public static RunOptions? Parse(string[] args, TextWriter stderr)
    {
        string hostname = DefaultHostname;
        string? rootfs = null;
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }              // explicit end of options
            if (!a.StartsWith("--", StringComparison.Ordinal))
                break;                                   // first positional => command

            if (a == "--hostname")
            {
                if (i + 1 >= args.Length)
                {
                    stderr.WriteLine("ccrun run: --hostname requires a value");
                    return null;
                }
                hostname = args[++i];
            }
            else if (a.StartsWith("--hostname=", StringComparison.Ordinal))
            {
                hostname = a["--hostname=".Length..];
            }
            else if (a == "--rootfs")
            {
                if (i + 1 >= args.Length)
                {
                    stderr.WriteLine("ccrun run: --rootfs requires a value");
                    return null;
                }
                rootfs = args[++i];
            }
            else if (a.StartsWith("--rootfs=", StringComparison.Ordinal))
            {
                rootfs = a["--rootfs=".Length..];
            }
            else
            {
                stderr.WriteLine($"ccrun run: unknown option '{a}'");
                return null;
            }
        }

        if (i >= args.Length)
        {
            stderr.WriteLine("ccrun run: missing command");
            stderr.WriteLine("usage: ccrun run [--hostname <name>] [--rootfs <path>] <command> [args...]");
            return null;
        }

        return new RunOptions(hostname, rootfs, args[i], args[(i + 1)..]);
    }
}
