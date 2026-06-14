# MGFX / MojoShader Format

The output of every ShadowDusk compile is a **`.mgfx` binary** — MonoGame's compiled-effect container, which its `Effect` class loads at runtime. ShadowDusk faithfully reproduces this format with the managed `MgfxWriter` (`ShadowDusk.Core/MgfxWriter.cs`).

## What's in a `.mgfx`

The writer assembles, in order:

- a **header** — the `MGFX` signature and a format version (see below);
- the **constant buffers** — cbuffer layouts with parameter offsets (from [reflection](reflection.md));
- the **shader blobs** — the per-stage compiled code: **GLSL text** for OpenGL/WebGL, **DXBC bytecode** for DirectX;
- the **parameters** — names, types, and offsets;
- the **techniques and passes** — reconstructed from the [FX9 pre-parser](fx9-preparser-preprocessor.md) metadata, mapping to MonoGame's `Technique` / pass model;
- the **render states** parsed from the effect's pass blocks.

## MojoShader heritage (OpenGL)

For OpenGL effects the shader blob is GLSL in the **MojoShader dialect** — the uniform-naming and fragment-output convention MonoGame's OpenGL effect loader expects. ShadowDusk's [GLSL dialect rewrite](glsl-dialect-rewrite.md) produces exactly that dialect so the bytes load and render like `mgfxc`'s.

## Format version: MGFX v10 (default), opt-in MGFX v11 / KNIFX v11

ShadowDusk produces **MGFX v10** by default (`CompilerOptions.MgfxVersion` / CLI `--mgfx-version`, defaults to `10`). v10 is the broadly compatible choice: it loads in MonoGame 3.8.x (DesktopGL and WindowsDX) and in KNI for both **Reach** (WebGL1) and **HiDef** (WebGL2 / GLSL ES 3.00). It is the **seamless default** and is never something a consumer must change to get correct output. ShadowDusk does not emit the older v9 (pre-3.8.2) format.

As of **0.6.0**, two **opt-in, additive** newer containers are also available (the v10 default is unchanged): a faithful MonoGame **MGFX v11** (`MgfxVersion = 11`; MonoGame 3.8.5+, which adds two per-shader diagnostic strings to the shader blob, render-proven in real MonoGame 3.8.5) and KNI's **KNIFX v11** (a distinct `KNIF`-signed container; `CompilerOptions.Container = EffectContainer.Knifx`; KNI v4.02+, render-proven in real KNI). Both render identically to v10. See [Parameters & Caveats](../guides/parameters-and-caveats.md).

## One writer, two backends

The writer is **backend-agnostic** — there is no separate structural branch for DirectX vs OpenGL. It consumes the shared reflection contract and embeds whichever blob the backend produced, so the DirectX `.mgfx` is byte-compatible with the OpenGL writer's structure (only the embedded shader bytes differ).

## Output is in-memory

The writer returns a `byte[]`. The CLI writes it to disk; the library returns it from <xref:ShadowDusk.Core.IShaderCompiler.CompileAsync*> as the <xref:ShadowDusk.Core.CompiledShader.Data> array — no temp file, no child process. That is what enables the in-memory and in-browser paths.

> **Reproducibility, not byte-equality with `mgfxc`.** ShadowDusk is deterministic: same version + source + target → identical bytes. It is **not** byte-identical to `mgfxc` (a different compiler); equivalence is behavioral — the `Effect` loads and renders the same.
