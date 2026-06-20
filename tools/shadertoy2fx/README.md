# shadertoy2fx — ShaderToy GLSL → HLSL `.fx` converter (Phase 46 experiment)

A **standalone, out-of-band** tool (NOT part of the ShadowDusk compiler pipeline) that converts a
single-pass GLSL **image** shader into a self-contained HLSL **`.fx`** source file. It accepts **both**
entry conventions (G2): a **ShaderToy** `void mainImage(out vec4 fragColor, in vec2 fragCoord)` shader
**and** a **plain-GLSL** `void main()` fragment shader (glslViewer / Bonzomatic / Shadertoy-export
style) that writes `gl_FragColor` or a user-declared `out vec4 <name>;` and reads `gl_FragCoord`. The
convention is auto-detected (no flag, no consumer choice); a shader that defines BOTH is an ambiguous,
loud reject. Once you have the `.fx`, the **existing ShadowDusk pipeline** compiles it to MonoGame/KNI
(OpenGL/DirectX) and, within fx_2_0/SM3 limits, FNA — with zero pipeline changes and no native
dependency.

See [`plan/PHASE-46-shadertoy-to-fx-conversion-tool.md`](../../plan/PHASE-46-shadertoy-to-fx-conversion-tool.md)
for the full design, scope, traps, and the bet being tested.

## Layout

- `src/ShadowDusk.ShaderToy/` — the converter library. Public entry point:
  `ShadowDusk.ShaderToy.ShaderToyConverter.Convert(string glsl, ConvertOptions? options = null) → ConvertResult`.
- `src/ShadowDusk.ShaderToy.Cli/` — the `shadertoy2fx` CLI wrapper.
- `tests/ShadowDusk.ShaderToy.Tests/` — unit tests (lex/parse/emit/traps) + golden regression
  tests over a corpus of ShaderToy-dialect shaders (`tests/.../corpus/`).

## Build & test (out-of-band — not in `ShadowDusk.slnx`)

```bash
dotnet build tools/shadertoy2fx/shadertoy2fx.slnx
dotnet test  tools/shadertoy2fx/shadertoy2fx.slnx
```

## Scope (v1)

Single-pass image shaders only (one `mainImage` **or** one plain-GLSL `void main()`, optional Common
tab). No multipass (Buffer A–D), no audio/video/cubemap channels. Unsupported constructs **fail loudly** (clear diagnostic + non-zero
exit), never a silently-wrong `.fx`. The oracle is ShaderToy's own WebGL output (an honestly weaker
bar than `mgfxc`). A separate `ShaderToyEffect` runtime helper (not in this tool) is needed to drive
the uniforms and draw a fullscreen triangle to actually render in-engine.
