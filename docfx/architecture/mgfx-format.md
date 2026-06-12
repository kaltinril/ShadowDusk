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

## Format version (MGFX v10)

ShadowDusk produces **MGFX v10** (`CompilerOptions.MgfxVersion` / CLI `--mgfx-version`, which sets the header version **byte** and defaults to `10`). v10 is the broadly compatible choice: it loads in MonoGame 3.8.x (DesktopGL and WindowsDX) and in KNI for both **Reach** (WebGL1) and **HiDef** (WebGL2 / GLSL ES 3.00). The version flag is a non-required escape hatch; `10` is the value produced and validated. New backends should be **additive** targets auto-selected from the chosen platform rather than changes to the existing v10 output.

## One writer, two backends

The writer is **backend-agnostic** — there is no separate structural branch for DirectX vs OpenGL. It consumes the shared reflection contract and embeds whichever blob the backend produced, so the DirectX `.mgfx` is byte-compatible with the OpenGL writer's structure (only the embedded shader bytes differ).

## Output is in-memory

The writer returns a `byte[]`. The CLI writes it to disk; the library returns it from <xref:ShadowDusk.Core.IShaderCompiler.CompileAsync*> as the <xref:ShadowDusk.Core.CompiledShader.Data> array — no temp file, no child process. That is what enables the in-memory and in-browser paths.

> **Reproducibility, not byte-equality with `mgfxc`.** ShadowDusk is deterministic: same version + source + target → identical bytes. It is **not** byte-identical to `mgfxc` (a different compiler); equivalence is behavioral — the `Effect` loads and renders the same.
