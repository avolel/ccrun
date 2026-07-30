using System.Security.Cryptography;

namespace ccrun;

/// <summary>
/// SHA-256 digest verification for registry blobs. Handles both the whole-buffer
/// case and the streaming case, where a blob is hashed as it is written to disk
/// so it never has to be held in memory in full (NFR-5).
///
/// Registry digests are the <c>sha256:&lt;64 hex&gt;</c> form; the hex compare is
/// case-insensitive.
/// </summary>
internal static class Digest
{
    private const string Sha256Prefix = "sha256:";

    public static bool Verify(string expected, ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(data, hash);
        return Matches(expected, hash);
    }

    /// <summary>Verifies against the accumulated hash of an incremental hasher
    /// (and resets it). Pair with <see cref="IncrementalHash"/> fed chunk by
    /// chunk while streaming a blob to disk.</summary>
    public static bool VerifyHash(string expected, IncrementalHash hasher) =>
        Matches(expected, hasher.GetHashAndReset());

    private static bool Matches(string expected, ReadOnlySpan<byte> hash)
    {
        if (!expected.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(
            expected[Sha256Prefix.Length..], Convert.ToHexString(hash), StringComparison.OrdinalIgnoreCase);
    }
}
