# Phase 16 — Visual Regression Tests (Image Comparison)

## Overview

This phase adds a visual regression test suite that proves ShadowDusk's compiled shaders produce **the same rendered output** as the golden reference shaders compiled by the original `mgfxc` tool. It closes the gap that structural tests (Phase 15) cannot: two `.mgfx` files may have identical headers and technique counts but still render completely differently if the GLSL is semantically wrong.

**The test loop:**
1. Extract the GLSL source from a golden `.mgfx` file (compiled by original `mgfxc`)
2. Render it offscreen to a PNG → **reference image**
3. Extract the GLSL source from ShadowDusk's compiled `.mgfx` for the same shader
4. Render it offscreen to a PNG → **candidate image**
5. Compare pixel-by-pixel — pass if images match within tolerance

**Project:** `tests/ShadowDusk.ImageTests/`

---

## Scope and Non-Goals

**In scope:**
- OpenGL target only (GLSL is UTF-8 text embedded in `.mgfx`; renderable without a D3D runtime)
- The 9 purpose-built Phase 15 fixture shaders (`tests/fixtures/shaders/*.fx`) — not the full 34-shader corpus
- Offscreen/headless rendering — no window, no physical GPU required
- A `--update-golden` mode that (re-)generates reference PNGs from the golden `.mgfx` files
- Per-fixture tolerance spec (exact match vs. ±1/255 per channel)

**Out of scope:**
- DirectX 11 or Vulkan targets — they require a D3D/Vulkan runtime not available in CI
- The 34-shader production corpus (`BasicEffect.fx`, `SpriteEffect.fx`, etc.) — deferred; same infrastructure applies
- Metal target
- GPU-side performance or timing

---

## 1. Rendering Infrastructure

### 1.1 Library

Use **Silk.NET.OpenGL** (`Silk.NET.OpenGL` NuGet) for OpenGL bindings combined with **Silk.NET.Windowing** (`Silk.NET.Windowing` NuGet) in headless/offscreen mode. On Windows, use a 1×1 hidden GLFW window to obtain a GL context; the actual rendering targets an FBO, not the window surface.

For software-only CI environments (no GPU): add `Silk.NET.Windowing.Glfw` and configure Mesa or Microsoft WARP via an environment variable (`LIBGL_ALWAYS_SOFTWARE=1` on Linux, or ANGLE on Windows). If context creation fails, skip all tests in the collection via `IAsyncLifetime.InitializeAsync` throwing a skip-friendly exception.

### 1.2 Offscreen Framebuffer

Each render uses a dedicated Framebuffer Object (FBO):
- Color attachment: `GL_RGBA8` texture, 128×128 pixels
- Depth attachment: `GL_DEPTH_COMPONENT16` renderbuffer
- `glViewport(0, 0, 128, 128)` before each draw

128×128 is large enough to detect color/coord errors but small enough that pixel readback is fast.

### 1.3 Pixel Readback

After drawing: `glReadPixels(0, 0, 128, 128, GL_RGBA, GL_UNSIGNED_BYTE, buffer)`. Buffer is a `byte[128 * 128 * 4]` (RGBA, top-left origin). Flip rows before saving (OpenGL origin is bottom-left; PNG convention is top-left).

### 1.4 PNG I/O

Use `System.Drawing.Common` (Windows) or `SixLabors.ImageSharp` (cross-platform) for PNG encode/decode. **Prefer `SixLabors.ImageSharp`** — it is cross-platform and does not require `System.Drawing.Common`'s native GDI+ on Linux.

---

## 2. MGFX → GLSL Extraction

The `MgfxBlobReader` (already exists in `tests/ShadowDusk.Integration.Tests/`) exposes `ShaderBlobs` as `IReadOnlyList<byte[]>`. For the OpenGL target, each blob is raw UTF-8-encoded GLSL text (as written by `MgfxWriter` via `Encoding.UTF8.GetBytes(transpileResult.Value.Text)`).

Add a helper `GlslShaderExtractor` to `tests/ShadowDusk.ImageTests/`:

```csharp
public sealed record GlslShaderPair(string VertexSource, string FragmentSource);

public static class GlslShaderExtractor
{
    // Reads a compiled OpenGL .mgfx blob, extracts VS and PS GLSL sources.
    // Technique index and pass index default to 0.
    public static GlslShaderPair Extract(byte[] mgfx, int techniqueIndex = 0, int passIndex = 0);
}
```

Implementation: use `MgfxBlobReader.Parse(mgfx)`, look up `Techniques[techniqueIndex].Passes[passIndex]` for VS/PS shader indices, return `Encoding.UTF8.GetString(ShaderBlobs[vsIndex])` and `ShaderBlobs[psIndex]`.

---

## 3. GLSL Shader Compilation (GL)

Add `GlslShaderProgram` to wrap GL shader/program lifecycle:

```csharp
public sealed class GlslShaderProgram : IDisposable
{
    public uint Handle { get; }

    public static GlslShaderProgram Compile(GL gl, string vertexSource, string fragmentSource);

    // Throws GlslCompileException with the info log if compilation or linking fails.
    public void Dispose();
}
```

`GlslCompileException` includes the full GLSL info log in `Message` so test failures show the compiler error, not just "failed".

---

## 4. Standard Test Scenes

Each fixture shader is rendered with a standardized scene. All scenes share the same geometry: **two triangles forming a unit quad** covering NDC `[-1, 1] × [-1, 1]`.

Vertex layout (interleaved):
```
float3 POSITION  (slot 0)
float4 COLOR0    (slot 1)
float2 TEXCOORD0 (slot 2)
```

The vertex attribute binding must match the SPIRV-Cross output semantic names. SPIRV-Cross names them after the HLSL semantic: `in_var_POSITION`, `in_var_COLOR0`, `in_var_TEXCOORD0`. Use `glBindAttribLocation` or parse from `gl_ProgramInfoLog` — see Section 4.1.

### 4.1 Semantic-to-Attribute Mapping

SPIRV-Cross generates input attribute names like `in_var_POSITION`, `in_var_TEXCOORD0`, etc. Query them with `glGetActiveAttrib` after linking, then bind locations to the hardcoded layout:

| Semantic | Location |
|----------|----------|
| `POSITION` | 0 |
| `COLOR0` | 1 |
| `TEXCOORD0` | 2 |

### 4.2 Per-Fixture Scene Parameters

| Fixture | WorldViewProj | Color uniform | Texture | Expected output |
|---------|--------------|---------------|---------|----------------|
| `Minimal.fx` | identity | — | — | Solid `(1, 0, 1, 1)` magenta |
| `textured.fx` | identity | — | 4×4 solid red `(1,0,0,1)` | Solid red |
| `cbuffer.fx` | identity | `Color=(0,1,0,1)` green | — | Solid green |
| `multipass.fx` | identity | — | — | Pass0=red `(1,0,0,1)`, Pass1=green `(0,1,0,1)` — render each pass separately |
| `multitechnique.fx` | identity | — | — | TechA=red, TechB=green, TechC=blue — one render per technique |
| `render-states.fx` | identity | — | — | Solid `(1,1,1,0.5)` white semi-transparent |
| `annotations.fx` | — | `TintColor=(1,0,1,1)` magenta | — | Solid magenta |
| `platform-macros.fx` | identity | — | — | OpenGL branch: `(0,1,0,1)` green |
| `basiceffect-mini.fx` | identity | vertex colors set in VB | — | Tech0=vertex-colored quad |

> **Note on render-states.fx**: `AlphaBlendEnable=True` requires `gl.Enable(GLEnum.Blend)` and `gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha)` before drawing. The expected color with a white `(1,1,1,0.5)` quad over a black `(0,0,0,1)` clear is `(0.5, 0.5, 0.5, 1.0)` → `(128, 128, 128, 255)` in bytes.

### 4.3 Uniform Binding

SPIRV-Cross wraps `cbuffer` uniforms in a uniform block or a struct (depending on GLSL version). For OpenGL ES 3.0 / GLSL 300 es: uniform blocks named after the cbuffer (`$Globals` for anonymous/global uniforms). Use `glGetUniformBlockIndex` + `glUniformBlockBinding` + a UBO, or fall back to individual `glGetUniformLocation` calls if SPIRV-Cross emits bare uniforms.

---

## 5. Image Comparison

### 5.1 `ImageComparer`

```csharp
public sealed class ImageComparison
{
    public bool Matches { get; }
    public int  DifferentPixels { get; }
    public int  TotalPixels { get; }
    public byte MaxChannelDelta { get; }
}

public static class ImageComparer
{
    // Returns comparison result. Tolerance is max per-channel deviation [0,255].
    public static ImageComparison Compare(byte[] expected, byte[] actual, byte tolerance = 1);
}
```

Report `DifferentPixels` and `MaxChannelDelta` in the xUnit failure message so the developer knows whether it's one rogue pixel or a complete mismatch.

### 5.2 Tolerance Policy

| Fixture | Tolerance |
|---------|-----------|
| Solid-color outputs (minimal, cbuffer, annotations) | `0` — exact match |
| Textured outputs | `1` — ±1/255 per channel for bilinear rounding |
| Blended outputs (render-states) | `2` — blend factor rounding |

---

## 6. Reference Image Strategy

### 6.1 Bootstrap

Reference images live under `tests/fixtures/reference-images/OpenGL/<fixture-stem>.png`. They are generated by rendering the **golden `.mgfx` files** (from `tests/fixtures/golden/OpenGL/`) through the same renderer.

Run with `dotnet test -- ImageTests.UpdateGolden=true` (via `TestContext` or environment variable `SHADOWDUSK_UPDATE_GOLDEN=1`) to regenerate all reference images. This is a manual step run locally or when intentionally updating the reference after a known-good compiler change.

Reference images are **checked into the repository**.

### 6.2 Normal CI Run

On every CI run, the test:
1. Compiles the `.fx` source fresh with ShadowDusk's `EffectCompiler`
2. Extracts GLSL from the resulting `.mgfx`
3. Renders to a candidate image
4. Loads the reference PNG from `tests/fixtures/reference-images/OpenGL/`
5. Calls `ImageComparer.Compare(reference, candidate, tolerance)`
6. Asserts `comparison.Matches`

---

## 7. Project Setup

### 7.1 `.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ShadowDusk.ImageTests</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ShadowDusk.Compiler\ShadowDusk.Compiler.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="FluentAssertions" />
    <PackageReference Include="Silk.NET.OpenGL" />
    <PackageReference Include="Silk.NET.Windowing.Glfw" />
    <PackageReference Include="SixLabors.ImageSharp" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\..\tests\fixtures\shaders\**\*"
          CopyToOutputDirectory="PreserveNewest"
          Link="fixtures\shaders\%(RecursiveDir)%(Filename)%(Extension)" />
    <None Include="..\..\tests\fixtures\golden\OpenGL\**\*"
          CopyToOutputDirectory="PreserveNewest"
          Link="fixtures\golden\OpenGL\%(RecursiveDir)%(Filename)%(Extension)" />
    <None Include="..\..\tests\fixtures\reference-images\OpenGL\**\*"
          CopyToOutputDirectory="PreserveNewest"
          Link="fixtures\reference-images\OpenGL\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

### 7.2 Directory Structure

```
tests/
├── fixtures/
│   ├── golden/OpenGL/          # Existing — original mgfxc .mgfx files
│   ├── shaders/                # Existing — .fx source files
│   └── reference-images/
│       └── OpenGL/             # New — PNG reference images (checked in)
└── ShadowDusk.ImageTests/
    ├── ShadowDusk.ImageTests.csproj
    ├── GlContext/
    │   ├── OffscreenRenderer.cs    # FBO, pixel readback
    │   └── GlslShaderProgram.cs    # Compile + link GLSL
    ├── GlslShaderExtractor.cs
    ├── ImageComparer.cs
    ├── SceneDescriptor.cs          # Per-fixture geometry + uniforms + expected tolerance
    └── Tests/
        ├── ImageRegressionTests.cs
        └── ReferenceImageGenerator.cs  # --update-golden path
```

---

## 8. Test Structure

### 8.1 `ImageRegressionTests.cs`

```csharp
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
public sealed class ImageRegressionTests : IClassFixture<GlContextFixture>
```

One `[Theory]` driven by `[MemberData]` over all 9 fixtures. For each:
- Compile with `EffectCompiler` (DirectPipeline, OpenGL target)
- Extract GLSL via `GlslShaderExtractor`
- Render via `OffscreenRenderer`
- Load reference PNG from `fixtures/reference-images/OpenGL/`
- Assert `ImageComparer.Compare(reference, candidate, tolerance).Matches`

Each fixture assertion is a single Theory row, not a `[Fact]`, so CI failure output identifies the failing shader by name.

### 8.2 `GlContextFixture`

`IAsyncLifetime` fixture shared across the test class. Creates the GL context once and disposes after the class. Skips with a clear message if the GL context cannot be created (e.g., no OpenGL 3.3 support in the environment).

---

## 9. Acceptance Criteria

- [ ] All 9 Phase 15 fixture shaders compile and render without GL errors
- [ ] Reference images for all 9 fixtures are checked into `tests/fixtures/reference-images/OpenGL/`
- [ ] All 9 ImageRegression tests pass when running ShadowDusk-compiled output against the reference images
- [ ] `SHADOWDUSK_UPDATE_GOLDEN=1` regenerates reference images from golden `.mgfx` files
- [ ] Tests skip gracefully (not fail) when no OpenGL 3.3 context is available
- [ ] No test takes longer than 30 seconds (CancellationToken timeout)
- [ ] No `Thread.Sleep`, `.Result`, `.Wait()`

---

## 10. Implementation Order

- [ ] 1. Create `tests/ShadowDusk.ImageTests/ShadowDusk.ImageTests.csproj` and add to `ShadowDusk.slnx`
- [ ] 2. Implement `GlContext/OffscreenRenderer.cs` — GLFW window (1×1 hidden), FBO 128×128, pixel readback
- [ ] 3. Implement `GlContext/GlslShaderProgram.cs` — compile/link GLSL, `GlslCompileException`, attribute location binding
- [ ] 4. Implement `GlslShaderExtractor.cs` — extract VS/PS GLSL from `.mgfx` blob using `MgfxBlobReader`
- [ ] 5. Implement `ImageComparer.cs` — per-channel tolerance comparison, `ImageComparison` result type
- [ ] 6. Implement `SceneDescriptor.cs` — per-fixture geometry, uniforms, textures, tolerance table (Section 4.2)
- [ ] 7. Implement `Tests/ReferenceImageGenerator.cs` — `SHADOWDUSK_UPDATE_GOLDEN=1` path that renders golden `.mgfx` → reference PNGs
- [ ] 8. Run `ReferenceImageGenerator` against the existing golden `.mgfx` files; commit resulting PNGs
- [ ] 9. Implement `GlContextFixture.cs` — `IAsyncLifetime`, skip-on-no-context
- [ ] 10. Implement `Tests/ImageRegressionTests.cs` — `[Theory]` over 9 fixtures, compile → render → compare
- [ ] 11. Run `dotnet test --filter "Category=ImageRegression"` — all 9 pass
- [ ] 12. Verify skip behavior when `LIBGL_ALWAYS_SOFTWARE` is unset in a GL-less environment
