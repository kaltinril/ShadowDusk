---
name: docs-maintenance
description: "Edit/maintain ShadowDusk's published documentation (the DocFX site). Trigger on doc edits, fixing docs, updating the site, or questions about where doc content lives."
---

# Docs Maintenance

## Scope: published = the DocFX site
- In scope: `docfx/**/*.md` + the 5 files it pulls in via `[!INCLUDE]`:
  - `docs/HOWTO-WASM-KNI.md`
  - `docs/glsl-uniform-naming.md`
  - `docs/references/compilation-pipeline.md`
  - `docs/test-shader-corpus.md`
  - `samples/ShaderFiddle.Web/README.md`
- Edit the **source** file, not the `docfx/` wrapper that includes it.
- Out of scope, never touch: `docs/research.md`, `plan/**`. Internal only.

## Hard rules
- **Phase numbers**: drop for *completed* work (bookkeeping); keep for *planned/future* work (reader's tracking handle).
- **Write as if published**: assume nuget.org packages exist. No time-relative framing ("not on nuget yet", "until first release") — it flips to wrong on publish. Frame source-build steps as evergreen "consuming an unreleased build".
- **No hardcoded versions**: use floating `*` or reference `Directory.Build.props <Version>`.
- **Cross-links inside `[!INCLUDE]`'d files**: use inline code paths (`` `docs/foo.md` ``), NOT markdown links — included files aren't site pages, links emit `InvalidFileLink`.
- Present tense, current truth. History lives in `plan/` only.

## Verify before done
- `dotnet tool restore && dotnet docfx docfx.json` → must be **0 warnings / 0 errors**.

## Audit greps (catch regressions)
- `Phase[ -]?\d|backlog|rung \d` in published files → only allowed on planned/future items.
- `0\.\d\.\d` hardcoded versions in published files.
