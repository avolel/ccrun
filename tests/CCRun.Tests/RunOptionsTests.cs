using ccrun;

namespace CCRun.Tests;

// Pure parsing tests for `run` options — no namespaces, no spawning, no root.
public class RunOptionsTests
{
    private static (RunOptions? opts, string err) Parse(params string[] args)
    {
        var stderr = new StringWriter();
        var opts = RunOptions.Parse(args, stderr);
        return (opts, stderr.ToString());
    }

    [Fact]
    public void DefaultHostname_WhenNoOption()
    {
        var (opts, _) = Parse("true");
        Assert.NotNull(opts);
        Assert.Equal(RunOptions.DefaultHostname, opts!.Hostname);
        Assert.Equal("true", opts.Command);
        Assert.Empty(opts.CommandArgs);
    }

    [Fact]
    public void HostnameOption_SpaceSeparated()
    {
        var (opts, _) = Parse("--hostname", "web", "true");
        Assert.NotNull(opts);
        Assert.Equal("web", opts!.Hostname);
        Assert.Equal("true", opts.Command);
        Assert.Empty(opts.CommandArgs);
    }

    [Fact]
    public void HostnameOption_EqualsForm()
    {
        var (opts, _) = Parse("--hostname=web", "true");
        Assert.NotNull(opts);
        Assert.Equal("web", opts!.Hostname);
        Assert.Equal("true", opts.Command);
    }

    [Fact]
    public void DoubleDash_EndsOptions_ThenCommand()
    {
        var (opts, _) = Parse("--hostname", "web", "--", "echo", "hi");
        Assert.NotNull(opts);
        Assert.Equal("web", opts!.Hostname);
        Assert.Equal("echo", opts.Command);
        Assert.Equal(new[] { "hi" }, opts.CommandArgs);
    }

    [Fact]
    public void FlagsAfterCommand_PassThroughUntouched()
    {
        // The first positional is the command; everything after it — even
        // `--`-prefixed tokens — belongs to the command, not to ccrun.
        var (opts, _) = Parse("echo", "--hostname");
        Assert.NotNull(opts);
        Assert.Equal(RunOptions.DefaultHostname, opts!.Hostname);
        Assert.Equal("echo", opts.Command);
        Assert.Equal(new[] { "--hostname" }, opts.CommandArgs);
    }

    [Fact]
    public void CommandArgs_PassThrough()
    {
        var (opts, _) = Parse("ls", "-la");
        Assert.NotNull(opts);
        Assert.Equal("ls", opts!.Command);
        Assert.Equal(new[] { "-la" }, opts.CommandArgs);
    }

    [Fact]
    public void MissingCommand_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse();
        Assert.Null(opts);
        Assert.Contains("missing command", err);
    }

    [Fact]
    public void MissingCommand_AfterOption_ReturnsNull()
    {
        var (opts, err) = Parse("--hostname", "web");
        Assert.Null(opts);
        Assert.Contains("missing command", err);
    }

    [Fact]
    public void UnknownOption_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--bogus", "true");
        Assert.Null(opts);
        Assert.Contains("unknown option", err);
    }

    [Fact]
    public void HostnameWithoutValue_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--hostname");
        Assert.Null(opts);
        Assert.Contains("--hostname requires a value", err);
    }

    [Fact]
    public void RootfsOption_SpaceSeparated()
    {
        var (opts, _) = Parse("--rootfs", "/r", "true");
        Assert.NotNull(opts);
        Assert.Equal("/r", opts!.Rootfs);
        Assert.Equal("true", opts.Command);
    }

    [Fact]
    public void RootfsOption_EqualsForm()
    {
        var (opts, _) = Parse("--rootfs=/r", "true");
        Assert.NotNull(opts);
        Assert.Equal("/r", opts!.Rootfs);
    }

    [Fact]
    public void DefaultRootfs_IsNull_WhenNoOption()
    {
        var (opts, _) = Parse("true");
        Assert.NotNull(opts);
        Assert.Null(opts!.Rootfs);
    }

    [Fact]
    public void RootfsAndHostname_Combined()
    {
        var (opts, _) = Parse("--hostname", "web", "--rootfs", "/r", "/bin/sh");
        Assert.NotNull(opts);
        Assert.Equal("web", opts!.Hostname);
        Assert.Equal("/r", opts.Rootfs);
        Assert.Equal("/bin/sh", opts.Command);
    }

    [Fact]
    public void RootfsWithoutValue_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--rootfs");
        Assert.Null(opts);
        Assert.Contains("--rootfs requires a value", err);
    }

    [Fact]
    public void RootfsAfterCommand_PassesThrough()
    {
        var (opts, _) = Parse("echo", "--rootfs", "/r");
        Assert.NotNull(opts);
        Assert.Equal("echo", opts!.Command);
        Assert.Equal(new[] { "--rootfs", "/r" }, opts.CommandArgs);
        Assert.Null(opts.Rootfs);
    }

    [Fact]
    public void NoLimitOptions_LeavesLimitsEmpty()
    {
        // The whole cgroup mechanism is skipped when nothing was requested, so
        // "no flags => Any is false" is the contract that keeps the common case
        // exactly as it was before Phase 6.
        var (opts, _) = Parse("true");
        Assert.NotNull(opts);
        Assert.False(opts!.Limits.Any);
        Assert.Null(opts.Limits.MemoryBytes);
        Assert.Null(opts.Limits.Cpus);
    }

    [Fact]
    public void MemoryOption_SpaceSeparated()
    {
        var (opts, _) = Parse("--memory", "512m", "true");
        Assert.NotNull(opts);
        Assert.Equal(512L * 1024 * 1024, opts!.Limits.MemoryBytes);
        Assert.Equal("true", opts.Command);
    }

    [Fact]
    public void MemoryOption_EqualsForm()
    {
        var (opts, _) = Parse("--memory=512m", "true");
        Assert.NotNull(opts);
        Assert.Equal(512L * 1024 * 1024, opts!.Limits.MemoryBytes);
    }

    [Fact]
    public void MemoryWithoutValue_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--memory");
        Assert.Null(opts);
        Assert.Contains("--memory requires a value", err);
    }

    [Fact]
    public void MemoryWithBadValue_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--memory", "bogus", "true");
        Assert.Null(opts);
        Assert.Contains("invalid --memory value", err);
    }

    [Fact]
    public void CpusOption_SpaceSeparated()
    {
        var (opts, _) = Parse("--cpus", "0.5", "true");
        Assert.NotNull(opts);
        Assert.Equal(0.5, opts!.Limits.Cpus);
    }

    [Fact]
    public void CpusOption_EqualsForm()
    {
        var (opts, _) = Parse("--cpus=2", "true");
        Assert.NotNull(opts);
        Assert.Equal(2.0, opts!.Limits.Cpus);
    }

    [Fact]
    public void CpusWithoutValue_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--cpus");
        Assert.Null(opts);
        Assert.Contains("--cpus requires a value", err);
    }

    [Fact]
    public void CpusWithBadValue_ReturnsNull_WithMessage()
    {
        var (opts, err) = Parse("--cpus", "-1", "true");
        Assert.Null(opts);
        Assert.Contains("invalid --cpus value", err);
    }

    [Fact]
    public void AllOptions_Combined()
    {
        var (opts, _) = Parse(
            "--hostname", "web", "--rootfs", "/r", "--memory=1g", "--cpus", "1.5", "/bin/sh", "-c", "id");
        Assert.NotNull(opts);
        Assert.Equal("web", opts!.Hostname);
        Assert.Equal("/r", opts.Rootfs);
        Assert.Equal(1024L * 1024 * 1024, opts.Limits.MemoryBytes);
        Assert.Equal(1.5, opts.Limits.Cpus);
        Assert.Equal("/bin/sh", opts.Command);
        Assert.Equal(new[] { "-c", "id" }, opts.CommandArgs);
    }

    [Fact]
    public void LimitFlagsAfterCommand_PassThrough()
    {
        var (opts, _) = Parse("echo", "--memory", "512m");
        Assert.NotNull(opts);
        Assert.Equal("echo", opts!.Command);
        Assert.Equal(new[] { "--memory", "512m" }, opts.CommandArgs);
        Assert.False(opts.Limits.Any);
    }
}
