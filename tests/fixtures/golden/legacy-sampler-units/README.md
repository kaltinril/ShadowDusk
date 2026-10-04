# Legacy-beside-modern sampler unit and slot goldens

Each `<Shape>.fx` mixes LEGACY samplers (`sampler2D A;`, `sampler A : register(sN);`, a
`sampler_state` block) with MODERN `Texture2D` + `SamplerState` pairs in a different order or
register layout; `<Shape>.OpenGL.mgfx` and `<Shape>.DirectX_11.mgfx` are what the pinned reference
compiler, `mgfxc` 3.8.4.1 (the `dotnet-mgcb` version in `.config/dotnet-tools.json`), emits for
it (measured 2026-10-03).

Consumers:

- `tests/ShadowDusk.Integration.Tests/Reflection/LegacySamplerUnitTests.cs`: OpenGL texture units
  for every shape, DirectX 11 legacy sampler slots for every shape, and the whole DirectX 11
  sampler table where the texture slots agree.
- `validation/SamplerRegisterOrderGl` arms "mixed-legacy-after-modern" and "mixed-two-of-each"
  render `GlMixedLegacyAfterModernRender.fx` and `GlMixedTwoOfEachRender.fx` in real MonoGame
  DesktopGL.
- `validation/DxModernFeatures` arm "legacy-register" renders `DxLegacyRegisterRender.fx` in real
  MonoGame WindowsDX.

The folder sits outside `tests/fixtures/shaders/` so the shapes do not join the corpus sweeps.

## Regenerating

From the repository root, after `dotnet tool restore`:

```powershell
$mgfxc = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\3.8.4.1\tools\net8.0\any\mgfxc.dll"
foreach ($fx in Get-ChildItem tests/fixtures/golden/legacy-sampler-units -Filter *.fx) {
    foreach ($p in "OpenGL", "DirectX_11") {
        dotnet $mgfxc $fx.FullName (Join-Path $fx.DirectoryName "$($fx.BaseName).$p.mgfx") "/Profile:$p"
    }
}
```
