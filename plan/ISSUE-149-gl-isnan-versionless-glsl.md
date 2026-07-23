# Issue #149 — OpenGL profile emits `isnan()` into versionless GLSL, rejected on macOS

**Status: OPEN, confirmed, not yet fixed.** Docs-only tracking so far (PRs #151/#152); no
source change has been made. This doc will move to `plan/DONE/` once a fix lands and is
render-proven, matching how `ISSUE-70-gl-vertex-fidelity.md` and
`ISSUE-145-vulkan-vs-driven-and-legacy-sampler.md` were handled.

Reported by **Apostolique (Jean-David Moisan)** — GitHub issue
[#149](https://github.com/kaltinril/ShadowDusk/issues/149). Same reporter and shader family as
[issue #145](DONE/ISSUE-145-vulkan-vs-driven-and-legacy-sampler.md) (Apos.Shapes), a different
backend, a different bug.

## TL;DR

ShadowDusk's OpenGL profile emits `isnan()` into GLSL that has no `#version` directive (i.e.
targets legacy GLSL 1.10/1.20). `isnan` does not exist before GLSL 1.30. Desktop NVIDIA/AMD/Intel
drivers on Windows/Linux accept it leniently anyway, so every render-validation gate this repo
runs — all of it on those drivers — passes without ever seeing the bug. Apple's strict GLSL
compiler rejects it outright, so **every ShadowDusk-compiled GL shader that uses `min`/`max`/
`clamp` fails to load on macOS.** This is a real, currently-shipping regression for a downstream
consumer: Apos.Shapes 0.7.6 ships a ShadowDusk-compiled GL effect that is unusable on Mac
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
collide: `isnan()` requires 1.30+, and this profile's GLSL cannot declare 1.30+.

## Evidence

Confirmed independently on 2026-07-23 while closing [Phase 51 A3](PHASE-51-consolidated-remainder-backlog.md)
(the Apos.Shapes GL render-proof), not just from the issue report. ShadowDusk's own GL
candidate for `tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes.fx` — the exact
fixture A3's render-proof uses — was inspected directly:

```
$ grep -a -c "isnan(" <candidate .mgfx>
28
$ grep -a -o "#version[^\\]*" <candidate .mgfx>
(no output — no #version directive anywhere)
```

The real `mgfxc` OpenGL golden for the same file has **zero** `isnan` occurrences and, like all
`mgfxc` GL output, also no `#version` directive — so `mgfxc`'s own translation of the same
`min`/`max`/`clamp` calls in this shader does not hit the constraint at all (MojoShader's D3D9
pipeline never produces NaN-aware ops in the first place; the bug is specific to DXC's NaN-aware
SPIR-V codegen plus SPIRV-Cross's stock GLSL lowering for it).

This does **not** invalidate the A3 render-proof (`validation/VsDriven -- apos`, maxd 2/255) —
that pixel-diff is real and ran to completion on this repo's established Windows/Linux GL
evidence ladder. It simply cannot see this bug: the desktop drivers every `validation/*` GL gate
runs on tolerate `isnan()` in versionless GLSL, so the shader loads and renders correctly there
regardless. **Not caused by A3's work and not specific to Apos.Shapes** — this is pre-existing in
the GL backend's NaN-lowering and latent for any GL shader that uses `min`/`max`/`clamp` (which is
most shaders); it was simply never noticed because no CI runner or dev machine in this project's
GL evidence ladder uses a strict GLSL compiler.

## Suggested fix (from the issue, not yet implemented)

Lower `NMin`/`NMax`/`NClamp` to plain `min`/`max`/`clamp` (and NaN-aware comparisons to ordinary
ones) for the OpenGL profile, as the **default** — not an opt-in flag. Per the seamless-by-default
rule (`CLAUDE.md`), a flag is not an acceptable fix path here, and it does not need to be one:
`mgfxc`'s own OpenGL output has zero `isnan` occurrences for this exact shader, so relaxing to
plain `min`/`max`/`clamp` is not a compromise — it is what "identical to `mgfxc`" already requires.
An `x != x`-style polyfill was considered and rejected (per the issue): that is at the mercy of
driver fast-math flags, so plain relaxation is the safer choice for a graphics profile.

## Acceptance criteria

- `ShadowDuskCLI <fx> out.mgfx /Profile:OpenGL` produces GLSL with **zero** `isnan` occurrences
  (and introduces no other GLSL 1.30+-only builtin as a side effect of the same lowering change)
  for any shader that exercises `min`/`max`/`clamp` on the OpenGL profile.
- Output still pixel-verifies against the existing GL render gates (`validation/VsDriven`,
  `validation/Baseline`/`Candidate`, the CI `validation-render.yml` in-process gates) — no
  regression in any already-passing cell.
- A regression fixture/test (a `min`/`max`/`clamp`-using GL fixture, asserting zero `isnan` in the
  emitted GLSL) pins it so it cannot silently return, per the "every fixed bug earns a permanent
  regression fixture" rule.
- The GL portability lint (`GlslPortabilityAnalyzer`, `SD0400`–`SD0402`) is the natural home for a
  companion warning if the fix cannot be made unconditional for some reason — but the *default*
  output must not need one.

## Cross-references

- `docs/validation-matrix.md` §7 — gap row for this issue.
- `tests/fixtures/shaders/third-party/Apos.Shapes/NOTICE.md` — the same evidence, in context of
  the Apos.Shapes render-proof it was found alongside.
- `CHANGELOG.md` `[Unreleased]` — "Known issues" entry.
- PRs #151 (the A3 GL render-proof that surfaced this) and #152 (initial docs-only tracking,
  since folded into this standalone doc).
