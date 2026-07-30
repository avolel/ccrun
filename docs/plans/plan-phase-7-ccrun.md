# Phase 7 — Pull an Image from Docker Hub

## Context

CCRun is at Phase 6: `ccrun run --rootfs <path>` gives a command the full
namespace + chroot + cgroup stack, but **you still have to supply the rootfs by
hand** (today: the git-ignored `alpine-rootfs/`). There is no networking, HTTP,
JSON, tar, or compression code anywhere in the repo yet — Phase 7 is greenfield.

Phase 7 delivers the image client (**FR-7.1–7.8**): a new `ccrun pull <image>`
verb that authenticates anonymously against Docker Hub, fetches and parses the
image manifest via the Docker Registry HTTP API V2, downloads each layer with its
SHA-256 digest verified, extracts the gzipped-tar layers in order into a local
image store, and stores the image config alongside.

The deliverable is verifiable **today**, without waiting for Phase 8: after a
`pull`, the produced rootfs is a normal directory, so
`ccrun run --rootfs ~/.ccrun/images/library/ubuntu/latest/rootfs /bin/bash`
runs the pulled image. Running an image *by name* (`ccrun run ubuntu …`) and
applying its config env/workdir is Phase 8 and explicitly **out of scope here**.

### Scope decisions (confirmed with the user)

1. **Multi-arch OCI index handling is mandatory.** Modern Docker Hub `library/*`
   images (ubuntu, alpine) return an OCI **image index** / Docker **manifest
   list**, not a bare manifest. `pull` must fetch the index, skip attestation
   entries (`platform.architecture == "unknown"`), select the `linux/<host-arch>`
   child, then fetch *that* manifest by digest. Without this, a real pull just
   fails — so it is in the plan regardless.
2. **Overlay whiteouts are honored.** The extractor implements `.wh.<name>`
   deletions and `.wh..wh..opq` opaque-dir markers, so a multi-layer image
   reconstructs correctly (not just single-layer alpine).
3. **ubuntu is the canonical demo/acceptance target** (the BRD's named example;
   multi-layer, exercises ordering + whiteouts). alpine still works and is fine
   for a quick smoke test.
4. **Tests stay fully offline/hermetic.** No test hits Docker Hub. The registry
   flow is tested against a fake `HttpMessageHandler`; extraction against
   in-memory tars. A real end-to-end pull is a documented **manual** verification
   step (§Verification), *not* a `[SkippableFact]`.

Out of scope: `pull` by tag other than what's parsed (tags work; the BRD's tag
*selection* stretch goal is just normal `:tag` parsing), image *building*/pushing,
and any run-by-name wiring (Phase 8).

## How it fits the existing architecture

Phase 7 touches **none** of the namespace / clone3 / cgroup runtime. It is a
self-contained subsystem that writes a directory tree; the existing
`run --rootfs` consumes it unchanged. The only edits to existing files are the
CLI verb dispatch and usage text.

It mirrors the established command conventions exactly (confirmed against
`Cli.cs`, `RunCommand.cs`, `RunOptions.cs`, `ResourceLimits.cs`):

- Commands are `static class …Command` with
  `int Execute(string[] args, TextWriter stdout, TextWriter stderr)`; **progress
  goes to the injected `stdout`, errors to `stderr`** prefixed `ccrun pull:` /
  `ccrun:`. No `Console.*` statics.
- Parsing is a `sealed record …Options` + `static …Options? Parse(args, stderr)`
  returning `null` on bad input (caller maps to `ExitCodes.UsageError`), with a
  `Try*` pure validator for the interesting value (here, the image reference).
- Types are `internal` and reachable from tests via the existing
  `<InternalsVisibleTo Include="CCRun.Tests" />`.

All of Phase 7's dependencies are in the .NET 10 BCL — `HttpClient`,
`System.Text.Json`, `System.Security.Cryptography.SHA256`,
`System.IO.Compression.GZipStream`, `System.Formats.Tar` — so **no new NuGet
packages and no csproj changes** (verify `System.Formats.Tar.TarWriter`/`TarReader`
compile on `net10.0` at first build; it has shipped in-box since .NET 7).

## Files to add

New folder `src/CCRun/Registry/`. One responsibility per file.

### `src/CCRun/Registry/ImageReference.cs` (new)
Pure parsing, no I/O. `internal sealed record ImageReference(string Registry,
string Repository, string Tag, string? Digest)` with
`static bool TryParse(string text, out ImageReference? reference)`.
- Bare name `ubuntu` → repo `library/ubuntu`, tag `latest`, registry
  `registry-1.docker.io`.
- `ubuntu:22.04`, `library/ubuntu`, `ubuntu@sha256:…` all normalize.
- `RepositoryPath` helper for URL building. Mirror `ResourceLimits.TryParse*`
  discipline: reject empty, bad chars, double colons, malformed digest.

### `src/CCRun/Registry/Manifests.cs` (new)
`System.Text.Json` DTOs + a source-generated `JsonSerializerContext` (trim-friendly;
`InvariantGlobalization` is on). Records for the index entry
(`{digest, mediaType, platform:{os,architecture}}`), the image manifest
(`{config:{digest,mediaType,size}, layers:[{digest,mediaType,size}]}`), and the
config blob (only what Phase 8 will need later — parse-and-store now).
- `static string? SelectPlatformDigest(index, string os, string arch)` — the
  pure arch-selection function (skips `architecture == "unknown"`). This is the
  unit-tested seam.
- Host arch via `RuntimeInformation.OSArchitecture` mapped X64→`amd64`,
  Arm64→`arm64`.

### `src/CCRun/Registry/Digest.cs` (new)
`internal static bool Verify(string expected, ReadOnlySpan<byte> data)` and a
streaming variant using `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)` so
blobs are hashed **while** streaming to disk (NFR-5: no whole-blob buffering).
Compares the `sha256:<hex>` form, case-insensitive.

### `src/CCRun/Registry/RegistryClient.cs` (new)
The network seam. `internal sealed class RegistryClient(HttpClient http)`:
- `Task<string> GetTokenAsync(repo)` → GET
  `https://auth.docker.io/token?service=registry.docker.io&scope=repository:<repo>:pull`,
  parse `{"token":…}`.
- `Task<ImageManifest> GetManifestAsync(repo, reference, token)` → GET
  `…/v2/<repo>/manifests/<ref>` with the four `Accept` types (OCI index + Docker
  list + OCI manifest + Docker schema2). If the response `mediaType` is an
  index/list, run `SelectPlatformDigest` and re-GET by digest.
- `Task DownloadBlobAsync(repo, digest, Stream dest, token)` → GET
  `…/v2/<repo>/blobs/<digest>`, streaming into `dest` while hashing, verifying at
  end (throws/returns false on mismatch — NFR-6). Tests inject a fake
  `HttpMessageHandler`; production uses one shared `HttpClient`.

### `src/CCRun/Registry/TarExtractor.cs` (new — the security-sensitive part)
`internal static void ExtractLayer(Stream gzippedTar, string rootfsDir)`:
`GZipStream` → `TarReader`, per entry:
- **Path-traversal guard (NFR-6):** resolve `Path.GetFullPath(Combine(rootfs,
  entry.Name))` and reject anything not under the canonicalized `rootfs` prefix —
  the same `Path.GetFullPath`-then-validate approach `RunCommand.cs:28` uses.
  Also validate hardlink targets resolve inside rootfs.
- **Whiteouts:** a basename of `.wh..wh..opq` clears the *existing* (lower-layer)
  contents of its parent dir; `.wh.<name>` deletes `<name>`; neither file is
  itself written. `.wh.` entries target lower layers already on disk, so they are
  applied as encountered.
- Otherwise extract dir/file/symlink/hardlink, preserving mode where practical.

### `src/CCRun/Registry/ImageStore.cs` (new)
Resolves and owns the on-disk layout under `~/.ccrun/images/`
(`Environment.GetFolderPath(UserProfile)`):
`<repository>/<tag>/rootfs` + `<repository>/<tag>/config.json`. Clears any stale
`rootfs` before a re-pull, drives `TarExtractor.ExtractLayer` over the layers **in
manifest order**, and writes the config blob.

### `src/CCRun/PullOptions.cs` (new)
`sealed record PullOptions(ImageReference Image)` + `static PullOptions?
Parse(args, stderr)`. Positional image ref only (arch override unnecessary —
selection is unit-tested directly). Own `Usage` const, same error style as
`RunOptions`.

### `src/CCRun/Commands/PullCommand.cs` (new)
Thin orchestration: `Parse` → build `HttpClient`/`RegistryClient`/`ImageStore` →
token → manifest (index→child) → for each layer: download+verify (progress to
`stdout`) → extract → store config → print the resulting rootfs path. Registry /
network / extraction failures → `stderr` + `ExitCodes.RuntimeError`; bad args →
`ExitCodes.UsageError`.

## Files to change

- **`src/CCRun/Cli.cs`** — add `case "pull": return
  PullCommand.Execute(args.AsSpan(1).ToArray(), stdout, stderr);` beside the
  `run` case, and a `ccrun pull <image>` line in `PrintUsage`.
- **`src/CCRun/ExitCodes.cs`** — reuse `RuntimeError = 125` for pull failures (no
  new code needed; a named `PullError` is optional and not proposed).
- **`README.md` / `CLAUDE.md` / `docs/code-overview/code-overview.md`** — document
  the `pull` verb, the image-store layout, and the registry flow; update the
  "still missing: image handling" notes to reflect Phase 7 landing.

## Tests (all offline, `tests/CCRun.Tests/`)

Mirror `ResourceLimitsTests` (table-driven) and `CliTests` (StringWriter helper):

- **`ImageReferenceTests`** — accept `[Theory]` asserting normalized
  registry/repo/tag/digest per case; reject `[Theory]` for empties/bad chars.
- **`ManifestParsingTests`** — feed fixture JSON string literals for an OCI index
  (including an `architecture:"unknown"` attestation entry that must be skipped),
  a Docker manifest list, and a single schema2 manifest; assert
  `SelectPlatformDigest` and the layer/config extraction; plus malformed rejects.
- **`DigestTests`** — known tiny byte arrays → known SHA-256, plus a tamper case
  that must fail.
- **`TarExtractorTests`** (highest value) — build gzipped tars in memory with
  `TarWriter` into a temp dir under the session scratchpad: assert `../escape`,
  absolute-path, and escaping-hardlink entries are rejected; assert a `.wh.`
  entry deletes a lower-layer file and `.wh..wh..opq` empties a dir. Plain
  `[Fact]`/`[Theory]`, no privileges.
- **`RegistryClientTests`** — fake `HttpMessageHandler` returns canned
  token/index/manifest/blob; assert the token→index→child-manifest→blob flow,
  the `Accept` headers, arch selection, and that a blob whose bytes don't match
  its digest is rejected.
- **`PullOptionsTests`** + a `pull` dispatch case in **`CliTests`** — arg parse
  and wiring.

No `[SkippableFact]` here — the whole Phase 7 suite runs green offline.

## Verification

```sh
dotnet build && dotnet test           # all Phase 7 unit tests, hermetic/offline

# End-to-end (manual; needs Docker Hub reachable) — FR-7 acceptance:
BIN=src/CCRun/bin/Debug/net10.0/CCRun
$BIN pull ubuntu                                        # token→index→layers→extract
ls ~/.ccrun/images/library/ubuntu/latest/rootfs        # a full rootfs
cat ~/.ccrun/images/library/ubuntu/latest/config.json  # stored image config

# Prove the pulled rootfs runs under the existing Phase 6 stack (no Phase 8 needed):
$BIN run --rootfs ~/.ccrun/images/library/ubuntu/latest/rootfs /bin/bash -c 'cat /etc/os-release'
# Digest-tamper path: a corrupted layer must abort the pull with a clear error (unit-tested).
```

Acceptance (BRD §10.7): `pull ubuntu` succeeds with no Docker CLI installed,
every layer digest verified, a complete rootfs unpacked — and it runs.
