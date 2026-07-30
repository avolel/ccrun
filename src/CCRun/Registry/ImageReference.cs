namespace ccrun;

/// <summary>
/// A parsed, normalized Docker/OCI image reference:
/// <c>[registry/]repository[:tag][@digest]</c>. Pure parsing, no I/O.
///
/// Docker Hub shorthands are expanded the way the Docker CLI expands them:
/// a bare <c>ubuntu</c> becomes registry <c>registry-1.docker.io</c>,
/// repository <c>library/ubuntu</c>, tag <c>latest</c>. The first path segment
/// is treated as a registry only when it looks like a host (contains a '.' or
/// ':', or is <c>localhost</c>) — otherwise <c>foo/bar</c> is a two-part
/// repository on Docker Hub.
/// </summary>
internal sealed record ImageReference(string Registry, string Repository, string Tag, string? Digest)
{
    public const string DefaultRegistry = "registry-1.docker.io";
    public const string DefaultTag = "latest";

    /// <summary>What addresses the manifest on the wire: a digest pins exactly,
    /// otherwise the tag.</summary>
    public string ManifestReference => Digest ?? Tag;

    public static bool TryParse(string text, out ImageReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var remaining = text.Trim();

        // 1. Peel off an optional @sha256:... digest suffix.
        string? digest = null;
        int at = remaining.IndexOf('@');
        if (at >= 0)
        {
            digest = remaining[(at + 1)..];
            remaining = remaining[..at];
            if (!IsValidDigest(digest))
                return false;
        }
        if (remaining.Length == 0)
            return false;

        // 2. Peel off an optional registry host (first segment that looks like one).
        string registry = DefaultRegistry;
        int slash = remaining.IndexOf('/');
        if (slash >= 0)
        {
            var first = remaining[..slash];
            if (first == "localhost" || first.Contains('.') || first.Contains(':'))
            {
                registry = first;
                remaining = remaining[(slash + 1)..];
            }
        }
        if (remaining.Length == 0)
            return false;

        // 3. Peel off an optional :tag on the final path segment (a ':' before
        //    the last '/' would belong to a registry port, already handled).
        string tag = DefaultTag;
        int lastSlash = remaining.LastIndexOf('/');
        int colon = remaining.IndexOf(':', lastSlash + 1);
        if (colon >= 0)
        {
            tag = remaining[(colon + 1)..];
            remaining = remaining[..colon];
            if (!IsValidTag(tag))
                return false;
        }

        // 4. What's left is the repository. Single-segment repos on Docker Hub
        //    live under library/.
        var repository = remaining;
        if (registry == DefaultRegistry && !repository.Contains('/'))
            repository = "library/" + repository;
        if (!IsValidRepository(repository))
            return false;

        reference = new ImageReference(registry, repository, tag, digest);
        return true;
    }

    private static bool IsValidDigest(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var hex = digest[prefix.Length..];
        if (hex.Length != 64)
            return false;
        foreach (var c in hex)
            if (!Uri.IsHexDigit(c))
                return false;
        return true;
    }

    private static bool IsValidTag(string tag)
    {
        if (tag.Length is 0 or > 128)
            return false;
        if (!(char.IsAsciiLetterOrDigit(tag[0]) || tag[0] == '_'))
            return false;
        foreach (var c in tag)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
                return false;
        return true;
    }

    private static bool IsValidRepository(string repository)
    {
        if (repository.Length == 0)
            return false;
        foreach (var component in repository.Split('/'))
        {
            // Empty component catches leading/trailing/double slash and a
            // leftover empty tag from a double colon.
            if (component.Length == 0)
                return false;
            foreach (var c in component)
                if (!(char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c) || c is '.' or '_' or '-'))
                    return false;
        }
        return true;
    }
}
