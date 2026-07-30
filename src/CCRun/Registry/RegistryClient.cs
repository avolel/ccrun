using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ccrun;

/// <summary>
/// Docker Registry HTTP API V2 client for anonymous pulls from Docker Hub. The
/// network seam: production wires it to one shared <see cref="HttpClient"/>;
/// tests inject a fake <c>HttpMessageHandler</c>.
///
/// Flow: anonymous bearer token → manifest (following a multi-arch index to the
/// host-platform child) → each blob streamed to a destination while hashed and
/// verified against its digest.
/// </summary>
internal sealed class RegistryClient(HttpClient http)
{
    // Docker Hub's token endpoint is fixed regardless of the image's registry host.
    private const string TokenEndpoint =
        "https://auth.docker.io/token?service=registry.docker.io&scope=repository:{0}:pull";

    public async Task<string> GetTokenAsync(string repository, CancellationToken ct = default)
    {
        var url = string.Format(TokenEndpoint, repository);
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var token = JsonSerializer.Deserialize(json, ManifestJsonContext.Default.TokenResponse);
        return token?.Value ?? throw new InvalidDataException("registry auth returned no token");
    }

    /// <summary>
    /// Fetches the image's single-platform manifest. If the registry answers with
    /// a multi-arch index/list, selects the host-platform child and re-fetches it
    /// by digest.
    /// </summary>
    public async Task<ImageManifest> GetManifestAsync(
        ImageReference image, string token, CancellationToken ct = default)
    {
        var (mediaType, body) = await FetchManifestAsync(image, image.ManifestReference, token, ct);

        if (Manifests.IsIndexMediaType(mediaType))
        {
            var index = Manifests.ParseIndex(body);
            var childDigest = Manifests.SelectPlatformDigest(index, Manifests.HostOs, Manifests.HostArchitecture)
                ?? throw new InvalidDataException(
                    $"image index has no {Manifests.HostOs}/{Manifests.HostArchitecture} manifest");
            (mediaType, body) = await FetchManifestAsync(image, childDigest, token, ct);
        }

        return Manifests.ParseManifest(body);
    }

    private async Task<(string? MediaType, string Body)> FetchManifestAsync(
        ImageReference image, string reference, string token, CancellationToken ct)
    {
        var url = $"https://{image.Registry}/v2/{image.Repository}/manifests/{reference}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var accept in Manifests.AcceptTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        // The body's own mediaType is authoritative; fall back to Content-Type.
        return (Manifests.ReadMediaType(body) ?? response.Content.Headers.ContentType?.MediaType, body);
    }

    /// <summary>
    /// Streams a blob into <paramref name="destination"/> while hashing it, then
    /// verifies the SHA-256 against <paramref name="digest"/>. Throws
    /// <see cref="InvalidDataException"/> on mismatch (NFR-6). Never buffers the
    /// whole blob (NFR-5). The caller must not consume a partially written
    /// destination until this returns successfully.
    /// </summary>
    public async Task DownloadBlobAsync(
        ImageReference image, string digest, Stream destination, string token, CancellationToken ct = default)
    {
        var url = $"https://{image.Registry}/v2/{image.Repository}/blobs/{digest}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        await destination.FlushAsync(ct);

        if (!Digest.VerifyHash(digest, hasher))
            throw new InvalidDataException($"blob {digest} failed SHA-256 verification");
    }
}
