# MonoGame MGFX v11 format, reverse-engineered spec + writer

**Status (2026-06-14): IMPLEMENTED + RENDER-PROVEN.** ShadowDusk emits a faithful MGFX v11 via
`CompilerOptions.MgfxVersion = 11`; the v11 corpus **loads + renders 10/10 in real MonoGame 3.8.5.0**
(`validation/MonoGameV11`), maxd 0 vs v10 and <= 1 vs the mgfxc goldens. Reverse-engineered from MonoGame
`develop` source (2026-06-13). **Do not confuse this with KNI's "v11" (KNIFX), a different format**, see
[knifx-format-spec.md](knifx-format-spec.md).

## The delta: v11 adds exactly two per-shader strings

MonoGame's MGFX v11 body is **v10 plus two `string` fields per shader** (`SourceFile`, then `Entrypoint`),
written immediately after the `isVertexShader` bool and **before** the bytecode length, read conditionally on
`version > 10`. **Everything else (header, footer, effectKey, constant buffers, parameters, techniques,
passes, samplers, attributes, render states) is byte-identical to v10.**

Per-shader record, stream order:

| Field | v10 | v11 |
|---|---|---|
| `isVertexShader` | bool | bool |
| **`SourceFile`** | (absent) | **string** (7-bit-len-prefixed UTF-8) |
| **`Entrypoint`** | (absent) | **string** |
| `bytecode length` | int32 | int32 |
| `bytecode` | bytes | bytes |
| samplers / cbuffer indices / attributes | ... | ... identical ... |

- **Source:** the change is MonoGame PR #8813 ("Better Runtime Shader Compiler Errors", commit `1a4682b1`,
  2025-06-12). Reader: `MonoGame.Framework/Graphics/Shader/Shader.cs` (`if (version > 10) { SourceFile =
  reader.ReadString(); Entrypoint = reader.ReadString(); }`, else both `"<unknown>"`). Writer:
  `Tools/MonoGame.Effect.Compiler/Effect/ShaderData.writer.cs` (`writer.Write(SourceFile ?? "<unknown>");
  writer.Write(Entrypoint ?? "<unknown>");`).
- The loader accepts the **inclusive range** `[MGFXMinVersion=10, MGFXVersion=11]`, so v10 still loads in a
  v11 runtime (the `version > 10` branch is skipped) and v11 requires the two strings.
- **The strings are diagnostic-only** (shader error messages); they do NOT affect rendering. But they shift
  the byte stream, so they must be present and correctly length-prefixed or the reader's `ReadString()`
  consumes the bytecode-length bytes and the whole stream desyncs (load throws).

## Why the old `--mgfx-version 11` was CORRUPT (not just unfaithful)

ShadowDusk's pre-2026-06-14 `--mgfx-version 11` bumped the header version byte to 11 over an **unchanged v10
body** (no per-shader strings). Against a real v11 reader that is **corrupt**: the reader hits `version > 10`,
`ReadString()` reads the 4 bytecode-length bytes as a UTF-8 string length, and the stream desyncs, the file
does not load. So the old path was not a benign "stub", it produced an unloadable v11 file. (It was also DOA
in KNI, which routes the `MGFX` signature to its v10-only migration path.)

## What ShadowDusk does now (faithful writer)

- `CompiledShaderBlob` carries `SourceFile` + `Entrypoint` (default `"<unknown>"`, mgfxc's own null-fallback).
  The pipeline populates them from `options.SourceFileName` and the pass's vertex/pixel entry point.
- `MgfxWriter.WriteShaders(bw, ir, mgfxVersion)`: when `mgfxVersion > 10`, writes the two strings between the
  `isVertexShader` bool and the bytecode length. Default v10 writes nothing new, **v10 output is byte-identical
  to before** (verified: v10 still renders maxd 0 vs the goldens, and the cross-host byte-identity holds).
- Selected via the existing escape hatch `CompilerOptions.MgfxVersion = 11` (or `--mgfx-version 11`), now
  **faithful**. Default stays v10 (the universal, seamless choice).

## Version / stability facts

- `MGFXVersion = 11`, `MGFXMinVersion = 10` on MonoGame `develop`. v11 is **develop/preview only** as of June
  2026 (latest *stable* is 3.8.4, which ships v10). It ships on the **3.8.5** line (`3.8.5-preview.6` /
  `3.8.5-develop.13` on NuGet); ShadowDusk render-validated against `3.8.5-preview.6` (assembly 3.8.5.0).
- Because 3.8.5 is **pre-release**, v11 stays an **opt-in escape hatch**, never the default and never required.
  The product baseline remains MonoGame 3.8.2.1105 / MGFX v10.

## Open items (byte-faithfulness, not load/render)

- The exact `SourceFile`/`Entrypoint` string *contents* mgfxc 3.8.5 emits (it uses the `.fx` path + the HLSL
  entry-point name; ShadowDusk populates the same best-effort, `"<unknown>"` is the faithful fallback). Pin
  against an mgfxc-3.8.5 reference compile if byte-identity to mgfxc is ever wanted (it is a non-goal).
- The strings are confirmed render-irrelevant by source (error-path only) and by the maxd-0 v11-vs-v10 render.
