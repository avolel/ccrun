// The suite exercises the Linux-only CCRun assembly (namespaces, cgroups, the
// image extractor's SetUnixFileMode). Declaring the test assembly Linux-only too
// keeps CA1416 quiet on those call sites; the tests already skip or no-op where
// the underlying kernel feature is unavailable.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
