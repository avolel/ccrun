// CCRun — a lightweight Linux container runtime (learning project).
// Entrypoint: delegates to Cli so the logic stays unit-testable.
using ccrun;

return Cli.Run(args);
