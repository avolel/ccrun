using ccrun;

namespace CCRun.Tests;

public class CliTests
{
    private static (int code, string outText, string errText) RunCli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void NoArgs_PrintsUsage_AndFails()
    {
        var (code, _, err) = RunCli();
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("usage:", err);
    }

    [Fact]
    public void UnknownVerb_ReportsError_AndFails()
    {
        var (code, _, err) = RunCli("bogus");
        Assert.NotEqual(ExitCodes.Ok, code);
        Assert.Contains("unknown command 'bogus'", err);
    }

    [Fact]
    public void Help_PrintsUsageToStdout_AndSucceeds()
    {
        var (code, outText, _) = RunCli("--help");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("usage:", outText);
    }

    [Fact]
    public void RunWithoutCommand_Fails()
    {
        var (code, _, err) = RunCli("run");
        Assert.Equal(ExitCodes.UsageError, code);
        Assert.Contains("missing command", err);
    }
}
