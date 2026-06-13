# Phase 35 appendix — KNIFX / KNI v11 (Area B) kickoff brief

**Status:** Kickoff brief for Area B (no code changed). Companion to the [version-format research](knifx-vs-mgfx-v11-research.md).
**Verified:** 2026-06-13 (re-verify the loader sources + version landscape when you start — these are live, evolving forks).
**Why this exists:** A self-contained handoff for the agent that picks up Phase 35 **Area B** (emit a newer format if a runtime ever needs it). The [research appendix](knifx-vs-mgfx-v11-research.md) establishes *what* the format landscape is; this brief turns it into an actionable starting point (goal, constraints, code touchpoints, open questions, first moves). Read the research doc first, then this.

---

## Goal
Add the ability to emit KNI's **KNIFX** effect container (and possibly MonoGame's `MGFX` v11) so ShadowDusk effects get the new-KNI render-quality fixes, **without** breaking the existing seamless v10 behavior. This is a **product-scope decision about render parity, not a bug fix** — see "Reality check" before writing any code.

## Reality check (read first, it changes the framing)
- **v10 already loads and runs in KNI v11.** KNI v4.02+ reads two signatures: its new `KNIFX`@11 *and* `MGFX`@10 (a dedicated migration path). ShadowDusk's default v10 output goes through the MGFX path. So **nothing is broken** — a current ShadowDusk effect works in a KNI v11 project today.
- Therefore the *only* thing KNIFX buys is the **quality/XNA-compat fixes** KNIFX adds over v10 (list below). If those aren't needed, this work may legitimately be "won't do." Decide that explicitly up front.
- Provenance: a KNI-Discord user observed ShadowDusk emits "the older MonoGame v10 Effect format" and pointed at KNIFX. That feedback is about quality parity, not loadability.

## The central finding: "v11" is two incompatible formats
The MonoGame and KNI forks diverged at version 11. They share the number, not the bytes (full loader evidence in the [research appendix §2-3](knifx-vs-mgfx-v11-research.md)):

| | MonoGame | KNI (nkast) |
|---|---|---|
| v10 baseline | `MGFX` v10 | `MGFX` v10 (migration path) |
| "v11" | `MGFX` signature, version byte 11, loader accepts range **[10, 11]** | **`KNIFX`** — a **new, distinct 4-byte signature**, accepted only at version 11 |
| v09 | (historical) | **dropped** |

So "support v11" is **two separate deliverables**, each its own reverse-engineering job:
1. A faithful **MonoGame `MGFX`-v11** writer (same signature, byte 11, a *real* v11 body — body deltas vs v10 unverified).
2. A faithful **KNI `KNIFX`** writer (new signature + reverse-engineered container at v11).

Pick per the runtime actually being served. The KNI-Discord feedback points at **KNIFX** as the likely target. KNIFX shipped in **KNI v4.02 (2025-10-19)** with a new compiler tool **KNIFXC**.

## What KNIFX actually adds over MGFX v10 (the motivation)
Per nkast's blog — these are things a v10 consumer does *not* get:
- Samplers no longer override textures when a `Sampler` is declared without a texture (a real XNA-port bug).
- Preserves optimized `Matrix4x4` types (no silent demotion to `Matrix4x3`); respects optimized matrix arrays without buffer overrides.
- Effect processor generates **distinct artifacts for OpenGL vs GL-ES**.
- GL-ES shaders keep **full precision** in fragment ops (fewer FP artifacts).
- Clearer exceptions for unsupported shaders.
- Still loads MGFX v10 (migration); **legacy MGFX v09 dropped.**

## Hard constraints (non-negotiable — from CLAUDE.md and the [main phase guardrails](../PHASE-35-forward-version-support.md))
- **Seamless for the end user.** Never a required ShadowDusk-specific flag to get correct output. Any newer format must be **auto-selected from the target**, never a consumer-set switch. A flag may exist *only* as a non-required escape hatch.
- **Default stays v10**, output format and the MonoGame 3.8.2.1105 pin unchanged. v10 is the one container every MGFX-lineage runtime loads; do not regress it.
- **Fail loudly** (diagnostic with an SD-code) if the pipeline can't faithfully produce a format — never silently miscompile, never punt the choice to the consumer.
- **The bar is the real runtime.** A new writer is not "done" until its output **loads and renders pixel-equivalent to the reference compiler** (here: KNIFXC) in **real KNI v4.02**. Tests are proxies, not the bar. Compare same-backend only.

## The hard design problem (resolve before coding)
`PlatformTarget` today encodes the **graphics backend** (`OpenGL` / `DirectX` / `Fna`), **not** the **runtime fork** (MonoGame vs KNI vs version). MGFX-vs-KNIFX is a runtime-fork distinction. So there is no existing signal that tells the compiler "this consumer is on KNI v11." Auto-selecting KNIFX without a consumer flag is genuinely unsolved — this is the crux. Options to weigh:
- Treat "KNI v11 / KNIFX" as a new platform the consumer's game already targets (the *allowed* kind of additive target), surfaced via the target axis rather than a behavior flag.
- Keep emitting universally-loadable v10 always, and never KNIFX (accept the quality delta).

The same one-artifact-vs-auto-select tension already appeared in Phase 33 (Reach/HiDef) and the DXBC/DXIL choice (Area C); reuse that reasoning.

## The existing `--mgfx-version 11` flag is a TRAP — do not treat it as a head start
It only flips the header version byte on an **unchanged v10 body**. That is:
- **Dead-on-arrival in KNI** (KNI routes the `MGFX` signature to its MGFX path, which requires version `== 10`, so byte 11 is rejected; KNIFX needs the *KNIFX* signature, which is never written).
- Unfaithful/unvalidated in MonoGame (header may pass the [10,11] check, but the body is not a real v11 body, never render-validated).

It must either be fixed into a real writer or remain clearly documented as a raw byte override. **Never advertise it as "v11 support."** Default and only validated value: **10**.

## Where the code lives (current state, verified 2026-06-13)
- `src/ShadowDusk.Core/MgfxWriter.cs` — the container writer. Header at `WriteHeader` (lines ~99-105): 4-byte `MGFX` signature (`MgfxSignatureBytes`, line 22) + 1-byte version + 1-byte profile + 4-byte effectKey (ManagedMd5 of body). Body order: constant buffers -> shaders -> parameters -> techniques, then a trailing `MGFX` footer (line 93). This is the file a KNIFX writer parallels (new signature + container deltas).
- `src/ShadowDusk.Core/MgfxWriterOptions.cs:7` — `MgfxVersion = 10` default.
- `src/ShadowDusk.Core/CompilerOptions.cs:54` — public `MgfxVersion { get; init; } = 10` (the escape hatch).
- `src/ShadowDusk.Core/PlatformTarget.cs:35` — `Fna` ignores `MgfxVersion` by design.
- `src/ShadowDusk.Compiler/Internal/CompilationPipeline.cs:625-637` — validates `MgfxVersion` fits a byte; where format/target selection would hook in.
- `src/ShadowDusk.Cli/ArgumentParser.cs:178`, `CliArguments.cs:13`, `PipelineRunner.cs:43` — CLI plumbing for `--mgfx-version`.
- Tests that only prove the **byte** is written (NOT that v11 loads): `tests/ShadowDusk.Core.Tests/MgfxWriterTests.cs:92` (`Header_Version11WhenRequested`), `ArgumentParserTests.cs:155-160`. Do not mistake these green tests for "v11 works."

## Open questions to resolve (reproduce-first, before trusting anything)
1. Does MonoGame's v11 **body** differ structurally from v10, or does a v10 body labeled 11 actually parse/render? (`MGFXMinVersion=10` hints the loader intends to read v10-era files, but unconfirmed.)
2. What are **KNIFX's exact signature bytes, header layout, and body deltas vs MGFX v10**? Read `KNIFXHeader` + the **KNIFXC** compiler in the KNI repo; reverse-engineer from a KNIFXC-produced sample.
3. The auto-select seam (see "hard design problem").

## Validation gap to close
ShadowDusk's v10 output has **never been rung-4 render-validated against KNI v4.02's loader specifically** — all render proof to date is MonoGame (3.8.2.1105 floor, 3.8.4.1 stable). Phase 24 did real-browser KNI render proof but **pre-dates v4.02/KNIFX**. Cheap, high-value first step: confirm "v10 still renders pixel-equivalent in KNI v4.02 web," then build any KNIFX validation on that harness.

## Key references in-repo
- **Master research:** [knifx-vs-mgfx-v11-research.md](knifx-vs-mgfx-v11-research.md) (re-read first; re-verify the loader sources, they evolve).
- **Main phase doc:** [PHASE-35-forward-version-support.md](../PHASE-35-forward-version-support.md) (Area B is the home of this work; guardrails + Area A forward-compat result).
- Directives/constraints: `CLAUDE.md` (THE PURPOSE, seamless-for-end-user, backwards-compat v10).
- Target axes (why Reach/HiDef/feature-level are NOT compile inputs): `docfx/guides/choosing-a-target.md`.
- Product bar definition: `docs/the-purpose.md`.

## External sources
- MonoGame `Effect.cs` (develop) — `MGFXVersion=11`, `MGFXMinVersion=10`, `MGFX` signature: https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/Effect/Effect.cs
- KNI `Effect.cs` (main) — dual `MGFX`(v10) / `KNIFX`(v11) signature check: https://github.com/kniEngine/kni/blob/main/src/Xna.Framework.Graphics/Graphics/Effect/Effect.cs
- nkast blog — KNIFX / KNI v4.02 announcement + XNA-compat fixes: https://blog.nkast.gr/
- KNI releases: https://github.com/kniEngine/kni/releases

## Suggested first moves
1. Re-read the master research doc; re-verify both loaders' current source.
2. Decide scope: KNIFX, MonoGame `MGFX`@11, or both. (KNIFX is the likely intent.)
3. Obtain a KNIFXC-produced sample and diff its container against ShadowDusk's v10 output.
4. Resolve the auto-select design question (no consumer flag) **before** writing a writer.
5. Stand up a KNI v4.02 render-validation harness; prove v10 first, then the new writer.
