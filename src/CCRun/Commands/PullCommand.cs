using System.Text.Json;

namespace ccrun;

/// <summary>
/// `pull`: fetches an image from Docker Hub into the local image store. Thin
/// orchestration over <see cref="RegistryClient"/> (network),
/// <see cref="TarExtractor"/> (extraction) and <see cref="ImageStore"/>
/// (layout): token → manifest (following a multi-arch index) → each layer
/// downloaded, digest-verified and extracted in order → config stored.
///
/// Progress goes to stdout; registry/network/extraction failures map to
/// <see cref="ExitCodes.RuntimeError"/>, bad args to
/// <see cref="ExitCodes.UsageError"/>.
/// </summary>
public static class PullCommand
{
    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var options = PullOptions.Parse(args, stderr);
        if (options is null)
            return ExitCodes.UsageError;

        try
        {
            // The CLI is synchronous; block on the async pull at the top only.
            return ExecuteAsync(options, stdout).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidDataException or InvalidOperationException
               or JsonException or IOException)
        {
            stderr.WriteLine($"ccrun pull: {ex.Message}");
            return ExitCodes.RuntimeError;
        }
    }

    private static async Task<int> ExecuteAsync(PullOptions options, TextWriter stdout)
    {
        var image = options.Image;
        using var http = new HttpClient();
        var client = new RegistryClient(http);
        var store = new ImageStore(image);

        stdout.WriteLine($"Pulling {image.Repository}:{image.Tag} from {image.Registry}");

        var token = await client.GetTokenAsync(image.Repository);
        var manifest = await client.GetManifestAsync(image, token);

        store.ResetRootfs();
        for (int i = 0; i < manifest.Layers.Count; i++)
        {
            var layer = manifest.Layers[i];
            stdout.WriteLine($"  layer {i + 1}/{manifest.Layers.Count}  {ShortDigest(layer.Digest)}  ({layer.Size} bytes)");

            // Stream each layer to a temp file (verified on the way in), then
            // extract from that file. The temp file keeps NFR-5 (no whole-blob
            // buffering) while giving the extractor a seekable source.
            var temp = Path.GetTempFileName();
            try
            {
                await using (var file = File.Create(temp))
                    await client.DownloadBlobAsync(image, layer.Digest, file, token);
                await using var read = File.OpenRead(temp);
                store.ExtractLayer(read);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        using (var config = new MemoryStream())
        {
            await client.DownloadBlobAsync(image, manifest.Config.Digest, config, token);
            store.WriteConfig(config.ToArray());
        }

        stdout.WriteLine($"Pulled {image.Repository}:{image.Tag} -> {store.RootfsDir}");
        return ExitCodes.Ok;
    }

    private static string ShortDigest(string digest)
    {
        // "sha256:" + 12 hex is enough to read in progress output.
        const int shown = 7 + 12;
        return digest.Length > shown ? digest[..shown] : digest;
    }
}
