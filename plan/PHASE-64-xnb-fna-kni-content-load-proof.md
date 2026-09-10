# Phase 64 — `.xnb` on FNA and KNI: render-prove the two `Content.Load<Effect>` arms Phase 60 left open

**Track:** Delivery shape / drop-in completeness (the Phase 60 follow-through). Additive; the only
product bytes this phase may move are the **XNB container's type-reader manifest** (§3 C1), which
has never shipped in a release (Phase 60 landed 2026-08-13; the last release is 0.18.0 of
2026-08-03), so no consumer holds a ShadowDusk-written `.xnb` yet.

**Status:** 🔵 **Planned (2026-09-09)** — investigation complete, implementation not started. Every
claim in §2 was **measured on this date** with real runtimes (three MonoGame versions, two KNI
versions, real FNA) and real reference tooling (`dotnet-mgcb` 3.8.4.1 and 3.8.5); nothing below is
inferred from documentation. The measurements changed the picture in one important way: **the
KNI arm is not merely unproven, it is broken on the KNI version this repo pins**, and the fix is a
one-string change to `XnbWriter` (§2.3, §3 C1).

**Depends on:** [Phase 60](DONE/PHASE-60-xnb-content-output.md) (the writer, the DirectX rung-4
gate, and the two unit-pinned whitelists), [Phase 39](DONE/PHASE-39-fna-fx2-output-target.md)/[40](DONE/PHASE-40-fna-fidelity-hardening.md)
(the FNA `.fxb` payload and the real-FNA harness `validation/FnaValidation`),
[Phase 44](DONE/PHASE-44-validation-breadth-and-matrix-coverage.md) (the real-KNI desktop harnesses
`validation/KniDesktopGL` / `KniWinFormsDX`, whose SDL2.GL recipe the KNI arm reuses),
[Phase 24](DONE/PHASE-24-browser-render-validation.md) (KNI WebGL, the other KNI consumer of the same
`Content` assembly).

**Blocks:** closing Phase 60's C4 checkbox and OQ3; the `project_facts.md` line that still says
*"FNA and KNI `Content.Load` arms not yet proven"*.

**Gated on:** nothing external. The requester is actively testing (§0), so the ordering pressure is
real: the KNI defect in §2.3 is the first thing a KNI user on the current-minus-one release hits.

---

## 0. The request, and where it stands

> *"How can we support XNB for monogame/kni/fna? This would allow users to replace their content
> pipeline with ShadowDusk, but not change any lines of code at all."*
> — [issue #199](https://github.com/kaltinril/ShadowDusk/issues/199), vchelaru (Victor Chelaru,
> Gum / FlatRedBall maintainer), 2026-08-09

The owner replied on 2026-08-14 that it shipped in PR #201 (Phase 60): ShadowDusk writes the
`.xnb` itself (`ShadowDuskCLI MyShader.fx Content/MyShader.xnb /Profile:OpenGL`, or
`result.Value.ToXnb()`), the platform byte is derived from the target, and it is rung-4 proven on
DirectX via `validation/XnbContentLoad` (4/4 fixtures pixel-identical vs a stock mgcb 3.8.4.1
build). The reply stated one honest gap: **"the FNA and KNI `Content.Load` arms are
whitelist-verified but not yet render-proven."**

On 2026-09-10 vchelaru replied *"I'll run some tests and see how this works."* He maintains Gum
(MonoGame, KNI, FNA, and SkiaSharp runtimes) and FlatRedBall, so the likely test surface is the CLI
against a **MonoGame DesktopGL** game and/or **KNI** and **FNA**. This phase exists so that what he
finds is what we already know.

---

## 1. What is already shipped and proven — do not redo this

Established by Phase 60 and re-confirmed by the §2 measurements on 2026-09-09:

- **The writer.** `XnbWriter` in `ShadowDusk.Core` (`src/ShadowDusk.Core/XnbWriter.cs`), pure
  managed, surfaced as `CompiledShader.ToXnb()` and as the CLI's **extension-driven** `.xnb`
  output (`PipelineRunner.cs`, the `wrapAsXnb` branch: `.xnb`/`.XNB` wraps, anything else passes
  through). One writer behind both surfaces; the payload is `CompiledShader.Data` verbatim.
- **The container** (uncompressed, format version 5, one type reader, no shared resources,
  type id 1, int32 payload length, payload) is byte-identical to `dotnet-mgcb` 3.8.4.1's through
  the type id, pinned offline by `XnbWriterTests` (golden captured from a real mgcb build) and
  live by `validation/XnbContentLoad`'s C1 assertion.
- **Platform byte derived from the target**, never asked for; pinned by
  `XnbWriterTests.PlatformIdentifier_IsDerivedFromTheTarget` and checked against both runtime
  whitelists by `EveryDerivedIdentifier_IsAcceptedByTheRuntimeWhitelists`.
- **Rung 4 on MonoGame WindowsDX** (`validation/XnbContentLoad`, default-ON in
  `run-windows-render-gates.ps1`): real `ContentManager.Load<Effect>(assetName)` on a
  ShadowDusk-written `.xnb` vs the stock-mgcb `.xnb`, 4/4 fixtures, 1,230,720 px identical each.
- **CLI half** (`CliXnbOutputTest`): `.xnb` payload byte-identical to the `.mgfx` the same
  invocation writes, on OpenGL and DirectX_11; non-`.xnb` extensions unwrapped.
- **Not proven by anything in-repo**: a `Content.Load<Effect>` on **MonoGame DesktopGL** (the most
  common consumer; the gate is WindowsDX only), on **KNI**, and on **FNA**. §2 measures all three
  with throwaway probes; §3 turns them into gates.

---

## 2. What was measured (2026-09-09)

All probes live in the session scratchpad (`…/scratchpad/issue199/{KniXnbProbe,KniXnbProbe43,
FnaXnbProbe,MgXnbProbe}`) and their sources are copied verbatim into
[`PHASE-64-appendix/`](PHASE-64-appendix/) so the implementation wave can start from them. Each
probe drops one `Grayscale.xnb` per arm into its own bare directory, points a real
`ContentManager(Services, dir)` at it, calls `Load<Effect>("Grayscale")`, renders the cat through
the same `SpriteBatch` scene `validation/XnbContentLoad` uses, and pixel-compares arms in process.
Fixture: `tests/fixtures/shaders/Grayscale.fx`. CLI: the worktree's Release build of
`ShadowDuskCLI` at commit 445fddb.

### 2.1 The platform byte, per CLI profile, against what real mgcb writes

Every ShadowDusk `.xnb` below was produced by
`ShadowDuskCLI Grayscale.fx Grayscale_<profile>.xnb /Profile:<profile>` (or `--target-runtime`),
then hex-dumped. The mgcb column is `dotnet mgcb /platform:<X> /compress:False` on the same
fixture: 3.8.4.1 (the repo's pinned tool) for the six classic platforms, and **3.8.5** (installed
into a scratch `--tool-path` for this measurement) for `WindowsDX12` and `DesktopVK`, which
3.8.4.1 does not know.

| CLI selector | `PlatformTarget` | ShadowDusk byte | Payload | mgcb writes | Match |
|---|---|---|---|---|---|
| `/Profile:OpenGL` (also `--target-runtime monogame-gl`) | `OpenGL` | **`'d'`** | MGFX v10, GL profile 0 | `/platform:DesktopGL` → `'d'` (3.8.4.1 and 3.8.5) | ✅ |
| `/Profile:DirectX_11` (also `monogame-dx`; **the CLI default when `/Profile` is omitted**) | `DirectX` | `'w'` | MGFX v10, DX profile 1 | `/platform:Windows` → `'w'` | ✅ |
| `/Profile:DirectX_12` | `DirectX12` | `'G'` | MGFX v11, profile 2 (DXIL) | `/platform:WindowsDX12` → **`'G'`** (3.8.5, now measured, not just whitelist-derived) | ✅ |
| `/Profile:Vulkan` | `Vulkan` | `'V'` | MGFX v11, profile 80 (SPIR-V) | `/platform:DesktopVK` → **`'V'`** (3.8.5, measured) | ✅ |
| `/Profile:FNA` (also `--target-runtime fna`) | `Fna` | `'w'` | fx_2_0 `.fxb` | n/a (no mgcb platform emits an `.fxb`); `'w'` is in FNA's list | ✅ by whitelist |
| `--target-runtime monogame-gl-v11` | `OpenGL` | `'d'` | MGFX **v11**, GL | `/platform:DesktopGL` → `'d'` (3.8.5 also writes v11 payloads) | ✅ |
| `--target-runtime kni-knifx` | `OpenGL` | `'d'` | **KNIFX** v11 | n/a (KNI's own pipeline) | ✅ (§2.4) |
| *(mgcb only)* `/platform:Android` / `iOS` / `MacOSX` / `Web` | — | — | — | `'a'` / `'i'` / `'X'` / `'b'` | ShadowDusk always emits `'d'` for GL; every runtime accepts it (§2.2) |

So **yes: `/Profile:OpenGL` emits `'d'`, the DesktopGL byte, and a MonoGame DesktopGL game loads
it** (§2.5, three MonoGame versions, pixel-identical to mgcb's own build). The DesktopGL-vs-WindowsDX
mismatch vchelaru could hit is not the byte, it is the **payload**: if he omits `/Profile` the CLI
defaults to `DirectX_11` (mgfxc parity) and the DesktopGL runtime fails with
`Exception: This MGFX effect was built for a different platform!` (MonoGame, all three versions) or
`Exception: Effect profile 'DirectX_11' is not compatible with the graphics backend 'OpenGL'.`
(KNI 4.3). That is a documentation gap (§2.7, §3 D), not a derivation defect.

The type-reader manifest is identical in every ShadowDusk file:
`Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, Version=3.8.4.1, Culture=neutral,
PublicKeyToken=null`, reader version 0. mgcb 3.8.5 writes the same shape with `Version=3.8.5.0`.

### 2.2 Whitelists, from source at the exact versions consumers run

| Runtime (source) | Accepts `'d'` | `'w'` | `'G'` | `'V'` | `'b'` | Rejection text for an unknown byte |
|---|---|---|---|---|---|---|
| MonoGame **3.8.1.263** (decompiled from the NuGet cache; the measured floor) | ✅ | ✅ | ✅ | ❌ | ✅ | `ContentLoadException: Asset does not appear to be a valid XNB file. Did you process your content for Windows?` |
| MonoGame **3.8.2.1105** (decompiled) | ✅ | ✅ | ✅ | ❌ | ✅ | same |
| MonoGame **3.8.5** (`MonoGame.Framework/Content/ContentManager.cs:37`, tag `v3.8.5`) | ✅ | ✅ | ✅ (`// Windows GDK`) | ✅ (`// DesktopVK`) | ✅ | same |
| KNI **4.2.9001** (`src/Xna.Framework.Content/Content/ContentManager.cs:30`, tag `v4.2.9001`; decompiled assembly agrees) | ✅ | ✅ | ✅ (`// Google Stadia`, legacy) | **❌** | ✅ (`// BlazorGL`) | `ContentLoadException: Asset does not appear to target a known platform. Platform Identifier: 'V'.` (measured) |
| KNI **4.3.9001** (raw file at tag) | ✅ | ✅ | ✅ | ❌ | ✅ | same (measured) |
| FNA **26.06** (`external/FNA/src/Content/ContentManager.cs:77`) and `master` (76b1aef) | ✅ | ✅ | ❌ | ❌ | ❌ | **no exception at the byte**: an unlisted byte makes `ReadAsset` treat the file as a *raw* asset and hand the whole `.xnb` to MojoShader, which fails with `InvalidOperationException: MOJOSHADER_compileEffect Error: Not an Effects Framework binary` (measured for `'V'` and `'G'`) |

Consequences already correct in the shipped derivation: `'V'` only needs to load on MonoGame
3.8.5 (the only runtime with DesktopVK) and `'G'` only on MonoGame 3.8.5 WindowsDX12; the FNA
target's `'w'` is in every list. All three runtime families validate the byte **only for
membership** — measured directly: a GL payload wrapped as `'w'`, `'b'` or `'G'` loads and renders
identically on MonoGame and on both KNI versions, and the FNA payload wrapped as `'d'` loads and
renders identically on FNA.

### 2.3 The type-reader manifest: the KNI arm is BROKEN on KNI 4.2.9001, and the fix is one string

This is the finding Phase 60 OQ3 existed to make, and "KNI is a MonoGame fork and consumes stock
mgcb output, so it almost certainly shares the list" turned out to be wrong for the part that
matters. The platform whitelist is fine; **the reader-name resolver is not**.

**How each runtime resolves the reader name** (source, exact versions):

- **MonoGame** `ContentTypeReaderManager.PrepareType` (identical in 3.8.1.263, 3.8.2.1105, 3.8.5):
  strips `, Version=…` **only when the string contains `PublicKeyToken`**, then replaces
  `, Microsoft.Xna.Framework.Graphics` / `.Video` / `, Microsoft.Xna.Framework` with its own
  assembly name and calls `Type.GetType`. A `, MonoGame.Framework` suffix is left as-is and resolves
  because that IS its assembly.
- **FNA** `ContentTypeReaderManager` (26.06 and master, line 59): one compiled regex,
  `, (Microsoft.Xna.Framework.Graphics|Microsoft.Xna.Framework.Video|Microsoft.Xna.Framework|MonoGame.Framework), Version=.+?, Culture=.+?, PublicKeyToken=[^\]]+`,
  replaced by FNA's own assembly full name. No match ⇒ `Type.GetType` fails ⇒
  `ContentLoadException: Could not find ContentTypeReader Type…`.
- **KNI** `ContentTypeReaderManager.ResolveReaderType`
  (`src/Xna.Framework.Content/Content/ContentTypeReaderManager.cs:195-286` at `v4.2.9001`): strips
  the version when `PublicKeyToken` is present, maps `, Microsoft.Xna.Framework.Graphics` →
  `Xna.Framework.Graphics` (etc.), tries `Type.GetType`, then **appends** its assembly names to the
  stripped string (`readerTypeName + ", Xna.Framework.Graphics"`, line 248), and only *after* that
  tries the `, MonoGame.Framework` → `Xna.Framework.*` replacements (lines 262-281). For the
  mgcb-shaped name the stripped string is `…EffectReader, MonoGame.Framework`, so line 248 builds
  `…EffectReader, MonoGame.Framework, Xna.Framework.Graphics` — an assembly-qualified name with two
  assembly parts — and **`Type.GetType` throws `FileLoadException: The given assembly name was
  invalid.` before the `MonoGame.Framework` replacements are ever reached.** KNI **v4.3.9001** wraps
  exactly those three `Type.GetType` calls in `catch (FileLoadException) { /* ignore */ }`
  (lines 249-261 at that tag; `main` is the same), so the fall-through works there.
- **What KNI's own content pipeline writes** for an effect
  (`src/Xna.Framework.Content.Pipeline.Graphics/Serialization/Compiler/CompiledEffectWriter.cs:22-30`
  at `v4.2.9001`): the **XNA 4.0 string**,
  `Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553`.
  That is also what XNA 4.0 itself wrote, i.e. the string every XNA-derived runtime was written
  to accept first.

**Measured manifest matrix** — each cell is a real `Content.Load<Effect>` of the ShadowDusk GL v10
payload (FNA: the `.fxb`) in a container whose only variable is the reader name; "OK" means it
loaded and rendered **pixel-identical (maxd 0)** to the reference arm on that runtime (MonoGame:
mgcb's own `.xnb`; KNI: mgcb's payload; FNA: the `fxc` oracle via `new Effect`):

| Reader name in the container | MonoGame 3.8.1.263 | MonoGame 3.8.2.1105 | MonoGame 3.8.5 | KNI 4.2.9001 | KNI 4.3.9001 | FNA 26.06 |
|---|---|---|---|---|---|---|
| **mgcb-shaped** (what `XnbWriter` emits today, and what stock mgcb writes) `…, MonoGame.Framework, Version=3.8.4.1, Culture=neutral, PublicKeyToken=null` | OK | OK | OK | **FAIL** `FileLoadException: The given assembly name was invalid.` (stock mgcb's own `.xnb` fails identically) | OK | OK |
| **XNA-4.0-shaped** (what KNI's pipeline and XNA wrote) `…, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553` | OK | OK | OK | **OK** | OK | **OK** |
| KNI-native `…, Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null` | FAIL `Could not find ContentTypeReader Type…` | FAIL | FAIL | OK | OK | not tried (cannot match FNA's regex) |
| bare `…EffectReader, MonoGame.Framework` (the "tidy" string Phase 60 rejected) | OK | OK | OK | FAIL (same `FileLoadException`) | OK | FAIL `ContentLoadException: Could not find ContentTypeReader Type. Please ensure the name of the Assembly that contains the Type matches the assembly in the full type name: Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework (…)` |

**Only the XNA-4.0-shaped manifest loads on every runtime measured.** It is the intersection of the
three resolvers by construction (MonoGame's `Graphics` replacement, FNA's regex alternative, KNI's
first-choice mapping), it is the string the oldest content any of these runtimes must read carries,
and it renders pixel-identically to each runtime's reference on every cell. Phase 60's finding 2
("emitting exactly what mgcb emits is the choice that cannot be wrong") was right about MonoGame
and FNA and wrong about KNI ≤ 4.2. The **first-class fix** is §3 C1.

Two things this also settles: KNI's `EffectReader` is structurally the same
length-prefixed-bytes-to-`new Effect(gd, data, 0, size)` reader as MonoGame's and FNA's
(`src/Xna.Framework.Graphics/Content/EffectReader.cs`), so the container shape needs no KNI
variant; and KNI checks two flag bits MonoGame does not (`ContentFlagHiDef`, and an "Ext"
compression header) — irrelevant for this writer's `flags = 0x00`.

### 2.4 KNI probe (`KniXnbProbe`, SDL2.GL desktop, real `nkast.*` 4.2.9001; `KniXnbProbe43`, 4.3.9001)

Runtime-integrity print: `Xna.Framework.Game 4.2.9001.0` / `4.3.9001.0`; content assembly
`Xna.Framework.Content`. Reference arm on 4.2 = the **mgcb DesktopGL payload re-wrapped with the
XNA manifest** (the only container the runtime accepts); on 4.3 the stock mgcb `.xnb` itself also
loads and the two agree at maxd 0.

| Arm | KNI 4.2.9001 | KNI 4.3.9001 |
|---|---|---|
| stock mgcb 3.8.4.1 DesktopGL `.xnb` | FAIL `FileLoadException` | OK, maxd 0 |
| ShadowDusk `/Profile:OpenGL` `.xnb` (v10, `'d'`) — **the shipped route** | **FAIL `FileLoadException: The given assembly name was invalid.`** | OK, maxd 0 |
| ShadowDusk `--target-runtime kni-knifx` `.xnb` (KNIFX, `'d'`) | FAIL (same) | OK, maxd 0 |
| same two payloads with the XNA manifest | **OK, maxd 0** (v10 and KNIFX both) | OK, maxd 0 |
| ShadowDusk `--target-runtime monogame-gl-v11` `.xnb` | (manifest fails first); with XNA manifest: `Exception: This effect seems to be for a newer version of KNI.` | `Exception: This effect is an unsupported effect format. Please rebuild the effect using the KNI content pipeline.` |
| GL payload as `'w'` / `'b'` / `'G'` | manifest fails first; with XNA manifest `'w'` OK maxd 0 | all OK, maxd 0 |
| GL payload as `'V'` | `ContentLoadException: Asset does not appear to target a known platform. Platform Identifier: 'V'.` | same |
| ShadowDusk `/Profile:DirectX_11` `.xnb` on the GL runtime | manifest fails first | `Exception: Effect profile 'DirectX_11' is not compatible with the graphics backend 'OpenGL'.` |

So: **KNI loads a v10 `.mgfx` and a `.knifx` from an `.xnb` equally** (OQ3's other half), MGFX v11
is a MonoGame-only container as documented, and the *only* KNI blocker is the manifest string.
The KNI DirectX (WinForms.DX11) runtime shares the same `Xna.Framework.Content` assembly, so the
resolver behaviour is the same there; it was not separately probed (§3 A covers it).

Exact commands (from the scratchpad): `dotnet build -c Release` in `KniXnbProbe/`, then
`bin/Release/net8.0/KniXnbProbe.exe <scratchpad>/issue199 <worktree>`; `KniXnbProbe43/` is the same
source with `Version="4.3.9001"`.

### 2.5 MonoGame DesktopGL probe (`MgXnbProbe`, built three times with `-p:MgVersion=`)

The gap Phase 60's gate left for the most common consumer, closed by measurement:

| Arm | 3.8.1.263 | 3.8.2.1105 | 3.8.5 |
|---|---|---|---|
| stock mgcb 3.8.4.1 DesktopGL `.xnb` (reference) | OK | OK | OK |
| ShadowDusk `/Profile:OpenGL` `.xnb` (mgcb manifest, `'d'`) | **OK, maxd 0** | **OK, maxd 0** | **OK, maxd 0** |
| ShadowDusk payload, XNA manifest | OK, maxd 0 | OK, maxd 0 | OK, maxd 0 |
| mgcb payload, XNA manifest | OK, maxd 0 | OK, maxd 0 | OK, maxd 0 |
| ShadowDusk payload, KNI-native manifest | FAIL (`Could not find ContentTypeReader Type…`) | FAIL | FAIL |
| ShadowDusk payload, bare manifest | OK | OK | OK |
| GL payload as `'w'` | OK, maxd 0 | OK, maxd 0 | OK, maxd 0 |
| ShadowDusk `/Profile:DirectX_11` `.xnb` on DesktopGL | `Exception: This MGFX effect was built for a different platform!` | same | same |

### 2.6 FNA probe (`FnaXnbProbe`, real FNA 26.06 from `validation/FnaValidation/external/FNA`, fnalibs win-x64, `FNA3D_FORCE_DRIVER=D3D11`, RTX 3080)

Reference arm = the `d3dcompiler_47` `fx_2_0` oracle (`ReferenceFx2Compiler`, the existing gate's
own oracle) loaded with `new Effect(gd, bytes)`. `FNALoggerEXT.LogError` hooked so MojoShader text
is captured.

| Arm | Result |
|---|---|
| ShadowDusk `/Profile:FNA` `.xnb` (`'w'`, `.fxb` payload) via `ContentManager.Load<Effect>` — **the shipped route** | **OK, 1,230,720 px, maxd 0 vs the fxc oracle** |
| fxc oracle bytes wrapped by `XnbWriter` (`'w'`) via `Content.Load` | OK, maxd 0 (the container is payload-independent) |
| ShadowDusk `.fxb` as `'d'` | OK, maxd 0 (whitelist-only validation, confirmed on FNA) |
| ShadowDusk `.fxb` as `'V'` / `'G'` | `InvalidOperationException: MOJOSHADER_compileEffect Error: Not an Effects Framework binary` — FNA falls to its raw-asset path (§2.2) |
| bare manifest | `ContentLoadException: Could not find ContentTypeReader Type…` — **Phase 60 §2 finding 2 verified on the real thing** |
| XNA-4.0 manifest | OK, maxd 0 |
| ShadowDusk `/Profile:OpenGL` `.xnb` on FNA | `MOJOSHADER_compileEffect Error: Not an Effects Framework binary` (a `.mgfx` is not an `.fxb`; same text as the wrong-byte case, which is worth a docs line) |

**The FNA arm is correct as shipped.** What it lacks is a *gate*, not a fix.

### 2.7 The CLI path a consumer takes (item 4 of the investigation)

Run with the worktree's Release `ShadowDuskCLI`, all exit 0 unless stated:

| Case | Result |
|---|---|
| `ShadowDuskCLI "dir with spaces/My Shader.fx" "dir with spaces/out dir/My Shader.xnb" /Profile:OpenGL` | OK, `XNBd…`, and the missing `out dir` was created |
| output in a directory three levels deep that does not exist | OK — `PipelineRunner` calls `Directory.CreateDirectory` before writing. (**mgfxc 3.8.4.1 does NOT**: `Could not find a part of the path … Failed to write …`, exit 1. ShadowDusk is strictly more lenient; not a defect.) |
| `/Debug`, `/Defines:FOO=1;BAR` with an `.xnb` output | OK; both flow through the same compile, then wrap (Grayscale's GL output is unaffected by either, 682 bytes) |
| `.XNB` (upper-case extension) | OK, wrapped (`OrdinalIgnoreCase`) |
| **no `/Profile`** with an `.xnb` output | OK, exit 0 — but emits `'w'` + DXBC, i.e. the DesktopGL load failure in §2.5. Same default as mgfxc; **documented only in the "Default profile" note of `dropin-mgfxc.md`, not next to any `.xnb` example** (§3 D) |
| `Desaturate.slang … out.xnb /Profile:OpenGL --input-format slang` and the auto-detected form | OK, `XNBd…` (Phase 61 route composes with the wrapper) |
| `ShadowDuskCLI` with no arguments (usage text) | **the usage text does not mention `.xnb` output at all** (§3 D) |

No CLI defect was found. Two rough edges are documentation (§3 D).

### 2.8 Where a consumer reads about `.xnb` today (item 5)

Mentions exist in `README.md` (§"Direct `.xnb` output"), `docs/the-purpose.md` (delivery shape 5),
`docfx/index.md`, `getting-started/overview.md`, `guides/dropin-mgfxc.md`, `guides/choosing-a-target.md`
(the `.mgfx`-vs-`.xnb` table), `guides/parameters-and-caveats.md`, `guides/mgcb-content-pipeline.md`
(route 0), `glossary.md`, `contributing/validation.md`, and `CHANGELOG.md`. Gaps, all of them
things vchelaru would hit before reading `docs/validation-matrix.md`:

1. **The "FNA and KNI not render-proven" caveat is stated nowhere a consumer reads.** Every
   consumer-facing mention says "proven with a real `ContentManager`" without saying *DirectX
   only*; the caveat lives in the owner's issue comment, `project_facts.md`, and the validation
   matrix. After this phase the caveat becomes false, but until §3 A/B land the docs overclaim.
2. **The platform byte is described as "derived" but never as *what*.** No page says
   `/Profile:OpenGL → 'd' (DesktopGL)`, `/Profile:DirectX_11 → 'w'`, or that the byte is
   whitelist-validated only (so one GL `.xnb` serves DesktopGL, Android, iOS, macOS and Web). A
   consumer who compares against an mgcb Android build sees `'a'` vs `'d'` and has no text telling
   them it does not matter.
3. **The CLI default profile is not restated beside any `.xnb` example**, and the CLI usage text
   and `docfx/cli/index.md` (the CLI reference page) and `src/ShadowDusk.Cli/README.md` do not
   mention `.xnb` output at all. The reference page's argument table says `<OutputFile>` is
   "the output" with no note that the extension selects the container.
4. **The wrong-runtime failure texts are undocumented** (§2.5/§2.6 last rows). A `/Profile:OpenGL`
   file on FNA fails with a MojoShader message that does not mention ShadowDusk, the profile, or
   the container; a `/Profile:DirectX_11` file on DesktopGL says "built for a different platform".
   One table mapping each message to "you picked the wrong `/Profile:`" is cheap and saves a
   support round-trip.
5. **KNI version dependence** (§2.3) is not documented anywhere because it was not known.

---

## 3. Areas

### Area C — fix the defect first (the manifest), then the small ones

- **C1. Switch `XnbWriter.EffectReaderTypeName` to the XNA-4.0-shaped string**
  `Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553`.
  It is the only manifest measured to load on all six runtime versions (§2.3), it is what KNI's own
  pipeline and XNA itself write, and the change is **free of compatibility cost**: Phase 60 has not
  been released, so no consumer's `.xnb` carries the mgcb-shaped string. The alternatives were
  weighed and rejected: deriving the manifest from `--target-runtime kni-*` would make a KNI ≤ 4.2
  consumer opt in to get correct output, exactly the defect the seamlessness directive forbids
  (and plain `/Profile:OpenGL` v10 is the *documented* KNI route, with no KNI marker on it); and
  "KNI ≤ 4.2 unsupported" contradicts both the product statement (MonoGame, KNI, FNA) and the
  runtime this repo's own KNI harnesses pin. Record the choice in `project_decisions.md`, replacing
  the Phase 60 rationale for the mgcb-shaped string, and update the `XnbWriter` XML doc comment
  (it currently says *"KNI is a MonoGame fork and consumes stock mgcb output"*, measured false).
  Consequences to carry:
  - `XnbWriterTests.MgcbDesktopGlEnvelope` golden → the new bytes (capture them from the writer
    once, after the load proofs below are green, and say in the comment which runtimes proved
    them); the `Envelope_IsByteIdenticalToRealMgcbOutput` name and intent change to "identical to
    mgcb through the reader-count byte and after the reader name; reader name is the XNA-4.0
    string".
  - `validation/XnbContentLoad`'s C1 assertion (`AssertEnvelopeMatches`) → compare header, reader
    count, reader version, shared-resource count and type id byte-for-byte, and assert the reader
    name equals the XNA-4.0 constant rather than mgcb's. The positive-control "payload differs"
    stays.
  - The MonoGame WindowsDX rung-4 gate must be re-run and stay 4/4 (it will: §2.5 proved the
    string on three MonoGame versions on GL, and `PrepareType` is backend-independent).
  - `docs/validation-matrix.md` §6 row, `CHANGELOG.md` `[Unreleased]`, and the Phase 60 doc's
    finding 2 paragraph get the corrected statement.
- **C2. No CLI code defect** (§2.7). Keep `Directory.CreateDirectory`; keep the mgfxc-parity
  default profile. If wave 2 wants a guard rail, the seamless-compatible option is a **warning on
  stderr** when an `.xnb` output is requested with the implicit default profile (never an error,
  never a required flag), since the implicit `DirectX_11` is the one case where "it compiled" and
  "it loads in my DesktopGL game" diverge silently. Optional.

### Area A — KNI `Content.Load<Effect>` render proof (new driver, default-ON)

- **A1.** New driver **`validation/KniXnbContentLoad`** (SDL2.GL desktop, `net8.0`, the
  `KniDesktopGL` package set and runtime-integrity guard; opted out of central package
  management like every KNI harness). Start from `PHASE-64-appendix/KniXnbProbe/`. Per fixture
  (the same four as `XnbContentLoad`: Grayscale, VertexAndPixel, MultiTexture, SpriteEffect):
  build the reference `.xnb` through stock `dotnet mgcb /platform:DesktopGL` (the `LocateMgcb`
  helper from `XnbContentLoad`), build the candidate through `EffectCompiler` + `ToXnb()` for
  **both** `EffectContainer.Mgfx` (v10) and `EffectContainer.Knifx`, load all three by asset
  name through real KNI `ContentManager`s, render through the shared scene, require pixel-identity
  (tolerance 0, GL-vs-GL on one device).
- **A2. Two KNI versions, because the resolver differs.** The driver builds twice
  (`-p:KniVersion=4.2.9001` and `4.3.9001`, the way `MgXnbProbe` did) and the gate runs both. On
  4.2 the stock mgcb `.xnb` is *expected* to fail to load (§2.3): assert that failure explicitly
  as a pinned KNI fact (it is the whole reason C1 exists), and use the mgcb payload re-wrapped by
  `XnbWriter` as the reference arm there. On 4.3 use the stock file directly and additionally
  assert it agrees with the re-wrapped one.
- **A3.** Optionally a KNI **DirectX** arm in `validation/KniWinFormsDX` (`/platform:Windows`
  reference, `PlatformTarget.DirectX` candidate) — same `Content` assembly, so it is a cheap
  belt-and-braces row rather than new evidence; include it if it costs under an hour.
- **A4.** Registration: a `docs/validation-matrix.md` §6 row with the exact command
  (`dotnet tool restore`, then `dotnet run -c Release --project validation/KniXnbContentLoad`, once
  per KNI version), a default-ON slot in `run-windows-render-gates.ps1` next to the XNB gate, a
  `docs/repository-layout.md` entry, and the §1 KNI cells' `.xnb` mention.

### Area B — FNA `Content.Load<Effect>` render proof (extend the existing opt-in gate)

- **B1.** Extend **`validation/FnaValidation`** rather than add a driver: FNA's restore is heavy
  and needs an authenticated `gh` (fnalibs are CI artifacts), which is exactly why that gate is
  `-IncludeFna` opt-in; a second FNA driver would inherit the same restore and the same flag. Add a
  **third arm per gate shader**: the candidate `.fxb` wrapped by `XnbWriter.Wrap(bytes,
  PlatformTarget.Fna)`, written to a bare temp directory as `<name>.xnb`, loaded with a real FNA
  `ContentManager.Load<Effect>(name)`, rendered through the existing scene selection (`Sprite` /
  `VsQuad`, technique-by-name rows included), and compared to the fxc-oracle arm with the gate's
  existing 4/255 tolerance (candidate-vs-oracle is a cross-compiler compare) **and** to the
  candidate-`new Effect` arm at tolerance 0 (same bytes, so any difference is the container).
  `FnaEffectImageRenderer.RunArm` already takes bytes; the smallest change is a `LoadVia` enum on
  `ShaderCase` (raw / `.xnb`) and a per-case content directory.
- **B2.** Keep `ExpectedGateTotal` honest: either the `.xnb` arm is a per-row sub-verdict that
  does not change the 17 count, or the constant and the documented "17/17" move together (the
  driver's own comment insists on that).
- **B3.** Registration: update the FNA §6 row (and its opt-in reason line) to say the gate now
  includes the `Content.Load` arm; `README.md` in `validation/FnaValidation`; the §1 FNA cell.

### Area D — consumer-facing docs (the §2.8 list)

- **D1.** State the platform-byte derivation as a table (`/Profile:` → byte → which runtime
  platforms accept it) in `guides/dropin-mgfxc.md`'s `.xnb` section and in `README.md`'s block; say
  in one sentence that the byte is whitelist-validated, so one GL `.xnb` serves every GL platform.
- **D2.** Restate the CLI default (`DirectX_11`) beside every `.xnb` example, with the exact
  DesktopGL failure text, and add the wrong-runtime message table (§2.5/§2.6 last rows, KNI 4.3's
  text too).
- **D3.** Add `.xnb` output to the CLI usage text (`ArgumentParser.UsageText`, one line under
  `<OutputFile>`), to `docfx/cli/index.md`'s argument table, and to `src/ShadowDusk.Cli/README.md`.
- **D4.** Once A and B are green, say "proven on MonoGame (DesktopGL and WindowsDX), KNI (4.2.9001
  and 4.3.9001) and FNA" wherever the docs currently say "proven with a real `ContentManager`";
  until then, say "DirectX only" there. Note the KNI-version fact in `parameters-and-caveats.md`
  ("KNI ≤ 4.2.9001 cannot load a stock MonoGame-mgcb effect `.xnb`; ShadowDusk's `.xnb` loads on
  both because it carries the XNA-4.0 reader name").
- **D5.** The support-surface checklist in `CLAUDE.md` applies: `validation-matrix.md` (§1 cells,
  §6 rows, §7), `the-purpose.md` delivery-shape 5, `repository-layout.md`, `README.md`, the DocFX
  pages above, `project_facts.md`, `CHANGELOG.md`, and `pipeline-overview.puml` + regenerated SVG
  only if the diagram names the `.xnb` route's proof (check; it may not).

---

## 4. Acceptance

- [ ] **C1 landed**: `XnbWriter.EffectReaderTypeName` is the XNA-4.0 string; `XnbWriterTests`
      golden re-captured with a comment naming the six runtime versions it was proven on;
      `validation/XnbContentLoad` still 4/4 on MonoGame WindowsDX with its C1 assertion restated;
      decision recorded in `project_decisions.md`; the `XnbWriter` doc comment no longer claims KNI
      consumes stock mgcb output.
- [ ] **KNI arm (A)**: `validation/KniXnbContentLoad` exists; a real KNI `ContentManager.Load<Effect>`
      on a ShadowDusk-written `.xnb` renders **pixel-identical (maxd 0)** to the stock-mgcb build on
      **KNI 4.2.9001 and 4.3.9001**, for the v10 and KNIFX payloads, 4/4 fixtures each; the
      "stock mgcb `.xnb` fails on 4.2" fact is asserted, not tolerated; §6 row with the exact
      command; default-ON slot in `run-windows-render-gates.ps1`; `repository-layout.md` entry.
- [ ] **FNA arm (B)**: `validation/FnaValidation` loads every gate shader's candidate through a real
      FNA `ContentManager.Load<Effect>` from a bare directory and renders within 4/255 of the fxc
      oracle **and** at maxd 0 of the raw-bytes candidate arm; 17/17 unchanged or the constant
      moved deliberately; the §6 FNA row and the `-IncludeFna` reason updated.
- [ ] **MonoGame DesktopGL** gets an in-repo `Content.Load` gate too (a `/platform:DesktopGL` →
      `PlatformTarget.OpenGL` case set in `validation/XnbContentLoad` or a GL sibling that can run
      in CI's llvmpipe lane with `dotnet tool restore`) — §2.5 proved it by probe; the promise needs
      a gate for the most common consumer, not a session's scratchpad.
- [ ] **Docs (D)**: D1-D4 done on every page listed in §2.8; the "not render-proven" caveat is
      gone because it is no longer true, and the KNI-version fact is stated.
- [ ] **Phase 60 closed out**: its C4 checkbox ticked with a pointer here, OQ3 answered with the
      §2.3 result, and its finding-2 paragraph corrected in place. `project_facts.md`'s `.xnb` line
      says what is now true (the §2 measurements, the manifest choice, the three proofs).
- [ ] Full `dotnet test` green on both TFMs; `./validation/run-windows-render-gates.ps1 -IncludeFna`
      green, including the new KNI slot.

## 5. Non-goals

- Changing the payloads. Every byte inside the container stays exactly what the corresponding
  target already emits and has render-proven; this phase is container-only.
- Emitting a KNI-native or FNA-native manifest, or any per-runtime manifest selection. One string,
  everywhere (C1), or the seamlessness directive is violated.
- Making MGFX v11 (`--target-runtime monogame-gl-v11`) loadable on KNI. It is a MonoGame 3.8.5
  container and KNI rejects it by design (§2.4); the documented KNI routes are v10 and KNIFX.
- Compressed XNB, other asset types, reading `.xnb` — Phase 60's non-goals stand.
- Making mgfxc create missing output directories, or making ShadowDusk stop doing so.

## 6. Open questions

- **OQ1. Should the KNI 4.2-vs-4.3 resolver difference be reported upstream?** KNI 4.3 already
  carries the `catch (FileLoadException)` fix, so the answer is probably "already fixed, note it in
  our docs"; but a KNI user on 4.2 with a stock MonoGame content build has a confusing failure
  and nothing in KNI's message points at the reader name. Low priority; a one-line issue at most.
- **OQ2. The MGCB plugin route on KNI.** `ShadowDusk.MgcbPlugin` runs inside MonoGame's MGCB, whose
  own `ContentTypeWriter` writes the mgcb-shaped manifest — so a plugin-built `.xnb` has the same
  KNI ≤ 4.2 failure as any stock mgcb `.xnb`. KNI ships its own pipeline (`nkast.Xna.Framework.Content.Pipeline`,
  `CompiledEffectWriter` above) which the plugin has never been loaded into. Is "KNI + MGCB plugin"
  a route anyone uses? If yes it needs its own measurement (does KNI's MGCB load a plugin compiled
  against `MonoGame.Framework.Content.Pipeline` 3.8.2.1105 at all?); if no, say so in the plugin
  guide so nobody expects it.
- **OQ3. A stderr warning for the implicit-default-profile `.xnb` case (C2)?** It is a pure
  convenience, never required for correct output, so it does not trip the directive; the question
  is whether mgfxc-parity of stderr (a warning-free compile keeps stderr empty, which
  `MgcbErrorFormatter`'s contract relies on) makes it unwelcome. Decide in wave 2; the docs fix (D2)
  is required either way.
- **OQ4. Does `validation/ForwardCompat` want an `.xnb` arm?** Phase 60 OQ2 suggested one. §2.5
  now covers 3.8.1.263, 3.8.2.1105 and 3.8.5 by probe (the floor, the harness default, and the
  ceiling); the four releases in between share the same `PrepareType` by inspection of the two
  ends. A sweep is cheap once the DesktopGL gate exists (acceptance bullet 4) and would make the
  "one v10 build loads everywhere" claim cover the container too.
