# CCRun

A lightweight Linux container runtime written in C# / .NET 10 — a "Build Your
Own Docker" learning project. It is developed in 8 phases; this repository is
currently at **Phase 0 (environment setup / scaffold)**. No container
functionality (`run`, `pull`, namespaces, cgroups, registry client) is
implemented yet.

## Prerequisites

- **.NET 10 SDK** (developed against 10.0.109)
- **Linux** host (uses Linux-specific syscalls in later phases)
- **cgroup v2** (unified hierarchy) — required from Phase 4 onward
- Unprivileged user namespaces enabled (`kernel.unprivileged_userns_clone = 1`)
  for the rootless work in Phase 5

## Layout

```
ccrun/
  CCRun.sln
  src/CCRun/            console app (net10.0)
  tests/CCRun.Tests/    xUnit test project
  alpine-rootfs/        downloaded Alpine root FS (git-ignored)
```

## Build & test

```sh
dotnet build          # compile the solution
dotnet test           # run the xUnit tests
dotnet run --project src/CCRun   # prints usage, exits non-zero (no commands yet)
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

Phases 1–8 (command parsing, P/Invoke, namespaces, chroot/pivot_root, cgroups,
rootless mode, image pull, registry client) are forthcoming.
