namespace ccrun;

/// <summary>
/// Owns the on-disk image layout under <c>~/.ccrun/images/</c>:
/// <c>&lt;repository&gt;/&lt;tag&gt;/rootfs</c> and a sibling
/// <c>config.json</c>. The produced <c>rootfs</c> is a plain directory that
/// <c>ccrun run --rootfs</c> consumes unchanged.
/// </summary>
internal sealed class ImageStore
{
    private readonly string _imageDir;

    public ImageStore(ImageReference image, string? baseDirectory = null)
    {
        baseDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccrun", "images");
        // Repository already uses '/'; map it onto the platform separator.
        var repoPath = image.Repository.Replace('/', Path.DirectorySeparatorChar);
        _imageDir = Path.Combine(baseDirectory, repoPath, image.Tag);
        RootfsDir = Path.GetFullPath(Path.Combine(_imageDir, "rootfs"));
        ConfigPath = Path.Combine(_imageDir, "config.json");
    }

    /// <summary>Absolute path of the extracted root filesystem.</summary>
    public string RootfsDir { get; }

    public string ConfigPath { get; }

    /// <summary>Clears any rootfs left by a previous pull so layers apply to a
    /// clean tree.</summary>
    public void ResetRootfs()
    {
        if (Directory.Exists(RootfsDir))
            Directory.Delete(RootfsDir, recursive: true);
        Directory.CreateDirectory(RootfsDir);
    }

    /// <summary>Extracts one gzipped-tar layer over the current rootfs. Call once
    /// per layer in manifest order.</summary>
    public void ExtractLayer(Stream gzippedTar) => TarExtractor.ExtractLayer(gzippedTar, RootfsDir);

    public void WriteConfig(byte[] config)
    {
        Directory.CreateDirectory(_imageDir);
        File.WriteAllBytes(ConfigPath, config);
    }
}
