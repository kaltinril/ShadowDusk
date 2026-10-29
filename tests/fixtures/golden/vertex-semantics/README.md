# OpenGL vertex-semantic and matrix-interpolant goldens

Each `<Shape>.fx` here is a vertex-input or interpolant shape; `<Shape>.OpenGL.mgfx` is what the
pinned reference compiler, `mgfxc` 3.8.4.1 (the `dotnet-mgcb` version in
`.config/dotnet-tools.json`), emits for it with `/Profile:OpenGL`. They pin the vertex
shader's attribute records (name, `VertexElementUsage`, semantic index) that MonoGame's and
KNI's GL runtimes bind each `VertexElement` through, and the `varying` names the two stages
link by.

- `Binormal`, `Tangent`, `BlendIndices`, `BlendWeight`, `PointSize` (`PSIZE`), `Fog`, `Depth`,
  `TexCoordIndex`, `MixedCase`: one semantic at index 0 and at a non-zero index.
- `SemanticsRender`: twelve inputs, one per semantic; `Pick` selects which one is the colour.
- `TangentFrameRender`: a `float3x3 : TEXCOORD2` interpolant read back three ways.
- `DeferredGBufferShape`: the shape of KNI EffectTest's `DeferredBasicEffect` (written
  independently): tangent-frame inputs and interpolant, three render-target outputs.

- `packing/`: one matrix interpolant declared differently by the two stages (a matrix written
  whole and read as separate vectors, or the reverse; 3x3, 4x3, 3x4, 2x2; column_major and
  explicit `row_major`; a `typedef row_major`; `#pragma pack_matrix(row_major)`), so a render
  shows the register layout fxc gives it; `DuplicatePosition` is `POSITION0` beside
  `SV_POSITION`. The entry-point attribution shapes: a row_major return value
  (`RetValRowMatToVec33`), comma declarators, `TEXCOORD00`, a row_major VS output read as a
  column_major PS input, a nested struct, an unused struct holding a `float1x3`, and two
  techniques declaring one semantic differently. Goldens are `packing/<Shape>.OpenGL.mgfx`.

Consumers:

- `tests/ShadowDusk.Integration.Tests/Reflection/GlVertexSemanticTests.cs` compares ShadowDusk's
  attribute records and varying names against these.
- `validation/VertexSemanticsGl` (MonoGame DesktopGL) and `validation/VertexSemanticsKniGl`
  (KNI SDL2.GL, MGFX v10 and KNIFX) render the three render shapes through a custom
  `VertexDeclaration` and compare each against the golden drawn in the same process.

The folder sits outside `tests/fixtures/shaders/` on purpose, so the shapes do not join the
corpus sweeps.

## Regenerating

From the repository root, after `dotnet tool restore`:

```powershell
$mgfxc = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\3.8.4.1\tools\net8.0\any\mgfxc.dll"
foreach ($fx in Get-ChildItem tests/fixtures/golden/vertex-semantics -Recurse -Filter *.fx) {
    dotnet $mgfxc $fx.FullName (Join-Path $fx.DirectoryName "$($fx.BaseName).OpenGL.mgfx") "/Profile:OpenGL"
}
```
