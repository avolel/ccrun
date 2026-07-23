namespace ccrun;

/// <summary>
/// Parsed form of `ccrun run [options] <command> [args...]`. Options are the
/// leading `--`-prefixed tokens; the first non-option token is the command and
/// everything after it is passed through to the command unchanged.
/// </summary>
public sealed record RunOptions(
    string Hostname,
    string? Rootfs,
    ResourceLimits Limits,
    string Command,
    IReadOnlyList<string> CommandArgs)
{
    public const string DefaultHostname = "container";

    public const string Usage =
        "usage: ccrun run [--hostname <name>] [--rootfs <path>] [--memory <size>] [--cpus <n>] <command> [args...]";

    /// <summary>
    /// Parses run arguments. Returns null and writes a usage/error message to
    /// <paramref name="stderr"/> on invalid input (missing command, unknown
    /// option, an option without a value, or a malformed --memory/--cpus value).
    /// </summary>
    public static RunOptions? Parse(string[] args, TextWriter stderr)
    {
        string hostname = DefaultHostname;
        string? rootfs = null;
        long? memoryBytes = null;
        double? cpus = null;
        int i = 0;
        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { i++; break; }              // explicit end of options
            if (!a.StartsWith("--", StringComparison.Ordinal))
                break;                                   // first positional => command

            if (Matches(a, "--hostname"))
            {
                if (!TryTakeValue(args, ref i, "--hostname", stderr, out string hv))
                    return null;
                hostname = hv;
            }
            else if (Matches(a, "--rootfs"))
            {
                if (!TryTakeValue(args, ref i, "--rootfs", stderr, out string rv))
                    return null;
                rootfs = rv;
            }
            else if (Matches(a, "--memory"))
            {
                if (!TryTakeValue(args, ref i, "--memory", stderr, out string value))
                    return null;
                if (!ResourceLimits.TryParseMemory(value, out long bytes))
                {
                    stderr.WriteLine(
                        $"ccrun run: invalid --memory value '{value}' (expected a positive size such as 512m or 1g)");
                    return null;
                }
                memoryBytes = bytes;
            }
            else if (Matches(a, "--cpus"))
            {
                if (!TryTakeValue(args, ref i, "--cpus", stderr, out string value))
                    return null;
                if (!ResourceLimits.TryParseCpus(value, out double parsed))
                {
                    stderr.WriteLine(
                        $"ccrun run: invalid --cpus value '{value}' (expected a positive number such as 0.5 or 2)");
                    return null;
                }
                cpus = parsed;
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
            stderr.WriteLine(Usage);
            return null;
        }

        return new RunOptions(
            hostname, rootfs, new ResourceLimits(memoryBytes, cpus), args[i], args[(i + 1)..]);
    }

    /// <summary>True when the argument is <paramref name="name"/> in either the
    /// `--opt value` or the `--opt=value` form.</summary>
    private static bool Matches(string arg, string name) =>
        arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal);

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
}
