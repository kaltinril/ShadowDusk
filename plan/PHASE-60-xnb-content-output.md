# Phase 60 — `.xnb` output: replace the content pipeline without changing a line of consumer code

**Track:** Delivery shape / drop-in completeness. Additive; **no existing output byte changes** (the
`.xnb` is a *wrapper* around payloads ShadowDusk already produces and has render-proven).

**Status:** 📋 **Planned / not started** (created 2026-08-11). **Committed by owner direction
2026-08-11** — *"the XNB byte output so that a user doesn't have to load an effect"* — i.e. the
consumer keeps `Content.Load<Effect>("Foo")` and never touches `new Effect(gd, bytes)`. That is
this phase's §1 exactly, so it is a scope confirmation rather than a change.

**Depends on:** [Phase 29](DONE/PHASE-29-mgcb-content-processor-plugin.md) (the MGCB plugin — its
`validation/MgcbPlugin` gate already parses and asserts the `.xnb` envelope, so the format knowledge
and the oracle both exist), [Phase 39](DONE/PHASE-39-fna-fx2-output-target.md)/[40](DONE/PHASE-40-fna-fidelity-hardening.md)
(the FNA `.fxb` payload this must also be able to wrap).

**Blocks:** nothing.

**Gated on:** nothing external. **This is the most directly purpose-serving of the three issues
Victor Chelaru filed on 2026-08-09** and the only one of them that needs no new decision.

> **The request in one sentence:** *"This would allow users to replace their content pipeline with
> ShadowDusk, but not change any lines of code at all."*
> — [issue #199](https://github.com/kaltinril/ShadowDusk/issues/199), vchelaru

---

## 1. Where this came from, and why it matters more than it looks

Today a consumer who wants ShadowDusk in place of `mgfxc` has three routes, and **all three ask
them to change something**:

| Route | What the consumer must change |
|---|---|
| `EffectCompiler.CompileAsync` at runtime | Their **code**: `new Effect(gd, bytes)` instead of `Content.Load<Effect>("Foo")` |
| The ShadowDusk **CLI** | Their **build**, and they still get a `.mgfx`, which `Content.Load<Effect>` cannot read |
| The **MGCB plugin** ([Phase 29](DONE/PHASE-29-mgcb-content-processor-plugin.md)) | Their **`.mgcb`** (a `/reference:` line and two dropdown selections) — and it requires MGCB to be in the picture at all |

Issue #199 asks for the fourth: **ShadowDusk emits the `.xnb` directly**, the consumer drops it in
their content directory, and `Content.Load<Effect>("Foo")` keeps working **unmodified**. That is the
purest expression of "drop-in `mgfxc` replacement" the project has been offered, because it is the
only route where the consumer's source tree is untouched.

It is also the route that removes the last hard dependency on MonoGame's own tooling. Phase 29's
plugin is excellent, but it runs *inside* MGCB — so MGCB must be installed, restorable, and working
on that host. A direct `.xnb` writer needs none of that.

---

## 2. What is already established — do not re-derive this

Measured from real artifacts and from MonoGame/FNA source at `v3.8.5` / FNA `main`, 2026-08-11.

### 2.1 ShadowDusk has NO `.xnb` writer today, and that is the whole gap

`ShadowDuskEffectProcessor` is declared
`ContentProcessor<EffectContent, CompiledEffectContent>` ([`ShadowDuskEffectProcessor.cs:40`](../src/ShadowDusk.MgcbPlugin/ShadowDuskEffectProcessor.cs#L40)).
It hands a `CompiledEffectContent` back to MGCB and **MonoGame's own `ContentTypeWriter` serializes
the `.xnb`** — ShadowDusk never writes one byte of the container. So the phase is not "fix the
writer", it is "there is no writer; build one."

### 2.2 The container is small, and the repo already parses it

`validation/MgcbPlugin`'s `XnbEffect.Parse` ([`Program.cs:321-350`](../validation/MgcbPlugin/Program.cs#L321-L350))
already reads the whole structure to run Phase 29's envelope assertion:

```
'X' 'N' 'B'                       magic
<platform byte>                   one of MonoGame's whitelist (see 2.4)
<format version>                  byte
<flags>                           byte; bit 0x80 = LZX/LZ4 compressed
<file size>                       int32
7-bit-encoded  reader count
  per reader:  7-bit-encoded name length, name bytes, int32 reader version
7-bit-encoded  shared-resource count
7-bit-encoded  type id of the primary object
int32          payload length     <-- the effect bytes start here
<payload>                         the .mgfx / .fxb, verbatim
```

That is the *entire* file for an uncompressed single-effect asset. **We can already read it; this
phase makes us able to write it.**

### 2.3 The payload is bytes we already ship, and both consumer families read it identically

MonoGame `v3.8.5` and FNA `main` have **structurally identical** effect readers:

```csharp
// MonoGame EffectReader.Read              // FNA EffectReader.Read
int dataSize = input.ReadInt32();          int length = input.ReadInt32();
...                                        Effect effect = new Effect(
var effect = new Effect(                       input.ContentManager.GetGraphicsDevice(),
    input.GetGraphicsDevice(),                 input.ReadBytes(length));
    data, 0, dataSize);
effect.Name = input.AssetName;             effect.Name = input.AssetName;
```

Both are *length-prefixed raw bytes handed straight to the `Effect` constructor*. So the wrapper is
the same shape for MonoGame, KNI, and FNA — **only the payload differs**, and ShadowDusk already
produces every payload it needs and has render-proven each one:

| Consumer | Payload | Its proof today |
|---|---|---|
| MonoGame DesktopGL / WindowsDX / DX12 / DesktopVK | `.mgfx` | rung 4, all four |
| KNI (GL desktop, DX11, WebGL) | `.mgfx` (v10) / `.knifx` | rung 4 |
| FNA | `.fxb` (`fx_2_0`) | rung 4 vs the `fxc` oracle |

**This is why the phase is additive and cheap on risk: nothing about shader compilation changes.**
It is a container around already-proven bytes.

### 2.4 The platform byte is a validated whitelist, and it is the one genuine design problem

MonoGame's `ContentManager` carries an explicit list of accepted platform identifiers, including
`'w'` Windows, `'a'` Android, `'d'` DesktopGL, `'X'` MacOSX, `'i'` iOS, `'b'` WebAssembly,
`'V'` DesktopVK, `'G'` Windows GDK, plus console and legacy identifiers. It **validates** the byte
rather than ignoring it.

That collides with the standing seamlessness directive, and §4 A2 is where it gets resolved rather
than guessed. The consumer already picks a graphics backend (that is what selects `.mgfx` flavour
today), so the platform byte may simply follow from the target they already chose — but *may* is
not *does*, and the failure mode of getting it wrong is a runtime load error in the consumer's
game, which is exactly the class this project refuses to ship.

### 2.5 The oracle already exists and is already wired

`validation/MgcbPlugin` builds the **same fixture twice** — once through the ShadowDusk plugin and
once through MGCB's stock processor — and asserts the envelope matches byte for byte. A directly
written `.xnb` can be diffed against **the same stock MGCB output**, giving this phase a real
reference-compiler oracle from day one. That is a materially stronger starting position than
Phases 59/60-style work that has no oracle at all.

---

## 3. Areas

### Area A — the `.xnb` writer (the core)

- **A1.** A managed `XnbWriter` in a shipped library, emitting the §2.2 structure around a payload
  and a target descriptor. Pure managed, no native dependency, so it works on every host including
  WASM and Android.
- **A2.** **Resolve the platform byte seamlessly.** Measure what MGCB writes for each `/platform:`
  and whether it is derivable from the `PlatformTarget`/container the consumer already selected. If
  it is, derive it and expose no new knob. If it is not, that is a **decision point, not a flag to
  add reflexively** — the seamlessness directive says a consumer must never have to opt in to get
  *correct* output.
- **A3.** The type-reader manifest string and reader version, **measured from a real MGCB `.xnb`,
  not transcribed from documentation**. Phase 52 is the standing warning here: a documented
  MonoGame behaviour ("the `mgfxc` PATH override") turned out never to have worked on any version.
- **A4.** Decide compression. `/compress:False` is what the Phase 29 gate uses and what §2.2
  describes; compressed XNB (LZX/LZ4) is a separate body format. Uncompressed is almost certainly
  right for a single small effect, but say so on evidence.
- **A5.** Surface it on the delivery shapes: a CLI output mode, and a library API that returns
  `.xnb` bytes. Both must go through **one** writer, the `CompilationPipeline.Run` precedent
  (Phase 42), so `.mgfx` and the payload inside the `.xnb` are identical *by construction*.

### Area B — FNA and KNI arms

- **B1.** FNA: wrap the `.fxb` and prove `Content.Load<Effect>` works in real FNA. FNA's reader is
  §2.3-identical, but its **accepted platform bytes are its own** (FNA is XNA-compatible and may
  accept a different set than MonoGame) — measure, do not assume.
- **B2.** KNI: same, for `.mgfx` v10 and for the `.knifx` container.

### Area C — the evidence (this is the part that makes it real)

The bar is **not** "the file parses". It is Phase 29's bar, which this phase can meet exactly:

- **C1.** The **envelope** is byte-identical to MGCB's own stock `.xnb` for the same asset (the
  §2.5 oracle), excluding only the file-size and payload-length fields.
- **C2.** The **payload** is byte-identical to the ShadowDusk CLI's output for the same source and
  target — the same by-construction property Phase 29 proved for the plugin.
- **C3.** **Rung 4, and it must be a real `ContentManager` load, not a hand-parse:** a real
  MonoGame game calls `Content.Load<Effect>("…")` on the ShadowDusk-written `.xnb`, renders, and
  matches the `mgfxc` build pixel for pixel. A new `validation/*` driver, with its
  `docs/validation-matrix.md` §6 row and a slot in `run-windows-render-gates.ps1`.
- **C4.** The same for FNA (against the `fxc` oracle) and for KNI.

---

## 4. Relationship to Phase 29 — additive, not a replacement

Phase 29's plugin stays. The two serve different consumers and the docs must not imply the new
route supersedes it:

- **The plugin** is for a team who *wants* MGCB in their build (an existing `.mgcb`, other content
  types, the Content Builder task in their `.csproj`) and simply wants ShadowDusk compiling the
  effects inside it.
- **The direct writer** is for a consumer who wants MGCB *out of the picture* — no plugin
  reference, no MGCB restore, no `.mgcb` edit.

Note the ordering consequence: Phase 52 measured that **MGCB compiles `.fx` in-process and never
shells out to `mgfxc` on any supported version**, which is what promoted Phase 29 from convenience
to the only native MGCB route. A direct `.xnb` writer is the first route that sidesteps that
finding entirely, because it never enters MGCB's process at all.

---

## 5. Acceptance

- [ ] `.xnb` written by ShadowDusk, envelope byte-identical to stock MGCB's for the same asset (C1).
- [ ] Payload byte-identical to the CLI's `.mgfx` for the same source + target (C2).
- [ ] **Rung 4:** real `Content.Load<Effect>` in real MonoGame renders pixel-equivalent to the
      `mgfxc`-built `.xnb`, via a new `validation/*` driver wired into the Windows gate script (C3).
- [ ] FNA arm proven against the `fxc` oracle; KNI arm proven (C4).
- [ ] The platform byte is **derived, not asked for** — or, if that proves impossible, the reason is
      recorded in `project_decisions.md` before any flag is added (A2).
- [ ] No existing output byte moves; full `dotnet test` + the Windows render gates green.
- [ ] Support surfaces updated in the same PR: `docs/validation-matrix.md` (§1 cells + a §6 driver
      row), `docs/the-purpose.md` (the delivery-shape list), `README.md`, the DocFX site, and
      `docs/pipeline-overview.puml` **with its SVG regenerated**.

## 6. Non-goals

- Writing **other** XNB asset types (textures, models, fonts). This is the *effect* writer only;
  ShadowDusk is a shader compiler, not a content pipeline.
- **Reading** `.xnb`. Nothing in the product needs it (the validation drivers' parser is test-side).
- Compressed XNB, unless A4 finds a consumer that requires it.
- Replacing or deprecating the Phase 29 MGCB plugin (§4).

## 7. Open questions

- **OQ1.** Does `Content.Load<Effect>` require anything of the asset **name/path** beyond the file
  location — and does the `.xnb` need a matching `.mgcb`-era companion file? (Expected: no, but the
  whole value of this phase is "drop the file in and it works", so this must be *demonstrated*.)
- **OQ2.** Is the type-reader version field stable across MonoGame 3.8.1.263 → 3.8.5? If it moved,
  the forward-compat sweep (`validation/ForwardCompat`) needs an `.xnb` arm too.
- **OQ3.** Does KNI accept MonoGame's platform bytes unchanged, or does it maintain its own list?
- **OQ4.** Should the CLI's `.xnb` mode be selected by output **extension** (`out.xnb` → wrap) or by
  an explicit switch? Extension-driven is more seamless and matches how `mgfxc` behaves; confirm it
  cannot mis-fire.
- **OQ5.** Does the asset need to sit under a `Content` directory with a `.mgcb`-era folder
  structure for `Content.Load` to find it by the name the consumer already uses? The owner
  direction's whole point is that **no consumer code changes**, so the answer must be "drop it where
  their existing `.xnb` was, and it works" — demonstrate that, do not reason about it.
