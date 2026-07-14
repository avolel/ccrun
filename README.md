# CCRun

A lightweight Linux container runtime written in C# / .NET 10 — a "Build Your
Own Docker" learning project. It is developed in 8 phases; this repository is
currently at **Phase 2 (hostname isolation)**. `ccrun run <command>` puts the
command in a new UTS namespace so it gets its own hostname, runs it, and
propagates its exit code. Creating that namespace needs root, so `run` requires
sudo for now. There is still no filesystem or process isolation (chroot,
PID/mount namespaces, cgroups) and no image handling (`pull`, registry client).

## Prerequisites

- **.NET 10 SDK** (developed against 10.0.109)
- **Linux** host (uses Linux-specific syscalls in later phases)
- **cgroup v2** (unified hierarchy) — required from Phase 4 onward
- Unprivileged user namespaces enabled (`kernel.unprivileged_userns_clone = 1`)
  for the rootless work in Phase 5

## Layout

```
ccrun/
  CCRun.slnx           .NET 10 XML solution file
  src/CCRun/           console app (net10.0)
    Program.cs         entrypoint, delegates to Cli
    Cli.cs             verb dispatch + usage
    ExitCodes.cs       named exit codes
    RunOptions.cs      argument parsing for `run`
    Commands/          RunCommand (parent stage) + ChildCommand (__child stage)
    Native/            libc P/Invoke (unshare, sethostname, geteuid)
    Container/         ReExec (re-launch as child) + ProcessRunner (spawn command)
  tests/CCRun.Tests/   xUnit test project
  alpine-rootfs/       downloaded Alpine root FS (git-ignored)
```

## Build & test

```sh
dotnet build          # compile the solution
dotnet test           # run the xUnit tests (namespace tests need root; they skip otherwise)
dotnet run --project src/CCRun               # no args → prints usage, exits 1
dotnet run --project src/CCRun -- --help     # show usage
```

`ccrun run` creates a UTS namespace, which needs root. Build first, then run the
produced binary under sudo — running `sudo dotnet run` would trigger a build as
root and clutter the output:

```sh
dotnet build
BIN=src/CCRun/bin/Debug/net10.0/CCRun
sudo "$BIN" run /bin/sh -c hostname                 # prints: container
sudo "$BIN" run --hostname web /bin/sh -c hostname  # prints: web
"$BIN" run true                                     # no sudo → prints the sudo hint, exits 125
```

### Self-contained publish (later phases / NFR-1)

The csproj is kept runtime-agnostic so it can target other architectures. To
produce a self-contained single-file binary:

```sh
dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true
```

Swap `linux-x64` for `linux-arm64` to cross-compile for arm64.

## Alpine root filesystem

Phase 3 uses an Alpine root FS for `chroot` testing. It is **not** committed
(git-ignored). To (re)create it:

```sh
mkdir -p alpine-rootfs
curl -fsSL -o /tmp/alpine.tar.gz \
  https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/x86_64/alpine-minirootfs-3.24.1-x86_64.tar.gz
tar -xzf /tmp/alpine.tar.gz -C alpine-rootfs
touch alpine-rootfs/ALPINE_FS_ROOT
```

Check the [CDN directory](https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/x86_64/)
for the current `alpine-minirootfs-<ver>-x86_64.tar.gz` filename if the version
above has moved on.

## Roadmap

- **Phase 1 — command parsing / `run`** ✅ done
- **Phase 2 — hostname isolation (UTS namespace) + re-exec architecture** ✅ done
- Phases 3–8 (chroot/pivot_root, more namespaces, cgroups, rootless mode,
  image pull, registry client) are forthcoming.
