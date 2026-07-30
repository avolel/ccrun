using System.Text.Json;
using ccrun;

namespace CCRun.Tests;

public class ManifestParsingTests
{
    // An OCI index with three children: an amd64 image, an arm64 image, and an
    // attestation entry (architecture "unknown") that must be skipped.
    private const string IndexJson = """
    {
      "mediaType": "application/vnd.oci.image.index.v1+json",
      "manifests": [
        { "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "digest": "sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "size": 1, "platform": { "os": "linux", "architecture": "amd64" } },
        { "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222",
          "size": 2, "platform": { "os": "linux", "architecture": "arm64" } },
        { "mediaType": "application/vnd.oci.image.manifest.v1+json",
          "digest": "sha256:3333333333333333333333333333333333333333333333333333333333333333",
          "size": 3, "platform": { "os": "unknown", "architecture": "unknown" } }
      ]
    }
    """;

    private const string ManifestJson = """
    {
      "mediaType": "application/vnd.oci.image.manifest.v1+json",
      "config": { "mediaType": "application/vnd.oci.image.config.v1+json",
                  "digest": "sha256:c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0",
                  "size": 452 },
      "layers": [
        { "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
          "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 10 },
        { "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip",
          "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "size": 20 }
      ]
    }
    """;

    [Fact]
    public void SelectPlatformDigest_PicksMatchingArch()
    {
        var index = Manifests.ParseIndex(IndexJson);
        Assert.Equal(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            Manifests.SelectPlatformDigest(index, "linux", "amd64"));
        Assert.Equal(
            "sha256:2222222222222222222222222222222222222222222222222222222222222222",
            Manifests.SelectPlatformDigest(index, "linux", "arm64"));
    }

    [Fact]
    public void SelectPlatformDigest_SkipsUnknownAttestation()
    {
        var index = Manifests.ParseIndex(IndexJson);
        // The "unknown" entry is never selected, even when its arch is asked for.
        Assert.Null(Manifests.SelectPlatformDigest(index, "unknown", "unknown"));
    }

    [Fact]
    public void SelectPlatformDigest_ReturnsNullWhenPlatformAbsent()
    {
        var index = Manifests.ParseIndex(IndexJson);
        Assert.Null(Manifests.SelectPlatformDigest(index, "linux", "riscv64"));
    }

    [Fact]
    public void ReadMediaType_IdentifiesIndex()
    {
        Assert.True(Manifests.IsIndexMediaType(Manifests.ReadMediaType(IndexJson)));
        Assert.False(Manifests.IsIndexMediaType(Manifests.ReadMediaType(ManifestJson)));
    }

    [Fact]
    public void ParseManifest_ExtractsConfigAndOrderedLayers()
    {
        var manifest = Manifests.ParseManifest(ManifestJson);
        Assert.Equal(452, manifest.Config.Size);
        Assert.Equal(2, manifest.Layers.Count);
        Assert.EndsWith("aaaa", manifest.Layers[0].Digest);
        Assert.EndsWith("bbbb", manifest.Layers[1].Digest);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public void Parse_RejectsMalformed(string json)
    {
        Assert.ThrowsAny<Exception>(() => Manifests.ParseManifest(json));
    }
}
