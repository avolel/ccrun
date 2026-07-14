namespace ccrun;

/// <summary>Exit codes produced by ccrun itself, distinct from codes
/// propagated from the child command.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int UsageError = 1;
    // Shell conventions for launch failures, reused so scripts/users see
    // familiar values.
    public const int CommandNotExecutable = 126;
    public const int CommandNotFound = 127;
    
    // Container setup failed before the user command ran (namespace / hostname
    // setup, re-exec). Mirrors Docker's 125 "runtime error" convention.
    public const int RuntimeError = 125;
}
