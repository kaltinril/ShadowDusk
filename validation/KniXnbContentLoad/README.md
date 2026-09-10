# validation/KniXnbContentLoad — KNI `Content.Load<Effect>` on a ShadowDusk-written `.xnb` (Phase 64)

The **KNI rung-4 gate for the direct `.xnb` writer** (issue #199). The claim is *"replace your
content pipeline with ShadowDusk and change no lines of code"*; only a real `ContentManager` can
test it, and on KNI the claim was **broken until Phase 64**: KNI 4.2.9001's
`ContentTypeReaderManager.ResolveReaderType` throws `FileLoadException: The given assembly name
was invalid.` on the mgcb-shaped type-reader name `XnbWriter` used to emit (stock `dotnet mgcb`
`.xnb` files fail there identically), and 4.3.9001 merely catches it. `XnbWriter` now emits the
XNA-4.0 name (`…EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, …`), the only
one measured to load on every MonoGame, KNI and FNA version. This driver is that proof, on a
real KNI SDL2.GL runtime.

## What it does, per fixture (Grayscale, VertexAndPixel, MultiTexture, Invert)

| Arm | Built by | Role |
|---|---|---|
| `stock-mgcb` | stock `dotnet mgcb /platform:DesktopGL` (the pinned 3.8.4.1) | **the KNI fact under pin**: must FAIL to load on 4.2.9001 (`FileLoadException`), must load on 4.3.9001 and agree with the next arm at maxd 0 |
| `mgcb-payload-rewrapped` | mgcb's own payload, re-wrapped by `XnbWriter.Wrap(payload, PlatformTarget.OpenGL)` | **the reference render**: the mgfxc compiler's output in a container every KNI version accepts, so the pixel comparison is compiler-vs-compiler on one runtime, never container-vs-container |
| `shadowdusk-v10` | `EffectCompiler` (OpenGL, MGFX v10) + `CompiledShader.ToXnb()` | arm under test, the documented KNI route |
| `shadowdusk-knifx` | `EffectCompiler` (OpenGL, `EffectContainer.Knifx`) + `ToXnb()` | arm under test, the KNI-native container |

Every arm is loaded by asset name through a real KNI `ContentManager(Services, dir).Load<Effect>`
from a bare directory holding only `<asset>.xnb`, rendered through one `SpriteBatch` scene, and
the two ShadowDusk arms must be **pixel-identical (maxd 0)** to the reference arm. Before any
GPU work the driver also asserts the envelope: header identical to mgcb's (`XNBd`, version 5,
uncompressed), reader count / reader version / shared-resource count / type id identical, the
reader name equal to `XnbWriter.EffectReaderTypeName`, mgcb's name **different** (the positive
control that the stock arm isolates the reader name), and the payload different from mgcb's
(ShadowDusk, not mgcb, produced it).

`Invert` stands in for the DX gate's `SpriteEffect`, whose `TECHNIQUE()` macro-defined technique
is the open Phase 41 GAP-1 on OpenGL (`SD0010`).

## Two KNI lines, two builds

The resolver differs between versions, so the gate runs twice. The requested line is baked in as
assembly metadata and checked against the runtime that actually loaded, so a stale build cannot
pass as the other version; each line keeps its own `packages.<version>.lock.json`.

```powershell
dotnet tool restore                                                                  # the pinned dotnet-mgcb
dotnet run --project validation/KniXnbContentLoad -c Release                         # KNI 4.2.9001
dotnet run --project validation/KniXnbContentLoad -c Release -p:KniVersion=4.3.9001  # KNI 4.3.9001
```

Both are default-ON in `validation/run-windows-render-gates.ps1`. PNGs land in
`validation/output-xnb-kni/<version>/`. A KNI line other than the two measured ones is refused
until someone classifies whether stock mgcb output loads on it (Phase 64 §2.3).

## Result (2026-09-09, RTX 3080, real KNI SDL2.GL)

| KNI | stock mgcb `.xnb` | ShadowDusk v10 `.xnb` | ShadowDusk KNIFX `.xnb` |
|---|---|---|---|
| 4.2.9001 | rejected, `FileLoadException: The given assembly name was invalid.` (4/4, as pinned) | 4/4 load, 1,230,720 px maxd 0 | 4/4 load, maxd 0 |
| 4.3.9001 | 4/4 load, maxd 0 vs the re-wrapped payload | 4/4 load, maxd 0 | 4/4 load, maxd 0 |

## Pins

`nkast.Xna.Framework[.*]` + `nkast.Kni.Platform.SDL2.GL` at `$(KniVersion).*` (default
`4.2.9001`, the line every other KNI harness in this repo pins). Same `KniPlatform=DesktopGL` +
`DESKTOPGL` wiring as `validation/KniDesktopGL`; not in `ShadowDusk.slnx`; opted out of central
package management.
