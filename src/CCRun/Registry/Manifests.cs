using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ccrun;

// --- Registry V2 / OCI DTOs ------------------------------------------------
// Only the fields ccrun needs are modeled; unknown JSON is ignored. Records are
// wired to the source-generated context below so serialization stays
// trim-friendly (InvariantGlobalization is on).

/// <summary>A content-addressable object reference (config or layer).</summary>
internal sealed record Descriptor(
    [property: JsonPropertyName("mediaType")] string? MediaType,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("size")] long Size);

internal sealed record ManifestPlatform(
    [property: JsonPropertyName("os")] string? Os,
    [property: JsonPropertyName("architecture")] string? Architecture);

/// <summary>One child entry in a multi-arch index / manifest list.</summary>
internal sealed record IndexEntry(
    [property: JsonPropertyName("mediaType")] string? MediaType,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("platform")] ManifestPlatform? Platform);

/// <summary>An OCI image index / Docker manifest list (the multi-arch case).</summary>
internal sealed record ImageIndex(
    [property: JsonPropertyName("mediaType")] string? MediaType,
    [property: JsonPropertyName("manifests")] IReadOnlyList<IndexEntry>? Manifests);

/// <summary>A single-platform image manifest: its config blob plus ordered layers.</summary>
internal sealed record ImageManifest(
    [property: JsonPropertyName("mediaType")] string? MediaType,
    [property: JsonPropertyName("config")] Descriptor Config,
    [property: JsonPropertyName("layers")] IReadOnlyList<Descriptor> Layers);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ImageIndex))]
[JsonSerializable(typeof(ImageManifest))]
[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;

/// <summary>Docker Hub anonymous-token response. Docker Hub returns
/// <c>token</c>; some registries use <c>access_token</c>.</summary>
internal sealed record TokenResponse(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("access_token")] string? AccessToken)
{
    public string? Value => Token ?? AccessToken;
}

internal static class Manifests
{
    public const string OciIndexMediaType = "application/vnd.oci.image.index.v1+json";
    public const string DockerListMediaType = "application/vnd.docker.distribution.manifest.list.v2+json";
    public const string OciManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    public const string DockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";

    public const string HostOs = "linux";

    /// <summary>The Accept types sent when requesting a manifest, so the
    /// registry may answer with any of index/list/manifest it prefers.</summary>
    public static readonly string[] AcceptTypes =
    [
        OciIndexMediaType, DockerListMediaType, OciManifestMediaType, DockerManifestMediaType,
    ];

    /// <summary>Docker's arch name for the running process.</summary>
    public static string HostArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var other => other.ToString().ToLowerInvariant(),
    };

    public static bool IsIndexMediaType(string? mediaType) =>
        mediaType is OciIndexMediaType or DockerListMediaType;

    /// <summary>
    /// Picks the child manifest digest matching <paramref name="os"/>/<paramref name="arch"/>
    /// from an index, skipping attestation entries (<c>os</c>/<c>architecture</c> of
    /// <c>unknown</c>). Returns null when the platform is absent. Pure — the
    /// unit-tested seam of the multi-arch logic.
    /// </summary>
    public static string? SelectPlatformDigest(ImageIndex index, string os, string arch)
    {
        if (index.Manifests is null)
            return null;
        foreach (var entry in index.Manifests)
        {
            var platform = entry.Platform;
            if (platform is null)
                continue;
            if (IsUnknown(platform.Os) || IsUnknown(platform.Architecture))
                continue;
            if (string.Equals(platform.Os, os, StringComparison.Ordinal) &&
                string.Equals(platform.Architecture, arch, StringComparison.Ordinal))
                return entry.Digest;
        }
        return null;

        static bool IsUnknown(string? value) =>
            string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a manifest body's own <c>mediaType</c> field (the reliable
    /// signal for index-vs-manifest; the HTTP Content-Type can be generic).</summary>
    public static string? ReadMediaType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("mediaType", out var value) ? value.GetString() : null;
    }

    public static ImageIndex ParseIndex(string json) =>
        JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ImageIndex)
        ?? throw new InvalidDataException("empty image index");

    public static ImageManifest ParseManifest(string json) =>
        JsonSerializer.Deserialize(json, ManifestJsonContext.Default.ImageManifest)
        ?? throw new InvalidDataException("empty image manifest");
}
