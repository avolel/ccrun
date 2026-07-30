namespace ccrun;

/// <summary>
/// Parsed form of <c>ccrun pull &lt;image&gt;</c>. A single positional image
/// reference; no options (the target platform is the host's, selected
/// automatically). Returns null on bad input after reporting to stderr, mirroring
/// <see cref="RunOptions"/>.
/// </summary>
internal sealed record PullOptions(ImageReference Image)
{
    public const string Usage = "usage: ccrun pull <image>";

    public static PullOptions? Parse(string[] args, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("ccrun pull: missing image reference");
            stderr.WriteLine(Usage);
            return null;
        }
        if (args.Length > 1)
        {
            stderr.WriteLine($"ccrun pull: unexpected argument '{args[1]}'");
            stderr.WriteLine(Usage);
            return null;
        }
        if (!ImageReference.TryParse(args[0], out var image))
        {
            stderr.WriteLine($"ccrun pull: invalid image reference '{args[0]}'");
            return null;
        }
        return new PullOptions(image!);
    }
}
