# FNA Effect-Format Research — Verdict

> Research output for `FNA-RESEARCH-BRIEF.md`. No product code changed. Citations are to primary
> source on the `master` branch of each repo as of 2026-06-08; line numbers are from those revisions.

## Verdict (one line)

**(C) New backend.** FNA does **not** read MGFX. Its `Effect(GraphicsDevice, byte[])` hands the bytes
straight to MojoShader, which requires a **Direct3D 9-era compiled-effect blob** (`fx_2_0`, magic
`0xFEFF0901`). ShadowDusk's `.mgfx` (magic `"MGFX"`) **will not load in FNA** — it is rejected as
"not an effect." **However**, the sizing is far better than a from-scratch DX9 emitter: the
**vkd3d-shader** library ShadowDusk already vendors has an `fx_2_0` target that writes exactly the
`0xFEFF0901` blob MojoShader parses — so a *cross-platform* FNA backend is plausibly feasible
**without** any new Windows-only shipping dependency, with the maintainer's local `fxc.exe` as the
characterization oracle.

Does ShadowDusk's current `.mgfx` load in FNA? **No** (definitive, by source).

---

## Evidence

### 1. FNA `Effect.cs` does no MGFX parsing — bytes go straight to MojoShader

FNA's public constructor passes the raw `byte[]` to FNA3D with **zero** header inspection — no `"MGFX"`
check, no version byte, no profile byte:

```csharp
// FNA/src/Graphics/Effect/Effect.cs  (lines 217–237)
public Effect(GraphicsDevice graphicsDevice, byte[] effectCode)
{
    GraphicsDevice = graphicsDevice;
    // Send the blob to the GLDevice to be parsed/compiled
    IntPtr effectData;
    FNA3D.FNA3D_CreateEffect(
        graphicsDevice.GLDevice,
        effectCode,
        effectCode.Length,
        out glEffect,
        out effectData
    );
    this.effectData = effectData;
    // This is where it gets ugly...
    INTERNAL_parseEffectStruct(effectData);   // parses MojoShader's struct, NOT the input bytes
    ...
}
```

The entire `Effect` class is built around `MOJOSHADER_*` enums (`XNAType`/`XNAClass`/`XNABlend`
arrays map `MOJOSHADER_SYMTYPE_*`, `MOJOSHADER_BLEND_*`, … to XNA types). There is no MGFX code path
anywhere in the type.

- Permalink: https://github.com/FNA-XNA/FNA/blob/master/src/Graphics/Effect/Effect.cs#L217-L237

### 2. FNA3D forwards the blob unchanged to `MOJOSHADER_compileEffect`

Both the D3D11 and OpenGL renderers call `MOJOSHADER_compileEffect(effectCode, effectCodeLength, …)`
with the bytes verbatim:

```c
// FNA3D/src/FNA3D_Driver_D3D11.c  D3D11_CreateEffect (~line 4724)
*effectData = MOJOSHADER_compileEffect(
    effectCode, effectCodeLength, NULL, 0, NULL, 0, &shaderBackend);

// FNA3D/src/FNA3D_Driver_OpenGL.c  OPENGL_CreateEffect (~line 5026) — identical call
```

- D3D11: https://github.com/FNA-XNA/FNA3D/blob/master/src/FNA3D_Driver_D3D11.c#L4699
- OpenGL: https://github.com/FNA-XNA/FNA3D/blob/master/src/FNA3D_Driver_OpenGL.c#L4989
- `FNA3D_CreateEffect` thunk: https://github.com/FNA-XNA/FNA3D/blob/master/src/FNA3D.c#L1219

### 3. MojoShader input format = Direct3D 9 effect bytecode (`fx_2_0`, magic `0xFEFF0901`)

The `MOJOSHADER_compileEffect` contract states its input is D3D9 shader bytecode:

```c
// mojoshader.h  (lines 2925-2939)
/* Fully compile/link the shaders found within the effect.
 *   (tokenbuf) is a buffer of Direct3D shader bytecode.
 *   (bufsize) is the size, in bytes, of the bytecode buffer. ...
 */
DECLSPEC MOJOSHADER_effect *MOJOSHADER_compileEffect(const unsigned char *tokenbuf, ...);
```

The effect parser **validates the D3DX9 version token** and rejects anything that isn't it:

```c
// mojoshader_effects.c  MOJOSHADER_parseEffect (lines 978-999)
/* Read in header magic, seek to initial offset */
read_version_token(&ptr, &len, &magic, &version_major, &version_minor);
if ((magic == 0xBCF0) && (version_major == 0x0B) && (version_minor == 0xCF))
{
    /* The Effect compiler provided with XNA4 adds some extra mess at the
     * beginning of the file. It's useless though, so just skip it. -flibit */
    const uint32 skip = readui32(&ptr, &len) - 8;
    ptr += skip; len += skip;
    read_version_token(&ptr, &len, &magic, &version_major, &version_minor);
}
if (!((magic == 0xFEFF) && (version_major == 0x09) && (version_minor == 0x01)))
{
    MOJOSHADER_deleteEffect(retval);
    return &MOJOSHADER_not_an_effect_effect;   // <-- rejection path
}
```

- Header contract: https://github.com/icculus/mojoshader/blob/master/mojoshader.h#L2925-L2945
- Magic validation: https://github.com/icculus/mojoshader/blob/master/mojoshader_effects.c#L978-L999

**What this means for ShadowDusk's `.mgfx`:** MojoShader reads the first 32-bit token and extracts
`magic = (token >> 16) & 0xFFFF`. ShadowDusk's header begins with ASCII `"MGFX"` =
`0x4D 0x47 0x46 0x58` (`MgfxWriter.cs:14`), i.e. a little-endian token `0x5846474D` → `magic = 0x5846`.
That is neither `0xFEFF` (D3DX9 effect) nor `0xBCF0` (XNA4 wrapper), so MojoShader returns
`MOJOSHADER_not_an_effect_effect`. **FNA load fails, deterministically, on the first 4 bytes.**

The two accepted headers are: `0xFEFF0901` = the raw D3DX9 `fx_2_0` compiled effect (what `fxc /T fx_2_0`
emits); `0xBCF00BCF` = the extra prefix XNA4's effect compiler prepends, which MojoShader skips to reach
the inner `0xFEFF0901`. Neither resembles MGFX.

### 4. Is there ANY FNA path that reads MGFX? — Confirmed absent

No. `Effect(byte[])` → `FNA3D_CreateEffect` → `MOJOSHADER_compileEffect` is the **only** path; there is
no alternate reader, no version sniff, no MonoGame-compat branch in `Effect.cs` or in FNA3D's
`*_CreateEffect`. FNA is an XNA4 *re-implementation* and deliberately consumes the XNA4/D3DX9 effect
format via MojoShader — it has no reason to read MonoGame's MGFX container. (Contrast: MonoGame/KNI's
`EffectReader` validates the `"MGFX"` signature + version/profile bytes — a wholly different format that
ShadowDusk's `MgfxWriter` targets.)

### 5. FNA's own `.fx` toolchain today — Windows/Wine `fxc.exe`, same lock-in as `mgfxc`

FNA has **no** cross-platform compile story of its own. The canonical templates shell out to the
Windows-only `fxc.exe` from the DirectX SDK, producing raw `.fxb` files (not `.xnb`):

> "FNA Template uses `fxc.exe` (from the DirectX SDK) to build shaders, rather than using a content
> pipeline. This produces raw `.fxb` files rather than the usual `.xnb` content files."
> … on Linux/macOS, "Setup Wine with `winecfg`" then "Install the DirectX SDK with
> `winetricks dxsdk_jun2010`." Alternatively drop `fxc.exe` in `build/tools` and
> `winetricks d3dcompiler_43`.
> — AndrewRussellNet/FNA-Template README

So FNA developers compile effects with **`fxc /T fx_2_0` under Wine** on non-Windows — exactly the
Windows/Wine lock-in `mgfxc` has and that ShadowDusk exists to remove. **This is a real reach gap
ShadowDusk could fill** (Part 1 of "What success actually means"): a cross-platform, no-Wine FNA effect
compiler is a genuine differentiator, not a me-too.

- https://github.com/AndrewRussellNet/FNA-Template (README, shader build section)
- Corroborating: Mozilla's `fxc2` "wine-runnable fxc" (https://github.com/mozilla/fxc2) exists precisely
  because the only blessed path is the Windows binary.

---

## Can ShadowDusk produce what FNA needs?

**Target needed:** a D3DX9 `fx_2_0` effect blob — `0xFEFF0901` header, technique/pass/parameter tables,
render-state assignments, and **embedded D3D9 shader bytecode** (`vs_3_0`/`ps_3_0`/`ps_2_0` token
streams) — all of which MojoShader walks at runtime.

**Current pipeline cannot emit this.** ShadowDusk's two emit paths are:
- `HLSL → DXC → SPIR-V → SPIRV-Cross → GLSL` (SM6 DXIL / SPIR-V intermediates), and
- `HLSL → vkd3d-shader → DXBC_TPF` (SM4/5 tokenized program format in a DXBC container).

Neither produces D3D9 SM1–3 token streams, and neither writes a D3DX9 effect container. So as the brief
predicted, FNA support is a **new backend**, not "validate the MGFX we already have."

**But the cross-platform engine likely already exists in a library we vendor.** From vkd3d-shader's
public API (`include/vkd3d_shader.h`):

- `VKD3D_SHADER_TARGET_D3D_BYTECODE` — *"Legacy Direct3D byte-code. This is the format used for
  Direct3D shader model 1, 2, and 3 shaders."* (since 1.3), and the supported conversion list includes
  **`VKD3D_SHADER_SOURCE_HLSL → VKD3D_SHADER_TARGET_D3D_BYTECODE`**. → vkd3d compiles HLSL to the
  per-shader DX9 bytecode MojoShader consumes, **cross-platform**.
- `VKD3D_SHADER_TARGET_FX` — *"Binary format used by Direct3D 9/10.x/11 effects profiles. Output is a
  raw FX section without container."* (since 1.11). The compile-option docs reference the **`fx_2_0`
  profile** explicitly (CHILD_EFFECT option, since 1.12).
- **The fx_2_0 writer emits the exact MojoShader magic.** `libs/vkd3d-shader/fx.c` line 925:
  `put_u32(&buffer, 0xfeff0901); /* Version. */` — and `fx_create_context_*`/`fx->min_technique_version`
  is set to 9 for `version == 2`. This is byte-for-byte the token MojoShader's `parseEffect` requires
  (§3 above). For `fx_2_0` there is no DXBC wrapper, so "raw FX section without container" *is* the full
  loadable blob.

  Citations (Beyley/vkd3d mirror of wine/vkd3d `master`):
  - target enum: `include/vkd3d_shader.h` (lines 825–862)
  - HLSL→D3D_BYTECODE supported pair: `include/vkd3d_shader.h` (~line 2063)
  - `0xfeff0901` writer: `libs/vkd3d-shader/fx.c:925`
  - fx_2_0 profile / technique version 9: `libs/vkd3d-shader/fx.c:182-199`

**Cross-platform conclusion:** an HLSL → `fx_2_0` path through vkd3d-shader is the open, no-Windows,
no-Wine route — the *same* native library ShadowDusk already ships for the DXBC backend. The local
`fxc.exe` is a **characterization oracle only** (like `d3dcompiler_47` for DXBC), never shipped.

**Open feasibility risks (must be runtime-verified, not assumed):**
1. **vkd3d fx_2_0 maturity.** vkd3d's HLSL→fx_2_0 writer is young (since 1.11/1.12, 2024-era). Coverage of
   render states, samplers, annotations, preshaders, and SM3 control flow across ShadowDusk's 52-shader
   corpus is unknown. Expect gaps vs `fxc`.
2. **Does vkd3d embed compiled shaders in the fx_2_0 blob, or only the metadata section?** Need to
   confirm the emitted blob contains `vs_3_0`/`ps_3_0` token streams MojoShader can parse, not just
   technique/parameter tables. (The fx.c writer assembles the section; whether shader bodies are
   compiled-and-embedded for fx_2_0 specifically must be checked against a real MojoShader load.)
3. **MojoShader dialect quirks.** Even a valid `0xFEFF0901` blob must survive MojoShader's parse *and*
   its GLSL/D3D11 re-translation at FNA runtime. That is the rung-4 bar, not the parse.

---

## Sizing & recommendation

**Classification: (C) new backend — but "feasible-C", not "build-a-DX9-compiler-from-scratch-C".**

Justification: FNA needs a format our pipeline can't currently emit (rung 0 fails today), so it is
categorically a new backend (C), *not* A (validate) or B (repackage what we have — we cannot repackage
GLSL text or SM4/5 DXBC into a `fx_2_0` D3D9 blob; the shader ISA itself differs). But the expensive part
of C — an HLSL→D3D9-effect compiler that runs on Linux/macOS — **already exists in vkd3d-shader**, which
we vendor. So the new work is an *adapter + writer-wiring + validation* layer, not a compiler.

**Proposed phase shape (if greenlit — its own phase, not this research):**

1. **Spike / feasibility gate (small, do first).** Take 2–3 corpus shaders, drive vkd3d-shader
   `HLSL → TARGET_FX` at `fx_2_0`, dump the bytes, and (a) confirm `0xFEFF0901` + embedded DX9 bytecode,
   (b) load them in a real FNA app and render. If vkd3d's fx_2_0 can't carry our shaders, the cost
   jumps and we reassess (fall back to fxc-oracle-only, or contribute upstream to vkd3d).
2. **Backend behind a target.** Add `PlatformTarget.Fna` (or a `fx_2_0` profile on the DX path) routing
   to a new `IFx2EffectCompiler` with a `Vkd3dFx` implementation (mirrors the existing
   `IDxbcShaderCompiler` / `DxbcBackend.Vkd3d` split), plus a Windows-only `Fxc` oracle implementation
   for characterization.
3. **No MGFX writer involvement.** This bypasses `MgfxWriter` entirely — the output is a `.fxb`/`fx_2_0`
   blob, a distinct artifact. Keep MGFX v10/v11 output untouched (additive, per `mgfx-format.md`).
4. **Docs.** Flip `choosing-a-target.md` FNA row from "⚠️ not supported" to a real target once rung-4
   passes; note FNA consumes `.fxb`, not `.mgfx`.

**Rung-4 validation plan (the only proof that counts):**
- **Runtime:** a minimal **real FNA** desktop app (FNA + FNA3D + MojoShader, D3D11 *and* OpenGL
  backends — same-backend comparison each side), `new Effect(gd, shadowDuskFxbBytes)`.
- **Oracle:** the maintainer's local **`fxc.exe /T fx_2_0`** compiling the same `.fx`, loaded into the
  *same* FNA app. Compare rendered frames pixel-equivalently (reuse the Phase 17/18 image-diff harness).
- **Reach proof (the differentiator):** the ShadowDusk-side `.fxb` must be produced **on Linux/macOS
  with no Wine and no `fxc`** (vkd3d path) and render identically to the Windows `fxc` oracle's output —
  that closes both axes of "What success actually means" for FNA.
- Scope to the PS-only SM3 corpus first (same beachhead as Phases 17/18), defer VS-driven effects.

**Effort estimate (rough):** Spike = 1–2 days. If the spike is green, full backend + oracle + rung-4
harness is comparable to Phase 18 (DXBC) — call it a medium phase. If the spike reveals vkd3d fx_2_0 gaps,
it becomes large (upstream contributions or a hand-written D3DX9 effect-container writer over vkd3d's
per-shader `D3D_BYTECODE` output).

---

## Unknowns / needs-a-runtime-test

1. **Does vkd3d-shader's `fx_2_0` output actually load in MojoShader/FNA?** Verified at the byte-header
   level (`0xFEFF0901` matches), **not** end-to-end. Must be a real FNA-runtime load test. *(highest risk)*
2. **Does the vkd3d fx_2_0 blob embed parseable `vs_3_0`/`ps_3_0` shader bytecode**, or only the
   effect metadata tables? Inspect a dumped blob + MojoShader parse.
3. **Corpus coverage of vkd3d's HLSL→fx_2_0** — render states, samplers, annotations, SM3 flow control,
   preshaders. Expect divergence from `fxc`; quantify against the 52-shader fixtures.
4. **`.fxb` vs `.xnb` consumption.** FNA games that load via `Content.Load<Effect>` need an `.xnb`
   wrapper, not a raw blob (same `.mgfx`-vs-`.xnb` distinction as MonoGame). Confirm whether XnaFiddle's
   FNA target hands raw bytes to `new Effect(...)` (raw `.fxb` fine) or routes through content (`.xnb`
   wrapping needed).
5. **vkd3d version pinning.** fx_2_0 support is recent and evolving; which vkd3d release to pin, and
   whether the vendored build enables the FX writer, needs checking against `tools/restore.*`.
6. **MojoShader fork drift.** FNA3D vendors MojoShader as a submodule (flibit's fork); confirm the parse
   logic cited (icculus/mojoshader) matches the FNA3D-vendored revision — the `0xBCF00BCF` skip comment
   is flibit's, so they track closely, but verify.

---

## Spike results (2026-06-08, branch `spike/fna-fx2-vkd3d`)

Goal of the spike: resolve PHASE-40's top risk — "can vkd3d's `fx_2_0` round-trip our shaders through a
real FNA load?" — before committing to a full phase.

**Outcome: the oracle/target half is fully proven; the vkd3d producer was built (1.17, in WSL) and RUN —
and its `fx_2_0` writer is too incomplete to use (no pass-assignment / sampler writing), though its
per-shader `d3dbc` output works. Net: Verdict C stands but the cross-platform producer needs a D3DX9
effect-container writer we build (or vkd3d upstream work), NOT just "call vkd3d's fx target." Real-FNA
render remains unproven.**

### Proven (runnable here, with the local Win10 SDK `fxc.exe`)

1. **The maintainer's `fxc` is a working `fx_2_0` oracle.**
   `C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe` (D3DCompiler 10.1 / `_47`)
   compiles `/T fx_2_0` successfully — it only emits a non-fatal `warning X4717: Effects deprecated for
   D3DCompiler_47`, exit code 0. So we do **not** need the DX SDK June 2010 `fxc`; the modern SDK fxc
   already present works as the characterization oracle. *(Resolves PHASE-40 oracle-vintage unknown.)*

2. **`fx_2_0` output carries the exact magic FNA/MojoShader requires.** A minimal hand-written D3D9
   effect produced a 528-byte blob whose first u32 is `0xFEFF0901` — the precise token
   `MOJOSHADER_parseEffect` validates (mojoshader_effects.c:995). First 16 bytes:
   `01 09 ff fe 88 00 00 00 …`.

3. **The blob embeds real D3D9 shader bytecode.** Hex-dump of the minimal blob shows the `ps_2_0`
   version token (`00 02 ff ff` @ 0x108), a `CTAB` constant table (`43 54 41 42` @ 0x110), the dcl/tex
   instruction stream, and the `0000FFFF` end token @ 0x20c — i.e. a genuine D3DX9 effect with a
   parseable embedded shader, not just a metadata shell. This is the structure MojoShader translates.

4. **A real corpus shader compiles to `fx_2_0`.** `tests/fixtures/shaders/Grayscale.fx` compiled to a
   680-byte `0xFEFF0901` blob via `fxc /T fx_2_0 /D OPENGL=1` — *after* stripping its UTF-8 BOM (raw fxc
   rejects the BOM with `error X3000`; ShadowDusk's own preprocessor already strips it). Forcing
   `OPENGL` selects the shader's `ps_3_0` + `: COLOR` path, which is the SM3/D3D9 dialect `fx_2_0`
   accepts. So the existing corpus is fx_2_0-amenable in its feature-level-9.1 form — no rewrite needed
   for at least the simple PS-only shaders, just BOM-strip + the right `#define`s.

   Artifacts left under `C:\temp\fna-spike\` (`min_fx2.fx/.fxb`, `grayscale_nobom.fx`, `grayscale.fxb`);
   not committed.

### Producer test — vkd3d 1.17 `HLSL → fx_2_0` was BUILT AND RUN, and it does NOT work yet

Built vkd3d **1.17** from the winehq source tarball inside WSL Ubuntu-24.04 (autotools: `./configure
&& make`; deps `build-essential meson ninja-build bison flex pkgconf libvulkan-dev spirv-headers
libjson-perl`). `vkd3d-compiler --print-target-types -x hlsl` lists `fx` ("Binary format used by
Direct3D 9/10.x/11 effects") and `fx_2_0` is an accepted profile (`--child-effect` help text names it).
**But the HLSL→fx_2_0 *writer* is a stub:** compiling even a trivial effect aborts —

```
$ vkd3d-compiler -x hlsl -p fx_2_0 -b fx min_fx2.fx -o min.fxb
min_fx2.fx:3:11:  E5017: not yet implemented: Writing fx_2_0 sampler objects initializers
min_fx2.fx:12:1:  E5017: not yet implemented: Write pass assignments
Failed to compile shader, ret -5.
```

`Write pass assignments` is the deal-breaker — that's the `PixelShader = compile ps_x_x PS();` binding,
the load-bearing content of an effect. vkd3d emits the `0xFEFF0901` magic but bails before writing
samplers or passes, so it **cannot produce a loadable effect today.** This *refutes* the optimistic read
in the research section above ("vkd3d's fx.c writes `0xfeff0901`, looks ready"): the magic constant is
present in the writer, but the writer itself is incomplete. **Only running it revealed this.**

**The per-shader building block DOES work.** `vkd3d-compiler -x hlsl -p ps_3_0 -e MainPS -b d3dbc
Grayscale.fx` produced a valid 396-byte D3D9 bytecode blob (`00 03 ff ff` = `ps_3_0` version token,
then `CTAB`). So vkd3d 1.17 reliably emits the **individual** D3D9 shader token streams MojoShader
needs — it just can't assemble them into a D3DX9 *effect container*.

(Artifacts: `C:\temp\fna-spike\grayscale_vkd3d.fxb` was NOT produced; `libvkd3d-shader.so.1.15.0` built
under `/root/vkd3d-spike/`. fxc oracle blobs from the earlier steps remain valid.)

### Still unproven

- **No real FNA/MojoShader load test.** No FNA app or `fnalibs` (FNA3D + MojoShader) present here, so
  even the working `fxc` blob has not been loaded by MojoShader at runtime. Header + structure match by
  source inspection; runtime acceptance is still inferred, not observed.

### What this means for sizing — REVISED UP from the research section

Verdict **C (new backend)** stands, but the spike **invalidates the earlier "feasible-C, engine already
exists in vkd3d" framing.** vkd3d's fx_2_0 *target* is not a usable producer today; only its per-shader
`d3dbc` output is. So the cross-platform shipping path is **not** "call vkd3d's fx target" — it is one of:

- **(C1) Build a D3DX9 effect-container writer in ShadowDusk** over vkd3d's per-shader `d3dbc` output —
  i.e. a new writer (sibling to `MgfxWriter`) that emits the `0xFEFF0901` header + parameter / sampler /
  technique / pass-assignment tables MojoShader parses, embedding the vkd3d `d3dbc` blobs. The format is
  fully specified by MojoShader's `parseEffect` (we have the source) and validatable against the `fxc`
  oracle. This is real, bounded work — comparable to the original `MgfxWriter` effort, plus reflection
  for the D3DX9 symbol tables. **Recommended path.**
- **(C2) Upstream the missing pieces to vkd3d** (`fx_2_0` sampler initializers + pass assignments), then
  use its `fx` target directly. Lower long-term maintenance, but unbounded timeline and out of our
  control; good as a parallel contribution, not the critical path.

Either way the **oracle** (`fxc`) and **target format** (`0xFEFF0901` + embedded `d3dbc`) are pinned, and
the **per-shader compile** is proven cross-platform. The remaining cost is the container writer (C1) and
the real-FNA render harness.

### Recommended next steps

1. **Decide C1 vs C2** (recommend C1 as critical path; optionally pursue C2 upstream in parallel).
2. **Prototype the D3DX9 effect writer (C1):** emit header + one technique/one pass binding a single
   `d3dbc` pixel shader for `Grayscale.fx`; diff structure against the `fxc` oracle blob
   (`C:\temp\fna-spike\grayscale.fxb`, 680 bytes) using MojoShader's `parseEffect` layout as the spec.
3. **Load both blobs in a real FNA app** (FNA + fnalibs, D3D11 and OpenGL) via `new Effect(gd, bytes)`
   and render — the rung-4 bar. Reuse the Phase 17/18 image-diff harness.
4. **Track vkd3d fx_2_0 writer maturity** — re-test on each release; if it completes pass-assignment +
   sampler writing, C1 could be retired in favour of the upstream `fx` target.

---

## Appendix — orientation & sources (for the implementor)

**Product context to read first** (so the FNA backend respects ShadowDusk's promise):
- `CLAUDE.md` → "THE PURPOSE" and "What success actually means" (the rung-1→4 evidence ladder; rung 4 =
  real-runtime render equivalence is the only proof).
- `docfx/guides/choosing-a-target.md` (the framework/backend/profile axes; the FNA row to update once
  this lands).
- `src/ShadowDusk.Core/MgfxWriter.cs` + `docfx/architecture/mgfx-format.md` (the MGFX format FNA does
  **not** read — the FNA backend bypasses this writer entirely).

**Hard constraints carried over from the research brief:**
- Same-backend comparison only (FNA-D3D11 vs FNA-D3D11, FNA-GL vs FNA-GL); never cross-backend.
- The bar is rendering in a **real FNA runtime**, not ShadowDusk's own tests.
- The cross-platform promise **forbids a shipping dependency on Windows-only tools**. A Windows-only
  `fxc.exe` **oracle** for characterization is fine; a Windows-only piece in the *shipping* pipeline is
  not (that's the whole reason ShadowDusk exists). vkd3d-shader's `fx_2_0` target is the cross-platform
  shipping candidate.

**Maintainer's local oracle:** `fxc.exe` is available on the maintainer's machine — usable as the
`fx_2_0` characterization oracle (the FNA-side analogue of `d3dcompiler_47` for DXBC). To verify before
the spike: which `fxc` vintage (DX SDK Jun 2010 emits `fx_2_0` natively; newer Windows-SDK `fxc` may
refuse `/T fx_2_0`), and that it compiles the full effect (`/T fx_2_0`), not just standalone
`vs_3_0`/`ps_3_0`.

**Key repos / primary sources:**
| Source | URL |
|---|---|
| FNA (`Effect.cs`) | https://github.com/FNA-XNA/FNA — `src/Graphics/Effect/Effect.cs` |
| FNA3D (vendors MojoShader; `*_CreateEffect`) | https://github.com/FNA-XNA/FNA3D — `src/FNA3D_Driver_{D3D11,OpenGL}.c`, `src/FNA3D.c` |
| MojoShader (effect/bytecode parser) | https://github.com/icculus/mojoshader — `mojoshader.h`, `mojoshader_effects.c` |
| vkd3d-shader (`fx_2_0` writer, `D3D_BYTECODE` target) | wine/vkd3d (`master`); GitHub mirror used: https://github.com/Beyley/vkd3d — `include/vkd3d_shader.h`, `libs/vkd3d-shader/fx.c` |
| FNA's own toolchain (fxc + Wine) | https://github.com/AndrewRussellNet/FNA-Template ; https://github.com/mozilla/fxc2 |

> All line numbers in this doc are from the `master` branch of each repo as of 2026-06-08. vkd3d's
> canonical home is `gitlab.winehq.org/wine/vkd3d` (bot-walled to automated fetches); the Beyley mirror
> was used for source quotes and should be re-checked against upstream before pinning a version.
