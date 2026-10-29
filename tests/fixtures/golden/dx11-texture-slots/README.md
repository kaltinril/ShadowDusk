# DirectX 11 legacy-sampler texture slot goldens

Each `<Shape>.fx` puts LEGACY samplers (`sampler2D A;`, `sampler A : register(sN);`, a
`sampler_state` block naming a declared or undeclared texture) beside each other and beside
MODERN `Texture2D` declarations, read in a different order from the one they are declared in;
`<Shape>.DirectX_11.mgfx` is what the pinned reference compiler, `mgfxc` 3.8.4.1 (the
`dotnet-mgcb` version in `.config/dotnet-tools.json`), emits for it (measured 2026-10-28).

What they pin (fxc's rule, per compiled shader): an explicit `register(tN)` on a texture keeps
`tN` and takes it out of circulation even when nothing reads the texture; then every legacy
sampler's texture in the order the shader FIRST READS it (evaluation order: an argument before
the call that takes it, an inner read before the dependent read around it, a dropped read takes
no slot); then every modern texture without a register, in declaration order. A legacy
sampler's own `register(sN)` decides its sampler slot only.

`mgfxc` deviates from XNA in one case these shapes cover, and ShadowDusk follows XNA there: a
`texture2D T : register(tN)` that a `sampler_state` names is NOT bound at `tN` by `mgfxc` (fxc
reserves `tN` for the unread texture object and gives the sampler's texture the next free slot);
`DirectX11TextureSlotOrderTests` reads those registers from the source and expects them honoured.
`TwoSamplersShareOneTexture` is an open divergence (two `mgfxc` records, one in ShadowDusk).

Consumer: `tests/ShadowDusk.Integration.Tests/Reflection/DirectX11TextureSlotOrderTests.cs`. The
folder sits outside `tests/fixtures/shaders/` so the shapes do not join the corpus sweeps.

## Regenerating

From the repository root, after `dotnet tool restore`:

```powershell
$mgfxc = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\3.8.4.1\tools\net8.0\any\mgfxc.dll"
foreach ($fx in Get-ChildItem tests/fixtures/golden/dx11-texture-slots -Filter *.fx) {
    dotnet $mgfxc $fx.FullName (Join-Path $fx.DirectoryName "$($fx.BaseName).DirectX_11.mgfx") "/Profile:DirectX_11"
}
```
