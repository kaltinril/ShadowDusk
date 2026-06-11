# Releasing ShadowDusk

This is the human runbook for cutting a ShadowDusk release. The `/release` skill
(`.claude/skills/release/SKILL.md`) automates every step below; this document is the
ground truth it follows, and the fallback when you cut a release by hand.

A release publishes **all six** `ShadowDusk.*` NuGet packages plus the `ShadowDuskCLI` `dotnet tool`
to nuget.org, and attaches self-contained CLI binaries for each RID to a GitHub Release.

| Package | What it is |
|---|---|
| `ShadowDusk.Core` | Core types, contracts, MGFX writer, SPIR-V reflection |
| `ShadowDusk.HLSL` | FX9 pre-parser, DXC integration, vkd3d-shader / `d3dcompiler_47` DXBC backends |
| `ShadowDusk.GLSL` | SPIR-V → GLSL via SPIRV-Cross + MojoShader-dialect rewriter |
| `ShadowDusk.Compiler` | The consumer-facing product library (`EffectCompiler : IShaderCompiler`) |
| `ShadowDusk.Cli` | The `ShadowDuskCLI` `dotnet tool` |
| `ShadowDusk.Wasm` | The `net8.0-browser` in-browser compiler |

---

## Prerequisites (one-time)

1. **`NUGET_API_KEY` repository secret.** The `release.yml` workflow pushes packages with
   this key. Set it under **Settings → Secrets and variables → Actions → New repository
   secret**. It must be an [nuget.org API key](https://www.nuget.org/account/apikeys) scoped
   to **Push** for the `ShadowDusk.*` package IDs (a glob-scoped key is simplest).

2. **nuget.org owner rights on all six package IDs.** You must be an owner (or have push
   rights) of every ID — `ShadowDusk.Core`, `ShadowDusk.HLSL`, `ShadowDusk.GLSL`,
   `ShadowDusk.Compiler`, `ShadowDusk.Cli`, `ShadowDusk.Wasm`. The **first** publish of each
   ID reserves the name to your account; confirm all six are reserved before relying on the
   automated push (an unreserved ID makes the `dotnet nuget push` for that package fail).

3. **A green `main`.** CI (`ci.yml`) runs the 3-OS build + test matrix on every push/PR.
   Releases cut from `main` only after CI is green; local green is not sufficient.

---

## The version is centralized — bump ONE line

ShadowDusk's package version lives in **exactly one place**:

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <Version>0.4.0</Version>
</PropertyGroup>
```

That single `<Version>` flows to every `ShadowDusk.*` project, so `dotnet pack` stamps all
six packages (and their inter-package dependency ranges) at the same version.

> **Do NOT edit the six `.csproj` files.** They no longer carry a per-project version.
> Editing one csproj and not the others is exactly the desync this centralization removes.
> (The `<PackageVersion Include=… />` *items* in `Directory.Packages.props` are unrelated —
> those pin third-party dependency versions under Central Package Management. Leave them
> alone.)

To bump for a release, change that one line (e.g. `0.3.0` → `0.4.0`), update
`CHANGELOG.md` (move `[Unreleased]` into a dated `[0.4.0]` section, leave a fresh empty
`[Unreleased]`), update the version examples in this file, commit, and merge to `main` via PR.

---

## Triggering a release

`release.yml` fires on **either** of two triggers; both run the same `validate` guard first.

### Option A — push a `v<version>` tag

After the version-bump PR is merged to `main`:

```bash
git checkout main && git pull
git tag v0.4.0
git push origin v0.4.0
```

### Option B — manual dispatch

**Actions → Release → Run workflow**, and enter the `version` input (e.g. `0.4.0`, no
leading `v`). On dispatch the workflow also creates and pushes the matching `v<version>` tag
so the GitHub Release anchors to a tag.

### The `validate` guard (tag ↔ version)

Before anything is packed or pushed, the `validate` job resolves the requested version (from
the tag or the dispatch input, stripping a leading `v`) and compares it against
`Directory.Build.props` `<Version>`. **If they disagree, the workflow fails fast and
publishes nothing.** A `v0.4.0` tag against a `Directory.Build.props` that still says `0.3.0`
is rejected — merge the version-bump PR first (the `/release` skill does this for you).

---

## What the workflow does

1. **`validate`** — resolve + verify the version against `Directory.Build.props`.
2. **build + test** on the 3-OS matrix (Linux / macOS / Windows).
3. **publish** self-contained `ShadowDuskCLI` binaries per RID (`win-x64`, `linux-x64`, `osx-x64`,
   `osx-arm64`) and archive them.
4. **pack + push** all six `ShadowDusk.*` packages (`.nupkg` + `.snupkg` symbols) to
   nuget.org at the validated version, with `--skip-duplicate` (re-running a release no-ops
   on already-published versions). `ShadowDusk.Wasm` is packed in the WASM job (it needs the
   `wasm-tools` workload + restored `dxcompiler.wasm`).
5. **GitHub Release** — create the release for the `v<version>` tag with the four CLI
   archives + the `.nupkg`/`.snupkg` set attached.

---

## Verify after release

1. **nuget.org shows all six at the new version.** Check each of
   `ShadowDusk.{Core,HLSL,GLSL,Compiler,Cli,Wasm}` is listed at `<version>` (indexing can
   take a few minutes after push).
2. **The `ShadowDuskCLI` tool installs and runs:**

   ```bash
   dotnet tool install -g ShadowDusk.Cli --version 0.4.0
   ShadowDuskCLI --help
   ```

   It should print usage and exit with the `mgfxc`-compatible exit code.
3. **The consumer (GL) self-contained path works on a clean machine:**

   ```bash
   dotnet add package ShadowDusk.Compiler --version 0.4.0
   ```

   then compile a `.fx` → GL `.mgfx` in memory. This restores `Core/HLSL/GLSL` plus
   `Vortice.Dxc` and `Silk.NET.SPIRV.Cross.Native` transitively — no manual native install.
4. **The GitHub Release** exists for `v<version>` with the four self-contained CLI archives
   and the package set attached.

> **vkd3d-shader packing (DirectX `DxbcBackend.Vkd3d` + FNA fx_2_0 — Phase 39):**
> `ShadowDusk.HLSL.csproj` packs each **restored** `tools/vkd3d` binary into the NuGet as
> `runtimes/<rid>/native` (win-x64, linux-x64, osx-x64, osx-arm64). Packing is
> restore-state-dependent by design (csproj entries are `Exists(...)`-conditioned), but
> since Phase 37 C **`tools/restore.{ps1,sh}` provision all four RIDs automatically**: the
> pinned binaries are downloaded from the fixed GitHub Release tag `native-vkd3d-1.17` and
> SHA-256-verified against pins embedded in the scripts — a clean CI runner is pack-ready
> after restore. Provenance: linux/macOS binaries are built by the dispatchable
> `.github/workflows/build-vkd3d-natives.yml` from the pinned WineHQ 1.17 tarball (linux on
> ubuntu:20.04 = glibc 2.31 baseline; macOS at `MACOSX_DEPLOYMENT_TARGET=11.0`, per-arch);
> the win-x64 dll is the MSYS2 build the Phase 18/39/40 goldens were proven against
> (recipe in `tools/restore.ps1`). The LGPL-2.1 notice for the bundled binaries
> (`src/ShadowDusk.HLSL/THIRD-PARTY-NOTICES.txt`) packs into the nupkg root.
> Self-containment + Windows↔Linux byte-identity of the packed FNA path were proven
> 2026-06-09 (see `plan/DONE/PHASE-39-fna-fx2-output-target.md`).
>
> **Since Phase 40 this is ENFORCED, not advisory:** `release.yml`'s `pack-desktop` job
> fails red if the packed `ShadowDusk.HLSL` nupkg is missing any of the four vkd3d
> natives or the THIRD-PARTY-NOTICES file — mirroring the `pack-wasm` dxcompiler.wasm
> gate. A red release beats silently shipping the FNA target and `DxbcBackend.Vkd3d`
> broken for any consumer RID. If the gate trips, check that the `native-vkd3d-1.17`
> release assets are intact and the restore-step log shows four "hash OK" lines.

---

## If something goes wrong

- **`validate` fails** — the tag/input version doesn't match `Directory.Build.props`. Bump
  the prop (via PR), then re-tag or re-dispatch.
- **A package push fails on an unreserved ID** — reserve it by a manual first push, or fix
  ownership on nuget.org, then re-run the workflow (idempotent via `--skip-duplicate`).
- **Re-running a release** is safe: already-published versions are skipped, so a partial
  failure can be retried by re-pushing the tag or re-dispatching.
