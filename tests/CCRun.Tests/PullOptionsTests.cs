using ccrun;

namespace CCRun.Tests;

public class PullOptionsTests
{
    private static (PullOptions? options, string err) Parse(params string[] args)
    {
        var stderr = new StringWriter();
        var options = PullOptions.Parse(args, stderr);
        return (options, stderr.ToString());
    }

    [Fact]
    public void Parse_AcceptsImageReference()
    {
        var (options, _) = Parse("ubuntu");
        Assert.NotNull(options);
        Assert.Equal("library/ubuntu", options!.Image.Repository);
        Assert.Equal("latest", options.Image.Tag);
    }

    [Fact]
    public void Parse_MissingImage_Fails()
    {
        var (options, err) = Parse();
        Assert.Null(options);
        Assert.Contains("missing image reference", err);
    }

    [Fact]
    public void Parse_ExtraArgument_Fails()
    {
        var (options, err) = Parse("ubuntu", "extra");
        Assert.Null(options);
        Assert.Contains("unexpected argument", err);
    }

    [Fact]
    public void Parse_InvalidReference_Fails()
    {
        var (options, err) = Parse("UPPER::bad");
        Assert.Null(options);
        Assert.Contains("invalid image reference", err);
    }
}
