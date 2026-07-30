using System.Security.Cryptography;
using System.Text;
using ccrun;

namespace CCRun.Tests;

public class DigestTests
{
    // SHA-256("abc"), the canonical test vector.
    private const string AbcDigest =
        "sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static readonly byte[] Abc = Encoding.ASCII.GetBytes("abc");

    [Fact]
    public void Verify_AcceptsMatchingDigest()
    {
        Assert.True(Digest.Verify(AbcDigest, Abc));
    }

    [Fact]
    public void Verify_IsCaseInsensitiveOnHex()
    {
        Assert.True(Digest.Verify(AbcDigest.ToUpperInvariant(), Abc));
    }

    [Fact]
    public void Verify_RejectsTamperedData()
    {
        var tampered = (byte[])Abc.Clone();
        tampered[0] ^= 0xFF;
        Assert.False(Digest.Verify(AbcDigest, tampered));
    }

    [Fact]
    public void Verify_RejectsWrongPrefix()
    {
        Assert.False(Digest.Verify("md5:" + AbcDigest["sha256:".Length..], Abc));
    }

    [Fact]
    public void VerifyHash_MatchesStreamedChunks()
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Abc, 0, 1);
        hasher.AppendData(Abc, 1, 2);
        Assert.True(Digest.VerifyHash(AbcDigest, hasher));
    }
}
