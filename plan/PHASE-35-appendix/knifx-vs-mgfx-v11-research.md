# Phase 35 appendix — MGFX v10 vs v11 vs KNIFX: the version-format research

**Status:** Research record (findings captured, no code changed).
**Verified:** 2026-06-12 (re-verify the loader source + version landscape when you start Area B — these are live, evolving forks).
**Why this exists:** Area B ("emit a newer format if a runtime ever needs it") was scoped on the assumption that "v11 / KNIFX" is *one* thing. It is not. This doc captures the research behind that correction so the implementer does not have to re-derive it. Provenance: a KNI-Discord user noted ShadowDusk emits "the older MonoGame v10 Effect format" and pointed at KNI's newer KNIFX format; the questions that follow ("who defines these versions? is v11 shared?") drove a source-level investigation of both runtime forks' effect loaders.

---

## TL;DR (read this, then the evidence below)

1. **There is no third-party standards body.** The MGFX format version is a private constant in **MonoGame's own `Effect.cs`** (`MGFXVersion` / `MGFXMinVersion`). Whoever owns a runtime's effect loader unilaterally decides what version numbers it accepts. MonoGame originated the format; **KNI is a fork of MonoGame** and inherited it; **FNA is a separate lineage** (`.fxb` / D3D9 fx_2_0, not MGFX-versioned at all).

2. **v10 is the one genuinely shared, interoperable format.** Both MonoGame and KNI read `MGFX` v10 today. That is *why* it is, and stays, ShadowDusk's default — it is the single artifact every MGFX-lineage runtime loads.

3. **"v11" is NOT one format — the forks diverged.** They are two incompatible containers that happen to share the number 11:
   - **MonoGame v11** keeps the `MGFX` signature, bumps the version byte to 11, and accepts the range **[10, 11]** under that one signature.
   - **KNI's "v11" is KNIFX** — a brand-new container with its **own distinct 4-byte signature**, accepted only at version 11. KNI still reads `MGFX` **v10** (the migration path) but dropped `MGFX` v09.

4. **ShadowDusk's `--mgfx-version 11` flag is a non-faithful stub** — it writes the `MGFX` signature with the version byte set to 11 on top of an unchanged **v10 body**. That is:
   - **Dead-on-arrival in KNI** (KNI routes the `MGFX` signature to its MGFX path, which requires version == 10, so byte 11 is rejected; the KNIFX path needs the *KNIFX* signature, which we never write).
   - **Unfaithful / unvalidated in MonoGame** (header may pass the [10,11] check, but the body is not a real v11 body; we have never render-validated it).
   - **Therefore it must never be advertised as "v11 support."** It is a header-byte escape hatch, nothing more. Default and only validated value: **10**.

5. **"Supporting v11" is two separate deliverables**, not a byte bump: a faithful MonoGame-`MGFX`-v11 writer *and* a faithful KNI-**KNIFX** writer (new signature + container). Each is its own reverse-engineering job. Both stay **auto-selected from the target, never a consumer flag** (Phase 35 guardrail #1).

---

## 1. Who defines MGFX versions?

- **MonoGame** defines the MGFX binary effect container and its version byte. It is not a published spec — it is a constant (`MGFXVersion`) in `MonoGame.Framework/Graphics/Effect/Effect.cs`, checked at load time. There is **no neutral/third-party authority**.
- **KNI** (nkast's fork of MonoGame) inherited the container and the version-check scheme, then diverged (see §2).
- **FNA** is unrelated to MGFX versioning — it loads the legacy D3D9 `fx_2_0` `.fxb` via FNA3D/MojoShader. ShadowDusk already targets it separately (`PlatformTarget.Fna`, Phase 39). It is mentioned here only to close the "is every XNA-reimpl on MGFX?" question: **no**.

So "v10 / v11" is a **MonoGame-lineage** concept, honored by MonoGame and KNI, irrelevant to FNA.

## 2. The central finding: the forks diverged at v11

| | **MonoGame** | **KNI** | **FNA** |
|---|---|---|---|
| Shared v10 baseline | `MGFX` v10 ✅ | `MGFX` v10 ✅ (migration path) | n/a (own `.fxb` lineage) |
| "v11" | `MGFX` v11 — **same signature**, byte bumped, loader accepts range **[10, 11]** | **KNIFX** — a **new, distinct signature**, accepted only at v11 | n/a |
| v09 | (historical) | **dropped** | n/a |
| Version defined by | MonoGame project | nkast (KNI) | FNA project |

**Consequence:** a real "v11" artifact for MonoGame (`MGFX`+11 body) is **not** byte-shaped like a real "v11" artifact for KNI (`KNIFX` signature). They are different formats. There is no single thing to emit.

## 3. Loader evidence (source-level, both forks)

### MonoGame (`develop`)
`MonoGame.Framework/Graphics/Effect/Effect.cs`:
- Signature: `"MGFX"` (4 bytes).
- `MGFXVersion = 11`, `MGFXMinVersion = 10`.
- Load-time checks: (1) signature == `MGFX`; (2) reject if version < `MGFXMinVersion` ("older release, rebuild"); (3) reject if version > `MGFXVersion` ("newer release"); (4) profile must match platform.
- **Net: accepts `MGFX` version in [10, 11] under one signature.** Our v10 output is accepted unchanged (this is what Phase 35 Area A relied on).
- Source: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs

### KNI (`main`)
`src/Xna.Framework.Graphics/Graphics/Effect/Effect.cs`:
- Checks **two** signatures: `KNIFXHeader.KNIFXSignature` **and** `MGFXHeader.MGFXSignature`.
- **KNIFX path:** accepted at version 11 (`KNIFXHeader.CurrentKNIFXVersion`).
- **MGFX path:** accepted at version **10** only (the migration path); v09 dropped.
- **Net: accepts `MGFX`@10 OR `KNIFX`@11 — two distinct signatures, not a range under one.** Our v10 `MGFX` output is accepted via the MGFX path.
- Source: https://github.com/kniEngine/kni/blob/main/src/Xna.Framework.Graphics/Graphics/Effect/Effect.cs

> The forward-safety conclusion from Phase 35 Area A ("our v10 keeps loading forward") **survives** for both forks — but the *mechanism* differs: MonoGame via a [10,11] range, KNI via a dedicated MGFX-v10 migration path. Do not conflate the two.

## 4. What KNI's KNIFX (v4.02) actually adds

KNIFX shipped in **KNI v4.02 (2025-10-19)** with a new compiler tool **KNIFXC**. Per nkast's blog, the new format exists "to support future features and fix structural limitations of the old format," and fixes several long-standing **XNA-compatibility** issues. These are improvements **over** MGFX v10 behavior — i.e. things a consumer on v10 output does *not* get:

- Samplers no longer override textures when a `Sampler` is declared without a texture (a real XNA-port bug).
- Preserves optimized `Matrix4x4` types (no silent demotion to `Matrix4x3`); respects optimized matrix arrays without buffer overrides.
- Effect processor generates distinct artifacts for OpenGL vs GL-ES.
- GL-ES shaders keep full precision in fragment ops (fewer FP artifacts).
- Clearer exceptions for unsupported shaders instead of cryptic GL/DX errors.
- "Loading previous MGFX v10 shaders is still supported" for migration; **legacy MGFX v09 dropped.**
- Source: https://blog.nkast.gr/

**Why this matters for the product bar:** ShadowDusk's promise is "behaves like `mgfxc` v10." On KNI that means it inherits old-mgfxc-v10 behavior, **including** the quirks KNIFX fixes. So the legitimate open question Area A did not weigh is **render-quality / XNA-compat parity on KNI**, *not* loadability (loadability is fine). Emitting KNIFX is the only way to close that delta — a **product-scope decision**, not a compatibility bug.

## 5. ShadowDusk's current state (as of 2026-06-12)

- Default `MgfxVersion = 10` everywhere (`src/ShadowDusk.Core/CompilerOptions.cs:54`, `MgfxWriterOptions.cs:7`). Validated/rung-4 proven.
- The writer emits the literal `MGFX` signature then the version byte (`src/ShadowDusk.Core/MgfxWriter.cs:100-101`). `--mgfx-version 11` only changes that one byte; **the body stays v10**. There is **no** KNIFX signature, no v11 body, no KNIFX container code anywhere.
- The arg parser accepts `10` or `11` (`ArgumentParserTests.cs`), and `MgfxWriterTests.Header_Version11WhenRequested` asserts byte[4]==11 — i.e. the tests only prove the **byte** is written, not that the result is a loadable v11/KNIFX artifact. Do not mistake those green tests for "v11 works."
- `PlatformTarget.Fna` ignores `MgfxVersion` by design (`PlatformTarget.cs:35`).

## 6. Implications for Phase 35 Area B

- **Re-scope Area B from one task to two.** Emitting "v11" means either (or both): a faithful MonoGame `MGFX`-v11 writer, and/or a faithful KNI **KNIFX** writer (new signature + reverse-engineered container). Pick per the runtime actually being served.
- **Default stays v10**; any newer emission is **auto-selected from the target**, never a consumer-set flag (guardrail #1). `--mgfx-version` remains a non-required escape hatch.
- **The existing `--mgfx-version 11` path is not a head start** — it is wrong for both forks and would need to be either fixed into a real writer or left clearly documented as a raw byte override.
- **v10 keeps working forward regardless** (§3), so Area B is "do we want the KNIFX-era *quality fixes* on KNI?", not "do we need it to load?". Likely never required for loading; only for parity with new-KNI render behavior.

## 7. Open validation gap (do this before claiming KNI parity)

Everything render-validated to date targeted **MonoGame** (3.8.2.1105 floor, 3.8.4.1 stable; 3.8.5 source-read only). **We have never rung-4 render-validated ShadowDusk's v10 output against KNI v4.02's loader specifically** — a *different fork*. The cheap, high-value next step is to confirm "v10 still renders pixel-equivalent in KNI v4.02 web," replacing the current assumption with evidence. (Phase 24 did real-browser KNI render proof, but pre-dates v4.02 / KNIFX.)

## 8. Open questions for the implementer

- Does MonoGame's v11 **body** differ structurally from v10, or is a v10 body labeled 11 actually parseable? (Unverified; `MGFXMinVersion=10` hints the loader *intends* to read v10-era files, but we have not confirmed a v10-bodied/11-labeled file renders. Resolve reproduce-first before trusting `--mgfx-version 11` on MonoGame.)
- What are KNIFX's exact signature bytes, header layout, and body deltas vs MGFX v10? (Read `KNIFXHeader` + the KNIFXC compiler in the KNI repo; reverse-engineer from a KNIFXC-produced sample.)
- Is there a target-detection seam that can auto-select MGFX-v10 vs MonoGame-v11 vs KNIFX without a consumer flag? (Same one-artifact-vs-auto-select design problem as Phase 33's Reach/HiDef and Area C's DXBC/DXIL.)

## 9. Sources

- MonoGame `Effect.cs` (develop) — MGFXVersion=11, MGFXMinVersion=10, `MGFX` signature: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs
- KNI `Effect.cs` (main) — dual `MGFX`(v10) / `KNIFX`(v11) signature check: https://github.com/kniEngine/kni/blob/main/src/Xna.Framework.Graphics/Graphics/Effect/Effect.cs
- nkast blog — KNIFX / KNI v4.02 (2025-10-19) shader-format announcement + XNA-compat fixes: https://blog.nkast.gr/
- KNI releases: https://github.com/kniEngine/kni/releases
