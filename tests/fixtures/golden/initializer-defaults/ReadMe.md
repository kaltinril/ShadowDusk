Fixtures for the global-initializer default tests (`InitializerDefaultsTests`) and the real-runtime
render drivers (`validation/InitializerDefaults*`).

`A_Tint` ... `F_NoInit` are the probe shapes: float4, scalar, float2 plus float2x2, int plus bool,
float4[2], and a control with no initializer. `mgfxc-3.8.4.1-DirectX_11/` holds what the real
`mgfxc` 3.8.4.1 `/Profile:DirectX_11` emits for each (its default blobs are the reference the
scalar, vector, int and bool blobs are compared to byte for byte; its float2x2 and array blobs are
the known-lossy ones ShadowDusk deliberately does not copy). mgfxc's OpenGL build writes zeros for
all of them, so there is no OpenGL golden to compare.

`kni-4.2.9001-Windows/` holds KNI's own build of the SM 2.0/3.0 sources the KNI DX driver
(`validation/InitializerDefaultsKniDx`) compiles: `A_Tint_ps_2_0` and `A_Tint_ps_3_0` are `A_Tint.fx`
with both `#define PS_SHADERMODEL` lines naming that profile (`InitializerDefaultsProbe.TintAt`), and
`ReachVs_vs_2_0` / `ReachVs_vs_3_0` are `InitializerDefaultsProbe.ReachVs(vs, "ps_2_0")`, and
`XnaTextured_ps_2_0` / `XnaTextured_ps_3_0` are `InitializerDefaultsProbe.XnaTextured(profile)`. Each was built
with KNI's MGCB (`nkast.Xna.Framework.Content.Pipeline.Builder` 4.2.9001, `tools/MGCB.exe`,
`/platform:Windows /profile:HiDef /compress:False`, an `EffectImporter`/`EffectProcessor` item per file),
and the `.knifx` is the effect payload of its `.xnb`: the int32-length-prefixed bytes starting at `KNIF`
that KNI's `EffectReader` hands to `new Effect`. 4.2.9001 because the driver runs KNI 4.2.9001, whose
runtime reads KNIFX v11 (4.3.9001's MGCB writes v12).

Provenance and licence: these `.knifx` files are build outputs, KNI's MGCB run on ShadowDusk's own
shader sources (the probe strings above, under this repository's licence). They contain no KNI source
or KNI binary code: only the effect container KNI's tool wrote, with the DXBC fxc (d3dcompiler_47,
called by KNI's processor) produced from those sources. Nothing from KNI's own test assets is
committed.
