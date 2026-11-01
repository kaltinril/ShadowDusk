# DirectX 11 legacy-sampler texture slot goldens

Each `<Shape>.fx` puts LEGACY samplers (`sampler2D A;`, `sampler A : register(sN);`, a
`sampler_state` block naming a declared or undeclared texture) beside each other and beside
MODERN resources, read in a different order from the one they are declared in;
`<Shape>.DirectX_11.mgfx` is what the pinned reference compiler, `mgfxc` 3.8.4.1 (the
`dotnet-mgcb` version in `.config/dotnet-tools.json`), emits for it (measured 2026-10-28). The
`S*`, `D*`, `A*`, `B*`, `C*`, `E*`, `F*`, `G*` and `ZZ_*` shapes came from the independent reviews of
the fix (evaluation-order shapes, among them every comparison and intrinsic whose operands a compiler
could emit out of order, dead-code and dead-store shapes, declaration forms the re-pin must handle).
The `K*` shapes (2026-10-29) put a texture's first read under a condition: ones fxc folds at
compile time though no syntactic test sees it (a call returning a constant, a local, a default or
`out` argument, `&& false`, a vector component, a value range, a loop that runs zero times, a
folded `return`), ones it does not fold (a uniform, a shader input, a `switch` on a uniform), and
`?:` arms, which fxc reads whatever the condition. The `P*`, `Q*` and `R*` shapes (2026-10-29, a
second review) are conditions that LOOK run-time and are folded anyway (a local declared in a
comma list shadowing the uniform, a uniform the shader assigns or passes as an `out` argument, a
parenthesized write to an input, an `unsigned int` compared with `0`, an `int` compared with
`0.5`), loop bounds that truncate (`int i = 0.5`, `static const int N = 0.5`, a `uint` counter),
`switch` arms (fxc numbers the `default` arm LAST), and reads after an unconditional `return`,
`break` or `continue` (fxc removes them). The `T*` shapes pin the `default`-last rule (a label
shared with a `case`, a `default` in the middle), that `discard` removes nothing, a bare block
ending in `return`/`break`, a `switch` on a `static const` (vkd3d keeps the dead arm), and that
the reads after one early `return` share its fate. The source walk never evaluates a condition:
every read under one is a maybe-read, and when the maybe-reads fxc keeps could change the order
it refuses with `SD0230` and keeps declaration order.

The `U*` shapes (2026-10-29, the final review) are shapes the walk does not model, each once
numbered silently wrong: `U01`..`U14`, `U68`, `U69` are statements fxc DROPS before it numbers (a
bare `tex2D(B, uv);`, in parentheses, a block, a loop, after an `if`, a void helper, a `for` init, a
constructor `float4(tex2D(B, uv));`, `tex2Dgrad`, `Tb.Sample(SB, uv);`), refused; `U20`..`U43` are
discarded values fxc KEEPS (`.r`, `* 2`, `* 0`, a cast, `clip`, `sincos`, a user function's call, a
comma, `?:`, an unused local, a `for` init declaration), numbered; `U50`..`U56` are bare intrinsic
calls fxc keeps and the walk refuses anyway (false refusals). `U60`..`U69` use a legacy sampler's
texture through a method of its own (`GetDimensions`, `Load`, `Sample`, `SampleLevel`): `mgfxc`
gives that use a second record (`A@t0 Tb@t1 Tb@t2`), refused. `U70`..`U74` read in a global
initializer, numbered before the entry point's even when the global is unused, refused.
`U80`..`U83` are a struct method and a namespace function (d3dcompiler only: vkd3d rejects the
syntax), refused.

`K14` and `K40`..`K44` (2026-10-31, issue #443) pass a legacy sampler to a function's
`sampler2D` parameter: read directly as well (`K14`, the issue's probe), passed on to a second
function (`K40`), `sampler_state` samplers (`K41`), explicit sampler registers (`K42`), a call
under a branch (`K44`), and a `tex2Dlod` through the parameter beside a modern texture (`K43`,
which warns: the `tex2Dlod` helper is not walked, for a global sampler either).

`N01`..`N06`, `N08` (2026-10-31, issue #441) read legacy samplers through a macro
(`#define RD(s,c) tex2D(s,c)`), which the raw pre-parse cannot see, so the backend compiles them
natively under the sampler's own name: a plain and a line-continued macro (`N01`, `N02`, the
issue's P06 and P18), one sampler rewritten beside one native (`N03`), beside a modern pair with
and without a sampler register (`N04`, `N08`), an explicit sampler register (`N05`) and an unused
sampler declared first (`N06`). fxc gives legacy SAMPLER slots in declaration order, and both
backends' native RDEF already agrees; the record must take the native sampler's slot, not its
texture's.

`M10`, `M18`, `M20`, `M21` and `I01`..`I09` (2026-10-31, issue #447): a legacy sampler's texture
also used as a value (an index in a global initializer, a `static Texture2D` alias, an argument:
`mgfxc` gives it a second record, so these warn), a declarator list and a `Texture = (Tb)`
binding that are not such uses (`M21`, no warning), and legacy samplers declared only in an
`#include` (`declAB.fxh`, `declState.fxh` beside them), which the backend compiles natively:
read in reverse (`I01`), behind a condition (`I02`), in a global initializer (`I07`), and a
`sampler_state` sampler whose texture is also loaded directly (`I08`, `I09`). They were never
checked before, silently.

What they pin (fxc's rule, per compiled shader): every explicit `register(tN)` on a `t`-register
declaration takes `tN` out of circulation even when nothing reads the resource, and a modern
resource read directly keeps it; then every legacy sampler's texture in the order the shader FIRST
READS it (evaluation order: an argument before the call that takes it, an inner read before the
dependent read around it); then every modern resource without a register, in declaration order. A
register on the `texture2D` a `sampler_state` names only reserves it, and a legacy sampler's own
`register(sN)` decides its sampler slot only.

`DirectX11TextureSlotOrderTests` compiles every shape with BOTH DXBC backends (vkd3d, and the
`d3dcompiler_47` option on Windows) and expects `mgfxc`'s table, except the shapes it lists as
known divergences (one texture behind two samplers or behind a sampler and a method call, declarator-list sampler slots, a resource array,
macro-declared samplers compiled natively, a `tex2Dlod` read through the rewrite's helper, the refusals
whose `mgfxc` order is not declaration order (the false-`SD0230` class), and on vkd3d the reads
fxc folds away), and the shapes it expects to warn `SD0230`; vkd3d must reject `U80`..`U83`.
Every shape either matches `mgfxc` exactly or warns.

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
