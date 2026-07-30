using ccrun;

namespace CCRun.Tests;

// Pure image-reference parsing/normalization — no I/O.
public class ImageReferenceTests
{
    [Theory]
    // input                       registry                 repository        tag
    [InlineData("ubuntu", "registry-1.docker.io", "library/ubuntu", "latest")]
    [InlineData("ubuntu:22.04", "registry-1.docker.io", "library/ubuntu", "22.04")]
    [InlineData("library/ubuntu", "registry-1.docker.io", "library/ubuntu", "latest")]
    [InlineData("myorg/myapp", "registry-1.docker.io", "myorg/myapp", "latest")]
    [InlineData("myorg/myapp:v1", "registry-1.docker.io", "myorg/myapp", "v1")]
    [InlineData("localhost:5000/foo", "localhost:5000", "foo", "latest")]
    [InlineData("gcr.io/proj/img:tag", "gcr.io", "proj/img", "tag")]
    public void TryParse_Normalizes(string input, string registry, string repository, string tag)
    {
        Assert.True(ImageReference.TryParse(input, out var image));
        Assert.Equal(registry, image!.Registry);
        Assert.Equal(repository, image.Repository);
        Assert.Equal(tag, image.Tag);
        Assert.Null(image.Digest);
    }

    [Fact]
    public void TryParse_KeepsDigest_AndAddressesByIt()
    {
        var digest = "sha256:" + new string('a', 64);
        Assert.True(ImageReference.TryParse($"ubuntu@{digest}", out var image));
        Assert.Equal("library/ubuntu", image!.Repository);
        Assert.Equal(digest, image.Digest);
        Assert.Equal(digest, image.ManifestReference); // digest wins over tag
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UPPER")]            // repositories are lowercase
    [InlineData("ubuntu::22.04")]    // double colon
    [InlineData("ubuntu:")]          // empty tag
    [InlineData("foo/")]             // trailing slash => empty component
    [InlineData("ubuntu@sha256:xyz")] // malformed digest
    [InlineData("ubuntu@deadbeef")]   // digest without sha256: prefix
    public void TryParse_Rejects(string input)
    {
        Assert.False(ImageReference.TryParse(input, out var image));
        Assert.Null(image);
    }
}
