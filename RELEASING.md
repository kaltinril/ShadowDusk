# Releasing ShadowDusk

This is the human runbook for cutting a ShadowDusk release. The `/release` skill
(`.claude/skills/release/SKILL.md`) automates every step below; this document is the
ground truth it follows, and the fallback when you cut a release by hand.

A release publishes **all seven** `ShadowDusk.*` NuGet packages plus the `ShadowDuskCLI` `dotnet tool`
to nuget.org, and attaches self-contained CLI binaries for each RID to a GitHub Release.

| Package | What it is |
|---|---|
| `ShadowDusk.Core` | Core types, contracts, MGFX writer, SPIR-V reflection |
| `ShadowDusk.HLSL` | FX9 pre-parser, DXC integration, vkd3d-shader / `d3dcompiler_47` DXBC backends |
| `ShadowDusk.GLSL` | SPIR-V → GLSL via SPIRV-Cross + MojoShader-dialect rewriter |
| `ShadowDusk.ShaderToy` | Standalone pure-managed ShaderToy/GLSL → `.fx` converter (optional; not in the `Compiler` graph) |
| `ShadowDusk.Compiler` | The consumer-facing product library (`EffectCompiler : IShaderCompiler`) |
| `ShadowDusk.Cli` | The `ShadowDuskCLI` `dotnet tool` |
| `ShadowDusk.Wasm` | The `net8.0-browser` in-browser compiler |

---

## Prerequisites (one-time)

1. **`NUGET_API_KEY` repository secret.** The `release.yml` workflow pushes packages with
   this key. Set it under **Settings → Secrets and variables → Actions → New repository
   secret**. It must be an [nuget.org API key](https://www.nuget.org/account/apikeys) scoped
   to **Push** for the `ShadowDusk.*` package IDs (a glob-scoped key is simplest).

2. **nuget.org owner rights on all seven package IDs.** You must be an owner (or have push
   rights) of every ID — `ShadowDusk.Core`, `ShadowDusk.HLSL`, `ShadowDusk.GLSL`,
   `ShadowDusk.ShaderToy`, `ShadowDusk.Compiler`, `ShadowDusk.Cli`, `ShadowDusk.Wasm`. The
   **first** publish of each ID reserves the name to your account; confirm all seven are
   reserved before relying on the automated push (an unreserved ID makes the `dotnet nuget
   push` for that package fail). `ShadowDusk.ShaderToy` is **new in 0.9.0**, so its first
   publish reserves the ID — the glob-scoped key in step 1 already covers it.

3. **A green `main`.** CI (`ci.yml`) runs the 3-OS build + test matrix on every push/PR.
   Releases cut from `main` only after CI is green; local green is not sufficient.

4. **A green Windows render gate — RUN IT FIRST (CI structurally cannot run this).** The
   DirectX / FNA / KNI-DirectX / real-KNI-desktop-GL / **Vulkan** / browser-ANGLE rung-4 render
   proofs ("renders like `mgfxc`/`fxc` in the real engine") have no headless CI driver — Mesa
   covers the in-process OpenGL gates on the Linux lane, but there is no verified headless
   D3D/WARP path on the runners, the real-KNI SDL2.GL rigs are not wired there, DesktopVK needs
   a real Vulkan GPU, and CI's browser smoke renders on SwiftShader (blind to ANGLE-D3D11
   behavior like the issue-#136 gradient poisoning). **`release.yml` does not check any of this
   either**, so this gate is the only thing between a render regression and nuget.org. Run it on
   a Windows + GPU box **before** bumping the version — it is the longest and most likely step
   to fail, so a divergence should stop the release before any version churn, commit, PR, or CI
   time is spent:

   ```powershell
   ./validation/run-windows-render-gates.ps1              # DX corpus + DX-modern (VTF) + KNI-DX + KNI-GL desktop + KNI-GL VS-driven + ANGLE derivative probe + BOTH Vulkan gates
   ./validation/run-windows-render-gates.ps1 -IncludeFna  # also FNA fx_2_0, for an FNA-affecting release (include it when in doubt)
   ```

   The **Vulkan gates are default-ON** since issue #145 — the PS corpus plus the VS-driven
   Apos.Shapes pixel diff against the `mgfxc 3.8.5` goldens. They need a Vulkan-capable GPU;
   `-SkipVulkan` opts out and is ONLY for a box without one (`-IncludeVulkan` is still accepted
   as a no-op). If the machine is not Windows or has no GPU, say so and stop: `dotnet test`
   alone is not a basis for releasing.

   A non-zero exit means a render diverged from `mgfxc`/`fxc` — **do not release.** (The
   in-process OpenGL render gates DO run in CI via `validation-render.yml`, so they are covered
   by item 3; this item is the DX/FNA/KNI-DX/KNI-GL/Vulkan/ANGLE gap. See `CLAUDE.md` →
   "Validation render drivers are the real bar". The `/release` skill performs this as its
   step 2, before the version bump.)

---

## The version is centralized — bump ONE line

ShadowDusk's package version lives in **exactly one place**:

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <Version>0.14.1</Version>
</PropertyGroup>
```

That single `<Version>` flows to every `ShadowDusk.*` project, so `dotnet pack` stamps all
seven packages (and their inter-package dependency ranges) at the same version.

> **Do NOT edit the seven `.csproj` files.** They no longer carry a per-project version.
> Editing one csproj and not the others is exactly the desync this centralization removes.
> (The `<PackageVersion Include=… />` *items* in `Directory.Packages.props` are unrelated —
> those pin third-party dependency versions under Central Package Management. Leave them
> alone.)

To bump for a release, change that one line (e.g. `0.14.0` → `0.14.1`), update
`CHANGELOG.md` (move `[Unreleased]` into a dated `[0.14.1]` section, leave a fresh empty
`[Unreleased]`), update the version examples in this file, commit, and merge to `main` via PR.

---

## Triggering a release

`release.yml` is **dispatch-only by design** — publishing is always a deliberate,
human-run action. Pushing a `v<version>` tag triggers **nothing** (a tag is only a
marker; the workflow creates and pushes it itself on a successful release).

### Manual dispatch (the only trigger)

After the version-bump PR is merged to `main`: **Actions → Release → Run workflow**, and
enter the `version` input (e.g. `0.14.1`, no leading `v`). On dispatch the workflow also
creates and pushes the matching `v<version>` tag so the GitHub Release anchors to a tag.

### The `validate` guard (input ↔ version)

Before anything is packed or pushed, the `validate` job compares the dispatch `version`
input (stripping a leading `v`) against `Directory.Build.props` `<Version>`. **If they
disagree, the workflow fails fast and publishes nothing.** Dispatching `0.12.1` against a
`Directory.Build.props` that still says `0.7.0` is rejected — merge the version-bump PR
first (the `/release` skill does this for you).

---

## What the workflow does

1. **`validate`** — resolve + verify the version against `Directory.Build.props`.
2. **build + test** on the 3-OS matrix (Linux / macOS / Windows).
3. **publish** self-contained `ShadowDuskCLI` binaries per RID (`win-x64`, `linux-x64`, `osx-x64`,
   `osx-arm64`) and archive them.
4. **pack + push** all seven `ShadowDusk.*` packages (`.nupkg` + `.snupkg` symbols) to
   nuget.org at the validated version, with `--skip-duplicate` (re-running a release no-ops
   on already-published versions). `ShadowDusk.Wasm` is packed in the WASM job (it needs the
   `wasm-tools` workload + restored `dxcompiler.wasm`).
5. **GitHub Release** — create the release for the `v<version>` tag with the four CLI
   archives + the `.nupkg`/`.snupkg` set attached.

---

## Verify after release

1. **nuget.org shows all seven at the new version.** Check each of
   `ShadowDusk.{Core,HLSL,GLSL,ShaderToy,Compiler,Cli,Wasm}` is listed at `<version>` (indexing
   can take a few minutes after push).
2. **The `ShadowDuskCLI` tool installs and runs:**

   ```bash
   dotnet tool install -g ShadowDusk.Cli --version 0.14.1
   ShadowDuskCLI --help
   ```

   It should print usage and exit with the `mgfxc`-compatible exit code.
3. **The consumer (GL) self-contained path works on a clean machine:**

   ```bash
   dotnet add package ShadowDusk.Compiler --version 0.14.1
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

- **`validate` fails** — the dispatch input version doesn't match `Directory.Build.props`.
  Bump the prop (via PR), then re-dispatch.
- **A package push fails on an unreserved ID** — reserve it by a manual first push, or fix
  ownership on nuget.org, then re-run the workflow (idempotent via `--skip-duplicate`).
- **Re-running a release** is safe: already-published versions are skipped
  (`--skip-duplicate`), so a partial failure can be retried by re-dispatching.
