using System.Net;
using System.Security.Cryptography;
using System.Text;
using ccrun;

namespace CCRun.Tests;

// The registry flow against a fake transport — no network. Asserts
// token -> index -> child-manifest -> blob, the Accept headers, arch selection,
// and digest rejection.
public class RegistryClientTests
{
    private static readonly ImageReference Ubuntu =
        Parse("ubuntu"); // library/ubuntu:latest @ registry-1.docker.io

    private static readonly byte[] LayerBytes = Encoding.UTF8.GetBytes("fake-layer-payload");
    private static readonly string LayerDigest = Sha256Of(LayerBytes);
    private const string ChildDigest =
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task GetTokenAsync_ParsesToken()
    {
        var (client, _) = Build();
        Assert.Equal("TESTTOKEN", await client.GetTokenAsync("library/ubuntu"));
    }

    [Fact]
    public async Task GetManifestAsync_FollowsIndexToHostChild_WithAcceptHeaders()
    {
        var (client, handler) = Build();

        var manifest = await client.GetManifestAsync(Ubuntu, "TESTTOKEN");

        Assert.Single(manifest.Layers);
        Assert.Equal(LayerDigest, manifest.Layers[0].Digest);

        // It re-requested the arch-selected child manifest by digest.
        Assert.Contains(handler.RequestedUris, u => u.Contains($"/manifests/{ChildDigest}"));

        // Every manifest request advertised all four media types.
        var manifestReq = handler.Requests.First(r => r.RequestUri!.AbsoluteUri.Contains("/manifests/"));
        foreach (var mt in Manifests.AcceptTypes)
            Assert.Contains(manifestReq.Headers.Accept, a => a.MediaType == mt);
    }

    [Fact]
    public async Task DownloadBlobAsync_WritesVerifiedBytes()
    {
        var (client, _) = Build();
        using var dest = new MemoryStream();

        await client.DownloadBlobAsync(Ubuntu, LayerDigest, dest, "TESTTOKEN");

        Assert.Equal(LayerBytes, dest.ToArray());
    }

    [Fact]
    public async Task DownloadBlobAsync_RejectsDigestMismatch()
    {
        var (client, _) = Build();
        using var dest = new MemoryStream();
        var wrongDigest = "sha256:" + new string('0', 64);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.DownloadBlobAsync(Ubuntu, wrongDigest, dest, "TESTTOKEN"));
    }

    // --- fake transport -----------------------------------------------------

    private static (RegistryClient, FakeHandler) Build()
    {
        var arch = Manifests.HostArchitecture;
        var indexJson = $$"""
        { "mediaType": "{{Manifests.OciIndexMediaType}}", "manifests": [
          { "digest": "sha256:{{new string('a', 64)}}", "size": 1,
            "platform": { "os": "linux", "architecture": "unknown" } },
          { "digest": "{{ChildDigest}}", "size": 2,
            "platform": { "os": "linux", "architecture": "{{arch}}" } } ] }
        """;
        var manifestJson = $$"""
        { "mediaType": "{{Manifests.OciManifestMediaType}}",
          "config": { "digest": "sha256:{{new string('e', 64)}}", "size": 3 },
          "layers": [ { "digest": "{{LayerDigest}}", "size": {{LayerBytes.Length}} } ] }
        """;

        var handler = new FakeHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.StartsWith("https://auth.docker.io/token"))
                return Json("""{ "token": "TESTTOKEN" }""");
            if (uri.Contains($"/manifests/{ChildDigest}"))
                return Json(manifestJson);
            if (uri.Contains("/manifests/latest"))
                return Json(indexJson);
            if (uri.Contains("/blobs/"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LayerBytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        return (new RegistryClient(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string Sha256Of(byte[] data) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ImageReference Parse(string text)
    {
        Assert.True(ImageReference.TryParse(text, out var image));
        return image!;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public IEnumerable<string> RequestedUris => Requests.Select(r => r.RequestUri!.AbsoluteUri);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
