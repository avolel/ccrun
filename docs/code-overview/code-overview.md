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
As of this writing the runtime is through **Phase 3**, which means it can do two
things that matter:

1. **Hostname isolation** (Phase 2). A container gets its own hostname, and
   changing it does not touch the host's hostname. This is done with a UTS
   namespace.
2. **Filesystem isolation** (Phase 3). With `--rootfs`, a container `chroot`s
   into a root filesystem so the command inside sees a different `/` and cannot
   climb out of it.

Everything the command sees still comes from the host in every other respect.
There is no process isolation yet (the container can see the host's process
table), no mount namespace or private `/proc`, no user namespace, no resource
limits via cgroups, and no image handling. A real `docker run alpine` pulls an
image, sets up a mount namespace, remaps user IDs, and caps memory and CPU.
CCRun does none of that yet. Those are Phases 4 through 8, and the code is
structured so they slot in cleanly. More on that at the end.

Because creating namespaces and calling `chroot` both require Linux capabilities
that ordinary users do not have (`CAP_SYS_ADMIN` for the namespace,
`CAP_SYS_CHROOT` for the chroot), `ccrun run` currently needs root. Rootless
mode, which uses a user namespace to get those capabilities without real root,
arrives in Phase 5.

## The one idea that shapes everything: two stages

If you take away a single concept from this document, make it this one. CCRun
runs your command in two stages, in two separate processes, and almost every
design decision follows from that split.

The reason is a chicken-and-egg problem. To isolate a process you want to put it
inside fresh namespaces. But some of that setup is awkward or impossible to do to
*yourself* after the fact. The clean approach, and the one every real runtime
uses in some form, is:

1. A **parent (host) stage** creates the new namespaces.
2. It then launches a **child (init) stage**, a brand new process that
   *inherits* those namespaces from birth.
3. The child does the in-namespace setup (set the hostname, chroot, later mount
   `/proc` and so on) and finally hands control to the user's actual command.

CCRun implements this by re-executing *itself*. The parent runs
`ccrun run ...`. After it creates the namespace, it launches `ccrun __child ...`,
where `__child` is a hidden verb that means "you are the init stage, finish the
setup." The same binary plays both roles depending on which verb it is invoked
with. You will see this hidden verb dispatched in [Cli.cs](../../src/CCRun/Cli.cs#L26)
and produced in [ReExec.cs](../../src/CCRun/Container/ReExec.cs#L44).

Keep this two-stage picture in mind and the rest of the code reads as a straight
line.

## Walking a `run` from the outside in

Let us trace one concrete invocation all the way through:

```sh
sudo ccrun run --rootfs alpine-rootfs /bin/busybox sh
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
- Both `--rootfs /path` and `--rootfs=/path` forms are accepted, mirroring how
  `--hostname` already worked.

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
    string Command,
    IReadOnlyList<string> CommandArgs);
```

`Rootfs` is nullable, and null carries real meaning: it means "no chroot, behave
like Phase 2." That single nullable field is what makes filesystem isolation
opt-in and backward compatible.

### The parent stage: creating the namespace

[RunCommand.Execute](../../src/CCRun/Commands/RunCommand.cs#L14) is the host stage. It
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

Second, it creates the namespace:

```csharp
if (Libc.Unshare(Libc.CLONE_NEWUTS) != 0)
```

[unshare(2)](https://man7.org/linux/man-pages/man2/unshare.2.html) is the
syscall that says "give the calling process new, private copies of the kernel
resources named by these flags." `CLONE_NEWUTS` asks for a new UTS namespace.
UTS stands for Unix Time-sharing System, a historical name; in practice it is the
namespace that owns the hostname and domain name. After this call succeeds, this
process has its own hostname slot, and changing it will not affect the host.

If `unshare` fails with `EPERM`, CCRun prints a hint telling the user to re-run
under sudo. This is the common case for anyone who forgets, and a helpful message
beats a bare "operation not permitted." Root is required because creating a
namespace needs `CAP_SYS_ADMIN`, and that changes in Phase 5 with rootless mode.

An important subtlety: at this point only the parent is in the new UTS namespace.
The parent does not set the hostname itself. It defers that to the child, which
will inherit the namespace. This keeps all in-namespace setup in one place and
matches how later phases will need to work, where the setup genuinely has to
happen in the child.

Third, it re-executes into the child stage, passing along the resolved absolute
rootfs:

```csharp
return ReExec.RunChild(options with { Rootfs = rootfs }, stderr);
```

The `with` expression makes a copy of the options record with the rootfs swapped
for its absolute form. The return value of `RunChild` is the child's exit code,
which becomes CCRun's exit code. We will see how that propagates in a moment.

### The re-exec: launching ourselves as the child

[ReExec.RunChild](../../src/CCRun/Container/ReExec.cs#L23) is the machinery that
re-launches CCRun in its init stage. It is short but has two genuinely tricky
parts.

The first is **passing data to the child.** The child is a fresh process, so it
does not share the parent's in-memory state. CCRun passes what the child needs
through environment variables: `CCRUN_HOSTNAME` always, and `CCRUN_ROOTFS` only
when a rootfs was given. The presence or absence of `CCRUN_ROOTFS` is exactly how
the child knows whether to chroot. Command-line arguments carry the command and
its arguments; environment variables carry the container configuration. This is
the same general approach real runtimes use to hand a config from the setup
process to the init process.

The second tricky part is **how to re-invoke this very program.** This is
genuinely awkward on .NET because there are two ways CCRun might be running:

- As a **published, self-contained binary** (an "apphost"), where
  `Environment.ProcessPath` points at the CCRun executable itself. In that case
  you re-run that path directly.
- Under the **`dotnet` muxer** during development (`dotnet run` or running the
  built DLL), where `Environment.ProcessPath` points at `dotnet`, not at CCRun.
  Running `dotnet __child ...` would tell the .NET launcher to look for a command
  called `__child`, which is nonsense. You have to run `dotnet path/to/CCRun.dll
  __child ...` instead.

The code handles both:

```csharp
string exe = Environment.ProcessPath ?? throw ...;
psi.FileName = exe;
if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
{
    string dll = Assembly.GetEntryAssembly()?.Location ...;
    psi.ArgumentList.Add(dll);
}
```

If the process we are running as is `dotnet`, we prepend the entry assembly's DLL
path so the muxer knows which managed program to launch. Otherwise we invoke the
apphost directly. Then it appends the `__child` verb, the command, and its
arguments.

Finally it starts the child and waits:

```csharp
using var child = Process.Start(psi) ?? throw ...;
child.WaitForExit();
return child.ExitCode;
```

`UseShellExecute = false` with no stream redirection means the child inherits the
parent's standard input, output, and error. That is what lets an interactive
shell work across the re-exec hop: keystrokes and output flow straight through.
And returning `child.ExitCode` is the mechanism by which the user command's exit
code climbs back out to CCRun's own exit code. The command exits with 3, the
child stage exits with 3, `RunChild` returns 3, `RunCommand` returns 3, and CCRun
exits 3. A caller's `echo $?` sees the real answer.

### The child stage: hostname, chroot, hand-off

[ChildCommand.Execute](../../src/CCRun/Commands/ChildCommand.cs#L15) is the init stage,
now running inside the UTS namespace the parent created. This is where the
container is actually assembled.

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

Then comes the Phase 3 branch. If `CCRUN_ROOTFS` is present, we chroot; if not,
we fall back to the Phase 2 behavior. This branch is the heart of the phase and
deserves its own sections.

## The chroot deep dive

When a rootfs was supplied, the child does this:

```csharp
if (Libc.Chroot(rootfs) != 0) { ... }
if (Libc.Chdir("/") != 0) { ... }
return Exec(args, stderr);
```

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
[Chroot_CannotEscapeAboveRoot](../../tests/CCRun.Tests/NamespaceIntegrationTests.cs#L78)
verifies: from inside the container, `cd ..` at `/` stays at `/`.

It is worth being honest about what chroot does *not* do, because this is where
people overestimate it. Chroot only restricts pathname resolution. It does not
give the process a private mount table, does not hide the host's processes, does
not isolate the network, and does not stop a sufficiently privileged process from
breaking out through other means. On its own it is a filesystem view, not a
security boundary. Real containers layer a mount namespace, `pivot_root`, a
private `/proc`, and dropped capabilities on top. Those are Phase 4 and beyond.
Phase 3 is intentionally just the plain `chroot`, so the concept stands on its
own before the next layer covers it.

## Why `execvp` here but `Process.Start` elsewhere

This is the most subtle decision in the phase, and it is worth slowing down for.

On the non-chroot path, CCRun launches the user command with
[ProcessRunner.Run](../../src/CCRun/Container/ProcessRunner.cs#L12), which uses .NET's
ordinary `Process.Start`. That spawns the command as a child process, waits for
it, and returns its exit code. Simple and safe.

On the chroot path, CCRun instead calls
[execvp(2)](https://man7.org/linux/man-pages/man2/execvp.2.html) through
[ChildCommand.Exec](../../src/CCRun/Commands/ChildCommand.cs#L61), which *replaces* the
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
runtime. Every function CCRun needs from libc is declared here: `unshare`,
`sethostname`, `chroot`, `chdir`, `execvp`, and `geteuid`.

Two conventions run through the file.

**Errno reporting.** Every call that can fail is declared with
`SetLastError = true`, and there is a helper:

```csharp
public static string LastErrorMessage() =>
    new Win32Exception(Marshal.GetLastPInvokeError()).Message;
```

When a syscall returns its failure value, `Marshal.GetLastPInvokeError` retrieves
the errno the kernel set, and wrapping it in a `Win32Exception` turns that number
into a human-readable string. Despite the Windows-flavored name, this works on
Linux and gives you messages like "Operation not permitted" instead of a bare
`1`. Every failure path in the commands uses this, so the diagnostics are always
in terms a person can act on.

**The NULL-terminated argv trick.** `execvp` expects a C array of string
pointers terminated by a null pointer, with `argv[0]` conventionally the program
name. [ChildCommand.Exec](../../src/CCRun/Commands/ChildCommand.cs#L61) builds this by
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
- [RunCommandTests](../../tests/CCRun.Tests/RunCommandTests.cs) covers parent-stage
  behavior that fails *before* `unshare`, including the missing-rootfs error and
  the non-root sudo hint. The missing-rootfs test is reachable without root
  precisely because validation runs before any namespace work.

The tests that genuinely need privileges live in
[NamespaceIntegrationTests](../../tests/CCRun.Tests/NamespaceIntegrationTests.cs) and
use `Xunit.SkippableFact`. They call `Skip.IfNot(RunCommandTests.IsRoot, ...)`,
so on an ordinary unprivileged machine or CI run they skip cleanly and
`dotnet test` stays green. Run them with `sudo dotnet test` to exercise the full
pipeline: unshare, re-exec, sethostname, chroot, and the `execvp` hand-off into
an in-rootfs BusyBox.

The chroot tests have a second gate. They call a small helper,
[FindAlpineRootfs](../../tests/CCRun.Tests/NamespaceIntegrationTests.cs#L52), that walks
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
and Phase 2 paid that cost up front so the rest could slot in. Here is the
trajectory:

- **Phase 4** adds the mount, PID, and user namespaces. The child gains a private
  mount table, a private `/proc` so `ps` only shows container processes, and
  `pivot_root` replaces the plain `chroot` for a stronger filesystem boundary.
  The `execvp` hand-off pulled forward in Phase 3 is also what a proper PID 1
  wants.
- **Phase 5** adds cgroup v2 for CPU and memory limits, and rootless mode, which
  uses a user namespace to obtain the capabilities that today force `sudo`.
- **Phases 7 and 8** add image handling: pulling from a registry and unpacking
  layers into the rootfs that `--rootfs` points at today.

Read in that light, each file you have just walked through is a small, honest
version of a piece of a real runtime, with room left for the next layer. If you
followed the `run` trace all the way down, you now understand the spine that the
remaining phases hang off of.
