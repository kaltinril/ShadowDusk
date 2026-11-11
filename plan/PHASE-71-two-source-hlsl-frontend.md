# Phase 71: Two-source HLSL frontend (a vertex shader and a pixel shader as two texts)

**Status:** 📋 **Open (2026-11-11):** implemented on the branch `feat/hlsl-vertex-pixel-sources`, **awaiting an independent adversarial review, the Windows render gate and CI** before it can merge to `main` (the standing owner directive: `main` is always releasable).

## 1. What it is

A new input for the one faithful pipeline: a vertex shader and a pixel shader given as **two separate HLSL texts** (no `technique` block), compiled to everything a single `.fx` compiles to (MGFX v10/v11, KNIFX v11/v12, FNA `fx_2_0`; OpenGL, DirectX 11, DirectX 12, Vulkan). It follows the Slang and ShaderToy precedents: a **pure managed text frontend** that produces `.fx` text, then the unchanged `EffectCompiler`.

Public surface (`ShadowDusk.Compiler`): `HlslStageSources`, `HlslStageFxConversion`, `HlslStageFrontend.ConvertToFx`, `HlslStageCompiler.Compile` / `CompileAsync` / `ValidateAsync`. CLI: `--vertex <file> --pixel <file> [--vertex-entry <n>] [--pixel-entry <n>] <OutputFile>`.

## 2. Established facts (read from the code before designing)

- `CompilationPipeline.RunCore` compiles every entry point with the whole preprocessed unit as source; vertex and pixel share one translation unit, and the cross-stage merge of constant buffers and parameters is by name, first stage wins, with no shape check outside FNA. See `project_decisions.md` for why that rules out per-stage compilation.
- The writers (`MgfxWriter`, `KnifxWriter`, `Fx2EffectWriter`) are decoupled from HLSL parsing, but the pipeline has no seam that accepts a pre-built model.
- Every existing alternative input reduces to one `.fx` text (`SlangFrontend`, `ShaderToyConverter`), so a text frontend is the established seam.
- **Spike (2026-11-11), the gate for this phase:** a hand-merged `.fx` with `#line N "file"` markers before each declaration compiles on OpenGL, DirectX 11, DirectX 12, Vulkan and FNA, and a planted error is reported as `pixel.hlsl(6,41)` (the original file and line) on OpenGL, DirectX 11, DirectX 12, Vulkan and FNA (vkd3d). The FNA effect-build diagnostics (`SD0303`) do not use `#line`.

## 3. Design

1. Validate names and non-empty sources (`SD0660`, `SD0667`).
2. Split each source into top-level declarations on a comment- and string-masked copy (`SlangSourceMask`): structs, constant buffers, functions, prototypes, resources, parameters, constants, typedefs, macros, directives; a balanced top-level `#if` block is one opaque unit.
3. Find each entry point: a function definition with that name (`SD0661`, `SD0662`). When both entries have the same name, rename each to `<name>_VS` / `<name>_PS` at its definition and calls only (an identifier followed by `(`, not preceded by `.`); a name on a preprocessor line is refused (`SD0663`).
4. Merge: vertex declarations, then the pixel ones that are not duplicates. Identical (whitespace and comments ignored) collapses; a different struct, typedef, constant, macro or same-signature function is `SD0664`; a different constant buffer, texture, sampler or uniform parameter is `SD0665`; a function with another parameter list is an overload and stays.
5. Emit `#line N "file"` before each declaration, padded to its original column, then the declaration verbatim.
6. Append the technique with the shader-model block the Slang and ShaderToy frontends use (`#if SM4` gates vs/ps 4.0 level 9.1 against 3.0).

## 4. Evidence

Byte identity with the hand-merged `.fx` (`HlslStagePairTests`, three pairs, nine targets and containers), the location contract (an error planted behind a removed duplicate, asserted at `(File, Line, Column)`), 62 pure tests, `CliHlslStagePairTest`, and the CLI parser tests. A distinct evidence axis: no `mgfxc` reads two sources (`docs/validation-matrix.md` section 8.0aa).

## 5. Acceptance (open items)

- [x] Spike: `#line` accepted on every target, errors located in the original files.
- [x] Frontend, compiler API, `ValidateAsync`, CLI.
- [x] Codes `SD0660`-`SD0667`, `X0012` registered; registry tests green.
- [x] Pure, integration and CLI tests; fixtures outside the swept corpus root.
- [x] Docs: README, `docs/the-purpose.md`, `docs/validation-matrix.md`, `docs/repository-layout.md`, DocFX guide, CLI page, glossary, overview and index tables, `CHANGELOG.md`, `project_facts.md`, `project_decisions.md`.
- [ ] **Independent adversarial review** (required for anything touching shader output; this changes no existing output, but it adds a public API).
- [ ] **Windows render gate** (`./validation/run-windows-render-gates.ps1`) and CI on every lane (apply `run-integration`).
- [ ] Decide whether to add a `validation/` render driver for a pair, or to rely on byte identity with the `.fx` (the pair compiles to the `.fx`'s exact bytes, so the existing rung-4 gates for that shape already apply).

## 6. Non-goals

Pixel-only pairs (the SpriteBatch shape), several techniques or passes, the MGCB plugin, SkSL and raylib directly from a pair (chain `ConvertToFx` into the converters), validating that vertex outputs match pixel inputs, and checking that each source is self-contained.
