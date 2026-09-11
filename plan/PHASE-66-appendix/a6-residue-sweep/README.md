# A6 residue sweep: the real-slangc route against non-Gum, non-Slang-authored bodies

Evidence for Phase 66 A6. See the phase doc's A6 section for the full write-up (sampling
method, pass/fail table, classification, the bug found and fixed); this folder holds the
15 hand-converted sample shaders the sweep actually ran, so the doc's claims stay
reproducible.

## What's here

`shaders/*.slang` — 15 files, each converted from one real `.fx` fixture already in
`tests/fixtures/shaders/` (third-party `Nez`/`MonoGame` vendored effects, root-level
regression fixtures, and `examples/` diagnostic fixtures). Every file's own header comment
names its source fixture and the construct(s) it was picked to exercise (loops, branches,
VPOS, legacy `sampler`/`tex2D`/`sampler_state` shapes, helper functions, MRT, array
uniforms, `Texture2DArray`/`TextureCube`/`Texture3D`, `SampleGrad`, `ddx` in a divergent
loop, a multi-parameter VS with a semantic-tagged matrix parameter and an `#if VULKAN`
branch). Bodies are copied as closely as the `[shader("vertex")]`/`[shader("fragment")]`
convention and modern `Texture2D`/`SamplerState` declarations (where the original already
used them) allow; comments call out every deliberate change.

## Repro

Each file was compiled through the real `SlangCompiler` route (`CompileAsync`) for
`OpenGL`, `DirectX`, `DirectX12`, `Vulkan`, and `Fna`, via a temporary xunit exploration
harness (not committed — see the phase doc for the raw pass/fail table it produced).
Equivalent ad hoc repro for any one file:

```csharp
string source = await File.ReadAllText("plan/PHASE-66-appendix/a6-residue-sweep/shaders/<name>.slang");
var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "<name>.slang" };
var result = await new SlangCompiler().CompileAsync(source, options);
```

## The bug this sweep found

`InstancingMatrixSemantic.slang` (derived from `third-party/MonoGame/Instancing.fx`)
branches on `#if VULKAN` to choose whether to `transpose()` an instancing matrix. Before
this stage's fix, `SlangCompiler` never forwarded `PlatformMacros.For(options.Target, ...)`
to slangc's own preprocessor pass (only `CompilerOptions.Defines` was forwarded) — so
`VULKAN`/`OPENGL`/`SM4`/`SM6`/`__KNIFX__` were never defined during the slangc compile,
and a `#if VULKAN` branch resolved the SAME way on every target. Measured directly (not
inferred): before the fix, the HLSL slangc emitted for BOTH `PlatformTarget.Vulkan` and
`PlatformTarget.DirectX` contained `mul(input.Position, transpose(worldTransposed))` — the
non-Vulkan branch, even when compiling for Vulkan. After the fix (forwarding the same
`PlatformMacros` the ordinary `.fx` route already forwards to DXC), Vulkan's emitted HLSL
contains `mul(input.Position, worldTransposed)` (no transpose) while DirectX's still
contains the `transpose()` call — the two targets now diverge exactly as the `#if VULKAN`
author intended. Fixed in `src/ShadowDusk.Slang/SlangCompiler.cs`
(`RunSlangc`/`Compile`); regression tests:
`SlangCompilerTests.PlatformMacros_ForwardedToSlangc_IfOpenglBranchResolvesPerTarget` and
`...PlatformMacros_KnifxContainer_DefinesKniFxMacro`.
