# DirectX 11 legacy-sampler texture slot goldens

Each `<Shape>.fx` puts LEGACY samplers (`sampler2D A;`, `sampler A : register(sN);`, a
`sampler_state` block naming a declared or undeclared texture) beside each other and beside
MODERN resources, read in a different order from the one they are declared in;
`<Shape>.DirectX_11.mgfx` is what the pinned reference compiler, `mgfxc` 3.8.4.1 (the
`dotnet-mgcb` version in `.config/dotnet-tools.json`), emits for it (measured 2026-10-28). The
`S*`, `D*`, `A*`, `B*`, `C*`, `E*`, `F*`, `G*` and `ZZ_*` shapes came from the independent reviews of
the fix (evaluation-order shapes, among them every comparison and intrinsic whose operands a compiler
could emit out of order, dead-code and dead-store shapes, declaration forms the re-pin must handle).

What they pin (fxc's rule, per compiled shader): every explicit `register(tN)` on a `t`-register
declaration takes `tN` out of circulation even when nothing reads the resource, and a modern
resource read directly keeps it; then every legacy sampler's texture in the order the shader FIRST
READS it (evaluation order: an argument before the call that takes it, an inner read before the
dependent read around it); then every modern resource without a register, in declaration order. A
register on the `texture2D` a `sampler_state` names only reserves it, and a legacy sampler's own
`register(sN)` decides its sampler slot only.

`DirectX11TextureSlotOrderTests` compiles every shape with BOTH DXBC backends (vkd3d, and the
`d3dcompiler_47` option on Windows) and expects `mgfxc`'s table, except the shapes it lists as
known divergences (one texture behind two samplers, declarator-list sampler slots, a resource array,
an order decided by an unrolled loop's compile-time branch, macro-declared samplers compiled
natively, and on vkd3d the reads fxc folds away).

Consumers: `tests/ShadowDusk.Integration.Tests/Reflection/DirectX11TextureSlotOrderTests.cs`, the
"texture-slots" arm of `validation/DxModernFeatures` and the texture-slot rows of
`validation/KniWinFormsDX`. The folder sits outside `tests/fixtures/shaders/` so the shapes do not
join the corpus sweeps.

## Regenerating

From the repository root, after `dotnet tool restore`:

```powershell
$mgfxc = Join-Path $env:USERPROFILE ".nuget\packages\dotnet-mgcb\3.8.4.1\tools\net8.0\any\mgfxc.dll"
foreach ($fx in Get-ChildItem tests/fixtures/golden/dx11-texture-slots -Filter *.fx) {
    dotnet $mgfxc $fx.FullName (Join-Path $fx.DirectoryName "$($fx.BaseName).DirectX_11.mgfx") "/Profile:DirectX_11"
}
```
