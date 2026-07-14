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
}
