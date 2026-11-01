# Phase 69 — MonoGame 3.8.6 support & validation matrix

**Track:** Forward-compatibility (newer versions, seamless).
**Status:** ✅ **Done (2026-10-31).** Opened and closed the same day, after MonoGame **3.8.6** shipped
stable on 2026-10-08 (nuget.org) and the repo had no record of it. One open follow-up is registered
outside this doc (DesktopGL4 and non-square matrices, §5).

**Outcome in one line:** the unchanged output (MGFX v10 by default, v11 for DirectX 12 and Vulkan) is
render-proven on MonoGame 3.8.6 on every runtime, plus the new native OpenGL 4.1 runtime
(`DesktopGL4`), and the sweep found and fixed **one consumer-visible break**: the content importer
threw `MissingMethodException` inside every 3.8.6 content build.

**What the sweep found:**
- **A real break in a shipped package** (§3.1). MonoGame 3.8.6 removed
  `ContentIdentity(string, string)`. `ShadowDuskEffectImporter`, compiled against the 3.8.2.1105
  floor, called it, so both `ShadowDusk.MgcbPlugin` (through `dotnet mgcb` 3.8.6) and
  `ShadowDusk.ContentPipeline` (through a 3.8.6 Content Builder) failed every `.fx`. Fixed, and a
  `dotnet test` check now resolves every MonoGame member the plugin calls against every supported
  content-pipeline release, so the next removed overload fails a PR instead of a consumer's build.
- **DesktopGL4 cannot compile a GLSL non-square matrix** (§3.4). It compiles ShadowDusk's
  version-less GLSL in a core profile, where `mat3x4`/`mat4x3` need `#version 120`; a matrix truncated
  by HLSL (`mul(float3, float4x4)`) reaches the GLSL as one. The process aborts. Open, owner decision
  (it changes OpenGL bytes).
- **The XNB gate used an absolute content root**, which MonoGame's native runtimes refuse. It now loads
  from a relative root beside the executable, as a game does.

> The product's compat promise is the output FORMAT, not a MonoGame version (CLAUDE.md → *Standing
> owner directives*). No default changed: MGFX v10 stays the OpenGL/DirectX 11 default, the
> `Directory.Packages.props` MonoGame pins stay, and the only `src/` change is the one-line importer fix.

Research (MonoGame source diff `v3.8.5`..`v3.8.6`, read from the tags):
[`PHASE-69-appendix/monogame-3.8.6-source-diff.md`](PHASE-69-appendix/monogame-3.8.6-source-diff.md).

---

## 1. What changed in MonoGame 3.8.6 that matters here

From the appendix, verified in source unless it says otherwise:

- **Effect loader unchanged.** `Effect.cs` still accepts MGFX `[10, 11]`; the DirectX 11, DirectX 12
  (`0xB00B00` wrapper) and Vulkan (`SlotOffset` 32) containers and profile bytes are unchanged.
- **`mgfxc` 3.8.6 still writes MGFX v11**, with no flag for v10, and bundles DXC 1.9.2607.1. The
  golden oracle stays pinned at `dotnet-mgcb` 3.8.4.1 (`docs/validation-matrix.md` §6.1).
- **Content pipeline nullable cleanup (MonoGame PR #9050)** removed public constructors:
  `ContentIdentity()`, `(string)`, `(string, string)` and `ContentImporterAttribute(string)`. The first
  three broke ShadowDusk; the attribute breaks only single-extension third-party importers.
- **`TargetPlatform` gained `Switch2` at the end** (append-only); the name-keyed `MgcbPlatformMap` refuses
  it like any unsupported platform. XNB adds platform byte `'U'`; nothing else in the reader changed.
- **New `DesktopGL4` target** (MonoGame PR #9491): a native C++ OpenGL 4.1-core runtime
  (`MonoGame.Framework.Native` + `MonoGame.Runtime.<OS>.DesktopGL4`) that loads the classic
  OpenGL-profile `.mgfx` but binds by GLSL names (`vs_v{i}`, `{vs,ps}_s{n}`, `{vs,ps}_uniforms_vec4`)
  and aborts the process on a mismatch.
- **Managed DesktopGL** now masks MRT draw buffers by scanning the GLSL for `gl_FragColor` /
  `gl_FragData[n]` (#9509).
- **DX12/Vulkan runtime fixes** (constant-buffer rebinding, render-target barriers, vertex-texture
  rebinding); no format change.
- **Content Builder template** moved to `MonoGame.Tool.Dxc` 1.9.2607.*.

## 2. Scope

**In scope:** render-prove the unchanged output on 3.8.6 on every MonoGame runtime ShadowDusk targets;
prove the delivery shapes (MGCB plugin, Content Builder library, direct `.xnb`) on 3.8.6; the new
DesktopGL4 runtime; make the "newer MonoGame release" sweep repeatable; fix what breaks.

**Out of scope:** changing the default output, the MonoGame pins, or the `mgfxc` oracle pin; Android
on-device proof on 3.8.6 (build-verified only, §4); Linux/Mesa DesktopGL4 (no CI driver yet, §5).

## 3. Areas

### 3.1 The content importer break — fixed

`new ContentIdentity(fullPath, "ShadowDusk")` → `new ContentIdentity(fullPath, "ShadowDusk", null)`.
The three-argument constructor exists on every release from 3.8.2.1105 to 3.8.6.

**Guard:** `tests/ShadowDusk.Integration.Tests/MgcbPlugin/ContentPipelineApiBindingTests.cs` loads each
supported `MonoGame.Framework.Content.Pipeline` release (3.8.2.1105, 3.8.4.1, 3.8.5, 3.8.5.1, 3.8.6;
PackageDownloads of 0.5 to 1.2 MB) into its own collectible load context with a second copy of the
plugin and resolves every member reference. Mutation-checked: with the old call it fails on 3.8.6 only,
naming `ContentIdentity..ctor(System.String, System.String)`; with the fix 6/6 pass.

**End to end:** the MGCB plugin gate (`validation/MgcbPlugin`) gained a `dotnet-mgcb` 3.8.6 arm (the same
eight cases as the 3.8.5 arm). Before the fix 8/8 failed with the `MissingMethodException`; after it,
23/23 pass across `dotnet-mgcb` 3.8.4.1, 3.8.5 and 3.8.6. Owner decision 2026-10-31: keep every MGCB
version as its own arm (each `dotnet-mgcb` is about 700 MB in the NuGet cache).

### 3.2 The repeatable newer-release sweep

`validation/MonoGameVersion.targets`, imported at the end of each driver, lets any driver build against
another MonoGame release with `-p:MonoGameVersion=<v>`:
- every MonoGame runtime package moves to that version (`VersionOverride` under central package
  management, `Version` otherwise), with its own lock file `packages.<v>.lock.json`;
- `validation/SharedMonoGameVersion/MonoGameVersionGuard.cs`, a module initializer, exits 3 unless the
  `MonoGame.Framework` that loaded is that release. Negative control: an override that silently did not
  apply fails with the loaded version named.

`validation/run-windows-render-gates.ps1` runs a **newer-MonoGame block** (`$newerMonoGame = @('3.8.6')`)
beside the pinned arms, never instead of them, and `-NewerMonoGameOnly` runs just that block.
`tools/check-lock-files.sh` restores the versioned lock files.

### 3.3 Results on MonoGame 3.8.6 (Windows, RTX 3080, 2026-10-31)

| Runtime | Evidence on 3.8.6 |
|---|---|
| **DesktopGL** (classic) | ForwardCompat: v10 on **9 releases, 3.8.1.263 → 3.8.6**, 90 renders, maxd 0 vs the floor, within 1/255 of the `mgfxc` goldens. Initializer defaults, KNI EffectTest, XNB `Content.Load`, Apos.Shapes, MGFX v11, VS rig, vertex semantics, MRT, sampler pairs and sampler register order all green. |
| **WindowsDX** (DX11) | DX corpus on both DXBC backends vs `mgfxc`, VS rig, Apos gallery, resource arrays, modern features, initializer defaults, non-square matrix `Set`, KNI EffectTest, XNB `Content.Load`: all green. |
| **WindowsDX12** | Corpus maxd 0 vs the `mgfxc` `DirectX_12` golden (10/10), VS rig, Apos gallery, texture array, legacy lod, initializer defaults, KNI EffectTest, Slang full corpus: all green. |
| **DesktopVK** | Corpus, VS rig, Apos gallery, `texarr`, legacy lod, register slots, initializer defaults, non-square matrix `Set`, KNI EffectTest, Slang full corpus: all green. |
| **DesktopGL4** (new) | See §3.4. |
| **MGCB plugin** | 23/23 across `dotnet-mgcb` 3.8.4.1, 3.8.5, 3.8.6 (after the §3.1 fix). |
| **Content Builder** | 11/11 on the real 3.8.6 Content Builder (envelope == stock, payload == CLI, `Content.Load<Effect>` render pixel-identical); 11/11 on the default 3.8.5.1 arm. |

Every driver run printed the guard line confirming `MonoGame.Framework 3.8.6+c610a529`.

### 3.4 DesktopGL4 — the new native OpenGL runtime

`-p:MonoGameDesktopGL4=true` (with `-p:MonoGameVersion=3.8.6`) swaps a DesktopGL driver's
`MonoGame.Framework.DesktopGL` for `MonoGame.Framework.Native` + `MonoGame.Runtime.<OS>.DesktopGL4`;
the guard also requires the netstandard2.1 native framework assembly. The drivers are unchanged,
because DesktopGL4 loads the same OpenGL `.mgfx`.

**Green on DesktopGL4:** the 10-shader corpus vs the `mgfxc` OpenGL goldens (8 at maxd 0, 2 at 1/255,
as on classic DesktopGL) and **pixel-identical (maxd 0) to classic DesktopGL 3.8.6** on all 10; the VS
rig maxd 0 (render target and backbuffer); Apos.Shapes maxd 2 (its usual tolerance); MRT
(`DeferredSpriteMrtGl`); sampler pairs; sampler register order; initializer defaults; XNB
`Content.Load`; MGFX v11.

**Open gap — non-square matrices.** DesktopGL4 compiles the GLSL verbatim in a core-profile context.
ShadowDusk's GLSL carries no `#version` and is read as GLSL 1.10, which has no non-square matrix types;
NVIDIA reports *"global type mat4x3 requires #version 120 or later"* and MonoGame's `MGGL_FAIL` aborts
the process. A truncating `mul(float3, float4x4)` is the usual source (KNI's `DeferredBasicEffect.fx`
emits `mat3x4 _60 = mat3x4(mat4(...))`). Corpus scan: 1 of 158 OpenGL corpus outputs contains a
non-square type; no other post-1.10 GLSL feature appears anywhere in the corpus. `mgfxc`'s output
avoids the types, so it is unaffected. `validation/VertexSemanticsGl` and `validation/KniEffectTestGl`
abort on DesktopGL4 and stay out of its arm. Fixing it is a GLSL rewrite rule that changes the OpenGL
bytes of every shader containing such a matrix (classic consumers too), so it is an owner decision and
its own fully gated change, not part of this phase. Registered in `docs/validation-matrix.md` §7.

## 4. Android

`validation/AndroidGl` builds and signs an APK against `MonoGame.Framework.Android` 3.8.6 with
`-p:MonoGameVersion=3.8.6` and no source change (BUILD-verified only; the on-device proof was not
repeated, as for 3.8.5). The first attempt failed at the APK zip step with `XABBA7000` "Renaming
temporary file failed", the same transient Windows file lock Phase 52 F2 recorded; the retry built clean.

## 5. Follow-ups registered elsewhere

- DesktopGL4 + GLSL non-square matrices: `docs/validation-matrix.md` §7, `project_facts.md`.
- DesktopGL4 on Linux/Mesa (core profile, version-less GLSL): unmeasured, §7.
- The newer-MonoGame arms run in the Windows render gate (the pre-merge/pre-release bar), not in CI: §7.
- Upstream: MonoGame PR #9050 removed public constructors in a patch release; worth an issue upstream.

## 6. Definition of Done

- [x] Unchanged output render-proven on 3.8.6 on DesktopGL, WindowsDX, WindowsDX12, DesktopVK.
- [x] ForwardCompat matrix extended to 3.8.6 (and 3.8.5.1), README table recorded.
- [x] Delivery shapes on 3.8.6: MGCB plugin, Content Builder, direct `.xnb`.
- [x] The importer break fixed, with a `dotnet test` regression check that fails without the fix.
- [x] DesktopGL4 measured; green where green, the gap registered.
- [x] The sweep is repeatable for the next release (`MonoGameVersion.targets`, the gate block, the
      rules in `project_rules.md`).
- [x] Docs per the support-surface rule.
