# CCRun

A lightweight Linux container runtime written in C# / .NET 10 — a "Build Your
Own Docker" learning project. It is developed in 8 phases; this repository is
currently at **Phase 3 (chroot filesystem isolation)**. `ccrun run <command>`
puts the command in a new UTS namespace so it gets its own hostname, runs it, and
propagates its exit code. With `--rootfs <path>` it also `chroot`s into that root
filesystem (then `chdir("/")`) so the command sees it as `/` and cannot escape
above it. Creating the namespace needs `CAP_SYS_ADMIN` and `chroot` needs
`CAP_SYS_CHROOT`, so `run` requires sudo for now. There is still no process
isolation (PID namespace), no private mount table (mount namespace, `pivot_root`,
private `/proc`), no resource limits (cgroups), and no image handling (`pull`,
registry client).

**New to the code?** [docs/code-overview/code-overview.md](docs/code-overview/code-overview.md) is a detailed,
educational walkthrough of how the runtime works: the two-stage re-exec
architecture, a full trace of a `run`, the chroot mechanics, why the chroot path
hands off with `execvp`, the libc P/Invoke layer, and how it is tested.

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
  docs/code-overview/  detailed educational walkthrough of how the runtime works
  src/CCRun/           console app (net10.0)
    Program.cs         entrypoint, delegates to Cli
    Cli.cs             verb dispatch + usage
    ExitCodes.cs       named exit codes
    RunOptions.cs      argument parsing for `run` (--hostname, --rootfs)
    Commands/          RunCommand (parent stage) + ChildCommand (__child stage: sethostname, chroot, execvp)
    Native/            libc P/Invoke (unshare, sethostname, chroot, chdir, execvp, geteuid)
    Container/         ReExec (re-launch as child) + ProcessRunner (spawn command)
  tests/CCRun.Tests/   xUnit test project
  alpine-rootfs/       downloaded Alpine root FS (git-ignored), used by --rootfs
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

# --rootfs chroots into the Alpine tree, then runs its in-tree busybox:
sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox sh -c 'cat /etc/alpine-release'
sudo "$BIN" run --rootfs alpine-rootfs /bin/busybox sh   # interactive shell inside the rootfs
"$BIN" run --rootfs /no/such/dir true               # bad rootfs → "does not exist", exits 125
```

Use **absolute** command paths with `--rootfs`: after `chroot`, a bare name is
resolved against `PATH` *inside* the new root, so `/bin/busybox` is reliable where
`busybox` may not be.

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
- **Phase 3 — filesystem isolation (`chroot` into a root FS via `--rootfs`)** ✅ done
- Phases 4–8 (PID/mount/user namespaces + `pivot_root`, cgroups, rootless mode,
  image pull, registry client) are forthcoming.
