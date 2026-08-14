---
name: local-test
description: "Set up and verify a local ShadowDusk build: prerequisites, native binaries, dotnet tools, build, full test suite, smoke compile, and optionally the GPU render gates and the experimental Slang toolchain. Trigger on setting up the repo, 'get me running', a first clone, verifying an environment, 'how do I test this', or a contributor reporting that something will not build or run."
---

# Local testing setup

The one command that takes a clone to a verified build is
[`tools/setup-local-testing.ps1`](../../../tools/setup-local-testing.ps1). It needs
**PowerShell 7+ (`pwsh`)**, which runs on Windows, Linux and macOS.

```bash
pwsh tools/setup-local-testing.ps1                              # build + full test suite
pwsh tools/setup-local-testing.ps1 -WithRenderGates             # + the GPU render proofs (Windows)
pwsh tools/setup-local-testing.ps1 -SkipTests                   # build only
```

It prints a PASS/WARN/FAIL line per step and exits non-zero if anything failed. Nothing
is silently skipped: a step that cannot run says why and gives the command that fixes it.

## The one thing to tell people first

**There are two halves to verifying ShadowDusk, and the script only runs both if asked.**

| Half | What it proves | Command |
|---|---|---|
| `dotnet test` | the compiler behaves | the default run |
| the **render gates** | the output actually loads and renders like `mgfxc`/`fxc` in the real engine | `-WithRenderGates` |

Only the second is the product bar (rung 4). It needs **Windows and a real GPU**, so it
cannot run in CI and it cannot run on a Mac or Linux box. If someone says "tests pass",
ask which half they ran.

## Prerequisites worth pre-empting

- **Both .NET 8 and .NET 10 SDKs.** The shipped libraries multi-target `net8.0`+`net10.0`,
  so a box with only one SDK cannot build the solution at all. This is the single most
  common first-run failure.
- **`dotnet tool restore`** — supplies the pinned `dotnet-mgcb`. The MGCB plugin gate and
  the XNB `Content.Load` gate both need it. The script does this for you.
- **`python`** — only for the render gates' image comparisons. Irrelevant otherwise.
- A **DX12-capable GPU** for the DX12 gates; `-SkipVulkan` on the gate script for a box
  with no Vulkan GPU.

## Trying the things Victor Chelaru asked for

- **`.xnb` output (issue #199) — shipped.** Name an `.xnb` output path and the CLI wraps
  it, so `Content.Load<Effect>("Foo")` works with no consumer code change:
  ```bash
  dotnet run --project src/ShadowDusk.Cli -- MyShader.fx Content/MyShader.xnb /Profile:OpenGL
  ```
  From the library it is `CompiledShader.ToXnb()`. The rung-4 proof is
  `dotnet run --project validation/XnbContentLoad -c Release`, which loads both a stock
  `mgcb` build and ours through a real `ContentManager` and pixel-compares.

- **Slang input (issue #198) — shipped.** Write entry points with Slang's
  `[shader("vertex")]` / `[shader("fragment")]` attributes (no technique block — it is
  synthesized), then:
  ```bash
  dotnet run --project src/ShadowDusk.Cli -- MyShader.slang out.mgfx /Profile:OpenGL
  ```
  **Nothing to install** — the frontend is a pure managed text transform and the body
  compiles through the same pipeline as every `.fx`, so this works on every host,
  browser included. The supported input is the **HLSL-compatible subset of Slang**;
  Slang-only features (`import`, generics, `extension`) are rejected with a named
  `SD0600`. A **17-shader corpus** lives in `tests/fixtures/shaders/slang/` — every one
  validated against the real Slang compiler by `validation/SlangCorpus` (part of the gate
  script; slangc is a downloaded-on-demand TEST oracle, never shipped), with the procedural
  subset proven pixel-identical to slangc's own HLSL emission. Library API:
  `ShadowDusk.Compiler.Slang.SlangFrontend.ConvertToFx`. See
  [`plan/PHASE-61-slang-support.md`](../../../plan/PHASE-61-slang-support.md).

- **SkiaSharp / SkSL (issue #197) — shipped (v1).** `ShadowDusk.Compiler.Sksl.SkslConverter`
  converts a pixel-only `.fx` to SkSL for `SKRuntimeEffect`. Know the shape of it: the
  converter **refuses loudly** anything SkSL cannot hold (varyings, vertex stages,
  derivatives, computed-UV sampling — `SD0610`–`SD0615`), and its default answer to a
  shader reading an interpolant is rejection with the `TreatVaryingsAsUniforms` opt-in
  named. Tests double as examples: `tests/ShadowDusk.Compiler.Tests/Sksl/`. SkiaSharp is a
  test-only dependency. See
  [`plan/PHASE-62-skiasharp-sksl-target.md`](../../../plan/PHASE-62-skiasharp-sksl-target.md).

## When someone reports a failure

Read the script's summary block first — it names the failing step and the fix. Then:

- **Build fails on a missing TFM** → the .NET 8/10 SDK gap above.
- **A native fails to load (`SD0102`, `SD0103`, `SD0211`)** → `tools/restore.ps1` (or
  `.sh`) did not supply it. Non-fatal for the shipped packages (the natives ride in via
  NuGet transitively), but the dev `tools/` copy is missing.
- **A render gate diverges** → that is a real product defect, not an environment problem.
  It means the output stopped matching the reference compiler. Do not paper over it; the
  gate exists precisely because CI cannot catch this class.
- **`dotnet-mgcb` not in the NuGet cache** → `dotnet tool restore` from the repo root.

## Related

- Full driver list and exact commands: [`docs/validation-matrix.md`](../../../docs/validation-matrix.md) §6.
- The testing bar and code conventions: [`project_rules.md`](../../../project_rules.md).
