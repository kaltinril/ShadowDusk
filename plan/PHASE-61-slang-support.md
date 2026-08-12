# Phase 61 — Slang support: which of the two questions is actually being asked

**Track:** Additive frontend / reach (purpose-gated). Additive only; **no existing output byte may
change**.

**Status:** 📋 **Planned / not started** (created 2026-08-11). **Scope set by owner direction
2026-08-11: ShadowDusk is to support Slang as BOTH an input and an output format.** That resolves
§3's disambiguation before it was asked — readings **B (input)** and **C (output)** are both in
scope; reading **A (Slang as a substitute compiler) remains closed** on Phase 23's measured
evidence, and nothing in the owner direction asks for it.

**Depends on:** [Phase 23](DONE/PHASE-23-in-browser-compilation.md) — **read §2.1 below before
anything else**; that phase already prototyped Slang end-to-end and rejected it for the product,
and that rejection is load-bearing. Also [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md)
(the additive-frontend precedent and its evidence model) and
[Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3 (the PURPOSE decision that governs
whether a source-fidelity-only frontend is in scope at all).

**Blocks:** nothing.

**Gated on:** **nothing, for the output direction** (§5.1 explains why: it has a self-contained
round-trip oracle and claims no new runtime target). The **input** direction has a *reduced* gate
compared with Phases 59/62 — see §5.2, which is a correction to this doc's own first draft.

> [issue #198](https://github.com/kaltinril/ShadowDusk/issues/198), vchelaru: *"Add slang support
> (see how complicated it is)"*. Filed with an empty body. §3's disambiguation was **answered by
> owner direction on 2026-08-11: both input and output.**

---

## 1. Where this came from

One of three issues Victor Chelaru filed on 2026-08-09, alongside
[#199](https://github.com/kaltinril/ShadowDusk/issues/199) (→ [Phase 60](PHASE-60-xnb-content-output.md))
and [#197](https://github.com/kaltinril/ShadowDusk/issues/197) (→ [Phase 62](PHASE-62-skiasharp-sksl-target.md)).
The body is empty and the title's parenthetical — *"see how complicated it is"* — reads as a
scoping request rather than a commitment. This doc is written to answer that: **here is how
complicated it is, and here is which half is already decided.**

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

### 2.3 Slang's relationship to HLSL — the fact that shapes BOTH directions

From Slang's own user guide (`docs/user-guide/00-introduction.md`, `02-conventional-features.md`,
read 2026-08-11):

> *"Slang extends the HLSL language with thoughtfully selected features from modern general-purpose
> languages."*
> *"Slang is **backward compatible with most existing HLSL code**."*
> *"Slang supports the following expression forms with **nearly identical syntax to HLSL**…"*

It keeps `cbuffer`, `register`, `SV_` semantics, and HLSL function-declaration syntax explicitly for
compatibility. Its named compile targets are **DXBC, DXIL, SPIR-V, HLSL, GLSL, CUDA** (plus MSL and
WGSL), and `slangc -target hlsl` is a documented, first-class invocation.

**Two consequences, and they are the backbone of this phase:**

1. **The input route is real and clean.** Because HLSL is a *supported emit target*,
   `.slang → [Slang] → HLSL → [ShadowDusk's existing faithful pipeline, untouched]` works without
   displacing DXC from anything.
2. **The output route is far cheaper than it sounds, and its cost sits somewhere unexpected.**
   Since Slang is a near-superset of HLSL, **the shader *body* is already valid Slang**. Emitting
   Slang from a `.fx` is therefore not a language translation at all — it is an **effect-framework
   translation**. All the work is in the FX9 layer (`technique` / `pass` / `sampler_state` /
   annotations), which Slang has no concept of and which must become entry points plus, at most, a
   module structure. Note the word "most" in Slang's own compatibility claim: it is *most* HLSL, not
   all, so the residue must be measured, not assumed away (§6 A5).

### 2.4 There is precedent for the shape that *is* open

[`ShadowDusk.ShaderToy`](../src/ShadowDusk.ShaderToy/) (Phases 46/47) takes a language the pipeline
cannot consume and emits ordinary `.fx` text — pure-managed, zero native dependency, upstream of the
pipeline, changing no existing output byte. A Slang **input** frontend would sit in exactly that
architectural slot.

---

## 3. The disambiguation — RESOLVED by owner direction (2026-08-11)

**Reading A — "use Slang as the compiler."** Replace or supplement DXC with Slang for
HLSL→SPIR-V/DXIL. **CLOSED by §2.1**, and the owner direction does not ask for it. Slang never
displaces DXC on the HLSL path.

**Reading B — "accept `.slang` as an input language." → IN SCOPE.** A consumer writes Slang;
ShadowDusk compiles it to the same `.mgfx` / `.fxb` / `.xnb` outputs it already produces. Area A.

**Reading C — "emit Slang." → IN SCOPE.** This doc's first draft called C *"almost certainly not
what is meant"* and proposed ruling it out. **That was wrong, and the correction is worth keeping
visible**, because the reasoning behind the dismissal was itself wrong in an instructive way: it
assumed the only reason to emit a format is for something to *load* it at runtime. Nothing does load
Slang — but that does not make emitting it pointless, because **Slang is a source language whose
consumer is the user's own toolchain, not a runtime.** Area B, and §4 states its value honestly.

---

## 4. What emitting Slang is actually *for* — state this before building it

Nothing loads Slang at runtime, so the output direction must justify itself on other grounds. Two
that hold up, and one that does not:

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

## 5. The evidence story — better than this doc first assumed

### 5.1 Output: there IS an oracle, and it is ShadowDusk itself

The first draft of this doc assumed the Slang work inherited Phase 59's "no reference compiler ⇒
source-fidelity only" problem wholesale. **For the output direction that is wrong, and the
correction matters because it removes the gate.**

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
the migration story in §4 needs a human read of a sample too. But correctness is nailed down, and
correctness is the part that would otherwise be unfalsifiable.

**Consequence: the output direction is NOT gated on
[Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3.** It claims no new runtime target and
no new evidence model — it makes a falsifiable byte claim against existing proven output.

### 5.2 Input: a reduced gate, not Phase 59's gate

The input direction's product is **an ordinary `.mgfx`/`.fxb`/`.xnb` for a target that is already
rung-4 proven**. So the new link in the chain is not "does an unproven runtime render this
correctly?" (Phase 59's problem) but "did Slang's own HLSL emission preserve the user's intent?" —
and *that* is Slang's correctness, not ShadowDusk's, at a boundary where ShadowDusk hands off a
source file and everything downstream stays faithful.

That is a genuinely weaker claim to have to defend than a new backend, so **the input direction
should not simply inherit Phase 59's hard gate.** What it does need is honesty about the boundary:
there is no `mgfxc` oracle for Slang *input*, so no route through it may ever be called
"mgfxc-equivalent". State the split plainly wherever it appears — the pipeline below the HLSL seam
is as faithful as it ever was; the seam above it is Slang's.

**If Phase 57 §3 resolves in a way that forbids source-fidelity-only claims entirely, Area A needs
re-scoping — not automatic closure.** Recording this as a correction to the first draft, which
asserted the harder gate without distinguishing the two directions.

---

## 6. Area A — reading B, the additive input frontend

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
  That number bounds both directions and should be known before either is designed.

---

## 7. Area B — reading C, emitting Slang

Because the shader body is already near-valid Slang (§2.3), **this is an effect-framework
translation, not a language translation.** Plan it that way, and expect the surprises in the FX9
layer rather than in the expressions.

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

- [x] §3 disambiguated. *(Owner direction 2026-08-11: input **and** output; reading A stays closed.)*
- [ ] **A5's compatibility sweep run first** — it bounds both areas and is cheap.
- [ ] **Area B (output):** B1's hand-written target established; the §5.1 **round-trip byte gate
      green across the corpus**; B2's FX9 convention recorded in `project_decisions.md`; B3 measured
      against a real multi-pass fixture.
- [ ] **Area A (input):** A1's hand-translate probe run and written up. A recorded "a human could
      not do this convincingly" closes the area, and that is a success, not a failure.
- [ ] Packaging decided on evidence (A3): if `slangc` is only needed at author time, **do not ship
      the native**. If it must ship, it follows the Phase 37/40 pin + SHA-256 + release-gate
      playbook with no exceptions.
- [ ] **Pure-additive:** full-corpus byte-identity on every existing target is an acceptance
      criterion for both areas, and the optional dependency stays optional (the
      `NoMonoGameInProductLibrariesTests` pattern).
- [ ] Neither direction is ever described as `mgfxc`-equivalent, and `.slang` never appears as a
      `docs/validation-matrix.md` §1 cell (§4) — a §8-style row instead.

## 9. Non-goals

- Slang as a replacement or alternative for DXC anywhere in the pipeline (§2.1, closed — and not
  asked for by the owner direction).
- Claiming a `.slang` file is a runtime target or a "backend" (§4).
- Slang's compute/mesh/raytracing features — [Phase 58](DONE/PHASE-58-extended-shader-stages.md)
  established that stock MonoGame and KNI can hold **only** vertex and pixel stages, so a Slang
  frontend inherits that ceiling exactly and gains nothing there. (Slang *output* is unaffected by
  this: it never has to load anywhere.)
- Vendoring Slang's standard-library modules beyond what a compile needs.
- Emitting *idiomatic* Slang that uses generics, interfaces, or link-time specialization. Round-trip
  correctness first; elegance is a later, separate question.

## 10. Open questions

- **OQ1.** ~~Which reading did Victor mean?~~ **Answered by owner direction 2026-08-11: both.**
- **OQ2.** Does Slang's HLSL output land in the SM3/SM4-level dialect ShadowDusk's targets need, or
  does it assume SM6-era HLSL? If the latter, the **OpenGL and FNA targets may be unreachable
  through the input route even when DirectX/Vulkan are** — the SM ceiling is a real constraint on
  those two (`SD0015`, `SD0300`, the Phase 51 A10 measurements). A5's sweep answers this.
- **OQ3.** Would an out-of-band author-time converter (the original `tools/shadertoy2fx` shape)
  satisfy the input direction at a fraction of the packaging cost? If yes, prefer it, and never ship
  the native. **Note this cuts differently for the two directions:** *emitting* Slang needs no Slang
  binary at all in the product (it is source generation), while *ingesting* it does — so the output
  direction may ship with zero new native dependency, which is a strong argument for doing it first.
- **OQ4.** Does the round-trip gate (§5.1) need `slangc` at **test** time only? If so it is a test
  dependency, not a product one, and the whole output direction stays native-free in the shipped
  packages.
- **OQ5.** Do the two directions share a file format contract? If ShadowDusk both emits and ingests
  Slang, the emitted form should obviously be one the ingest path accepts — a cheap, high-value
  self-consistency test (`emit → ingest → compile == direct compile`) that subsumes part of §5.1.
