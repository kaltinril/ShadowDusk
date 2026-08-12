# Phase 61 — Slang support: which of the two questions is actually being asked

**Track:** Additive frontend / reach (purpose-gated). Additive only; **no existing output byte may
change**.

**Status:** 📋 **Planned / not started** (created 2026-08-11).

**Depends on:** [Phase 23](DONE/PHASE-23-in-browser-compilation.md) — **read §2.1 below before
anything else**; that phase already prototyped Slang end-to-end and rejected it for the product,
and that rejection is load-bearing. Also [Phase 47](DONE/PHASE-47-shadertoy-frontend-promotion.md)
(the additive-frontend precedent and its evidence model) and
[Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3 (the PURPOSE decision that governs
whether a source-fidelity-only frontend is in scope at all).

**Blocks:** nothing.

**Gated on:** §3's disambiguation first, then — for the only surviving interpretation — the same
Phase 57 §3 PURPOSE decision that gates [Phase 59](PHASE-59-raylib-cs-backend.md).

> [issue #198](https://github.com/kaltinril/ShadowDusk/issues/198), vchelaru: *"Add slang support
> (see how complicated it is)"*. Filed with an empty body, so **§3 exists to establish which of two
> very different questions this is** before any work starts. One of them is already closed.

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

### 2.3 There is precedent for the shape that *is* open

[`ShadowDusk.ShaderToy`](../src/ShadowDusk.ShaderToy/) (Phases 46/47) takes a language the pipeline
cannot consume and emits ordinary `.fx` text — pure-managed, zero native dependency, upstream of the
pipeline, changing no existing output byte. A Slang **input** frontend would sit in exactly that
architectural slot.

---

## 3. The disambiguation — which question is this? (do this FIRST, it is cheap)

**Reading A — "use Slang as the compiler."** Replace or supplement DXC with Slang for
HLSL→SPIR-V/DXIL. **CLOSED by §2.1.** Costs nothing to answer; answer it and move on.

**Reading B — "accept `.slang` as an input language."** A consumer writes Slang, and ShadowDusk
compiles it to the same `.mgfx`/`.fxb`/`.xnb` outputs it already produces. This is the ShaderToy
shape and is **genuinely open**.

**Reading C — "emit Slang."** Almost certainly not what is meant (nothing consumes Slang at
runtime; it is a source language), but rule it out explicitly rather than silently.

**Deliverable for this section: ask Victor which he meant, and record the answer here.** The
question is one sentence and it determines whether the rest of the phase exists. Do not build
toward B on an assumption.

---

## 4. Area A — reading B, the additive input frontend (the only open shape)

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

---

## 5. The evidence problem, and the gate it inherits

**There is no `mgfxc` oracle for Slang input.** `mgfxc` cannot compile Slang, so there is nothing to
be equivalent *to* — identical to the ShaderToy situation, and it means the bar can only ever be
**source-fidelity against the Slang shader's own reference output**, never `mgfxc`-equivalence.

That is precisely the question **[Phase 57](PHASE-57-universal-compiler-auto-detection.md) §3**
exists to decide: whether THE PURPOSE widens from *"drop-in `mgfxc` replacement"* to two peer
evidence models. So:

> **Area A is HARD-GATED on Phase 57 §3 resolving "yes"**, on the same terms as
> [Phase 59](PHASE-59-raylib-cs-backend.md). If §3 resolves "no", Area A is **closed, not
> deferred**, and this phase reduces to §3's recorded answer plus §2.1.

Whatever is documented must say this in plain words wherever it appears, the way
`docs/validation-matrix.md` §8 already does for the ShaderToy route, so no reader mistakes a Slang
route for a rung-4 drop-in claim.

---

## 6. Acceptance

- [ ] §3 answered by Victor and recorded here; reading A closed by reference to §2.1.
- [ ] If reading B **and** Phase 57 §3 says yes: A1's hand-translate probe run and written up. A
      recorded "a human could not do this convincingly" closes the area, and that is a success.
- [ ] If Phase 57 §3 says no: this phase is closed with that recorded, and no code is written.
- [ ] Any implementation is **pure-additive**: full-corpus byte-identity is an acceptance criterion,
      and `NoMonoGameInProductLibrariesTests`-style guards keep the optional dependency optional.
- [ ] The evidence model is stated as source-fidelity, **not** `mgfxc`-equivalence, in the phase
      doc, `docs/validation-matrix.md` §8, and any user-facing page.

## 7. Non-goals

- Slang as a replacement or alternative for DXC anywhere in the pipeline (§2.1, closed).
- Emitting Slang (reading C).
- Slang's compute/mesh/raytracing features — [Phase 58](DONE/PHASE-58-extended-shader-stages.md)
  established that stock MonoGame and KNI can hold **only** vertex and pixel stages, so a Slang
  frontend inherits that ceiling exactly and gains nothing there.
- Vendoring Slang's standard-library modules beyond what a compile needs.

## 8. Open questions

- **OQ1.** §3: which reading did Victor mean? Everything else waits on this.
- **OQ2.** Does Slang's HLSL output land in the SM3/SM4-level dialect ShadowDusk's targets need, or
  does it assume SM6-era HLSL? If the latter, the OpenGL and FNA targets may be unreachable through
  this route even when DirectX/Vulkan are.
- **OQ3.** Is there a real consumer? Phase 58's lesson is worth applying: one exploratory question
  is not demand, and an unvalidated cell is worse than an absent one. Ask what Slang shader Victor
  actually wants to compile.
- **OQ4.** Would an out-of-band author-time converter (the original `tools/shadertoy2fx` shape)
  satisfy the request at a fraction of the packaging cost? If yes, prefer it, and never ship the
  native.
