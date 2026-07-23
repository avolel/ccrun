# CCRun code overview

This is a guided tour of the CCRun codebase for engineers who want to understand
how a container runtime actually works, not just how to use one. CCRun is a
teaching reimplementation of the core mechanics behind Docker, built up one
isolation primitive at a time. If you have ever wondered what `docker run`
really does when it "starts a container," this project takes the magic apart
into a handful of syscalls you can read in an afternoon.

The code is deliberately small. The interesting part is not the volume of code
but the ideas behind it, so this document spends most of its time on the *why*.

## Where the project stands

CCRun is planned as eight phases. Each phase adds one real piece of isolation.
As of this writing the runtime is through **Phase 6**, which means it can do five
things that matter:

1. **Hostname isolation** (Phase 2). A container gets its own hostname, and
   changing it does not touch the host's hostname. This is done with a UTS
   namespace.
2. **Filesystem isolation** (Phase 3). With `--rootfs`, a container `chroot`s
   into a root filesystem so the command inside sees a different `/` and cannot
   climb out of it.
3. **Process isolation** (Phase 4). With `--rootfs`, the container also gets a PID
   namespace, so its process tree starts fresh and the command runs as PID 1; a
   mount namespace, so its mounts are invisible to the host; and a private `/proc`,
   so `ps` inside shows only container processes.
4. **Rootless operation** (Phase 5). Every container gets a user namespace, in
   which the invoking user's ID is mapped to 0. The command believes it is root
   and holds the capabilities the steps above need, while the host still sees an
   ordinary unprivileged process. `ccrun` no longer needs `sudo`.
5. **Resource limits** (Phase 6). With `--memory` and/or `--cpus`, the container
   runs in a cgroup v2 directory of its own that caps how much memory and CPU time
   it may consume. Namespaces answer "what can it *see*"; cgroups answer "how much
   can it *take*", and a runtime needs both.

That third one is what makes the container start to *feel* like a container. Up
through Phase 3 you could chroot into Alpine and still see every process on the
machine, which gives away the illusion immediately.

There is a deliberate asymmetry to understand before reading further: **the Phase 4
stack is gated on `--rootfs`.** A rootfs run gets everything above; a bare
`ccrun run` gets the user and UTS namespaces and nothing else. The reason is
not laziness. The no-rootfs path hands off with managed `Process.Start`, and you do
not want the .NET runtime to become PID 1 of a PID namespace — PID 1 has special
kernel duties (notably reaping orphans, and immunity to default signal handling)
that a runtime never signed up for. Keeping the rule "`--rootfs` means a real
container, bare `run` means hostname only" makes that boundary obvious and mirrors
the chroot gate that already existed.

What is still missing: image handling. A real `docker run alpine` pulls an image
from a registry and unpacks it; CCRun still expects you to hand it a rootfs
directory. That is Phases 7 and 8, and the code is structured so they slot in
cleanly. More on that at the end.

Creating namespaces, mounting filesystems, and calling `chroot` all require Linux
*capabilities* — individual slices of root's authority, here `CAP_SYS_ADMIN` for
namespaces and mounts and `CAP_SYS_CHROOT` for the chroot — that ordinary users do
not have. Until Phase 5 that meant `ccrun run` needed real root. It no longer
does: the [user namespace section](#the-user-namespace-and-rootless-containers)
explains how creating one namespace grants the capabilities to create all the
others.

## The one idea that shapes everything: two stages

If you take away a single concept from this document, make it this one. CCRun
runs your command in two stages, in two separate processes, and almost every
design decision follows from that split.

The reason is a chicken-and-egg problem. To isolate a process you want to put it
inside fresh namespaces. But some of that setup is awkward or impossible to do to
*yourself* after the fact. The clean approach, and the one every real runtime
uses in some form, is:

1. A **parent (host) stage** creates a **child (init) stage** — a brand new
   process, born directly into fresh namespaces.
2. The child does the in-namespace setup (set the hostname, chroot, later mount
   `/proc` and so on) and finally hands control to the user's actual command.
3. The parent waits for it and reports its exit code.

Note "born into" rather than "moved into." Phase 5 made that distinction load-bearing;
the [user namespace section](#the-user-namespace-and-rootless-containers) explains why.

CCRun implements this by re-executing *itself*. The parent runs
`ccrun run ...` and launches `ccrun __child ...`,
where `__child` is a hidden verb that means "you are the init stage, finish the
setup." The same binary plays both roles depending on which verb it is invoked
with. You will see this hidden verb dispatched in [Cli.cs](../../src/CCRun/Cli.cs#L26)
and produced in [ReExec.cs](../../src/CCRun/Container/ReExec.cs#L276).

Keep this two-stage picture in mind and the rest of the code reads as a straight
line.

## Walking a `run` from the outside in

Let us trace one concrete invocation all the way through:

```sh
ccrun run --rootfs alpine-rootfs /bin/busybox sh
```

This asks CCRun to start an interactive BusyBox shell inside the Alpine root
filesystem, with its own hostname.

### The entrypoint and verb dispatch

[Program.cs](../../src/CCRun/Program.cs) is deliberately tiny. It is a top-level
statement that hands `args` straight to `Cli.Run`. Keeping the entrypoint this
thin means the real logic lives in a normal method that tests can call directly.

[Cli.Run](../../src/CCRun/Cli.cs#L10) looks at the first argument, the verb, and routes
it. `run` goes to `RunCommand`, the hidden `__child` goes to `ChildCommand`, and
`--help` or an unknown verb print usage. Notice the signature:

```csharp
public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
```

The output writers are parameters that default to `Console.Out` and
`Console.Error`. Nothing in the command logic writes to `Console` directly.
This is a small thing with a big payoff: a test can pass in a `StringWriter`,
run a command, and assert on exactly what it printed, without capturing global
console state. You will see the same pattern threaded through every command.

### Parsing the arguments

[RunOptions.Parse](../../src/CCRun/RunOptions.cs#L21) is a small hand-rolled argument
parser. There is no third-party parsing library here, partly to keep
dependencies at zero and partly because the grammar is simple enough that a loop
is clearer than a framework.

The grammar is: leading tokens that start with `--` are options, and the first
token that does not is the command. Everything after the command is passed
through untouched as the command's own arguments. So in our example, `--rootfs
alpine-rootfs` are options, `/bin/busybox` is the command, and `sh` is an
argument to BusyBox.

A couple of details are worth calling out because they are the kind of thing
that bites naive parsers:

- A bare `--` explicitly ends option parsing. This lets you run a command whose
  own arguments start with dashes.
- Because the loop stops at the first positional token, options that appear
  *after* the command are not treated as CCRun options. `ccrun run echo --rootfs
  /r` runs `echo` with the literal arguments `--rootfs /r`. That is the correct
  behavior: once you name the command, the rest belongs to it.
- Every option accepts both the `--opt value` and the `--opt=value` form, which is
  why the branches all route through one `TryTakeValue` helper rather than each
  spelling the two cases out.

One design choice matters for later: **the parser does no filesystem work.** It
does not check whether the rootfs exists, does not resolve paths, does not touch
the disk at all. It just turns strings into a `RunOptions` record. That keeps it
pure and trivially unit-testable, and it puts all the environment-dependent
validation in one place, the parent stage, which we get to next.

The result is an immutable `record`:

```csharp
public sealed record RunOptions(
    string Hostname,
    string? Rootfs,
    ResourceLimits Limits,
    string Command,
    IReadOnlyList<string> CommandArgs);
```

`Rootfs` is nullable, and null carries real meaning: it means "no chroot, behave
like Phase 2." That single nullable field is what makes filesystem isolation
opt-in and backward compatible.

[ResourceLimits](../../src/CCRun/ResourceLimits.cs) plays the same trick for Phase 6.
Both of its fields are nullable, and `Limits.Any` being false is what makes the
entire cgroup machinery skip itself, so a run with no limits behaves exactly as it
did before the phase existed. The one thing the parser *does* do here is convert
and validate: `--memory 512m` becomes a byte count and `--cpus 0.5` a double, and
a value that is not a positive size or number is a usage error reported before any
namespace exists. Unit conversion is pure string work, so it belongs with the
parser rather than in the code that talks to the kernel.

### The parent stage: creating the namespace

[RunCommand.Execute](../../src/CCRun/Commands/RunCommand.cs#L15) is the host stage. It
does three things in order, and the order is deliberate.

First it **validates the rootfs before touching any namespaces.** If `--rootfs`
was given, it resolves the path to an absolute one with `Path.GetFullPath` and
confirms the directory exists. If it does not, CCRun fails immediately with a
clear message and never creates a namespace:

```csharp
rootfs = Path.GetFullPath(options.Rootfs);
if (!Directory.Exists(rootfs))
{
    stderr.WriteLine($"ccrun: rootfs '{options.Rootfs}' does not exist or is not a directory");
    return ExitCodes.RuntimeError;
}
```

Two reasons for doing this here and now. Failing early keeps error handling
simple: a bad path is a plain "does not exist" message, not a confusing failure
halfway through container setup. And resolving to an absolute path is not
optional. The child process is going to `chroot` after inheriting a fresh working
directory, so a relative path like `alpine-rootfs` would be meaningless by the
time it is used. We pin it down to an absolute path in the parent, while the
current directory is still the one the user launched from.

Note that the validation is intentionally shallow. It checks that the path is an
existing directory, and nothing more. It does *not* check for the Alpine marker
file. That is on purpose: in Phase 8 the rootfs will come from a pulled image
that has no such marker, so baking in an Alpine-specific check would be a dead
end.

Second, it decides which namespaces the container gets:

```csharp
ulong flags = (ulong)Libc.CLONE_NEWUSER | (ulong)Libc.CLONE_NEWUTS;
if (rootfs is not null)
    flags |= (ulong)Libc.CLONE_NEWNS | (ulong)Libc.CLONE_NEWPID;
```

Four flags are in play, each naming one kind of namespace:

- `CLONE_NEWUSER` asks for a new **user namespace** — a private mapping between
  user and group IDs inside and outside it. This is the Phase 5 addition and the
  reason ccrun no longer needs sudo; the next section is devoted to it.
- `CLONE_NEWUTS` asks for a new **UTS namespace**. UTS stands for Unix
  Time-sharing System, a historical name; in practice it is the namespace that owns
  the hostname and domain name. Inside it the container has its own hostname slot,
  and changing it will not affect the host's.
- `CLONE_NEWNS` asks for a new **mount namespace** — a private mount table. (The
  `NS` is another historical artifact: mount namespaces were the first namespace
  Linux ever had, so they got the generic name.) This is what will keep the `/proc`
  we are about to mount from ever appearing on the host.
- `CLONE_NEWPID` asks for a new **PID namespace**: a fresh process-id number space
  starting at 1.

Third, it hands the flags and the options to `ReExec`, which does the actual work:

```csharp
return ReExec.RunChild(options with { Rootfs = rootfs }, flags, stderr);
```

The `with` expression makes a copy of the options record with the rootfs swapped
for its absolute form. The return value of `RunChild` is the container's exit code,
which becomes CCRun's exit code.

Note what `RunCommand` does *not* do: it never creates a namespace itself. That is
deliberate and it is the subject of the next section.

### The user namespace, and rootless containers

This is the piece that changed most in Phase 5, so it is worth taking slowly.

**The problem.** Everything CCRun does — creating namespaces, `chroot`, mounting
`/proc` — needs `CAP_SYS_ADMIN` or `CAP_SYS_CHROOT`. A normal user has neither.
Requiring `sudo` to run a container is a real limitation: it means any bug in the
runtime is a bug running as root.

**The escape hatch.** Linux allows *one* exception. Creating a **user namespace**
requires no privileges at all, and the process that creates one receives a full set
of capabilities *inside* it. Those capabilities are real but scoped: they let you
create other namespaces, mount things, and chroot within your own namespace, and
they give you no authority over anything outside it. So the trick behind every
rootless runtime is to create a user namespace first and then do everything else
with the capabilities it hands you.

**The ID map.** A new user namespace also needs to be told how IDs inside it
correspond to IDs outside. That is the *map*, written to
`/proc/<pid>/uid_map` and `/proc/<pid>/gid_map` as lines of

```
<first ID inside the namespace> <first ID outside it> <how many IDs>
```

CCRun writes `0 1000 1` (for a user whose UID is 1000): container ID 0 — root —
means host ID 1000, and exactly one ID is mapped. So a process in the container
reads its own UID as 0 and behaves as root, while the kernel accounts for
everything it does against UID 1000. Start a `sleep` in a container and `ps` on the
host shows it owned by you. That asymmetry *is* rootless containers.

Until a map is written, every ID in the namespace reads as the "overflow" ID
(65534, `nobody`), so writing it is not optional.

Two rules constrain how the map gets written, and both shape the code:

- Writing a map normally requires `CAP_SETUID` in the *parent* namespace, which we
  do not have. The exception we rely on is that a process may write a **single-ID**
  map when that ID is its own. Mapping yourself to yourself grants no authority you
  did not already have, so it is permitted unprivileged. (This is the same rule
  behind `unshare --map-root-user`.) It is also why CCRun needs no `newuidmap`
  helper — the setuid binary general-purpose runtimes use to map whole *ranges* of
  IDs from `/etc/subuid`.
- Before an unprivileged writer may write `gid_map`, it must write `deny` to
  `/proc/<pid>/setgroups`, permanently disabling `setgroups(2)` in that namespace.
  Otherwise a user could enter a namespace, drop a supplementary group, and reach a
  file that group was being used to deny them. The order matters: `deny` is
  rejected once `gid_map` has been written.

**The .NET-specific wrinkle.** Given the above you would expect CCRun to call
`unshare(CLONE_NEWUSER)` and write its own maps. It cannot. From
[unshare(2)](https://man7.org/linux/man-pages/man2/unshare.2.html):

> **EINVAL**: CLONE_NEWUSER was specified in flags, but the process is
> multithreaded.

The CLR is always multithreaded — the finalizer thread alone is enough — so a
managed process can *never* unshare a user namespace. The call fails with EINVAL
and there is no way to make it succeed. Go has the identical problem, which is why
runc's namespace setup lives in C code that runs before the Go runtime starts.

The way around it is to stop thinking of namespaces as something you *enter* and
start thinking of them as something a process can be *born into*. The
single-threaded restriction applies to `unshare`, which mutates an existing process.
It does not apply to creating a **new** process with the same flags, because a new
process starts with exactly one thread by definition. So CCRun creates all its
namespaces at process-creation time with `clone3`, and the parent never changes its
own namespaces at all.

A side benefit: `unshare(CLONE_NEWPID)` has a famous quirk — it does not move the
caller into the new PID namespace, it only arranges for the caller's *next* forked
child to be PID 1. Passing `CLONE_NEWPID` to `clone3` has no such indirection. The
cloned child simply *is* PID 1. And because that child later `execvp`s the user's
command in place, the user's command is PID 1 — which is what we want before
mounting a private `/proc`.

The old code also carried a `WarmUpProcessSubsystem` workaround, needed because a
process that has called `unshare(CLONE_NEWPID)` may never create a thread again
(`clone(2)` rejects `CLONE_THREAD` for it with EINVAL), which collided with .NET
creating threads lazily. With no `unshare` anywhere, that whole landmine is gone.

### The clone: launching ourselves as the child

[ReExec.RunChild](../../src/CCRun/Container/ReExec.cs#L63) creates the container.
It has four parts.

**One: staging everything the child will need.** `StageExecArguments` works out how
to re-invoke this very program, builds the argument and environment arrays, and
copies them into native memory. It runs *before* the clone because the cloned child
is not allowed to allocate — more on that below.

Re-invoking ourselves is genuinely awkward on .NET, because there are two ways
CCRun might be running:

- As a **published, self-contained binary** (an "apphost"), where
  `Environment.ProcessPath` points at the CCRun executable itself. In that case you
  re-run that path directly.
- Under the **`dotnet` muxer** during development (`dotnet run` or running the built
  DLL), where `Environment.ProcessPath` points at `dotnet`, not at CCRun. Running
  `dotnet __child ...` would tell the .NET launcher to look for a command called
  `__child`, which is nonsense. You have to run `dotnet path/to/CCRun.dll __child
  ...` instead.

```csharp
var argv = new List<string> { exe };
if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    argv.Add(dll);
argv.Add(ChildVerb);
```

Configuration reaches the child through the environment: `CCRUN_HOSTNAME` always,
`CCRUN_ROOTFS` only when a rootfs was given. The presence or absence of
`CCRUN_ROOTFS` is exactly how the child knows whether to chroot. Command-line
arguments carry the command; environment variables carry the container config —
the same split real runtimes use.

**Two: the clone itself.** `clone3` has no glibc wrapper, so CCRun calls it through
the raw `syscall(2)` escape hatch, passing a `struct clone_args` in which only two
fields are non-zero: the namespace flags, and `exit_signal = SIGCHLD` (which is
what makes the child waitable with `waitpid`). Leaving `stack` zero gives fork-like
behaviour — the child gets its own copy of our address space rather than sharing
it.

```csharp
long ret = Libc.Syscall(Libc.SYS_clone3, args, Libc.CLONE_ARGS_SIZE_VER0);
```

`clone3` is syscall number 435 on every architecture, unlike the older `clone`,
whose number *and argument order* vary between x86-64 and arm64. That is worth a
line of explanation because it is the reason CCRun targets `clone3` (Linux 5.3+)
rather than the more widely available `clone`: it keeps the arm64 cross-compilation
story trivial.

Like `fork`, `clone3` returns twice — the child pid in the parent, and 0 in the
child.

**Three: the handshake.** The child must not `execve` until its ID maps exist, and
it cannot write them itself. Why not is subtle: `execve` *clears* a process's
permitted capability set unless it is running as root or the binary carries file
capabilities. Right after the clone, the child is the unmapped overflow UID — not
root — so if it exec'd first it would land in the init stage with its
`CAP_SYS_ADMIN` stripped, and the chroot and mount would fail. Written maps make it
root-in-namespace, and a root-in-namespace `execve` keeps its capabilities.

So the two processes rendezvous over a pipe:

```
child:   read(pipe)  ──blocks──────────────┐
parent:  write /proc/<pid>/setgroups        │
         write /proc/<pid>/uid_map          │
         write /proc/<pid>/gid_map          │
         write(pipe) ───────────────────────┘ unblocks the child
child:   execve(ccrun __child ...)
```

The parent can write the child's maps because of the single-ID rule above: it is
mapping *its own* effective UID. The `finally` block closes the write end even when
the map writes fail, so an error surfaces as the child exiting rather than as a
hang.

**Four: reaping.** `waitpid` blocks until the container finishes and yields a *wait
status*, which is not the exit code but a packed integer. The `W*` accessors in C
are macros, so there is nothing to P/Invoke and CCRun unpacks it by hand: low seven
bits zero means a normal exit with the code in the next eight bits; otherwise the
process was killed by the signal in those low bits, reported as `128 + signal` the
way a shell does.

This is how the user command's exit code climbs back out: the command exits 3, the
init stage exits 3, `waitpid` reports 3, `RunChild` returns 3, and CCRun exits 3. A
caller's `echo $?` sees the real answer. Because the clone shares the parent's file
descriptors and CCRun redirects nothing, the container inherits stdin, stdout and
stderr directly — which is what lets an interactive shell work across the hop.

#### The rule for code in the cloned child

`RunAsClonedChild` looks like ordinary C#, and it is the most constrained code in
the repository. Everything it may do is: block on a pipe, close two descriptors,
`execve`.

The reason is what `clone` does to a multithreaded process. The child gets a copy
of the parent's memory but only *one* thread — the one that called clone. Every
lock another thread happened to hold at that instant is copied in its locked state,
and there is no longer a thread alive that could release it. If the child then
touches anything that takes a runtime lock — allocating, JIT-compiling a method,
initializing `Console` — it deadlocks, silently and permanently.

There is a subtler version of the same hazard that is worth spelling out, because it
is invisible in the source. An ordinary P/Invoke does not just call the native
function: it brackets the call with a *GC transition*, moving the thread out of and
back into cooperative garbage-collection mode. That bookkeeping touches shared
runtime state. If a collection was being coordinated at the instant of the clone,
the child inherits a half-finished suspension that can never complete — the threads
that would complete it do not exist in it — and the runtime responds to the
impossible state by calling `abort()`. It is rare, timing-dependent, and shows up as
the container dying of a signal rather than exiting.

The defence is three-layered:

```csharp
[LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
[SuppressGCTransition]                       // no GC bookkeeping around the call
internal static partial nint Read(int fd, IntPtr buf, nuint count);
```

```csharp
RuntimeHelpers.PrepareMethod(...RunAsClonedChild...MethodHandle);  // no JIT in the child
Libc.Read(-1, IntPtr.Zero, 0);                      // EBADF — resolves the stub
Libc.Execve(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // EFAULT — ditto
```

`[SuppressGCTransition]` turns those imports into bare native calls that touch no
runtime state; `PrepareMethod` compiles the method in advance; and the two
deliberately-failing calls make the runtime do its lazy interop setup at a moment
when doing so is safe. The attribute normally comes with a rule that the callee must
be brief and must not block, which `read` plainly violates — that is knowingly
waived here, and it is sound for the same reason the rest of this works: the child
has one thread, so there is no collection for a blocked call to obstruct.

The child's budget is down to two calls, `read` and `execve`, which is why the pipe
is created with `O_CLOEXEC` — the descriptors close themselves during the exec, so
the child needs no `close`. Anything added to `RunAsClonedChild` later must obey the
same rules, or the failure will be an intermittent hang or abort with no usable
stack trace.

### The child stage: hostname, chroot, /proc, hand-off

[ChildCommand.Execute](../../src/CCRun/Commands/ChildCommand.cs#L22) is the init stage,
now running inside the namespaces the parent created — and, on the rootfs path, as
PID 1 of the new PID namespace. This is where the container is actually assembled.

It starts by setting the hostname, reading the value from the environment
variable the parent set:

```csharp
string hostname = Environment.GetEnvironmentVariable(ReExec.HostnameEnv)
    ?? RunOptions.DefaultHostname;

if (Libc.Sethostname(hostname, (nuint)Encoding.UTF8.GetByteCount(hostname)) != 0)
    ...
```

Because this process is in its own UTS namespace,
[sethostname(2)](https://man7.org/linux/man-pages/man2/sethostname.2.html)
changes only the container's hostname. Run `hostname` inside the container and
you see `container` (or whatever `--hostname` set); the host is untouched. The
length argument is the UTF-8 byte count, since that is what the syscall expects,
not a character count.

Then comes the branch that carries both Phase 3 and Phase 4. If `CCRUN_ROOTFS` is
present, we build the real container; if not, we fall back to the Phase 2
behavior. This branch is the heart of both phases and deserves its own sections.

## The chroot deep dive

When a rootfs was supplied, the child runs this sequence:

```csharp
Libc.Mount("none", "/", null, Libc.MS_REC | Libc.MS_PRIVATE, null);   // Phase 4
if (Libc.Chroot(rootfs) != 0) { ... }
if (Libc.Chdir("/") != 0) { ... }
Libc.Mount("proc", "/proc", "proc", ...);                             // Phase 4
return Exec(args, stderr);
```

The two chroot calls came first, in Phase 3, so start there; the mounts that now
bracket them are covered in the next section.

[chroot(2)](https://man7.org/linux/man-pages/man2/chroot.2.html) changes what the
process considers to be the root directory, `/`. After the call, any absolute
path the process resolves is interpreted relative to the new root. If you
`chroot` into `alpine-rootfs`, then opening `/etc/alpine-release` really opens
`alpine-rootfs/etc/alpine-release` on the host, and the process has no way to
name anything above that directory using an absolute path. This is the oldest
filesystem isolation primitive in Unix, and it is the reason the container's
`ls /` shows the Alpine tree rather than your host's root.

The `chdir("/")` immediately after is not optional cleanup. It closes a
well-known chroot escape. `chroot` changes the root but does *not* change the
process's current working directory. If the working directory is still somewhere
in the old root when you chroot, the process holds a live reference to a
directory outside its new root, and it can walk upward from there with `..` and
escape the jail entirely. Calling `chdir("/")` right after the chroot moves the
working directory to the new root, so there is no dangling handle to climb. This
is exactly what the integration test
[Chroot_CannotEscapeAboveRoot](../../tests/CCRun.Tests/NamespaceIntegrationTests.cs#L100)
verifies: from inside the container, `cd ..` at `/` stays at `/`.

It is worth being honest about what chroot does *not* do, because this is where
people overestimate it. Chroot only restricts pathname resolution. It does not
give the process a private mount table, does not hide the host's processes, does
not isolate the network, and does not stop a sufficiently privileged process from
breaking out through other means. On its own it is a filesystem view, not a
security boundary. Phase 4 layers the mount and PID namespaces on top, which is
the subject of the next section, and Phase 5 adds the user namespace; dropping
capabilities inside the container comes later still.

One thing Phase 4 deliberately did *not* do is replace `chroot` with
[pivot_root(2)](https://man7.org/linux/man-pages/man2/pivot_root.2.html). Real
runtimes prefer `pivot_root` because it detaches the old root from the mount table
entirely, rather than merely making it unnameable — a process that still holds a
file descriptor pointing outside a chroot can walk out through it, and `pivot_root`
removes that possibility. CCRun keeps the plain `chroot` for now because the
Phase 4 requirements are about process isolation, and mixing a second filesystem
change into the same phase would blur what each primitive contributes. It is
still on the list — Phase 5 kept the plain `chroot` too, since the user-namespace
work did not need the stronger boundary either.

## PID namespace, mount namespace, and a private `/proc`

Here is the problem Phase 4 exists to solve. After Phase 3 you could chroot into
Alpine, run `ps`, and see every process on your laptop. The filesystem was
isolated, the process table was not.

The naive fix — "just mount a fresh `/proc` inside the rootfs" — fails twice over,
and the two mount calls in the child each fix one of those failures.

**Why the process list is wrong without a PID namespace.** `/proc` is not a
directory of files on disk; it is a synthetic filesystem the kernel generates on
demand, and `ps` works purely by reading it. Crucially, a procfs instance shows the
processes of *the PID namespace of whoever mounted it*. Mount a fresh procfs while
still in the host's PID namespace and you get a fresh view of the same hundreds of
host processes. The PID namespace is what makes the container's process list short;
the `/proc` mount is only what makes it *visible* to `ps`. You need both, which is
why `CLONE_NEWPID` and the mount are gated on the same condition.

**Why the mount would leak without making the tree private.** A new mount namespace
does not start empty — it starts as a *copy* of its parent's mount table. And on
most modern distros the root mount is marked *shared*, which means mount and unmount
events propagate between the copies. Mount `/proc` in a naive new mount namespace on
such a system and it dutifully shows up on the host too, which is the exact opposite
of the point. So before touching anything, the child does:

```csharp
Libc.Mount("none", "/", null, Libc.MS_REC | Libc.MS_PRIVATE, null);
```

This is a *propagation change* rather than a real mount — there is no device to
attach, which is why `source` is the conventional `"none"` and both
`filesystemtype` and `data` are null. `MS_PRIVATE` turns propagation off, and
`MS_REC` applies that recursively to every mount inherited from the host, not just
`/` itself. It runs before the `chroot` precisely so the recursion reaches the whole
tree while it is all still nameable.

With those two problems out of the way, the actual `/proc` mount is unremarkable:

```csharp
Libc.Mount("proc", "/proc", "proc", Libc.MS_NOSUID | Libc.MS_NODEV | Libc.MS_NOEXEC, null);
```

It happens *after* the chroot, so `/proc` resolves inside the new root — the rootfs
has to contain a `/proc` directory to serve as the mountpoint, which the Alpine
minirootfs does. The three flags are the conventional hardening set for `/proc`:
no set-uid bits honored, no device files, no executing anything from it. None of
them are load-bearing for the isolation; they are just what you would find on any
real runtime's `/proc` and cost nothing.

**Why there is no cleanup code.** Requirement FR-4.4 asks that the `/proc` mount be
torn down cleanly on exit, including on error paths — and CCRun satisfies it by
doing nothing at all. The mount exists only inside the container's mount namespace,
and a namespace lives exactly as long as it has members. When PID 1 exits, the
kernel terminates any remaining processes in the PID namespace, the mount namespace
loses its last member, and the kernel destroys it along with every mount in it. That
holds whether the command exited cleanly, crashed, or was killed. It is worth
appreciating how much this buys: no unmount call to forget, no error path to leak
through, no stale mounts accumulating on the host after a crash. It is also the only
option available, since after `execvp` replaces the process image there is no CCRun
code left to run a cleanup handler.

## Resource limits and the cgroup

Namespaces are about *visibility*: what a process can see and name. They say
nothing about consumption. A container in its own PID namespace with its own root
filesystem can still allocate every page of RAM on the machine and spin every core.
The kernel feature that bounds consumption is the **control group**, and Phase 6
adds cgroup v2 support behind `--memory` and `--cpus`.

The model is simple once you see it. `/sys/fs/cgroup` is a synthetic filesystem
where **a directory is a cgroup**. You create one with `mkdir`, set a limit by
writing a number to one of the interface files the kernel puts inside it
(`memory.max`, `cpu.max`), and put a process in it by writing that process's PID to
`cgroup.procs`. Children inherit their parent's cgroup, so admitting one process
captures the whole tree it goes on to spawn. Deleting the cgroup is `rmdir`, which
the kernel allows only once the directory holds no processes.

`cpu.max` is the only value that needs decoding. It is written as two numbers,
`"<quota> <period>"`, both in microseconds, and it means "this cgroup may use
*quota* microseconds of CPU time in every *period*". CCRun fixes the period at the
kernel default of 100 000µs, so `--cpus 0.5` becomes `50000 100000`. Quota is
allowed to exceed period — that is how you express a limit larger than one core:
`--cpus 2` is `200000 100000`.

**Where the work happens, and why it cannot happen anywhere else.** All of it is in
the parent, in the window `ReExec.RunChild` already had between `clone3` returning
and the go-byte that releases the child:

```
clone3 → WriteIdMaps(pid) → cgroup: mkdir + limits + cgroup.procs → go-byte → child execs
```

Three separate constraints force that placement, and any one of them would be
enough:

- The limits have to be in force *before* the user command runs, and at this point
  the child is parked on the pipe read, which is the only moment we can be sure it
  has not started yet.
- The cloned child may not allocate, JIT, or do managed I/O (see the
  [rules for that method](#the-clone-launching-ourselves-as-the-child)), so it
  cannot open and write cgroup files itself.
- After `chroot` the child cannot even *reach* `/sys/fs/cgroup` — it is outside the
  new root.

So the parent does it, using the child's **host-side** PID. That distinction
matters: the container thinks it is PID 1, but `cgroup.procs` is a host interface
and knows nothing about the container's PID namespace.

**Finding a directory we are allowed to create.** This is the part that is genuinely
about rootless containers rather than about cgroups. `/sys/fs/cgroup` itself is
root-owned; an unprivileged user cannot `mkdir` there, and Phase 5 exists precisely
so ccrun does not need root. What an unprivileged user *does* usually have on a
systemd host is a **delegated subtree** — systemd hands the user's login session
(`user.slice/user-<uid>.slice/user@<uid>.service`) ownership of its own cgroup
directory and the controllers listed in its `cgroup.subtree_control`, and inside
that subtree the user may create cgroups freely.

[Cgroup.Create](../../src/CCRun/Container/Cgroup.cs) therefore does not hard-code a
path. It reads our own cgroup out of `/proc/self/cgroup` and walks *up* towards the
mount root, trying each ancestor in turn, and takes the first one where two things
hold: the `mkdir` succeeds, and the resulting directory actually contains the
interface files for the controllers we need. That second check is the interesting
one — it is how we detect delegation. A controller only exists in a child cgroup if
the parent enabled it in `cgroup.subtree_control`, and rather than parse that file
and reason about what it implies, we create the directory and ask the kernel the
same question directly: is `memory.max` there or not? If not, we `rmdir` and keep
walking. The nearest ancestors typically fail this way, because the leaf cgroup
holding our own process cannot delegate controllers while it holds tasks (cgroup
v2's "no internal processes" rule), so the search usually settles on the session
service a couple of levels up.

If no ancestor works, the run fails with an explanation rather than proceeding.
Running a container *without* the limit the user explicitly asked for is worse than
not running it: the whole point of `--memory` is that something on the other side is
counting on the cap.

**Why swap is turned off too.** Alongside `memory.max`, CCRun writes
`memory.swap.max = 0`. Left at the default, a container that hits its memory limit
does not die — it starts swapping, and a runaway allocation thrashes indefinitely
instead of being killed. The limit technically holds, but nothing observable
happens, which is not what someone typing `--memory 16m` is asking for. Denying swap
makes the number a hard cap, and a container that exceeds it is SIGKILLed by the
OOM killer. `WaitForExitCode`'s signal branch then reports the conventional
`128 + 9 = 137`, exactly as a shell would.

**Cleanup.** `rmdir` only succeeds on an empty cgroup, so removal has to wait for
the container to be reaped — which is what the existing `waitpid` in `RunChild`
already does. The `Dispose` therefore hangs off a `finally` around that call, and it
is deliberately quiet about failure: a container that somehow left a descendant
behind keeps the directory busy, and failing an otherwise successful run over a
stray empty directory would be a poor trade. Note the contrast with the `/proc`
mount, which needs no cleanup at all because the kernel destroys the mount namespace
with its last member. A cgroup is not tied to any namespace's lifetime, so this one
really does have to be cleaned up by hand.

Which exposes the one hole: if the ccrun *parent* is itself SIGKILLed, no cleanup
code runs and an empty `ccrun-<pid>` directory is left behind. Nothing in-process
can fix that — SIGKILL is by definition unhandleable — and it is a good part of why
real runtimes put a supervising daemon or shim outside the container. An empty
cgroup costs nothing but a directory entry, so CCRun accepts the hole rather than
growing a supervisor for it.

One consequence worth knowing: the `__child` stage runs *inside* the container's
cgroup, so the .NET runtime's own memory is charged against `--memory` until it
`exec`s away. In practice the pages it inherited from the parent stay charged to the
parent's cgroup — cgroup v2 does not re-charge existing pages when a process is
moved — so the overhead is small, but a limit of a few megabytes is not realistic.

## Why `execvp` here but `Process.Start` elsewhere

This is the most subtle decision in the phase, and it is worth slowing down for.

On the non-chroot path, CCRun launches the user command with
[ProcessRunner.Run](../../src/CCRun/Container/ProcessRunner.cs#L12), which uses .NET's
ordinary `Process.Start`. That spawns the command as a child process, waits for
it, and returns its exit code. Simple and safe.

On the chroot path, CCRun instead calls
[execvp(2)](https://man7.org/linux/man-pages/man2/execvp.2.html) through
[ChildCommand.Exec](../../src/CCRun/Commands/ChildCommand.cs#L118), which *replaces* the
current process image with the command. After a successful `execvp`, there is no
more .NET code running at all; the process has become BusyBox (or whatever the
command is). `execvp` only returns if the exec fails.

Why the difference? Because `Process.Start` after a `chroot` is a trap. The .NET
runtime loads assemblies and JIT-compiles code lazily, and a self-contained build
may extract files to a temporary directory. All of those files live at host paths
that are now *outside* the new root. The moment the runtime needs to touch one of
them after the chroot, it may fail, and it may fail non-deterministically
depending on what has already been loaded. So the rule on the chroot path is: do
no more managed work. Hand off to the command by replacing the process entirely,
which needs nothing from the old root.

That rule has a sharp edge that Phase 4 found the hard way, and it is the same
shape as the thread problem in the parent. "Do no more managed work" has to include
the *error paths*, and they are the easiest thing in the world to forget, because
they are exactly the code you do not run when everything works. Reporting "cannot
exec that command" means writing to stderr, and Console — which has not
necessarily written anything yet in this process — initializes itself on first
write by loading another assembly from the runtime directory that the chroot just
made unreachable. The result was a process that died with a `FileNotFoundException`
instead of printing the error and exiting 127. `ChildCommand` therefore forces that
assembly resident before the chroot, alongside the `strerror` change in `Libc`
described earlier. Both exist so that the failure paths can still *report* failure.

The non-chroot path has no such problem. There was no chroot, the runtime's files
are all still reachable, so `Process.Start` is perfectly safe, and keeping it has
a nice side benefit. `ProcessRunner` can be unit-tested without root, since
spawning a plain child process needs no privileges. So CCRun keeps both hand-off
mechanisms, each matched to its situation.

There is one small gotcha this creates for the user. `execvp` searches `PATH` for
a bare command name, but that `PATH` search happens *inside the new root*. If you
pass `sh` rather than `/bin/busybox`, the lookup depends on what `PATH` and which
binaries exist in the container, not on the host. The safe habit under `--rootfs`
is to give an absolute command path.

The `Exec` helper also has to build a C-style `argv`, which is a small but real
piece of interop, covered next.

## The libc layer

[Native/Libc.cs](../../src/CCRun/Native/Libc.cs) is the whole native surface of the
project. It uses .NET's source-generated P/Invoke, the `[LibraryImport]`
attribute, which generates the marshalling code at compile time rather than at
runtime. Every function CCRun needs from libc is declared here: `syscall` (used
only to reach `clone3`, which glibc exposes no wrapper for), `sethostname`,
`chroot`, `chdir`, `mount`, `execve` and `execvp`, the `pipe`/`read`/`write`/`close`
handshake calls, `waitpid`, `_exit`, and `geteuid`/`getegid`. That is the entire
native surface of a container runtime's core, which is a fair summary of this
project's thesis.

Three conventions run through the file.

**Errno reporting.** Every call that can fail is declared with
`SetLastError = true`, and there is a helper:

```csharp
public static string LastErrorMessage()
{
    int err = Marshal.GetLastPInvokeError();
    IntPtr msg = Strerror(err);
    return msg == IntPtr.Zero ? $"errno {err}" : Marshal.PtrToStringUTF8(msg) ?? $"errno {err}";
}
```

When a syscall returns its failure value, `Marshal.GetLastPInvokeError` retrieves
the errno the kernel set, and `strerror` turns that number into a human-readable
string like "Operation not permitted" instead of a bare `1`. Every failure path in
the commands uses this, so the diagnostics are always in terms a person can act on.

The idiomatic .NET way to write that is `new Win32Exception(errno).Message`, which
works fine on Linux despite the Windows-flavored name, and that is what this
project used until Phase 4. It had to go, for a reason that is a good introduction
to the next section: `Win32Exception` lives in `Microsoft.Win32.Primitives`, which
the runtime loads *lazily, from disk, on first use*. The child stage's failure
paths run after `chroot`, where the runtime's own assemblies are no longer
reachable. So the very act of reporting "cannot exec that command" would itself
fail — with a `FileNotFoundException` that killed the process outright, losing both
the diagnostic and the exit code. `strerror` is a plain libc call that needs
nothing loaded, so it survives the chroot.

**The NULL-terminated argv trick.** `execvp` expects a C array of string
pointers terminated by a null pointer, with `argv[0]` conventionally the program
name. [ChildCommand.Exec](../../src/CCRun/Commands/ChildCommand.cs#L118) builds this by
allocating one extra slot and leaving it null:

```csharp
var cargv = new string?[argv.Length + 1];
Array.Copy(argv, cargv, argv.Length);
cargv[argv.Length] = null;   // marshals to a null pointer, terminating the array
Libc.Execvp(command, cargv);
```

The UTF-8 marshaller that `[LibraryImport]` generates turns a null string element
into a null pointer, which is exactly the terminator C expects. It is a neat case
of the managed representation lining up with the native one with no manual pointer
work.

The same null-marshalling behavior is why `Mount` can declare `source`,
`filesystemtype`, and `data` as nullable strings and pass C# `null` straight
through as a NULL pointer — which several of `mount`'s modes, like the propagation
change above, genuinely require.

**Pointers where the marshaller would be unsafe.** `Execve` is the odd one out: it
takes three raw `IntPtr`s rather than the friendly string types `Execvp` uses. That
is deliberate. It is called from the cloned child, where allocating is forbidden,
and string marshalling allocates. So `ReExec` builds the `char*[]` arrays itself
with `Marshal.StringToHGlobalAnsi` *before* the clone, and the child passes along
pointers that already exist. Where a signature looks needlessly low-level in this
file, that is usually the reason.

## Exit codes

[ExitCodes.cs](../../src/CCRun/ExitCodes.cs) collects the process exit codes CCRun
itself produces, as opposed to codes that come from the user's command. The
values are chosen to match conventions people already know from Docker and the
shell:

- `0` success, `1` usage error.
- `125` for a runtime error, meaning container setup failed before the command
  ran. This mirrors Docker's own use of 125.
- `126` for "command found but not executable" and `127` for "command not
  found," which are the long-standing shell conventions.

The nice property here is that these codes are consistent across both hand-off
paths. `ProcessRunner` maps a launch failure's native error to 126 or 127, and
the `execvp` path maps its errno to the same two codes, so a missing command
looks the same whether or not you used `--rootfs`. And any code the *command*
returns passes straight through untouched, thanks to the `WaitForExit` /
`ExitCode` chain described earlier. CCRun's own codes and the command's codes stay
in separate lanes.

## How it is tested

The test project is [tests/CCRun.Tests](../../tests/CCRun.Tests). The interesting thing
about it is the split between tests that need root and tests that do not, and how
much of the behavior was made testable *without* root by the design choices
above.

The pure, always-run tests cover the parts that touch no privileged syscalls:

- [RunOptionsTests](../../tests/CCRun.Tests/RunOptionsTests.cs) exercises the argument
  parser. Because parsing does no filesystem work, these are fast and
  deterministic.
- [CliTests](../../tests/CCRun.Tests/CliTests.cs) covers verb dispatch and usage.
- [ProcessRunnerTests](../../tests/CCRun.Tests/ProcessRunnerTests.cs) checks the
  child-process exit-code contract on the non-chroot path, which is exactly why
  that path was kept on `Process.Start`.
- [ResourceLimitsTests](../../tests/CCRun.Tests/ResourceLimitsTests.cs) is a table of
  `--memory`/`--cpus` values and the numbers they should become, including the ones
  that must be rejected (zero, negative, a bad suffix, a size that would overflow a
  `long`). Keeping the unit conversion in a pure type is what makes this possible
  without a cgroup anywhere in sight.
- [RunCommandTests](../../tests/CCRun.Tests/RunCommandTests.cs) covers parent-stage
  behavior that returns *before* any namespace is created — argument errors, the
  missing-rootfs error, a malformed limit value — and hosts the `IsRoot` /
  `IsUserNsAvailable` / `IsCgroupV2Delegated` gates the integration tests share.
  These are reachable with no privileges precisely because validation runs before
  any namespace work.

The tests that genuinely need privileges live in
[NamespaceIntegrationTests](../../tests/CCRun.Tests/NamespaceIntegrationTests.cs) and
use `Xunit.SkippableFact`. Before Phase 5 they were gated on being root and
skipped on a normal `dotnet test`; now that ccrun makes its own user namespace they
are gated on `RunCommandTests.IsUserNsAvailable` — root *or* a kernel that permits
unprivileged user namespaces — so a plain `dotnet test` exercises the whole
pipeline: clone3, the ID maps, sethostname, chroot, the `/proc` mount, and the
`execvp` hand-off into an in-rootfs BusyBox. They skip only where user namespaces
are switched off, which the gate detects by reading the two sysctl knobs
(`kernel.unprivileged_userns_clone` and `user/max_user_namespaces`).

Two of them exist specifically to pin down FR-5.2 and FR-5.3, the rootless
requirements. `Rootless_ContainerRootMapsToInvokingUser` runs `id -u` in the
container and asserts `0` — which also proves the map was actually applied, since an
unmapped namespace would report the overflow ID 65534 instead.
`Rootless_HostSeesProcessOwnedByInvokingUser` is the interesting half: it starts a
`sleep` with a distinctive duration, finds that process in the *host's* `/proc` by
scanning command lines, reads the effective UID out of `/proc/<pid>/status`, and
asserts it equals the test runner's own UID and is not 0. That is the whole point of
rootless containers expressed as an assertion — root inside, you outside. It skips
when running as root, where container root maps to real root and the check would be
vacuous.

Unlike every other test class, these spawn the ccrun **binary** rather than calling
`Cli.Run` in-process, and that is a hard requirement rather than a style choice.
`run` clones a child into new namespaces and reaps it with `waitpid`; done
in-process, that child and its wait status would be grafted onto the xunit test
host, which is also busy managing its own processes. Spawning a fresh ccrun per test
keeps each container's process tree, PID namespace and exit plumbing to itself.

The out-of-process design pays a bonus: stdout is a real inherited file
descriptor, so these tests can assert on what the container actually printed, even
across the `execvp` hand-off that replaces the process image and would defeat an
in-process `StringWriter`. `PidNamespace_ContainerShellIsPidOne` just runs
`echo $$` and asserts the output is `1`. `PrivateProc_OnlyContainerProcessesVisible`
counts the numeric entries in `/proc` and requires between 1 and 4 — the shell plus
its `ls`/`wc` pipeline. Those bounds catch both ways it can break: the host's
`/proc` counts in the hundreds, and a missing `/proc` mount counts zero.

`MountNamespace_ContainerProcMountNotVisibleOnHost` is the fiddliest of them,
because FR-4.3 is a claim about what is true *while a container is running*.
Checking the host's mount table after the container exits proves little, since the
mount is gone either way. So the container touches a marker file and then idles;
the test polls for the marker, reads `/proc/self/mounts` on the host while the
container is definitely live, and kills it in a `finally`.

The three Phase 6 tests have a gate of their own, `IsCgroupV2Delegated`, and it is
worth noting how it is written: instead of guessing at the host's layout, it runs
the *same* search `Cgroup.Create` does and reports whether it found somewhere
usable. The precondition for the tests is then exactly the precondition for the
feature, so they cannot skip on a host where the feature works or fail on one where
it does not.

The tests themselves read the limits from the **host** side, because the container
has no cgroup namespace and no `/sys` of its own. Getting the path is easier than it
sounds: without `--rootfs` the container sees the host's `/proc`, so the command can
simply `cat /proc/self/cgroup` and tell us which directory it landed in.
`Cgroup_ContainerIsInItsOwnCgroup_WithTheRequestedLimits` has it write that path to
a file and then idle, so the values can be checked while the container is
demonstrably live — the same trick the mount-namespace test uses, and for the same
reason. `Cgroup_RemovedAfterContainerExits` gets the path from the container's
stdout and asserts the directory is gone by the time ccrun returns, which is FR-6.5.
`Cgroup_MemoryLimitIsEnforced_ProcessKilled` grows a shell variable a megabyte at a
time under a 16 MB cap and asserts the exit code is 137; it gives up with a skip
rather than hanging if the host turns out to have no swap accounting, since then
ccrun's `memory.swap.max` write cannot take and the container would thrash instead
of dying.

The chroot tests have a second gate. They call a small helper,
[FindAlpineRootfs](../../tests/CCRun.Tests/NamespaceIntegrationTests.cs#L73), that walks
up from the test binary's directory looking for `alpine-rootfs/ALPINE_FS_ROOT`
and a real `bin/busybox`. If the Alpine rootfs is not present on the machine,
those tests skip too, rather than failing. The rootfs is git-ignored, so this
keeps a fresh checkout from breaking. README.md has the commands to recreate it.

The broader lesson is that a few unglamorous design choices, injecting the output
writers, keeping the parser free of side effects, and validating before the
privileged syscall, are what let most of the runtime be tested on a laptop with
no sudo. Only the genuinely privileged behavior needs a privileged test.

## Where this goes next

The two-stage architecture was not built just for hostname and chroot. It exists
because the later phases *require* setup to happen in a freshly namespaced child,
and Phase 2 paid that cost up front so the rest could slot in. Phase 4 mostly bore
that out: the PID and mount namespaces and the private `/proc` slotted into the
existing two stages without new structure, because the fork ordering and the
`execvp` hand-off were already what a proper PID 1 needs.

Phase 5 tested the architecture harder. The *shape* survived — still two stages,
still a hidden `__child` verb, still the same env-var handoff — but the mechanism
underneath the parent stage had to be replaced wholesale, because a multithreaded
.NET process simply cannot `unshare` a user namespace. Swapping `unshare` +
`Process.Start` for `clone3` + `waitpid` touched only `ReExec`, which is a fair
verdict on the original boundaries. The recurring theme across both phases is that
the .NET runtime's laziness fights low-level process work: the warm-ups, the move
off `Win32Exception`, and now the pre-JIT of the cloned child's code path all exist
for that reason.

Phase 6 was the easy one by comparison, and that is the architecture paying off:
the cgroup work needed no new stage, no new env var and no change to the child at
all, because the parent already held the child's PID and already had a window in
which the child was guaranteed not to have started. The prediction made here before
Phase 6 — that rootless cgroups would interact with Phase 5 more than they first
appear — held exactly: nearly all the difficulty was in *finding a directory an
unprivileged user may write to*, not in writing the limits.

Here is the rest of the trajectory:

- `pivot_root` remains deferred, and is the natural companion to whichever phase
  next needs a stronger filesystem boundary than `chroot` provides.
- A `--pids-limit` would drop straight into `ResourceLimits` and `Cgroup` — the
  pids controller works exactly like the two already here — and mounting the
  container's own cgroup at `/sys/fs/cgroup` inside it would need a cgroup
  namespace, which is a fifth `CLONE_*` flag and little else.
- **Phases 7 and 8** add image handling: pulling from a registry and unpacking
  layers into the rootfs that `--rootfs` points at today.

Read in that light, each file you have just walked through is a small, honest
version of a piece of a real runtime, with room left for the next layer. If you
followed the `run` trace all the way down, you now understand the spine that the
remaining phases hang off of.
