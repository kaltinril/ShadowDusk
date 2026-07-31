# Phase 29 — MGCB Content-Processor Plugin

**Track:** Delivery shapes.
**Status:** ✅ **Done (2026-07-31).** `src/ShadowDusk.MgcbPlugin` is a real, packaged MonoGame
Content Builder content-processor plugin: a `.mgcb` that `/reference:`s it compiles `.fx → .xnb`
through ShadowDusk **in MGCB's own process**, with no `mgfxc`, no `fxc.exe`, and no Wine, and the
`.mgfx` inside that `.xnb` is **byte-for-byte** the ShadowDusk CLI's output for the same source and
target.

> **This document was rewritten on completion.** It was originally written (2026-06-03) on the
> belief that a working "Tier 1" existed — a drop-in binary named `mgfxc` on `PATH` that MGCB would
> shell out to — and framed this phase as a *convenience* layered on top of it. **That premise was
> measured false** ([Phase 52](PHASE-52-monogame-3.8.5-support.md) Area E, 2026-07-28):
> `dotnet mgcb` compiles `.fx` **in-process** at 3.8.2.1105, 3.8.4.1, and 3.8.5 alike and launches
> no external effect compiler (a real logging `mgfxc.exe` first on `PATH`: **zero invocations**,
> valid `.xnb` each time). There was never a process for a `PATH` alias to intercept. The
> "Tier 1 / Tier 2" framing, and every sentence that called this phase optional, are gone. What
> survived unchanged is the **acceptance bar**: the plugin's bytes must equal the **CLI's** bytes.

**Depends on:**
- [Phase 8 — Compiler Library](PHASE-8-compiler-library.md): `EffectCompiler : IShaderCompiler` (`src/ShadowDusk.Compiler/EffectCompiler.cs`) is what the processor wraps. The plugin adds **zero** compilation logic.
- [Phase 9 — CLI Entry Point](PHASE-9-cli-entry-point.md): the CLI is the **byte-identity oracle**. `ShadowDuskCLI` is still a faithful `mgfxc` drop-in for anything that genuinely invokes `mgfxc`; that is simply not MGCB.
- [Phase 42 — Sync Compile API](PHASE-42-sync-compile-api.md): `EffectCompiler.Compile()`. `IContentProcessor.Process` is synchronous, and this is exactly the call site that API exists for — no `.Result`, no `.Wait()`, no sync-over-async anywhere on the path.

---

## Overview

MonoGame's stock content pipeline builds `.fx` with the built-in `EffectImporter` +
`EffectProcessor` (the `/importer:` / `/processor:` lines in every `#begin` block of a `.mgcb`).
Those run **inside MGCB's own process**: SharpDX `D3DCompiler` through 3.8.4.1, and bundled DXC +
MojoShader native tool packages from 3.8.5. Nothing is shelled out, so nothing can be intercepted
from outside.

MGCB does, however, have a real first-class plugin seam: the `/reference:<assembly>` directive.
MGCB loads that assembly with `Assembly.LoadFrom` and reflects it for `[ContentImporter]` /
`[ContentProcessor]` types, which the consumer then names per item. This phase supplies:

- **`ShadowDuskEffectImporter`** — `[ContentImporter(".fx", ".fxh")]`, reads the source text.
- **`ShadowDuskEffectProcessor`** — `[ContentProcessor]`, maps MGCB's build context to a
  `CompilerOptions`, calls `EffectCompiler.Compile`, and wraps the `.mgfx` bytes in
  `CompiledEffectContent` for **MonoGame's own effect `ContentTypeWriter`** to serialize into the
  `.xnb` that a `ContentManager` loads.

Because both delivery shapes call the same `EffectCompiler`, the output is byte-identical by
construction, not by coincidence — and the phase proves it rather than asserting it.

---

## Scope & Non-Goals

**In scope (all delivered):**
- Real source in `src/ShadowDusk.MgcbPlugin` (it was a `.csproj` with zero `.cs` files).
- The importer + processor pair, discovered by MGCB via `/reference:`.
- Processor parameters mapping the user-relevant `CompilerOptions`: `DebugMode`, `Defines`,
  `IncludeDirs`, and the escape hatches `ShaderProfile`, `MgfxVersion`, `DxbcBackend`. **The target
  comes from the content project's own `/platform:` line** — no ShadowDusk-specific flag is ever
  required for correct output (the seamless-for-the-consumer directive).
- Native resolution inside MGCB's process (`PluginNativeLibraryResolver`).
- A self-contained NuGet package shape.
- `samples/mgcb/Content/Content.ShadowDusk.mgcb` — the same content project, through ShadowDusk.
- Tests + a validation driver (below).

**Out of scope / non-goals:**
- **New compilation behavior.** The plugin is a thin adapter; all HLSL→`.mgfx` logic stays in
  `EffectCompiler`/`CompilationPipeline`. No second pipeline, no substitute compiler.
- **Expanding backend coverage.** The plugin inherits exactly what the library supports — including
  its gaps. Ten of `samples/mgcb`'s 34 effects declare techniques through preprocessor macros, which
  the OpenGL path cannot yet compile (`SD0010`, the [Phase 41](PHASE-41-fxc-oracle-monogame-fidelity.md)
  GAP-1 GL half); they fail **identically through the CLI**, so they are excluded from the
  ShadowDusk sample variant and remain a library item, not a plugin one.
- A bespoke `.mgcb` editor / GUI integration.

---

## What the plugin actually does

- **Target mapping** (`MgcbPlatformMap`, explicit — never an ordinal cast; `TargetPlatform.Windows = 0`
  is DirectX while `PlatformTarget.OpenGL = 1`, and `MgfxProfile` is a third numbering again):
  `Windows` → `DirectX`; `DesktopGL` / `MacOSX` / `iOS` / `Android` / `RaspberryPi` / `Web` /
  `NativeClient` → `OpenGL`; the consoles → a loud `SD0501` failure, never a silent GL artifact
  their runtimes cannot load. `ShaderProfile` is the non-required escape hatch for MonoGame's
  `WindowsDX12` / `DesktopVK` runtimes, which MGCB's `TargetPlatform` enum cannot name.
- **`DebugMode`** resolves the way MonoGame's stock processor does: `Auto` follows the content build
  configuration, so the common empty `/config:` optimizes — matching the CLI with no `/Debug`.
- **Errors** translate at the edge only. ShadowDusk's contract is `Result<T, TError>`; MGCB's is
  "throw `InvalidContentException`". The exception message is produced by the CLI's **own**
  `MgcbErrorFormatter` (source-linked into the plugin, so the two can never drift), i.e. the
  canonical `file(line,col-col): error CODE: message` form with the underlying compiler's words
  verbatim beneath. The `ContentIdentity` is deliberately left null: MGCB prefixes
  `sourceFilename(fragment): ` to the message when one is present, which would print the location
  twice.
- **`#include` dependencies** are recorded by a decorator around `FileSystemIncludeResolver` and
  registered with `context.AddDependency`, so editing an `.fxh` rebuilds the effect.
- **Warnings** go through `context.Logger.LogWarning` with the text as a format **argument**, never
  as the format string — HLSL is full of braces, and MonoGame's logger runs `string.Format`.

### The two things that were not obvious until measured

1. **A class library does not copy NuGet assets to its output.** MGCB resolves a `/reference:`d
   plugin's dependencies from that plugin's directory, so `CopyLocalLockFileAssemblies=true` is
   load-bearing: without it the directory holds only the ShadowDusk DLLs and MGCB dies at the first
   `Vortice.Dxc` type.
2. **Nothing in ShadowDusk's existing native loaders can work inside MGCB.** They probe
   `AppContext.BaseDirectory` (MGCB's directory) and `NATIVE_DLL_SEARCH_DIRECTORIES` (from MGCB's
   `deps.json`, which knows nothing about our packages). Measured: a real `dotnet mgcb` build failed
   with `SD0103 SPIRV-Cross native library not found`. `PluginNativeLibraryResolver` hooks
   `AssemblyLoadContext.Default.ResolvingUnmanagedDll` — the runtime's **last** resort, so it can
   never displace a native the normal loaders resolved — and probes the plugin's own install
   directory for the three module names ShadowDusk P/Invokes. It loads **the same pinned natives**;
   it is a lookup path, never a substitute compiler.

### Package shape

**Tools-only.** Everything lands under `tools/net8.0/any/` (no `lib/`), laid out exactly like
`dotnet-mgcb`'s own tool directory, because MGCB's `/reference:` needs the plugin and every
dependency — managed and native — in one directory a consumer can point at. `DevelopmentDependency`
+ `SuppressDependenciesWhenPacking` keep a build-time content-pipeline plugin out of the consumer's
shipped game assembly. It is packed **without** `-p:IncludeSymbols`: a package with no build output
fails `NU5017` trying to build a `.snupkg`.

The MonoGame reference is `IncludeAssets="compile" PrivateAssets="all"`. `ExcludeAssets="runtime"`
is **not** sufficient — `native` is a separate asset group, and MonoGame's content-pipeline natives
(FreeImage, Assimp, mojoshader, ffmpeg) came along for the ride until the reference was narrowed to
`compile`. Shipping `MonoGame.Framework.Content.Pipeline.dll` beside the plugin would also give MGCB
a *second* `CompiledEffectContent` type, for which it finds no `ContentTypeWriter`. `release.yml`
gates both conditions.

---

## Evidence

**The bar: the plugin's `.mgfx` is byte-for-byte the CLI's.** Proven twice, at two levels.

| Proof | What it runs | Where |
|---|---|---|
| **In-suite byte identity** | `ShadowDuskEffectProcessor.Process` driven in-process over 8 fixture × platform cases, compared against the **real `ShadowDuskCLI` executable** run as a separate process (not another in-process `EffectCompiler` call, which would only compare the library to itself). Plus the `ShaderProfile` override on OpenGL/DirectX_11/Vulkan, the loud unsupported-platform failure, the `file(line,col)` error surface, and `#include` dependency registration. | `tests/ShadowDusk.Integration.Tests` `MgcbPluginByteIdentityTests` — **14/14, runs under `dotnet test`** |
| **End-to-end through real MGCB** | A real `.mgcb` `/reference:`ing the built plugin, built by the pinned `dotnet mgcb`. Per case: MGCB exits 0; the `.xnb`'s payload equals the CLI binary's bytes; the `.xnb` **envelope** equals the one MGCB writes for its OWN stock `EffectProcessor`; and the payload **differs** from stock (the positive proof ShadowDusk compiled it). | `validation/MgcbPlugin` — **7/7**, `docs/validation-matrix.md` §6, default-ON in `run-windows-render-gates.ps1` |

Measured additionally, by hand, during the phase:

- The same payload SHA-256 out of **`dotnet mgcb` 3.8.2.1105, 3.8.3, 3.8.4, 3.8.4.1, and 3.8.5** —
  a plugin compiled against the 3.8.2.1105 contract loads into all of them.
- The same payload from the **packed `.nupkg`**, extracted into a bare directory with nothing else
  in it: the self-contained promise, verified on the artifact a consumer actually gets.
- The `.xnb` envelope is byte-identical to MGCB's stock build through byte 137, where the payload
  **length** field (`0x21d` vs `0x28f`) is the first divergence, immediately followed by `MGFX`.

**Rung-4 note.** No new render proof was needed and none is claimed as new evidence: the bytes are
*identical* to the CLI's, and the CLI's `.mgfx` for these targets is already rung-4 proven. What the
plugin adds is the `.xnb` envelope, and that is proven equal to MonoGame's own.

---

## Definition of Done — met

- [x] An MGCB project that `/reference:`s `ShadowDusk.MgcbPlugin` and selects its processor builds
      `.fx → .xnb` with **no `mgfxc` child process** and **no PATH override**.
- [x] The plugin's `.mgfx` bytes are **identical** to the CLI's for the same source + target.
- [x] Shader errors surface through MGCB with file/line/column in the format MGCB and MSBuild parse.
- [x] `samples/mgcb` exercises it end to end (`Content.ShadowDusk.mgcb`, 24/24 effects).
- [x] The NuGet package is self-contained; `release.yml` fails red if a native is missing.
- [x] The PHASE-100 carry-forward *"Full MGCB content processor plugin — separate undertaking
      post-Phase 8"* is closed.

## Known limits / follow-ups (not blockers)

- **Macro-defined techniques on GL** (`SD0010`) — a library gap ([Phase 41](PHASE-41-fxc-oracle-monogame-fidelity.md)
  GAP-1 GL half, blocked on DXC legacy-SM2 codegen), identical through the CLI. It is why 10 of the
  sample's 34 effects are absent from the ShadowDusk variant.
- **The package is ~81 MB**, because it carries the pinned DXC for every RID. That is the cost of
  "add the package, point `/reference:` at it, done"; a per-RID split would trade the promise for
  size and has not been scoped.
- **`/reference:` needs a literal path.** MGCB does not expand MSBuild properties in a `.mgcb`, so a
  consumer spells out the package-cache path (or copies `tools/net8.0/any` somewhere stable). An
  MSBuild-side ergonomic wrapper is a possible future nicety, not a correctness gap.
- **Measured on Windows.** The plugin is pure-managed over the same cross-platform natives and the
  driver is OS-agnostic, but the Linux/macOS MGCB runs have not been taken.
- **KNI** ships no MGCB of its own; a KNI consumer using MonoGame's MGCB gets the same `.mgfx` and is
  unaffected.
