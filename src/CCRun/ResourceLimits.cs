using System.Globalization;

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

        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0)
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
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out cpus)
        && cpus > 0
        && double.IsFinite(cpus);
}
