# Issue #149 — OpenGL profile emits `isnan()` into versionless GLSL, rejected on macOS

**Status: FIXED (2026-07-23).** Merged to `main` in PR #153. GitHub issue #149 can be closed.

Reported by **Apostolique (Jean-David Moisan)** — GitHub issue
[#149](https://github.com/kaltinril/ShadowDusk/issues/149). Same reporter and shader family as
[issue #145](ISSUE-145-vulkan-vs-driven-and-legacy-sampler.md) (Apos.Shapes), a different
backend, a different bug.

## TL;DR

ShadowDusk's OpenGL profile emitted `isnan()` into GLSL that has no `#version` directive (i.e.
targets legacy GLSL 1.10/1.20). `isnan` does not exist before GLSL 1.30. Desktop NVIDIA/AMD/Intel
drivers on Windows/Linux accepted it leniently anyway, so every render-validation gate this repo
runs — all of it on those drivers — passed without ever seeing the bug. Apple's strict GLSL
compiler rejected it outright, so **every ShadowDusk-compiled GL shader that used `min`/`max`/
`clamp` failed to load on macOS.** This was a real, shipping regression for a downstream
consumer: Apos.Shapes 0.7.6 shipped a ShadowDusk-compiled GL effect that was unusable on Mac
(Apostolique/Apos.Shapes#34).

## Root cause

DXC compiles HLSL `min`/`max`/`clamp` to the **NaN-aware** SPIR-V ops `NMin`/`NMax`/`NClamp`
(HLSL semantics: if either operand is NaN, return the *other* operand). SPIRV-Cross's stock GLSL
lowering for those ops is a ternary that calls `isnan()` to preserve that semantic:

```glsl
isnan(a) ? b : (isnan(b) ? a : max(a, b))
```

`isnan` is a GLSL 1.30+ builtin. ShadowDusk's OpenGL profile deliberately emits **versionless**
GLSL — no `#version` directive — because that is what the MonoGame GL runtime's legacy
(MojoShader-era) shader path expects, and it is otherwise the correct choice (targeting `#version
130`+ is not a fix: MonoGame's GL loader and the legacy contexts it targets, including macOS's
legacy GL 2.1 / GLSL 1.20 ceiling, do not accept a versioned shader here). The two constraints
collided: `isnan()` requires 1.30+, and this profile's GLSL cannot declare 1.30+.

Concretely: `SpirvCrossGlslTranspiler` (`src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs`) has
SPIRV-Cross emit `#version 140` GLSL (valid, `isnan` legal there), but
`MonoGameGlslRewriter`'s Rule 1 unconditionally strips that `#version` line afterward to match
`mgfxc`'s legacy dialect — so the `isnan()` calls SPIRV-Cross already emitted survive into GLSL
that can no longer declare the version that justified them.

## Evidence

Confirmed independently on 2026-07-23 while closing
[Phase 51 A3](../PHASE-51-consolidated-remainder-backlog.md) (the Apos.Shapes GL render-proof),
not just from the issue report. ShadowDusk's own GL candidate for
`tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes.fx` — the exact fixture A3's
render-proof uses — was inspected directly, pre-fix:

```
$ grep -a -c "isnan(" <candidate .mgfx>
28
$ grep -a -o "#version[^\\]*" <candidate .mgfx>
(no output — no #version directive anywhere)
```

The real `mgfxc` OpenGL golden for the same file has **zero** `isnan` occurrences and, like all
`mgfxc` GL output, also no `#version` directive — so `mgfxc`'s own translation of the same
`min`/`max`/`clamp` calls in this shader never hit the constraint at all (MojoShader's D3D9
pipeline never produces NaN-aware ops in the first place; the bug was specific to DXC's NaN-aware
SPIR-V codegen plus SPIRV-Cross's stock GLSL lowering for it).

This never invalidated the A3 render-proof (`validation/VsDriven -- apos`, maxd 2/255) — that
pixel-diff was real and ran to completion on this repo's established Windows/Linux GL evidence
ladder. It simply could not see this bug: the desktop drivers every `validation/*` GL gate runs
on tolerate `isnan()` in versionless GLSL, so the shader loaded and rendered correctly there
regardless. **Not caused by A3's work and not specific to Apos.Shapes** — this was pre-existing in
the GL backend's NaN-lowering and latent for any GL shader using `min`/`max`/`clamp` (most
shaders); it was simply never noticed because no CI runner or dev machine in this project's GL
evidence ladder uses a strict GLSL compiler.

## Fix implemented (2026-07-23)

Set SPIRV-Cross's `RELAX_NAN_CHECKS` compiler option (`SPVC_COMPILER_OPTION_RELAX_NAN_CHECKS`,
value `78 | SPVC_COMPILER_OPTION_COMMON_BIT` = `0x100004E`, confirmed against upstream
KhronosGroup/SPIRV-Cross `spirv_cross_c.h`) to `true`, unconditionally, in
`SpirvCrossGlslTranspiler` — the single shared SPIR-V→GLSL leg for the entire OpenGL profile.
This makes SPIRV-Cross emit plain `min`/`max`/`clamp` instead of the `isnan()`-ternary form, for
every GL compile, with no flag: matching `mgfxc`'s own output exactly (its D3D9-era pipeline
never produces NaN-aware ops), so this is the *faithful* choice, not a relaxation of correctness
— a real shader never legitimately depends on `min`/`max`/`clamp`'s NaN tie-break, so the two
forms are behaviorally identical here.

**Verified:**
- `apos-shapes.fx` (the Phase 51 A3 fixture): 28 `isnan(` occurrences → **0**, `#version`
  presence unchanged (still absent, correct). A3's render-proof still passes at maxd 2/255
  (unchanged — this is a lowering-strategy change, not a math change, so pixels don't move).
- `apos-shapes-sm6.fx` (the DX/Vulkan fixture, also affected): 147 `isnan(` occurrences → **0**.
- Full `dotnet test ShadowDusk.slnx -c Release`: 2220/2220 green, **zero byte changes** in
  `tests/fixtures/golden/byte-identity/manifest.json` — no existing corpus fixture had actually
  triggered DXC's NaN-aware lowering before, so this is a surgical, zero-regression fix.
- `validation/VsDriven` (both the `vs` rig and `-- apos`), `StateFidelity`, `CbufferModel`,
  `ReservedWordGl` all still green on this machine's real DesktopGL device.
  (`TextureBreadthValidation`'s pre-existing cube-face-binding flake reproduces identically on
  `main` before this change — unrelated, not touched by this fix.)
- Permanent regression test:
  `ThirdPartyShaderCorpusTests.AposShapes_OpenGl_EmitsNoIsnan_Issue149`
  (`tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs`) compiles the real
  vendored `apos-shapes.fx` for OpenGL and asserts zero `isnan(` in the emitted GLSL.

## Files touched

- `src/ShadowDusk.GLSL/Interop/SpvcNative.cs` — added the `RelaxNanChecks` option constant.
- `src/ShadowDusk.GLSL/SpirvCrossGlslTranspiler.cs` — sets it to `true` unconditionally.
- `tests/ShadowDusk.Integration.Tests/ThirdPartyShaderCorpusTests.cs` — the regression test.

## Cross-references

- `docs/validation-matrix.md` §7 — the issue row, updated to fixed.
- `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md` — the same evidence, in context of
  the Apos.Shapes render-proof it was found alongside.
- `CHANGELOG.md` `[Unreleased]` — moved from "Known issues" to "Fixed".
- PRs #151 (the A3 GL render-proof that surfaced this), #152 (initial docs-only tracking, since
  folded into this doc), #153 (the fix).
