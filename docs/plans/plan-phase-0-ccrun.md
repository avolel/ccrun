# Plan: CCRun — Phase 0 (Environment Setup)

## Context

CCRun is a "Build Your Own Docker" learning project: a lightweight Linux
container runtime in C# / .NET 10 (per the BRD). It's built in 8 phases; this
plan covers **Phase 0 only** — the scaffolding every later phase depends on.
Nothing container-related is implemented yet; the goal is a clean, buildable
project skeleton plus the Alpine root filesystem used for `chroot` testing in
Phase 3.

**Environment already verified (read-only checks):**
- .NET SDK **10.0.109** installed → target `linux-x64` (host is `x86_64`).
- Kernel **6.17**, cgroup **v2** (unified), **unprivileged user namespaces enabled**
  (`kernel.unprivileged_userns_clone = 1`) — good news for the rootless Phase 5.
- `git`, `curl`, `wget`, `tar`, `gzip` all present. `/home/avolel` is **not** a
  git repo; `~/Code` is where the user keeps projects.

**Decisions (confirmed with user):** project at `~/Code/ccrun`; tests use
**xUnit**; Alpine rootfs is **downloaded** (latest 3.x minirootfs, x86_64).

## Scope (maps to BRD FR-0.1 / FR-0.2 / FR-0.3, Milestone M0)

- FR-0.1: .NET 10 console app with clear structure (`src/CCRun`, `tests/CCRun.Tests`).
- FR-0.2: Linux available — **already satisfied** (native Linux host, no VM needed).
- FR-0.3: Alpine rootfs unpacked locally, git-ignored, with an `ALPINE_FS_ROOT`
  marker file at its root.

Out of scope for this phase: any `run`/`pull` logic, P/Invoke, namespaces,
cgroups, registry client — those are Phases 1–8.

## Steps

### 1. Create project root and initialize git
- `mkdir -p ~/Code/ccrun` and `git init` there.
- Git identity is already set globally (Andre Volel / avolel@gmail.com).

### 2. Scaffold the solution and projects
From `~/Code/ccrun`:
- `dotnet new sln -n CCRun`
- `dotnet new console -n CCRun -o src/CCRun` (targets `net10.0`)
- `dotnet new xunit -n CCRun.Tests -o tests/CCRun.Tests`
- `dotnet sln add src/CCRun tests/CCRun.Tests`
- `dotnet add tests/CCRun.Tests reference src/CCRun`

Resulting layout:
```
ccrun/
  CCRun.sln
  src/CCRun/CCRun.csproj        (net10.0 console)
  tests/CCRun.Tests/            (net10.0 xunit, references src/CCRun)
  alpine-rootfs/                (downloaded, git-ignored)
  .gitignore
  README.md
```

### 3. Tune the app csproj for the project's needs
Edit `src/CCRun/CCRun.csproj` to set expectations used by later phases and NFRs:
- `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` (defaults from template — confirm present).
- `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>` **not** hard-pinned here;
  instead document `dotnet publish -r linux-x64 --self-contained` in README so
  cross-arm64 stays possible (NFR-1). Keep the csproj RID-agnostic.
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — needed later for P/Invoke /
  marshalling work (Phase 2+). Harmless now, avoids re-touching the file.
- `<InvariantGlobalization>true</InvariantGlobalization>` for a lean self-contained build.

### 4. Minimal placeholder program
Replace `src/CCRun/Program.cs` with a small stub that prints a usage message and
exits non-zero on no args (aligns with FR-1.5 direction without implementing
`run` yet). Keep it tiny — Phase 1 will replace it with real command parsing.

### 5. Download and unpack the Alpine rootfs (FR-0.3)
- Determine the latest Alpine 3.x minirootfs for `x86_64` from the CDN
  (`https://dl-cdn.alpinelinux.org/alpine/latest-stable/releases/x86_64/`),
  select the `alpine-minirootfs-<ver>-x86_64.tar.gz` file.
- Download to a temp location (scratchpad), then extract into
  `~/Code/ccrun/alpine-rootfs/` with `tar -xzf ... -C alpine-rootfs`.
- Create the marker: `touch alpine-rootfs/ALPINE_FS_ROOT`.
- Verify: `ls alpine-rootfs/` shows `bin etc usr ... ALPINE_FS_ROOT`, and
  `alpine-rootfs/bin/busybox` exists (needed for FR-3.3 in Phase 3).

Note: unpacking as a normal user (not root) is fine for Phase 0 — device nodes
in the tarball may warn but the FS is usable for chroot testing.

### 6. `.gitignore`
Create `.gitignore` excluding:
- `alpine-rootfs/` (the large downloaded FS — BRD Assumption 3 / FR-0.3).
- Standard .NET artifacts: `bin/`, `obj/`, `*.user`, `.vs/`.
- Later image store isn't inside the repo (`~/.ccrun/...`), so no entry needed.

### 7. README.md stub
Short README covering: what CCRun is, prerequisites (.NET 10, Linux, cgroup v2),
how to build (`dotnet build`), how to run tests (`dotnet test`), how to obtain
the Alpine rootfs (the download command from step 5), and a note that Phases 1–8
are forthcoming. Satisfies the documentation start of NFR-4 / M0.

### 8. Sanity test in the test project
Keep the xUnit template's placeholder test (or add one trivial passing test) so
`dotnet test` is green — this is the "CI build passing" exit criterion for M0.

## Critical files created/modified
- `~/Code/ccrun/CCRun.sln`
- `~/Code/ccrun/src/CCRun/CCRun.csproj` (tuned) and `Program.cs` (stub)
- `~/Code/ccrun/tests/CCRun.Tests/` (xunit project)
- `~/Code/ccrun/.gitignore`, `README.md`
- `~/Code/ccrun/alpine-rootfs/` + `ALPINE_FS_ROOT` (git-ignored content)

## Verification (Milestone M0 exit criteria)
1. `cd ~/Code/ccrun && dotnet build` → succeeds, no errors.
2. `dotnet test` → all tests pass (green).
3. `dotnet run --project src/CCRun` → prints usage message, exits non-zero (no args).
4. `test -f alpine-rootfs/ALPINE_FS_ROOT && test -x alpine-rootfs/bin/busybox` → both present.
5. `git status` → `alpine-rootfs/`, `bin/`, `obj/` are ignored; source files are tracked.
6. (Optional) `git add -A && git commit` a clean initial commit — only if the user wants it.

## Notes / deferrals
- No P/Invoke, namespaces, or cgroups in this phase — csproj is pre-flagged
  (`AllowUnsafeBlocks`) only so later phases don't re-edit it.
- Self-contained single-file publish (NFR-1) is documented in README but not run
  as part of Phase 0.
