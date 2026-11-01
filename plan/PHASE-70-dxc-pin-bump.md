# Phase 70 — DXC pin bump (v1.7.2212 → the DXC MonoGame ships)

**Track:** Pins and natives.
**Status:** 📋 **Planned (2026-10-31, owner decision on [issue #287](https://github.com/kaltinril/ShadowDusk/issues/287)).**
Not started. Its own phase, never folded into another change, because it moves output bytes on
three targets.

## Why now

The owner's 2026-10-04 decision on #287 was "stay on v1.7.2212; revisit when MonoGame moves to a
newer DXC". That trigger has fired: MonoGame 3.8.5 bundled DXC 1.8.2505.11, and 3.8.6 bundles
**`MonoGame.Tool.Dxc` 1.9.2607.1** ([Phase 69 appendix](DONE/PHASE-69-appendix/monogame-3.8.6-source-diff.md)).
1.9.2607.1 is past both upstream fixes #287 lists (the `RegisterHandlers` race, fixed in v1.7.2308,
and the `setlocale` fix in v1.9.2602), and it is what the `mgfxc` `DirectX_12` goldens are built
with, so the documented DX12 Apos.Shapes `maxd 1` (the DXC pin, `docs/validation-matrix.md` §7)
would close with it.

## What it moves

- **Bytes on DirectX 12, Vulkan and OpenGL** (OpenGL goes through DXC's SPIR-V). DirectX 11 (vkd3d)
  and FNA do not use DXC. Every byte-identity manifest row for those targets is re-baselined, and
  the rung-4 render proof is re-run on every runtime, not inferred.
- **Natives on every RID**: Vortice.Dxc ships win-x64/win-arm64/linux-x64; ShadowDusk builds
  osx-x64/osx-arm64 and android-arm64/x64 itself, and the WASM build. All are re-pinned by hash
  (`DxcNativeIdentity`, the exact `[3.3.4]` Vortice range, `SD0219`).
- **Seeded DXC predefined-macro values** (`__DXC_VERSION_*`, issue #442) move with the pin.
- **Diagnostic text**: DXC's warning and error wording, which ShadowDusk passes through verbatim.

## Plan (to refine before starting)

1. Choose the target: the `MonoGame.Tool.Dxc` version the newest MonoGame bundles (1.9.2607.1 today),
   and find a Vortice.Dxc release that ships it, or build the natives ourselves as for macOS.
2. Rebuild and re-pin every native (desktop, Android, WASM); keep the old pin's assets published.
3. Re-baseline the manifests on Windows, then measure Linux and macOS (the cross-host identity
   tests) and record every divergence.
4. Full `dotnet test` and the full Windows render gate, plus the CI lanes, on the new pin; a
   `CHANGELOG` "Changed" entry naming the byte movement.

## Done when

The new DXC is pinned on every RID, every manifest is re-baselined with a reviewed diff, every
DXC-target render gate is green on it, and #287 is closed.
