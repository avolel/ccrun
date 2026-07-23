using ccrun;

namespace CCRun.Tests;

// Pure value parsing for --memory/--cpus — no cgroups, no privileges.
public class ResourceLimitsTests
{
    [Theory]
    [InlineData("1024", 1024L)]           // bare bytes
    [InlineData("512b", 512L)]
    [InlineData("2k", 2L * 1024)]
    [InlineData("512m", 512L * 1024 * 1024)]
    [InlineData("1g", 1024L * 1024 * 1024)]
    [InlineData("1G", 1024L * 1024 * 1024)] // suffix is case-insensitive
    public void TryParseMemory_Accepts(string text, long expected)
    {
        Assert.True(ResourceLimits.TryParseMemory(text, out long bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("5x")]
    [InlineData("m")]
    [InlineData("9223372036854775807g")]  // would overflow long
    public void TryParseMemory_Rejects(string text)
    {
        Assert.False(ResourceLimits.TryParseMemory(text, out _));
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("1", 1.0)]
    [InlineData("1.5", 1.5)]
    [InlineData("2", 2.0)]
    public void TryParseCpus_Accepts(string text, double expected)
    {
        Assert.True(ResourceLimits.TryParseCpus(text, out double cpus));
        Assert.Equal(expected, cpus);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParseCpus_Rejects(string text)
    {
        Assert.False(ResourceLimits.TryParseCpus(text, out _));
    }

    [Theory]
    [InlineData(0.5, "50000 100000")]
    [InlineData(1.0, "100000 100000")]
    [InlineData(2.0, "200000 100000")]   // quota above period == more than one core
    public void CpuMaxValue_IsQuotaAndPeriod(double cpus, string expected)
    {
        Assert.Equal(expected, new ResourceLimits(null, cpus).CpuMaxValue);
    }

    [Fact]
    public void Any_IsFalse_WhenNoLimitsRequested()
    {
        Assert.False(new ResourceLimits(null, null).Any);
        Assert.True(new ResourceLimits(1024, null).Any);
        Assert.True(new ResourceLimits(null, 1.0).Any);
    }
}
