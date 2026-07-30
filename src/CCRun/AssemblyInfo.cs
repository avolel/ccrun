// CCRun targets Linux + cgroup v2 only (see CLAUDE.md). Declaring the whole
// assembly Linux-only lets it call BCL APIs annotated [SupportedOSPlatform("linux")]
// — e.g. File.SetUnixFileMode in the image extractor — without CA1416 warnings
// rippling out to every caller.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
