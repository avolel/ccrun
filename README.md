# CCRun

A lightweight Linux container runtime written in C# / .NET 10 — a "Build Your
Own Docker" learning project. It is developed in 8 phases; this repository is
currently at **Phase 5 (rootless containers)**. `ccrun run <command>` puts the
command in a new user namespace and UTS namespace — so it gets its own hostname
and runs as root *inside* the container without you being root outside — runs it,
and propagates its exit code.

How much isolation you get depends on `--rootfs`:

- **With `--rootfs <path>`** the command gets the full stack: user + UTS + mount +
  PID namespaces, a `chroot` into that root filesystem (then `chdir("/")`) so it
  sees it as `/` and cannot escape above it, and a private `/proc` so `ps` reports
  only container processes. The command runs as PID 1 of its own namespace.
- **Without `--rootfs`** it stays at Phase 2 behaviour plus the user namespace:
  hostname isolation and rootless operation, nothing more.

A *namespace* is the kernel feature that gives a process its own private copy of
some global resource — its own hostname (UTS), its own process-ID numbering (PID),
its own mount table (mount), its own user/group ID numbering (user). A
*capability* is one slice of root's power, e.g. `CAP_SYS_ADMIN` (mount, create
namespaces) or `CAP_SYS_CHROOT` (call `chroot`).

Ordinary users have neither capability — which is why `run` used to need `sudo`.
Phase 5 removes that: creating a **user namespace** is the one namespace operation
the kernel allows anybody to do, and inside the new namespace you hold the full
capability set. ccrun creates that namespace first and does everything else with
the capabilities it grants, so **no sudo is required**. Your UID is mapped to 0
inside the container, so a process there believes it is root, while the host still
sees it as owned by you.

Still missing: resource limits (cgroups), and image handling (`pull`, registry
client). `pivot_root` is deferred; Phase 4 kept the plain `chroot`.

**New to the code?** [docs/code-overview/code-overview.md](docs/code-overview/code-overview.md) is a detailed,
educational walkthrough of how the runtime works: the two-stage re-exec
architecture, a full trace of a `run`, the chroot mechanics, the PID/mount
namespaces and private `/proc`, why the chroot path hands off with `execvp`, the
libc P/Invoke layer, and how it is tested.

## Prerequisites

- **.NET 10 SDK** (developed against 10.0.109)
- **Linux** host, kernel **5.3 or newer** (ccrun creates its namespaces with the
  `clone3` syscall, added in 5.3)
- **cgroup v2** (unified hierarchy) — required for the resource limits in Phase 6
- **Unprivileged user namespaces enabled** — this is what lets ccrun run without
  sudo. Most distributions ship them on. To check:

  ```sh
  sysctl kernel.unprivileged_userns_clone   # Debian/Ubuntu: want 1, not 0
  cat /proc/sys/user/max_user_namespaces    # want a number well above 0
  ```

  If the first is `0`, enable it with
  `sudo sysctl -w kernel.unprivileged_userns_clone=1` (add it to
  `/etc/sysctl.d/` to make it survive a reboot). Running ccrun under `sudo` also
  works and needs neither knob.

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
    Commands/          RunCommand (parent stage) + ChildCommand (__child stage: sethostname, chroot, mount /proc, execvp)
    Native/            libc P/Invoke (clone3, sethostname, chroot, chdir, mount, execve/execvp, pipe, waitpid, geteuid/getegid)
    Container/         ReExec (clone into namespaces, write the UID/GID maps, launch the child) + ProcessRunner (spawn command)
  tests/CCRun.Tests/   xUnit test project
  alpine-rootfs/       downloaded Alpine root FS (git-ignored), used by --rootfs
```

## Build & test

```sh
dotnet build          # compile the solution
dotnet test           # run the xUnit tests — all of them, no sudo needed
dotnet run --project src/CCRun               # no args → prints usage, exits 1
dotnet run --project src/CCRun -- --help     # show usage
```

Since Phase 5 none of this needs sudo. Build first and run the produced binary
directly — `dotnet run` would work too, but building separately keeps the build
output from mixing into the container's:

```sh
dotnet build
BIN=src/CCRun/bin/Debug/net10.0/CCRun
"$BIN" run /bin/sh -c hostname                 # prints: container
"$BIN" run --hostname web /bin/sh -c hostname  # prints: web
"$BIN" run /bin/sh -c 'id -u'                  # prints: 0 — root inside the container
"$BIN" run --rootfs /no/such/dir true          # bad rootfs → "does not exist", exits 125

# --rootfs chroots into the Alpine tree, then runs its in-tree busybox:
"$BIN" run --rootfs alpine-rootfs /bin/busybox sh -c 'cat /etc/alpine-release'
"$BIN" run --rootfs alpine-rootfs /bin/busybox sh   # interactive shell inside the rootfs

# --rootfs also brings PID + mount namespaces and a private /proc:
"$BIN" run --rootfs alpine-rootfs /bin/busybox ps          # only container processes, not the host's
"$BIN" run --rootfs alpine-rootfs /bin/busybox sh -c 'echo $$'   # prints: 1
```

To see rootless working from the outside, start something long-running and look at
it from the host. `-o pid,user,cmd` tells `ps` which columns to print, and `-C
sleep` selects processes whose command is `sleep`:

```sh
"$BIN" run /bin/sleep 500 &
ps -o pid,user,cmd -C sleep    # USER is your login name, not root
kill %1
```

The command believes it is root (`id -u` prints 0) but the host reports it as
owned by you. That is the UID map at work: container UID 0 is your UID.

Running under `sudo` still works, and is the fallback on kernels with
unprivileged user namespaces disabled. There, container root maps to *real* root,
so the `ps` check above would show `root`.

The container's `/proc` mount lives in its own mount namespace, so it never
appears on the host (`mount | grep alpine-rootfs/proc` finds nothing while a
container runs) and the kernel tears it down automatically when the container
exits — there is no cleanup step to forget.

`--rootfs` is resolved against your **current directory**, so run these from the
repo root or pass an absolute path.

Earlier phases needed the `unshare --user --map-root-user` wrapper (a util-linux
command that creates a user namespace and maps you to root in it) to avoid sudo.
ccrun now does that for itself, so the wrapper is no longer needed anywhere.

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

`--rootfs` needs a root filesystem to chroot into; the tests use an Alpine one. It
is **not** committed (git-ignored). To (re)create it:

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
- **Phase 4 — process isolation (PID + mount namespaces, private `/proc`)** ✅ done
- **Phase 5 — rootless containers (user namespace + UID/GID mapping)** ✅ done
- Phases 6–8 (cgroup resource limits, image pull, registry client) are
  forthcoming. `pivot_root` remains deferred.
