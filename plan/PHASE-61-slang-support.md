# Phase 61 — Slang as an INPUT language

**Track:** Additive frontend / reach. Additive only; **no existing output byte may change**.

**Status:** ✅ **SHIPPED in its final shape, 2026-08-14.** Scope: INPUT only (owner direction
2026-08-13), and — a second owner direction the same day, superseding the first implementation —
**the HLSL-compatible subset of Slang, as a pure managed text transform, with no Slang toolchain
shipped or invoked anywhere.** ShadowDusk is a multi-input compiler (`.fx`, ShaderToy GLSL, Slang)
over one faithful pipeline, and this frontend follows that shape exactly.

**What shipped:** `ShadowDusk.Compiler.Slang.SlangFrontend.ConvertToFx(slang) → .fx text` plus CLI
auto-routing (`.slang` extension, or `--input-format slang`). Entry points come from Slang's own
`[shader("vertex")]` / `[shader("fragment")]` attributes; the technique block is synthesized (the
ShaderToy `#if SM4` profile convention, reasons and all); the attributes are stripped; and the
body — HLSL-compatible Slang is near-HLSL by Slang's own design (§2.3) — compiles through the
**same DXC as every `.fx`**. Consequences that fall straight out of the shape:

- **Works everywhere.** Pure managed text + the pipeline's existing natives ⇒ every OS, every
  target, and the browser/WASM host. Nothing new in the supply chain, nothing to provision.
- **The author's names are the parameter names, verbatim.** No compiler sits between the
  `.slang` text and DXC, so there is no mangling to undo.
- **The trade, accepted consciously** (owner, after reviewing the alternative): **Slang-only
  language features** — `import` modules, generics, `extension`, `associatedtype` — are rejected
  **by name** (`SD0600`), never approximated. This matches the request as filed: *"write slang,
  but still go through the normal compilation pipe… features not supported in HLSL are likely
  also not supported in MonoGame."* Unloadable stages reject as `SD0602` (Phase 58's two-stage
  measurement); no/ambiguous entry points as `SD0603`/`SD0604`. Subtler Slang-isms fall through
  to DXC, whose verbatim diagnostics remain the authority.

**Proven — and cross-validated against the real Slang compiler (2026-08-14, the owner's
"10-50 comparable shaders" bar):** a **17-shader corpus** in `tests/fixtures/shaders/slang/`
(8 uniform-free procedural — gradients, SDF circle, rings, checkerboard, stripes, color wheel,
plasma; 6 textured/cbuffer-driven; 3 VS+PS pairs including matrix and displaced-vertex cases).
Three gates:

1. **In-suite** (`SlangCorpusCompileTests`, CI, no oracle): all 17 convert through the frontend
   and compile to `.mgfx` on **OpenGL and DirectX**; corpus-size floor pinned at 17.
2. **slangc validity** (`validation/SlangCorpus` gate 1): every shader is accepted per-entry by
   the **real pinned slangc v2026.14.1** — the proof this corpus is genuine Slang, not HLSL
   wearing a `.slang` extension. slangc is a **test-time oracle** exactly as `fxc` and stock
   `mgcb` are elsewhere: downloaded on demand, SHA-256-verified, cached, never shipped, never
   invoked by the product — so the no-Slang-binaries decision holds untouched.
3. **Pixel equivalence** (gate 2): the 8-shader procedural subset renders **pixel-identical at
   measured maxd 0** (tolerance ±2, spatial-variation non-degeneracy check) through ShadowDusk's
   route vs through **slangc's own HLSL emission** fed to the same DXC + SPIRV-Cross. Identical
   downstream stages ⇒ any divergence is attributable to the one thing that differs — whether
   ShadowDusk's reading of the Slang text matches slangc's — which is precisely the
   silent-divergence risk of the subset decision, now measured instead of assumed.

Still open: render equivalence for the uniform/texture-driven members (extending gate 2 there
means solving slangc's name mangling in the harness — the demangle groundwork below is where
that would start), and the FNA arm (modern-syntax bodies vs the SM2–3 ceiling — OQ2, measure
don't assume).

**A slangc-invoking route was built first (2026-08-13) and never shipped.** It worked end-to-end
— and the owner rejected shipping or requiring the Slang binary when the implication surfaced, so
it was replaced the next day by the pure-managed subset route. §A5 below preserves what that day
measured about `slangc` (emission shapes, name mangling, the `SLANG_ParameterGroup_*` cbuffer
wrapping the GL lowering refuses), because it is the groundwork any future full-Slang route would
start from.

**Depends on:** [Phase 23](DONE/PHASE-23-in-browser-compilation.md) — **read §2.1 below before
anything else**; that phase already prototyped Slang end-to-end and rejected it for the product,
and that rejection is load-bearing. Also [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md)
(the additive-frontend precedent and its evidence model) and
[Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3 (the PURPOSE decision that governs
whether a source-fidelity-only frontend is in scope at all).

**Blocks:** nothing.

**Gated on:** a **reduced** gate, not Phase 59's — see §5. The product of this phase is an ordinary
`.mgfx`/`.fxb`/`.xnb` for a target that is **already rung-4 proven** — and in the final
pure-managed shape the "new link in the chain" shrank further still: the body text reaches DXC
verbatim, so the only new code is the entry-scan + technique synthesis, both pure and unit-tested.

> [issue #198](https://github.com/kaltinril/ShadowDusk/issues/198), vchelaru: *"Add slang support
> (see how complicated it is)"*. Filed with an empty body. The disambiguation is **settled: input
> only** — by the requester's own clarification on 2026-08-12 and by owner direction on 2026-08-13,
> which agree.

---

## 1. Where this came from

One of three issues Victor Chelaru filed on 2026-08-09, alongside
[#199](https://github.com/kaltinril/ShadowDusk/issues/199) (→ [Phase 60](DONE/PHASE-60-xnb-content-output.md))
and [#197](https://github.com/kaltinril/ShadowDusk/issues/197) (→ [Phase 62](PHASE-62-skiasharp-sksl-target.md)).
The body is empty and the title's parenthetical — *"see how complicated it is"* — reads as a
scoping request rather than a commitment. This doc is written to answer that: **here is how
complicated it is, and here is which half is already decided.**

The scope moved twice before settling, and the trail is kept deliberately: opened 2026-08-11 with
the disambiguation open → widened the same day by owner direction to input **and** output → the
requester clarified on 2026-08-12 that he meant input → **narrowed to input only by owner direction
2026-08-13**, which is where it stands. The output material survives in §4 and §7 as a closed
record.

---

## 2. What is already established — do not re-derive this

### 2.1 Slang as a **substitute compiler** is CLOSED, was prototyped, and the reasons still hold

This is the single most important fact in the phase, and it is not a matter of opinion: it was
built, measured, and rejected in [Phase 23](DONE/PHASE-23-in-browser-compilation.md) (2026-06-01),
on a merged prototype branch (`phase22-web-inbrowser-compile`).

What that prototype found, verbatim from the phase record:

- On the 10-shader corpus, **Slang's SPIR-V reflected identically to DXC's**, and after a managed
  `NormalizeSlangNaming` shim it **yielded matching GLSL**. So it was *reconcilable for the corpus* —
  it is not that Slang is bad.
- **But two DXC flags do not forward through Slang's API** — `-fvk-use-dx-layout` (the cbuffer byte
  layout the MojoShader GLSL chain and `SpirvReflector` both depend on) and
  `-fvk-use-entrypoint-name`. So **byte-identity across arbitrary shaders is unprovable**, and a
  novel user shader can diverge **silently**.
- A shader fiddle's whole point is open user input, so "validated on a 10-shader corpus" cannot
  generalize into a safety guarantee.

`CLAUDE.md`'s THE PURPOSE names this exact failure mode — *"a host must not swap in a different
frontend/compiler to make a platform work — different compiler ⇒ different output ⇒ silently breaks
the 'identical to mgfxc' promise"* — and Phase 23 spent its entire budget building the faithful
DXC→WASM frontend rather than accepting the Slang one that already worked.

**Therefore: Slang will not replace DXC anywhere in the pipeline, will not become a second
HLSL→SPIR-V frontend, and will not be "an alternative backend the user can pick".** If issue #198
means that, the answer is no, and §2.1 is the answer. Do not reopen it without new evidence that
those two flags now forward and that byte-identity is provable on arbitrary input — and note that
even then it would buy nothing, because DXC already works everywhere.

### 2.2 Slang the project, measured 2026-08-11

Relevant because the surviving interpretation (§3, reading B) would mean shipping it.

| | |
|---|---|
| Repository | [`shader-slang/slang`](https://github.com/shader-slang/slang), 5.5k stars, **actively developed** (pushed 2026-08-12) |
| Licence | **Apache-2.0 WITH LLVM-exception** — clean, permissive, compatible |
| Release cadence | Frequent; `v2026.14.1` on 2026-07-30, `v2026.14` on 2026-07-24 |
| Platforms | Prebuilt for linux-x64/aarch64 (incl. glibc-2.27/2.28 variants), macos-x64/aarch64, windows-x64/aarch64, **and `wasm`** |
| C API | Yes — `include/slang.h`, plus `slang-com-helper.h` / `slang-com-ptr.h` (a COM-style API, the same shape as DXC's) |
| NuGet | **None.** `Slang` on nuget.org is an unrelated `0.0.1` placeholder; there is no `SlangNet`. |

Two consequences worth stating up front:

- The **`wasm` build existing is genuinely good news** for reach, and is the one thing Slang has
  that a hand-rolled frontend would not.
- The **absence of a NuGet package is a real cost**, not a detail. "Self-contained is a hard
  requirement" — native pieces ride *inside* the package. Adopting Slang means owning a
  pinned, SHA-256-verified, per-RID native the way `tools/restore.*` already does for DXC, vkd3d,
  and SPIRV-Cross, plus a release gate that fails if it is missing. That is the Phase 37 / Phase 40
  playbook and it is several days of work **before any shader compiles**.

### 2.3 Slang's relationship to HLSL — the fact everything else rests on

From Slang's own user guide (`docs/user-guide/00-introduction.md`, `02-conventional-features.md`,
read 2026-08-11):

> *"Slang extends the HLSL language with thoughtfully selected features from modern general-purpose
> languages."*
> *"Slang is **backward compatible with most existing HLSL code**."*
> *"Slang supports the following expression forms with **nearly identical syntax to HLSL**…"*

It keeps `cbuffer`, `register`, `SV_` semantics, and HLSL function-declaration syntax explicitly for
compatibility. Its named compile targets are **DXBC, DXIL, SPIR-V, HLSL, GLSL, CUDA** (plus MSL and
WGSL), and `slangc -target hlsl` is a documented, first-class invocation.

**Two consequences. The first is the backbone of this phase; the second is why §4 stays on file.**

1. **The input route is real and clean.** Because HLSL is a *supported emit target*,
   `.slang → [Slang] → HLSL → [ShadowDusk's existing faithful pipeline, untouched]` works without
   displacing DXC from anything. Note the word "most" in Slang's own compatibility claim: it is
   *most* HLSL, not all, so the residue must be measured, not assumed away (§6 A5).
2. *(Bearing on the now-closed output direction.)* Since Slang is a near-superset of HLSL, **the
   shader *body* would already be valid Slang**, so emitting Slang would have been an
   **effect-framework translation** — all the work in the FX9 layer (`technique` / `pass` /
   `sampler_state`) rather than in the expressions. Recorded because it is the single fact that
   would make reopening §4 cheap, and it is not obvious.

### 2.4 There is precedent for the shape that *is* open

[`ShadowDusk.ShaderToy`](../src/ShadowDusk.ShaderToy/) (Phases 46/47) takes a language the pipeline
cannot consume and emits ordinary `.fx` text — pure-managed, zero native dependency, upstream of the
pipeline, changing no existing output byte. A Slang **input** frontend would sit in exactly that
architectural slot.

---

## 3. The disambiguation — SETTLED 2026-08-13

**Reading A — "use Slang as the compiler."** Replace or supplement DXC with Slang for
HLSL→SPIR-V/DXIL. **CLOSED by §2.1** on Phase 23's measured evidence, and confirmed independently by
the requester (§3.1). Slang never displaces DXC on the HLSL path.

**Reading B — "accept `.slang` as an input language." → THE PHASE.** A consumer writes Slang;
ShadowDusk compiles it to the same `.mgfx` / `.fxb` / `.xnb` outputs it already produces. This is
what the issue asked for and what the owner has committed. Everything below §5 is about this.

**Reading C — "emit Slang." → CLOSED by owner direction 2026-08-13.** It was briefly in scope
(2026-08-11 to 2026-08-13) and §4 keeps the analysis. Nothing about it was found to be *wrong* —
the round-trip oracle in §7 is still a good idea if it ever returns — it simply is not what was
asked for, and §3.1 is why that mattered.

### 3.1 How the scope settled — and the general lesson in it

The confirmation, verbatim from [PR #201](https://github.com/kaltinril/ShadowDusk/pull/201):

> *"Yes, the intent here is to use Slang as an additive language -> HLSL. In other words to just
> allow people to write slang, but still go through the normal compilation pipe. This seems the
> safest and it would be the way forward unless we find some features in slang that are just not
> supported in HLSL; however if that were the case then it's likely that those features might also
> not be supported in MonoGame."*

Three things fall out of it:

1. **Reading B is the ask, and it is the ask in exactly the shape §6 describes** — Slang upstream of
   an untouched pipeline, not Slang inside it. *"Still go through the normal compilation pipe"* is
   the same boundary §2.1 draws.
2. **Reading A is independently confirmed closed by the requester**, who reaches it from the
   *"safest"* direction rather than from Phase 23's evidence. Two routes, one answer.
3. **The residue argument is addressed in §6 A5**, where it belongs, because it is only *partly*
   right and the part that does not hold is the part that would bite.

Owner direction followed on 2026-08-13: **input only, as the requester asked.** That closed reading
C and dissolved the sequencing question the previous draft had opened (there is only one direction
left to sequence).

**The lesson worth keeping, because this phase is a clean example of it.** For two days the doc
carried a direction with **no named consumer**, and it was the *cheap* one (source generation, no
native to package), which made it look like the obvious thing to build first. Cost pointed one way,
demand pointed the other, and cost is the easier signal to read. Phase 58's finding is the tiebreak
and it held again here: **an unvalidated capability nobody asked for is worse than an absent one**,
and cheapness does not buy an exception.

---

## 4. CLOSED — what emitting Slang would have been for

> **Closed by owner direction 2026-08-13. Nothing in this section is scheduled work.** It is kept
> because it is the reopening brief: if a consumer ever appears for Slang *output*, start here
> rather than re-deriving it. §7 keeps the matching build notes and §5.1 the oracle that made it
> attractive.

Nothing loads Slang at runtime, so the output direction had to justify itself on other grounds, and
by 2026-08-12 it had to do so **without a named consumer** (§3.1) — which is ultimately what closed
it. Two arguments held up, and one did not. **The first two are the trigger to watch for:** if
either ever acquires a real requester, that is the moment to reopen.

- **Migration (the strong one).** A studio with a pile of legacy `.fx` files can run them through
  ShadowDusk and get modern Slang **modules** out — keeping the shader bodies, which are already
  near-valid Slang (§2.3), and turning the FX9 effect scaffolding into entry points. That is a real
  job that nothing else does, and it is squarely in the spirit of a tool whose whole existence is
  "you should not need the old Windows-only toolchain to work with these files."
- **Interchange (the plausible one).** Slang's own target list (DXBC, DXIL, SPIR-V, HLSL, GLSL,
  CUDA, MSL, WGSL) is wider than ShadowDusk's. A consumer who wants their `.fx` to reach a runtime
  ShadowDusk does not target can get there via Slang, with ShadowDusk doing the `.fx`-shaped half
  and Slang doing the rest. Note honestly that this hands the fidelity question to Slang at the
  boundary — which is fine, because we would be handing them a *source file*, making no claim about
  what their toolchain does with it.
- **NOT a runtime target (say so plainly, everywhere).** A `.slang` file is not loadable by
  MonoGame, KNI, FNA, or anything else this project renders in. It must never appear as a cell in
  `docs/validation-matrix.md` §1 and must never be described as a "backend". It is a **conversion
  artifact**, the same category as `ShadowDusk.ShaderToy`'s emitted `.fx`.

---

## 5. The evidence story

### 5.1 CLOSED with §4 — the round-trip oracle the output direction would have had

> **Not scheduled work** (owner direction 2026-08-13). Kept with §4 as part of the reopening brief,
> because this oracle is the best idea the output direction produced and it should not have to be
> invented twice.

Emitting Slang admits a **round-trip byte test that needs no external oracle at all**:

```
        .fx ──[ShadowDusk pipeline]──────────────────────────────> .mgfx  (A)
        .fx ──[ShadowDusk Slang emitter]──> .slang
                                            └─[slangc -target hlsl]─> HLSL
                                                                      └─[same pipeline]─> .mgfx  (B)
        REQUIRE: A == B, byte for byte, across the whole corpus.
```

If the emitted Slang round-trips to byte-identical output, the conversion demonstrably lost
nothing — and it is checked against **bytes this project has already render-proven at rung 4**, not
against a guess. That is a *stronger* evidence model than the ShaderToy route has, and it is
mechanically checkable in CI on every fixture.

It does **not** prove the emitted Slang is *idiomatic* (a mechanical transliteration would pass), so
§4's migration story would have needed a human read of a sample too. But correctness is nailed down,
and correctness is the part that would otherwise be unfalsifiable.

### 5.2 Input — the live one: a reduced gate, not Phase 59's gate

The input direction's product is **an ordinary `.mgfx`/`.fxb`/`.xnb` for a target that is already
rung-4 proven**. So the new link in the chain is not "does an unproven runtime render this
correctly?" (Phase 59's problem) but "did Slang's own HLSL emission preserve the user's intent?" —
and *that* is Slang's correctness, not ShadowDusk's, at a boundary where ShadowDusk hands off a
source file and everything downstream stays faithful.

That is a genuinely weaker claim to have to defend than a new backend, so **this phase does not
inherit Phase 59's hard gate.** What it does need is honesty about the boundary: there is no `mgfxc`
oracle for Slang *input*, so no route through it may ever be called "mgfxc-equivalent". State the
split plainly wherever it appears — the pipeline below the HLSL seam is as faithful as it ever was;
the seam above it is Slang's.

**If Phase 57 §3 resolves in a way that forbids source-fidelity-only claims entirely, this phase
needs re-scoping — not automatic closure.** Recording this as a correction to the first draft, which
asserted the harder gate without distinguishing the readings.

---

## 6. The work — the additive input frontend

> **Read the Status block first.** This section was drafted for the slangc-invoking route, which
> was built, worked, and was then superseded on owner direction by the pure-managed
> HLSL-compatible-subset route that actually shipped. Items below that assume a Slang toolchain
> (A3, A4, parts of A5/A6) are **historical record and future-route groundwork**, not open work —
> the shipped frontend needs none of them.

**The architecturally clean route, and it is cleaner than it first appears.** Slang can *emit
HLSL*. So the route is:

```
.slang --[Slang: HLSL target]--> HLSL --[the existing faithful pipeline, untouched]--> .mgfx / .fxb / .xnb
```

That keeps DXC as the only HLSL→SPIR-V compiler, so **"no substitute compilers" is not violated** —
Slang is a *source translator upstream of the pipeline*, exactly as `ShadowDusk.ShaderToy` is. Every
existing output byte is unaffected by construction, because the pipeline below the seam never
changes.

- **A1 (the gate, do it by hand first).** The Phase-46 Phase-0 precedent: **hand-translate one real
  Slang shader to `.fx` and compile it** before writing any integration. If a human cannot produce
  a `.fx` that compiles and renders, no frontend should be built.
- **A2.** The FX9 gap, which is the same one ShaderToy hit: Slang has **no `technique`/`pass`
  concept**. Something must synthesize the technique block, and that is a *convention decision*
  (which entry points, what pass structure, what render states) that has no single right answer.
  Record how ShaderToy solved it and whether the same synthesis applies.
- **A3.** Packaging: pin a Slang release, host per-RID binaries, restore + SHA-256 verify, pack into
  the NuGet, and add the release gate — the §2.2 cost. **Alternatively**, evaluate whether Slang's
  own `slangc` is only needed at *author* time (an out-of-band converter like `tools/shadertoy2fx`
  originally was), which would avoid shipping the native entirely. **Do A1 and this evaluation
  before A3.**
- **A4.** WASM: the `slang-wasm` artifact means the browser arm is possible. Note the irony worth
  recording — Phase 23 removed `slang-wasm` from the shipping package as a *substitute compiler*;
  this would reintroduce it in a role that does not violate the rule.
- **A5.** Measure the residue in *"backward compatible with **most** existing HLSL code"* (§2.3).
  Sweep the fixture corpus through `slangc -target hlsl` and record what Slang refuses or changes.
  That number bounds the phase and should be known before anything is designed.

  **A5 STARTED 2026-08-13, and the first probe already changed the shape of the phase.** Pinned
  Slang `v2026.14.1` (fetchable via `tools/setup-local-testing.ps1 -WithSlang`, SHA-256 verified).
  Run against `tests/fixtures/shaders/Grayscale.fx`:

  ```
  slangc Grayscale.fx -target hlsl -entry MainPS -stage pixel -o out.hlsl
    → error[E20001]: unexpected token
      error[E20003]: unexpected token          (no output file produced)
  ```

  **`slangc` rejects a whole `.fx`.** It parses the HLSL body fine — the only *warnings* are the
  undefined `OPENGL` / `SM6` preprocessor identifiers, which are expected because the platform
  macros are not defined on that bare invocation — and then it errors on the **FX9 `technique` /
  `pass` block**, which Slang has no concept of.

  **Why this matters more than a compatibility statistic.** The draft framed A5 as *"how much
  ordinary HLSL does Slang refuse?"* The answer so far is "the shader bodies are fine; the
  **effect framework** is the wall" — which means A2 is not a side task, it is the phase's core
  problem. A `.slang` input cannot be an `.fx` with a different extension: there is nowhere for
  `technique`/`pass`/`sampler_state`/render-state to live, and something must decide where the
  entry points and pass structure come from. That is the same synthesis decision `ShadowDusk.ShaderToy`
  faced, and it needs an answer before any integration code is written.

  **Still to do for A5:** sweep the *whole* corpus (not one file) with the platform macros defined
  and the FX9 block stripped, so the body-level residue is measured separately from the effect-level
  wall. Those are two different numbers and conflating them would hide whichever is smaller.

  **A5's risk is NOT the one the requester named, and the difference decides whether A5 can be
  skipped.** vchelaru's 2026-08-12 note (§3.1) reasons that Slang features with no HLSL
  representation are *"likely… also not supported in MonoGame"* — so the residue would be harmless.
  **For the risk he is describing, that is right, and this doc reaches the same conclusion
  independently:** §9's non-goal already rules out Slang's compute/mesh/raytracing surface on
  Phase 58's measurement that stock MonoGame and KNI hold **only** vertex and pixel stages. Two
  routes, one answer, and that half of the residue needs no sweep to dismiss.

  **But A5 measures the other direction, and that one does not dissolve.** A5 feeds *our existing
  HLSL* through `slangc`; the failures it hunts are **valid HLSL that Slang refuses or silently
  rewrites** — not Slang features with nowhere to go. A shader whose HLSL Slang mangles is one
  MonoGame supports perfectly today, so "MonoGame wouldn't support it anyway" does not cover it.
  **OQ2 is the sharp case:** Slang emitting SM6-era HLSL is not an unsupported *feature* at all, it
  is ordinary HLSL in a dialect the OpenGL and FNA targets cannot reach (`SD0015`, `SD0300`, the
  Phase 51 A10 measurements) — a target-reach failure that looks like nothing from the language
  side. **Keep A5, and keep it first.**

- **A6 — the acceptance rule: accept valid Slang; reject only what MonoGame cannot hold.** Owner
  direction 2026-08-13, and it is a design constraint rather than a sentiment, so it needs to be
  made concrete before it can be honoured. It says ShadowDusk is **not** in the business of
  maintaining its own subset of Slang — if `slangc` accepts it and it lands somewhere an `Effect`
  can hold, it works; the frontend is transparent up to the real ceiling and **loud exactly at it.**

  The rule cuts the input space in three, and **only the middle band is ShadowDusk's problem**:

  | Band | Example | Behaviour |
  |---|---|---|
  | Slang that `slangc` itself refuses | a syntax error | **Slang's diagnostic, verbatim** — file, line, column, text, never reformatted (`CLAUDE.md`, "fail loudly") |
  | Slang that compiles to HLSL with **nowhere to land in an `Effect`** | a compute or mesh entry point ([Phase 58](DONE/PHASE-58-extended-shader-stages.md): stock MonoGame and KNI hold **only** VS and PS); SM6-only constructs on the GL/FNA targets (OQ2) | **A registered ShadowDusk diagnostic naming the construct and the reason.** Never a silent pass-through, and never a generic parse error — Phase 58's `FX0014` exists precisely because a wrong-but-plausible code is worse than a blunt one |
  | Everything else | ordinary Slang, ordinary HLSL | **Compiles.** No allow-list, no curated subset |

  Two things to settle while building it: **(a)** the middle band's membership is *target-dependent*
  — a construct DirectX/Vulkan can hold may be out of reach on OpenGL/FNA (OQ2), so the diagnostic
  has to name the target, not just the construct; **(b)** whether these reuse existing codes
  (`SD0015`, `SD0300`, `FX0014`) or need new ones, decided when A5's sweep says what actually turns
  up. **A5 feeds A6 directly** — the sweep's residue *is* the middle band's first draft.

---

## 7. CLOSED — the emitter that reading C would have needed

> **Closed by owner direction 2026-08-13, with §4 and §5.1. No item here is scheduled.** Kept as the
> reopening brief: these five notes are what two days of analysis produced, and B4 in particular is
> a trap someone would otherwise walk into.

Because the shader body is already near-valid Slang (§2.3), **this would have been an
effect-framework translation, not a language translation** — the surprises in the FX9 layer rather
than in the expressions.

- **B1 (the probe, mirroring A1).** Hand-write the Slang you would want out of one real corpus
  `.fx`, and run the §5.1 round-trip on it by hand. This simultaneously establishes what "good"
  output looks like for the migration story and proves the round-trip gate is achievable before any
  emitter exists.
- **B2. Decide what happens to the FX9 constructs, and record it as a convention.** `technique` /
  `pass` / `sampler_state` / annotations have no Slang equivalent. Options run from "entry points
  plus a comment block" to "a Slang module with the pass structure encoded in metadata". This is a
  *design decision with no single right answer*, exactly like ShaderToy's synthesis problem (A2),
  and it must be written down in `project_decisions.md`, not just implemented.
- **B3. Multi-pass and multi-technique effects are the hard case.** A `.fx` can hold several
  techniques with several passes each, sharing globals. A single Slang file with N entry points may
  or may not preserve that structure legibly. Measure against a real multi-pass fixture
  (`FnaMultiPassStates.fx` is in the corpus) rather than designing against the single-pass case.
- **B4. Emit at the *source* level, not from SPIR-V.** The temptation is to reuse the
  `CompilationPipeline.cs:2035` seam that Phases 59/62 use, but that seam holds *SPIRV-Cross's GLSL*
  — round-tripping through SPIR-V would destroy exactly the readability that makes the migration
  story worth anything. Slang output should come off the HLSL source, which is where the
  §2.3 near-superset property lives.
- **B5. Round-trip gate in CI** (§5.1), over the whole corpus, as a `dotnet test` theory — it needs
  no GPU, so unlike a render gate it can run everywhere.

---

## 8. Acceptance

- [x] §3 disambiguated. *(Settled 2026-08-13: **input only.** Reading A closed on evidence, reading
      C closed by owner direction.)*
- [x] **A5, resolved by the final shape rather than by a sweep.** The question was "how much HLSL
      does Slang refuse or change?" — which mattered only while a Slang compiler sat between the
      user's text and DXC. In the shipped route **nothing sits between them**: the body reaches
      DXC verbatim, so there is no emission residue to measure. The slangc-era findings the
      2026-08-13 probes captured (slangc rejects whole `.fx` files — the effect framework is the
      wall, which is *why* the attribute convention exists; slangc mangles every symbol `_0` and
      wraps cbuffers in `SLANG_ParameterGroup_*` structs the GL lowering refuses with `SD0210`)
      are kept in §6's annotated items as groundwork for any future full-Slang route.
- [x] **A1's hand-translate probe run and written up** — inverted but equivalent: a real two-stage
      Slang effect (`Desaturate.slang`, VS+PS, cbuffer, texture, matrix) written in Slang's own
      idiom, converted, and compiled to `.mgfx` on OpenGL **and** DirectX with the user's parameter
      names intact in the decoded effect. Now the pinned fixture
      `tests/fixtures/shaders/slang/Desaturate.slang`.
- [x] **A6, in its superseded-and-final form.** The original rule ("accept whatever `slangc`
      accepts") assumed the toolchain; the owner's final direction replaces it with **accept
      HLSL-compatible Slang; reject Slang-only features by name** (`SD0600`), which keeps the
      part that mattered — *nothing is ever silently narrowed or approximated* — while removing
      the toolchain dependency entirely. Implemented and tested at the boundaries: Slang-only
      constructs reject by name with true line numbers (comment-stripped, line-anchored scan, so
      an identifier merely containing a keyword can never false-reject — tested); a compute
      entry point rejects as `SD0602`; everything subtler falls through to DXC's own verbatim
      diagnostics, which remain the authority.
- [x] **Packaging (A3): resolved by making the question disappear.** The owner rejected shipping
      or requiring any Slang binary; the shipped route needs none, on any platform, browser
      included. Nothing to pin, restore, verify, or gate.
- [x] **Pure-additive:** the frontend is new files plus CLI routing — `CompilationPipeline` and
      every writer are untouched, so no existing output byte *can* move (full suite green
      confirms). No new dependency of any kind, managed or native.
- [x] The route is never described as `mgfxc`-equivalent (§5.2 wording used everywhere), and
      `.slang` appears in `docs/validation-matrix.md` as a §8-style distinct-evidence row, never a
      §1 cell.
- [x] **The three "Slang is dead" pages updated with the two-part statement** (README's
      acknowledgements row, `docfx/architecture/wasm-frontend.md`,
      `docfx/guides/in-browser-kni-blazor.md`): the substitute-compiler rejection stays stated —
      it is the reason DXC is the only HLSL frontend — and the input language is stated as the
      separate, non-violating thing it is. Done in the same change that shipped the frontend,
      exactly as this item required.

## 9. Non-goals

- Slang as a replacement or alternative for DXC anywhere in the pipeline (§2.1, closed on measured
  evidence and confirmed by the requester).
- **Emitting Slang** (§4/§7, closed by owner direction 2026-08-13). ShadowDusk reads `.slang`; it
  does not write it.
- Claiming a `.slang` file is a runtime target or a "backend".
- Slang's compute/mesh/raytracing features — [Phase 58](DONE/PHASE-58-extended-shader-stages.md)
  established that stock MonoGame and KNI can hold **only** vertex and pixel stages, so a Slang
  frontend inherits that ceiling exactly and gains nothing there. Note this is a **non-goal, not a
  silent limit**: per A6 these must be *rejected loudly*, since a Slang author has every reason to
  expect a compute entry point to work.
- Vendoring Slang's standard-library modules, or any Slang binary, tool, or native — on any host
  (owner direction 2026-08-13; the defining constraint of the shipped shape).
- Slang-only language features (`import` modules, generics, `extension`s, `associatedtype`).
  Rejected **by name**, never approximated — the consciously accepted trade of the
  HLSL-compatible-subset route, matching the requester's own framing. If real demand for them
  appears, a full-Slang route can be added additively later; §6's annotated groundwork is where
  it would start.

## 10. Open questions

- **OQ1.** ~~Which reading did Victor mean?~~ **CLOSED. Input.** The requester confirmed it on
  2026-08-12 and owner direction fixed it as the phase's whole scope on 2026-08-13 (§3.1).
- **OQ2 — the open question that matters most, and A5 answers it.** Does Slang's HLSL output land in
  the SM3/SM4-level dialect ShadowDusk's targets need, or does it assume SM6-era HLSL? If the
  latter, **OpenGL and FNA may be unreachable through this route even when DirectX/Vulkan are** —
  the SM ceiling is a real constraint on those two (`SD0015`, `SD0300`, the Phase 51 A10
  measurements). Note what this would mean under A6: not a *rejected shader*, but the **same shader
  reaching some targets and not others**, which is the hardest kind of limit to report well. If A5
  finds it, the diagnostic design in A6(a) is the deliverable that follows.
- **OQ3 — now the packaging question for the phase.** Would an out-of-band author-time converter
  (the original `tools/shadertoy2fx` shape) satisfy this at a fraction of the packaging cost? If
  yes, prefer it, and never ship the native. **This is the phase's main cost lever**, since
  ingesting Slang is the direction that needs a binary at all (§2.2), and A3 says to answer it
  before doing any packaging work.
- **OQ4.** ~~Does the round-trip gate need `slangc` at test time only?~~ **Moot** — the round-trip
  gate belonged to the closed output direction (§5.1).
- **OQ5.** ~~Do the two directions share a file format contract?~~ **Moot** — there is only one
  direction.
- **OQ6.** ~~Which direction goes first?~~ **Moot** — settled by scope, not by sequencing. §3.1 keeps
  the reasoning, because the trap it describes (cheap-but-unrequested beating
  requested-but-costly) is not specific to Slang.
