# Legacy-sampler effect-parameter name goldens

Each `<Shape>.fx` here is one legacy sampler declaration shape; `<Shape>.OpenGL.mgfx` and
`<Shape>.DirectX_11.mgfx` are what the pinned reference compiler, `mgfxc` 3.8.4.1 (the
`dotnet-mgcb` version in `.config/dotnet-tools.json`), emits for it. They pin the NAME, type and
binding of every effect parameter a legacy sampler produces, which is what game code reaches
through `effect.Parameters["..."]`.

Consumers:

- `tests/ShadowDusk.Integration.Tests/Reflection/LegacySamplerParameterNameTests.cs` compares
  ShadowDusk's table against these on OpenGL and DirectX 11, and holds DirectX 12 and Vulkan to
  the DirectX 11 names (no `mgfxc` reference exists there: 3.8.5 rejects the legacy sampler
  types for Vulkan and emits an empty table for DirectX 12, measured 2026-10-03).
- `validation/SamplerRegisterOrderGl` arm "param-names" renders `ParamNamesRender.fx` in real
  MonoGame DesktopGL, binding the textures only through `mgfxc`'s parameter names.

The folder sits outside `tests/fixtures/shaders/` on purpose, so the shapes do not join the
corpus sweeps. `texCUBE`/`tex3D` shapes are absent because ShadowDusk refuses those intrinsics
off FNA (`FX0012`).

## Regenerating

From the repository root, after `dotnet tool restore`:

```powershell
$mgfxc = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\3.8.4.1\tools\net8.0\any\mgfxc.dll"
foreach ($fx in Get-ChildItem tests/fixtures/golden/legacy-sampler-names -Filter *.fx) {
    foreach ($p in "OpenGL", "DirectX_11") {
        dotnet $mgfxc $fx.FullName (Join-Path $fx.DirectoryName "$($fx.BaseName).$p.mgfx") "/Profile:$p"
    }
}
```
