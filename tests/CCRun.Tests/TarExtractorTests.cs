using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ccrun;

namespace CCRun.Tests;

// Extraction is exercised end-to-end against real gzipped tars built in memory
// and unpacked into a throwaway temp dir. No privileges, no network. The
// security cases (traversal, write-through-symlink, escaping hardlink) are the
// point of the file, so they get the most coverage.
public sealed class TarExtractorTests : IDisposable
{
    private readonly string _root;

    public TarExtractorTests() =>
        _root = Directory.CreateTempSubdirectory("ccrun-tar-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- happy path --------------------------------------------------------

    [Fact]
    public void ExtractLayer_WritesFileWithContentAndExecBit()
    {
        var layer = BuildLayer(tar =>
            AddFile(tar, "bin/run.sh", "#!/bin/sh\n", Mode0755));

        TarExtractor.ExtractLayer(layer, _root);

        var path = Path.Combine(_root, "bin/run.sh");
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(path));
        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void ExtractLayer_CreatesSymlinkVerbatimWithoutFollowing()
    {
        var layer = BuildLayer(tar =>
        {
            AddDir(tar, "usr/bin");
            AddSymlink(tar, "bin", "usr/bin");
        });

        TarExtractor.ExtractLayer(layer, _root);

        var link = Path.Combine(_root, "bin");
        Assert.Equal("usr/bin", new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void ExtractLayer_AbsoluteNameIsContainedNotEscaped()
    {
        // A leading slash is stripped (standard tar behavior) and the entry
        // lands inside the rootfs rather than at the host's /etc.
        var layer = BuildLayer(tar => AddFile(tar, "/etc/hostname", "container\n"));

        TarExtractor.ExtractLayer(layer, _root);

        Assert.True(File.Exists(Path.Combine(_root, "etc/hostname")));
        Assert.False(File.Exists("/etc/ccrun-should-not-exist"));
    }

    // ---- traversal / escape guards ----------------------------------------

    [Fact]
    public void ExtractLayer_RejectsParentTraversal()
    {
        var layer = BuildLayer(tar => AddFile(tar, "../escape.txt", "pwned"));

        Assert.Throws<InvalidDataException>(() => TarExtractor.ExtractLayer(layer, _root));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escape.txt")));
    }

    [Fact]
    public void ExtractLayer_RejectsWriteThroughPlantedSymlink()
    {
        // The classic tar-slip: entry 1 plants a symlink pointing outside the
        // rootfs; entry 2 writes "through" it. Lexically entry 2 looks inside,
        // so only the symlink-ancestor guard catches it.
        var outside = Directory.CreateTempSubdirectory("ccrun-outside-").FullName;
        try
        {
            var layer = BuildLayer(tar =>
            {
                AddSymlink(tar, "evil", outside);
                AddFile(tar, "evil/pwned.txt", "escaped");
            });

            Assert.Throws<InvalidDataException>(() => TarExtractor.ExtractLayer(layer, _root));
            Assert.False(File.Exists(Path.Combine(outside, "pwned.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void ExtractLayer_RejectsHardlinkEscapingRootfs()
    {
        var layer = BuildLayer(tar => AddHardLink(tar, "loot", "../../../../etc/passwd"));

        Assert.Throws<InvalidDataException>(() => TarExtractor.ExtractLayer(layer, _root));
    }

    // ---- whiteouts ---------------------------------------------------------

    [Fact]
    public void ExtractLayer_WhiteoutDeletesLowerLayerFile()
    {
        TarExtractor.ExtractLayer(BuildLayer(tar =>
        {
            AddFile(tar, "data/keep.txt", "keep");
            AddFile(tar, "data/gone.txt", "gone");
        }), _root);

        TarExtractor.ExtractLayer(BuildLayer(tar =>
            AddFile(tar, "data/.wh.gone.txt", "")), _root);

        Assert.True(File.Exists(Path.Combine(_root, "data/keep.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "data/gone.txt")));
    }

    [Fact]
    public void ExtractLayer_OpaqueWhiteoutEmptiesDirectory()
    {
        TarExtractor.ExtractLayer(BuildLayer(tar =>
        {
            AddFile(tar, "data/a.txt", "a");
            AddFile(tar, "data/b.txt", "b");
        }), _root);

        TarExtractor.ExtractLayer(BuildLayer(tar =>
            AddFile(tar, "data/.wh..wh..opq", "")), _root);

        var dir = Path.Combine(_root, "data");
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    // ---- tar-building helpers ---------------------------------------------

    private const UnixFileMode Mode0644 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode0755 =
        Mode0644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static Stream BuildLayer(Action<TarWriter> write)
    {
        var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
            write(tar);
        raw.Position = 0;
        return raw;
    }

    private static void AddFile(TarWriter tar, string name, string content, UnixFileMode mode = Mode0644)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = mode,
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        };
        tar.WriteEntry(entry);
    }

    private static void AddDir(TarWriter tar, string name) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name) { Mode = Mode0755 });

    private static void AddSymlink(TarWriter tar, string name, string target) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target });

    private static void AddHardLink(TarWriter tar, string name, string target) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, name) { LinkName = target });
}
