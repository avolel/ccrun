namespace ccrun;

/// <summary>
/// Top-level command dispatch. Parses the verb and routes to the matching
/// command. Writers are injectable (no Console statics) so dispatch and usage
/// behaviour are unit-testable.
/// </summary>
public static class Cli
{
    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        if (args.Length == 0)
        {
            PrintUsage(stderr);
            return ExitCodes.UsageError;
        }

        string verb = args[0];
        switch (verb)
        {
            case "run":
                return RunCommand.Execute(args.AsSpan(1).ToArray(), stdout, stderr);

            case "-h":
            case "--help":
            case "help":
                PrintUsage(stdout);
                return ExitCodes.Ok;

            default:
                stderr.WriteLine($"ccrun: unknown command '{verb}'");
                PrintUsage(stderr);
                return ExitCodes.UsageError;
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("ccrun: a lightweight Linux container runtime");
        w.WriteLine();
        w.WriteLine("usage:");
        w.WriteLine("  ccrun run <command> [args...]   run a command in a container");
        w.WriteLine("  ccrun --help                    show this help");
        w.WriteLine();
        w.WriteLine("Phase 1: 'run' executes the command directly (no isolation yet).");
    }
}
