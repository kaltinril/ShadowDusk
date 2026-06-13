# Phase 25 — Security: state the trust model honestly

**Status:** Reframed 2026-06-12. The original draft modeled ShadowDusk as if it had to
defend a victim against **untrusted `.fx` supplied by a third party**. For the actual
product that threat model is wrong, so most of the original findings were **removed as
not-real-harm** (recorded below with rationale so they are not re-litigated). What
remains is small: write down the real trust model, and confirm the supply-chain hygiene
that already exists.

---

## The real trust model

The product is a **library a developer adds to their own MonoGame/KNI/FNA project** (or
the CLI / MGCB delivery shapes of it) to compile shaders at build time or runtime. **The
`.fx` author and the person running the compile are the same trust domain** — the
developer. Compiling a `.fx` is **running code you chose to run**, exactly like compiling
C++ or C# you wrote or copied. If a developer pastes a malicious shader from the internet,
that is the same risk as pasting malicious C# from the internet: dangerous to use someone
else's code regardless, and **not something a shader compiler can or should "sandbox" away.**

Concretely, that means a `.fx` doing "hostile" things to the machine running it
(`#include "../../etc/passwd"`, a huge include that OOMs the process, a giant source
string) is **the developer compiling code on their own machine** — they can already read
their own files and exhaust their own memory. No privilege boundary is crossed, so there is
nothing for the library to defend.

ShadowDusk is a **build-time / in-app developer tool, not a multi-tenant sandbox.** We do
not pretend otherwise, and we do not add input-validation theater that implies we are one.

### The one genuinely different case (the consumer's, not ours)

A consumer could *choose* to build a **public service or in-browser fiddle that compiles
strangers' `.fx`** (the XnaFiddle shape). That crosses a real trust boundary — but it is
**the consumer's architecture decision**, and the library cannot own it: compiling
arbitrary shader source is running a compiler over attacker-controlled input, and the
honest mitigation is process/host isolation + resource limits at the service layer, which
only the consumer can apply. Our responsibility is to **say so plainly** (see Deliverable),
not to ship a half-sandbox that invites the consumer to trust it.

Note: a no-filesystem consumer (a browser fiddle) uses `InMemoryIncludeResolver`, which
only resolves from an in-memory dictionary — there is no host filesystem to traverse in the
first place.

---

## Deliverable

A short **`SECURITY.md`** at the repo root that states, in plain language:

1. **Compiling a `.fx` runs code.** Treat shader source like any other source code: only
   compile shaders you trust, the same way you only build C#/C++ you trust.
2. **ShadowDusk is a developer tool, not a sandbox.** It does not isolate the compile from
   the host and does not claim to make untrusted shader input safe.
3. **If you accept third-party `.fx`** (a hosted compile service, a public fiddle), that
   trust boundary is yours: run the compile in an isolated process/container with CPU,
   memory, and filesystem limits. The library will not do this for you.
4. **Supply chain of the natives we ship** (how to verify them) — see below.
5. How to report a vulnerability.

That is the whole phase. There is no input-validation work to gate a release on.

---

## Supply chain (already handled — confirm, don't rebuild)

The only place ShadowDusk itself sits in a trust path is the **native binaries it
distributes** (the developer trusts our package). That discipline already exists and is
enforced; this phase just confirms it and documents it in `SECURITY.md`:

- **vkd3d-shader, the macOS DXC dylib, and the `.wasm` modules** (DXC→WASM, SPIRV-Cross,
  vkd3d→WASM) are **version-pinned and SHA-256-verified** in `tools/restore.{ps1,sh}`
  against hashes embedded in the scripts, hosted on fixed GitHub Release tags, and packed
  under **hard release gates** (`release.yml` / `wasm.yml` fail red if a native is missing
  or mismatched). Established in Phases 37 (vkd3d + DXC dylib), 40 (the FNA pack gate), and
  4.1 (the wasm modules).
- **The runtime SPIRV-Cross native ships transitively via the versioned
  `Silk.NET.SPIRV.Cross.Native` NuGet** (integrity via NuGet + the committed
  `packages.lock.json` under CI `RestoreLockedMode`), **not** the `tools/spirv-cross/`
  copy. That `tools/` copy is an **optional build-from-source convenience** pulled from the
  developer's own Vulkan SDK / vcpkg (their own trusted toolchain) and never ships to
  consumers.

If a future native is added to ShadowDusk's distribution, it must join the same
pin + SHA-256 + release-gate discipline. That is the standing rule; there is no new work
here today.

---

## Removed from the original draft (not real concerns — rationale recorded)

Kept so a future agent does not re-add these as "missing hardening." Each assumed the wrong
threat model (a victim compiling a third party's `.fx`); under the real trust model above,
none is a vulnerability for the product.

- **Path traversal in `FileSystemIncludeResolver` (`#include "../../etc/passwd"`).**
  Removed. The developer compiling their own/copied `.fx` can already read their own files;
  no privilege boundary is crossed. A `FileSystemIncludeResolver` only runs in a
  developer/desktop context where the user owns the files. (A consumer building a hostile-
  input *service* should not point a filesystem resolver with sensitive access at stranger
  input — that is the consumer's isolation responsibility, per the trust model.)
- **No size limit on `#include`d files / on the root source (`/dev/urandom`, huge input).**
  Removed. This is the developer exhausting their own process's memory by choosing to
  compile that input — self-inflicted, exactly like copied code that allocates without
  bound. Not a security boundary. (A hostile-input service applies resource limits at the
  process/container layer; the library cannot meaningfully cap this without becoming the
  sandbox it explicitly is not.)
- **Macro name/value validation in `DxcFlagBuilder` (null bytes, whitespace in `-D`).**
  Removed as a *security* item: macro defines come from the developer's own build
  invocation, not from the untrusted `.fx`. A malformed self-supplied define is a
  correctness/robustness nit (it breaks the developer's own compile), not an attack. If
  ever worth doing, it belongs in normal CLI argument hygiene, not a security phase.
- **DLL planting via the bare-name `TryLoad` fallback in `SpvcLoader`.** Removed.
  Reaching it requires an attacker who already controls the developer's `PATH` or working
  directory — i.e. already owns the machine, where planting a trojan needs no help from us.
  It is also now only a *fallback* after the explicit `AppContext.BaseDirectory ->
  runtimes/<rid>/native` probe (and the loaders were rewritten in 0.5.1 to probe explicit
  per-RID paths first). Not real harm for a developer-run tool.
- **Native supply-chain pinning as outstanding work.** Removed as outstanding — it is
  **done** (see Supply chain above). Only the *documentation* of it remains, folded into
  `SECURITY.md`.

---

## Definition of Done

1. `SECURITY.md` exists at the repo root stating the trust model (compiling `.fx` runs
   code; ShadowDusk is a tool, not a sandbox; third-party-input services are the consumer's
   isolation responsibility), the supply-chain verification of the natives we ship, and a
   vulnerability-reporting contact.
2. No input-validation "hardening" is added that would imply the library sandboxes
   untrusted shader input (it does not, by design).
3. This phase no longer blocks v1.0 — the original "untrusted-input findings" were security
   theater for the product's real threat model and have been removed with rationale above.
