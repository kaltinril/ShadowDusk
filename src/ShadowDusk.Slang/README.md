# ShadowDusk.Slang

**Status: scaffolding only (Phase 66 A2). No compile route yet — see below.**

Bundles the real [Slang](https://github.com/shader-slang/slang) compiler (`slangc`, win-x64
today) so a consumer who adds this package gets genuine Slang input — `import`, generics,
`interface`s, everything real `slangc` accepts — compiled via `slangc -target hlsl` and
handed to ShadowDusk's existing, unchanged, faithful DXC pipeline (the same DXC every `.fx`
uses). This is a separate, **optional** package: a consumer who does not add
`ShadowDusk.Slang` pays zero size or dependency cost (the `ShadowDusk.ShaderToy` precedent).

`ShadowDusk.Compiler`'s own `.slang` support — an HLSL-compatible subset frontend, no extra
package required — is untouched and stays the default for consumers who don't need full
Slang (`import`, generics, `interface`s).

## Current state

This package currently ships **only the native vendoring plumbing**: `slangc.exe` +
`slang-compiler.dll` (the true minimal file set — see
`plan/PHASE-66-appendix/slang-native-minimal-set-probe/` in the ShadowDusk repository)
packed under `runtimes/win-x64/native/`, plus `SlangToolPath`, a stub that resolves the
path to the packaged native and asserts it exists.

**There is no compile route yet.** The process-based `SlangCompiler` wrapper (mirroring
`ShadowDusk.HLSL.Dxc.DxcShaderCompiler`'s shape) that actually invokes `slangc` and feeds
its HLSL output to DXC is a later stage (Phase 66 A3) — see
`plan/PHASE-66-full-slang-input-implementation.md`.

## License note

The bundled `slangc` binaries are Apache-2.0 licensed (see `THIRD-PARTY-NOTICES.txt` in the
package); ShadowDusk itself is MIT.
