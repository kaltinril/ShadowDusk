# Issue #202 reproduction fixture — Apos.Shapes `apos-shapes.fx`

This directory holds the exact file behind GitHub issue
[#202](https://github.com/kaltinril/ShadowDusk/issues/202) ("Line numbers reported in errors are
outside of the range of the file"): the **current** upstream `apos-shapes.fx` from
[Apostolique/Apos.Shapes](https://github.com/Apostolique/Apos.Shapes), `Source/Content/apos-shapes.fx`
at commit `3517579be3d087d189db3dff81b4cc109262324e` (2026-08-02), 3235 lines. The 523-line
`tests/fixtures/shaders/third-party/Apos.Shapes/apos-shapes.fx` is an older upstream (`3fb73b8`)
and does not reproduce the reporter's numbers (it drifts by one line, on a different construct).

**License:** MIT, Copyright (c) 2021 Jean-David Moisan — `LICENSE` here is the upstream file
fetched verbatim from the same commit. The shader code is **unmodified**; the only change is the
provenance comment block prepended at the top (17 lines, so upstream line *n* is line *n + 17*
here).

## Why it lives outside `tests/fixtures/shaders`

Three integration suites enumerate `tests/fixtures/shaders/**/*.fx` and compile every member for
OpenGL, Vulkan, and the Phase 41 structural census. This file is a **diagnostic-location**
fixture, not a corpus member: it exists so `FnaDiagnosticLocationTests` can assert that the
FNA-target diagnostic lands on line 3026 (`if (isGlyph ? glyphFade <= 0.0 : d >= aaSize * (1.0 -
aaBias))`, upstream 3009), column 29 — where vkd3d 1.17 itself reports 3590 — and nothing else
should pick it up.

## What it exercises

Dozens of `atan2` / `sincos` / `smoothstep` / `asin` calls ahead of an int-typed ternary. Each of
those calls makes vkd3d 1.17 lex an internal HLSL template against the user file's line counter
(+20 per `atan2`, +11 per `asin`, +4 per `sincos`/`smoothstep`), so the diagnostic on line 3009
came out as 3586/3590. The shader itself cannot become an FNA effect under either compiler:
`fxc /T fx_2_0` rejects it too (`X3506` as written, `X4505` maximum temp register index with the
`ps_3_0` arm forced). See `plan/DONE/ISSUE-202-fna-error-line-numbers.md`.
