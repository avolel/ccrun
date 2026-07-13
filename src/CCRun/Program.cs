// CCRun — a lightweight Linux container runtime (learning project).
// Phase 0: placeholder entrypoint. Real command parsing arrives in Phase 1.

if (args.Length == 0)
{
    Console.Error.WriteLine("ccrun: a lightweight Linux container runtime");
    Console.Error.WriteLine("usage: ccrun <command> [options]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("No commands are implemented yet (Phase 0 scaffold).");
    return 1;
}

Console.Error.WriteLine($"ccrun: unknown command '{args[0]}' (not implemented yet)");
return 1;
