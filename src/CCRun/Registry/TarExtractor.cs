using System.Formats.Tar;
using System.IO.Compression;

namespace ccrun;

/// <summary>
/// Extracts a single gzipped OCI/Docker image layer into a rootfs directory.
///
/// It honors overlayfs whiteouts (<c>.wh.&lt;name&gt;</c> deletions and the
/// <c>.wh..wh..opq</c> opaque-directory marker) so a multi-layer image
/// reconstructs correctly, and it refuses any entry that would escape the
/// rootfs — both the lexical <c>../</c> / absolute-path case and the subtler
/// "write through a symlink an earlier entry planted" case, which a purely
/// lexical prefix check does not catch.
///
/// Extraction is rootless: it never chowns (uid/gid are ignored) and skips
/// device nodes, which an unprivileged user cannot create and a container does
/// not need to run.
/// </summary>
internal static class TarExtractor
{
    // A basename of exactly this empties the parent dir's lower-layer contents.
    private const string OpaqueWhiteout = ".wh..wh..opq";
    
    // A basename with this prefix deletes "<parent>/<rest-of-name>".
    private const string WhiteoutPrefix = ".wh.";

    internal static void ExtractLayer(Stream gzippedTar, string rootfsDir)
    {
        // Canonicalize the rootfs once; every entry path is validated against
        // this prefix. Trailing separator stripped so the StartsWith guard can
        // append exactly one separator without a false "rootfs" vs "rootfs/".
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootfsDir));

        using var gzip = new GZipStream(gzippedTar, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        // GetNextEntry() defaults to copyData:false, so entry.DataStream is a
        // forward-only view over the archive valid only until the next call —
        // hence each entry's payload is consumed fully inside this loop body.
        while (reader.GetNextEntry() is { } entry)
        {
            var name = NormalizeName(entry.Name);
            if (name is null)
                continue;

            var full = ResolvePath(root, name);
            var baseName = Path.GetFileName(full);

            // Whiteouts are markers, not files: applied to already-extracted
            // lower layers and never written to disk. Opaque is checked first
            // because it also carries the ".wh." prefix.
            if (baseName == OpaqueWhiteout)
            {
                ClearDirectory(Path.GetDirectoryName(full)!);
                continue;
            }
            if (baseName.StartsWith(WhiteoutPrefix, StringComparison.Ordinal))
            {
                var victim = Path.Combine(Path.GetDirectoryName(full)!, baseName[WhiteoutPrefix.Length..]);
                RemoveExisting(victim);
                continue;
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(full);
                    ApplyMode(full, entry);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    RemoveExisting(full);
                    using (var file = File.Create(full))
                        entry.DataStream?.CopyTo(file);
                    ApplyMode(full, entry);
                    break;

                case TarEntryType.SymbolicLink:
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    RemoveExisting(full);
                    // The target is stored verbatim and never followed here; an
                    // absolute or ../ target is data, resolved later relative to
                    // the container's chroot, not a path we traverse now.
                    File.CreateSymbolicLink(full, entry.LinkName);
                    break;

                case TarEntryType.HardLink:
                    // LinkName is archive-relative; guard it exactly like any
                    // other path so a hardlink can't reach a file outside rootfs.
                    var sourceName = NormalizeName(entry.LinkName)
                        ?? throw new InvalidDataException($"tar hardlink '{entry.Name}' has an empty target");
                    var source = ResolvePath(root, sourceName);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    RemoveExisting(full);
                    // No BCL hardlink API; a copy is semantically fine for a
                    // throwaway rootfs and avoids P/Invoke here.
                    File.Copy(source, full, overwrite: true);
                    break;

                // Character/block/fifo devices and anything else: unprivileged
                // extraction can't mknod them and images don't need them. Skip.
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Strips a leading "./" and any leading slash from a tar name and rejects
    /// the no-op names. Returns null for an entry that should be skipped.
    /// </summary>
    private static string? NormalizeName(string rawName)
    {
        var name = rawName.Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name[2..];
        name = name.TrimStart('/');
        return name.Length == 0 || name == "." ? null : name;
    }

    /// <summary>
    /// Combines <paramref name="relative"/> onto <paramref name="root"/> and
    /// validates the result stays inside the rootfs — both lexically (after
    /// collapsing any <c>..</c>) and against symlinked parent directories.
    /// Throws <see cref="InvalidDataException"/> on any escape.
    /// </summary>
    private static string ResolvePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException($"tar entry '{relative}' is an absolute path");

        // GetFullPath collapses "a/../b" etc., so a traversal that escapes the
        // rootfs fails the prefix check below rather than sneaking through.
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"tar entry '{relative}' escapes the rootfs");

        if (EscapesViaSymlink(root, full))
            throw new InvalidDataException($"tar entry '{relative}' is written through a symlink");

        return full;
    }

    /// <summary>
    /// True if any existing parent directory of <paramref name="full"/> is a
    /// symlink. Extracting through it would follow the link and could land the
    /// write outside the rootfs, which the lexical check cannot see because
    /// GetFullPath does not resolve symlinks. Real image layers store files at
    /// canonical paths, so a symlinked ancestor here means a hostile archive.
    /// </summary>
    private static bool EscapesViaSymlink(string root, string full)
    {
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
            return false;

        var relative = Path.GetRelativePath(root, parent);
        if (relative == ".")
            return false;

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsSymlink(current))
                return true;
        }
        return false;
    }

    /// <summary>Removes every child of a directory (the opaque-whiteout action).</summary>
    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var child in Directory.EnumerateFileSystemEntries(dir))
            RemoveExisting(child);
    }

    /// <summary>
    /// Deletes a path if present. A symlink is checked first and removed as a
    /// link — never recursed into — so deleting a symlink-to-dir cannot reach
    /// the link's target contents.
    /// </summary>
    private static void RemoveExisting(string path)
    {
        if (IsSymlink(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>
    /// Applies the tar entry's permission bits (perms + setuid/setgid/sticky).
    /// uid/gid are deliberately not applied — extraction is rootless.
    /// </summary>
    private static void ApplyMode(string path, TarEntry entry)
    {
        // entry.Mode already carries the perm + setuid/setgid/sticky bits.
        if (entry.Mode != UnixFileMode.None)
            File.SetUnixFileMode(path, entry.Mode);
    }
}
