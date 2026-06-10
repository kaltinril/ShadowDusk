# Phase 39 Option A — Product-Purpose Review (FNA fx_2_0 target)

**Scope reviewed:** `C:\git\ShadowDusk\CLAUDE.md`, `C:\git\ShadowDusk\docs\the-purpose.md`, `C:\git\ShadowDusk\plan\PHASE-39-fna-fx2-output-target.md`, `C:\git\ShadowDusk\plan\plan.md` (Key Decisions, lines 120–176), plus the implicated source/packaging: `PlatformTarget.cs`, `CompilerOptions.cs`, `CompiledShader.cs`, `IShaderCompiler.cs`, `MgfxWriter.cs`, `CompilationPipeline.cs`, `Vkd3dShaderCompiler.cs`, `Vkd3dNative.cs`, `ShadowDusk.HLSL.csproj`, `ShadowDusk.Compiler.csproj`, `ShadowDusk.Cli.csproj`, `ShadowDusk.Core.csproj`, `tools/restore.ps1`, `tools/restore.sh`, `ArgumentParser.cs`, `README.md`, `tests/golden/`.

**Overall:** Option A (vkd3d `VKD3D_SHADER_TARGET_D3D_BYTECODE` SM1–3 blobs + managed `Fx2EffectWriter` emitting `0xFEFF0901`) is the purpose-faithful design. It is the same shape of work that produced the MGFX writer, uses a component the purpose doc already blesses, and is genuinely additive. The review found **one blocker (packaging/self-contained)**, **four majors (vkd3d pin vs byte-unchanged DoD; CTAB parameter binding; evidence-ladder honesty; pipeline/determinism specifics)**, and a set of minors (naming/doc wording). None invalidates Option A; all must be folded into the phase plan before/during implementation.

---

## 1. Constraint-by-constraint audit

### "One pipeline, no substitute compilers" — HONORED
`docs/the-purpose.md:15` already names vkd3d-shader as a faithful pipeline component ("or `vkd3d-shader` → DXBC for DirectX"), and Phase 18 set the precedent that a target whose reference compiler is closed (fxc) may use vkd3d as the faithful cross-platform equivalent, validated against the real runtime. FNA's reference compiler (`fxc /T fx_2_0`, d3dcompiler_43-era) is closed and Windows-only; there is no "same compiler" option that doesn't involve Wine (Option C, correctly rejected at PHASE-39:122). Using vkd3d's real HLSL→SM3 **compiler** (not a transpile shim) plus a managed container writer is squarely inside the "one faithful pipeline" rule — provided **every host uses the same backend** (see §1-Determinism and finding 5). The rejected alternatives (B: wait indefinitely; C: Wine; D: write our own SM3 codegen) are correctly rejected for the right reasons.

One guard to write down: when WASM is ever in scope, FNA-in-WASM is blocked by the same no-native-P/Invoke problem as DXBC (Phase 4.1, `src\ShadowDusk.Wasm\JsShaderBackends.cs:19`). Per the-purpose.md:15, that host is **not done** for FNA — never a licence to substitute a different SM3 compiler. `WasmShaderCompiler` must return a clear `ShaderError` for `PlatformTarget.Fna`.

### "Self-contained" — currently VIOLATED for any NuGet consumer of the vkd3d path; BLOCKER for FNA
Verified from the csproj/packaging:
- `src\ShadowDusk.HLSL\ShadowDusk.HLSL.csproj:46-62`: libvkd3d-shader is carried only as `<None CopyToOutputDirectory="PreserveNewest">` items, **conditional on the file existing** in `tools/vkd3d/` (a restored, gitignored artifact). `None` items without `Pack="true"`/`PackagePath` are **not** included in the NuGet. Repo-wide grep confirms the only packed extras are a README and an icon (`ShadowDusk.Wasm.csproj:31`, `ShadowDusk.Cli.csproj:32`). There is no `runtimes/{rid}/native` packing anywhere.
- `tools/restore.sh:82`: "Non-fatal: vkd3d is opt-in (default DXBC backend is the d3dcompiler oracle)." `tools/restore.ps1:67-81`: only a **win-x64** build recipe; hosting "NOTE: This binary is NOT committed… Hosting" is unresolved; `ShadowDusk.HLSL.csproj:51` says "Only the Windows binary exists locally today."
- `ShadowDusk.Compiler.csproj:10` (package description) promises only "native DXC + SPIRV-Cross binaries transitively" — accurately omitting vkd3d.
- `plan/plan.md:168-172`'s delivery table ("Downloaded by tools/restore.sh, bundled into `dotnet publish` output") describes the **repo-build/CLI-publish** path, not the library NuGet path.

Today this gap is masked: the DirectX target defaults to the Windows-shipped `d3dcompiler_47` oracle (`CompilerOptions.cs:58`, `CompilationPipeline.cs:140-144`), so Windows NuGet consumers work. **FNA has no system fallback on any OS** — vkd3d is the only SM3 compiler. So `PlatformTarget.Fna` via the published `ShadowDusk.Compiler` package would fail with SD0211 (`Vkd3dShaderCompiler.cs:20-22`) on Windows, Linux, and macOS alike. That breaks CLAUDE.md's hard requirement ("native pieces ride *inside* the NuGet package… never a separate manual install") and README.md:11.

**Required in this phase:** per-RID vkd3d builds (linux-x64, osx-x64/arm64, win-x64), hosted artifacts, and `runtimes/{rid}/native` packing in ShadowDusk.HLSL — or the phase doc must honestly scope the FNA target as repo-build/CLI-only and mark the library delivery shape **not done** (and no public docs may claim FNA support meanwhile). The phase doc currently does not mention packaging at all; that omission is the single biggest purpose risk in the plan.

### "Seamless for the end user" — HONORED as designed, with three definitions to pin down
Choosing the FNA target = choosing the runtime the consumer's game already targets — explicitly the allowed kind of choice (CLAUDE.md User Directives: "Supporting a new platform the consumer's game already targets… is seamless and fine"). Good seamless properties of the design: SM3 source mode is keyed off the target automatically (PHASE-39:109), shader profiles come from the source's own `compile ps_2_0` statements (no per-shader flags), defaults are unchanged (library default OpenGL, PHASE-39:108; CLI default DirectX_11, `CompilerOptions.cs:16`), and one `.fxb` serves **all** FNA graphics backends because MojoShader translates at load — no per-backend variants to expose.

Flag in the design — three options whose Fna semantics must be defined so they can never become correctness flags:
1. `CompilerOptions.DxbcBackend` (`CompilerOptions.cs:52-58`, "Ignored for non-DirectX targets") — keep that literally true for Fna. Do not let Windows default to a d3dcompiler SM3 path while Linux uses vkd3d: that makes output host-dependent (breaks the-purpose.md:36 cross-host byte-identity) and makes a flag affect correctness. d3dcompiler_47 *can* compile vs_3_0/ps_3_0 and is welcome as a **test-time oracle** (Phase 18 posture), never the consumer path.
2. `CompilerOptions.Debug` — PHASE-39:55: MojoShader is stricter on fxc-debug-style code ("compile optimized, not /Od"). `Debug=true` must never produce a `.fxb` MojoShader rejects; define Debug as ignored-or-safe for Fna and document it.
3. The XNA4 `0xBCF0…` header question (PHASE-39:145) must be resolved by the implementation (emit whatever real FNA accepts), never surfaced as an option.

Also note `MgfxVersion` (`CompilerOptions.cs:47-50`) is ignored for Fna — document, don't error.

### "Deterministic output" — ACHIEVABLE; three requirements
(1) `Fx2EffectWriter` is fully managed — enforce stable orderings (parameter/technique/object tables, string ordering), no timestamps, no hash-iteration order (the `MgfxWriter` precedent, `src\ShadowDusk.Core\MgfxWriter.cs`). (2) vkd3d's compiler is deterministic for a fixed version — pin it (see §vkd3d pin). (3) Same backend on every host (above) so the Phase-30-style cross-OS byte-identity checks can extend to `.fxb`. Reminder per the-purpose.md:43: determinism is ShadowDusk's own reproducibility; byte-equality with fxc is a non-goal (plan.md:160 already records vkd3d ≠ fxc bytes for SM5 — same applies to SM3).

### "Fail loudly with diagnostics" — REQUIRES DESIGN, not inheritance
Three surfaces (finding 6):
- **SM4-style source under the FNA target.** Because FNA mode skips the FxPreParser SM3→SM4 forward rewrites (PHASE-39:109, `FxPreParser.cs`), MonoGame-DX11-style source (`Texture2D`/`.Sample()`/`SV_Target`) or `compile vs_4_0+` in a technique must produce a ShadowDusk diagnostic with guidance ("FNA requires D3D9-style HLSL: texture + sampler_state, tex2D, COLOR0/POSITION0, compile ps_2_0/ps_3_0"), not vkd3d's raw parse errors. Never silently down-translate.
- **vkd3d SM3 gaps.** `VKD3D_ERROR_NOT_IMPLEMENTED` (`tools\vkd3d\vkd3d_types.h:54`) must surface with file/line/op (Core Design Constraint 5), following the SD0211 clear-error pattern; never emit a partial blob. Keep `LogLevel=Warning` capture (`Vkd3dShaderCompiler.cs:82-83`).
- **Missing native lib / WASM host** — SD0211-style clear errors, tested.

### "Backwards compatibility" — HONORED, with one shared-binary caveat
No MonoGame bump; `MgfxWriter`/MGFX v10 untouched; `PlatformTarget.Fna` is additive (existing values 0–3 keep their numeric identity, `PlatformTarget.cs:16-28`); CLI profile addition is additive (mgfxc has no FNA profile, so drop-in parity is unaffected; `ArgumentParser.cs:188-216`). FxPreParser changes must be mode-gated; the existing `tests/golden/{DirectX_11,OpenGL}` fixtures are the right CI mechanism to prove "existing targets byte-unchanged" — add that assertion explicitly to the phase plan. **The caveat:** the shared vkd3d native binary (next section).

### The vkd3d 1.17 pin vs the phase's 1.18 analysis — MAJOR, must be resolved explicitly
`tools/restore.ps1:67-70` and `tools/restore.sh:63-66` pin **vkd3d-1.17**; `plan/plan.md:155` records the 1.17 build. PHASE-39:88-94 evaluates "state in vkd3d **1.18**" and notes SM1–3 texture ops only landed in 1.18 (Nov 2025). Two consequences:
1. The corpus may not compile on the shipped 1.17 — run the 10-shader SM3 corpus through `D3D_BYTECODE` at ps_2_0/ps_3_0 **as the first implementation step** (cheap: the binding enum `Vkd3dTargetType.D3dBytecode = 4` already exists at `Vkd3dNative.cs:50`; only a new compile path is needed). Note the 1.18-landed ops named (tex/texbem/texcoord/bem) are ps_1_x-era, so 1.17 may suffice for ps_2_0+ — verify, don't assume.
2. If a bump to ≥1.18 is needed, the **same binary serves the validated DirectX SM5 target** — DX vkd3d-backend bytes may change, contradicting the DoD "Existing MonoGame GL/DX `.mgfx` output is byte-unchanged" (PHASE-39:136). A bump is *allowed* (determinism is per compiler version, CLAUDE.md constraint 3 / the-purpose.md:43) but must be deliberate: re-baseline the DirectX_11 goldens, re-run the Phase 18 `DxbcBackend.Vkd3d` runtime validation, and amend the DoD wording to "OpenGL bytes unchanged; DX goldens consciously re-baselined and re-validated." It must never happen as a silent side effect of the FNA work.

---

## 2. The bar for FNA, and what this phase can honestly claim

**The bar (FNA analog of "identical to mgfxc in the real runtime"):** ShadowDusk's `.fxb`, loaded by **real FNA** (`new Effect(gd, bytes)` → FNA3D → MojoShader), renders **pixel-equivalent** to the same source compiled with `fxc /T fx_2_0`, compared on the **same FNA3D backend** (never cross-backend, never vs MonoGame). PHASE-39:112,135 states this correctly.

**Evidence ladder (weakest → strongest):**
1. Compiles without error (vkd3d SM3 + writer produce bytes).
2. `.fxb` is structurally well-formed: starts `0xFEFF0901`; parses against a managed structural checker written from `mojoshader_effects.c`; byte-layout cross-checked against a known-good fxc-produced `.fxb` fixture.
3. **Actual MojoShader** (the real consumer C library, test-only native harness — the analog of "our own renderer" in the-purpose.md:41 rung 3) parses **and translates** the effect with no errors, and its symbol table exposes the expected parameters.
4. **Real FNA** loads and renders pixel-equivalent to fxc's output for the SM3 PS-only corpus.

**What THIS phase can honestly claim:** no FNA app, MojoShader binding, or fxc golden exists in-repo (`tests/` holds only MonoGame-oriented projects; `tests/golden/` only `DirectX_11/` and `OpenGL/`). So the implementation phase as described reaches **rungs 1–2**; **rung 3** only if it adds a MojoShader harness (strongly recommended — cheapest high-value gate, and the parser *is* the spec); **rung 4** needs an FNA render harness plus one-time fxc-built reference `.fxb`s (building those on a Windows box with fxc is legitimate test-oracle use, mirroring Phase 18's d3dcompiler_47 posture — it never ships in the product).

**What the phase doc must say to stay honest:** keep rung 4 as the DoD, but add a "claimed rungs" status line stating which rungs each PR proves; status stays "proxies green; bar not proven" until rung 4; **no README/docs/release-notes claim of FNA support before rung 4**. This is exactly the drift the-purpose.md:40 warns about ("a proxy can be green while the real goal is unmet… This has happened").

---

## 3. Naming / API review

**`PlatformTarget.Fna` — endorsed, single axis.** Although PlatformTarget's summary says "MonoGame/KNI backend," its operative definition — "Each target is a distinct emitted artifact loaded by a different runtime path" (`PlatformTarget.cs:6-8`) — fits FNA exactly. A separate output-format axis (PlatformTarget × EffectFormat) would create invalid combinations (Fx2+OpenGL?) and expose a choice the consumer must never make (seamless directive: "never expose the choice"). One member only — MojoShader's load-time translation means one `.fxb` serves all FNA backends; never add `Fna_OpenGL`-style variants.

- **Member name:** `Fna` (Microsoft.Xna namespace precedent; .NET 3+-letter-acronym convention; the brand-cased `DirectX`/`OpenGL` precedent doesn't transfer to a pure acronym).
- **CLI:** `/Profile:FNA`, case-insensitive like the others (`ArgumentParser.cs:190-194`); update usage text (`:14-15`) and the unknown-profile message (`:216`). Defaults unchanged.
- **Doc comments:** reword `PlatformTarget.cs:5-9` to "the consumer runtime/loader and its emitted artifact"; the `Fna` member doc states: D3D9 Effects Framework binary (`0xFEFF0901`, fx_2_0), embedded SM1–3 bytecode, consumed by FNA via FNA3D/MojoShader, conventional extension `.fxb`, `MgfxVersion`/`DxbcBackend` ignored.
- **Writer location:** `Fx2EffectWriter` in **ShadowDusk.Core**, next to `MgfxWriter.cs`. Dependency directions support it: Core is the dependency-free root (`ShadowDusk.Core.csproj` has no project refs) referenced by HLSL (`ShadowDusk.HLSL.csproj:29`) and Compiler (`ShadowDusk.Compiler.csproj:28-30`); Core already owns the container-writer precedent and the writer's inputs (`RenderStateBlock`, reflection types, `CompiledShaderBlob`). HLSL is the wrong layer (frontend). **Do not** create a `ShadowDusk.Fna` package — the release machinery is hardwired to the six-package set (CLAUDE.md Releases).
- Optional nicety: a derived (read-only) container-format/extension helper on `CompiledShader` for CLI/docs — derived from Target, never a choice.

---

## 4. Risk register (top risks to the promise, with required this-phase mitigations)

| # | Risk | Mitigation required in THIS phase |
|---|---|---|
| 1 | **vkd3d SM3 gaps** (pinned 1.17; ops landing through 1.18) reject or miscompile corpus shaders | First step: corpus-wide compile audit through `D3D_BYTECODE` on pinned 1.17 before any writer work; decide pin (stay vs bump); map `VKD3D_ERROR_NOT_IMPLEMENTED` to clear SD-coded diagnostics; if bumping, re-baseline DX goldens + re-run Phase 18 validation (see §vkd3d pin) |
| 2 | **fx_2_0 layout fidelity, no public spec** — MojoShader's parser is the spec | Treat `mojoshader_effects.c` as normative; structural golden test against a real fxc-produced `.fxb` fixture; MojoShader parse+translate harness in CI (rung 3); resolve the XNA4-header question empirically |
| 3 | **In-pass render states not honored by FNA** → silent visual divergence with green proxies | Emit faithfully from `RenderStateBlock`; include a state-bearing shader in the rung-3/4 corpus; record the honored/ignored split in the phase doc; document (don't promise) when FNA docs ship |
| 4 | **Parameter binding via CTAB names** — if vkd3d's SM3 blobs lack the CTAB comment, by-name parameter sets silently no-op | Named verification step: inspect blobs for CTAB fourcc + register mapping vs fxc golden; if absent, inject managed CTAB from ShadowDusk reflection; MojoShader symbol-table round-trip test; rung-4 shader visibly driven by a runtime-set parameter |
| 5 | **Packaging** — vkd3d not in any NuGet; win-x64 only | Per-RID builds + hosting + `runtimes/{rid}/native` packing, or honest "library shape not done" scoping (blocker finding) |
| 6 | **Host-dependent output** (oracle on Windows vs vkd3d elsewhere) breaks one-pipeline byte-identity | Fna path uses vkd3d on all hosts; `DxbcBackend` stays ignored for Fna; d3dcompiler SM3 is test-oracle only; define `Debug` semantics (never an fxc-debug-style blob MojoShader rejects) |
| 7 | **WASM host** silently broken for Fna | Explicit, tested `ShaderError` from `WasmShaderCompiler` for `PlatformTarget.Fna`; no substitute compiler ever |
| 8 | **Scope drift** — FNA reach work redefining the product | FNA is additive reach (Part 1); the mgfxc-replacement promise stays primary in all docs; README/the-purpose edits subordinate FNA to the existing promise |

---

## 5. Already-shipped wording that constrains/needs updating

- **README.md: no FNA mentions exist on this branch** (grep verified; PHASE-39:3 confirms VIC's README work isn't here) — nothing public to retract. When FNA ships (post-rung-4 only): README.md:7/11/15/48/52/68/141-142 need additive updates; note README.md:11 "the native pieces ride inside the package" becomes false-as-written for FNA until the packaging blocker is fixed.
- **XML docs hardcoding ".mgfx bytes"** must be generalized in the same PR that adds `Fna`: `IShaderCompiler.cs:6-9` ("compiles… into a MonoGame/KNI `.mgfx` effect"), `:24`, `:33-35`; `CompiledShader.cs:5-13` (summary + `Data` param — for Fna the bytes feed FNA's `Effect`); `CompilerOptions.cs:8-10` ("the MGFX container version"), `:47-50` (MgfxVersion ignored for Fna), `:52-58` (DxbcBackend ignored for Fna); `EffectCompiler.cs:13-22`; package descriptions `ShadowDusk.Compiler.csproj:10`, `ShadowDusk.Core.csproj:10`.
- **docs/the-purpose.md:49-56** backend table gains an FNA row + an explicit FNA-analog bar statement ("identical to `fxc /T fx_2_0` under MojoShader/real FNA"), written so the mgfxc promise is not diluted; **plan/plan.md:120-129** Key Decisions gains the FNA decision (vkd3d SM3 + managed fx_2_0 writer; Option B as upstream watch).
- **CLI usage/error text:** `ArgumentParser.cs:14-15`, `:216`.
- **Regression mechanism exists:** `tests/golden/DirectX_11` + `tests/golden/OpenGL` — add an explicit byte-unchanged CI assertion to the phase plan (and a conscious re-baseline step if the vkd3d pin is bumped).

## Bottom line
Proceed with Option A. Before merging implementation: add the vkd3d NuGet-packaging work (or honest scoping) to the phase, resolve the 1.17/1.18 pin with a goldens/Phase-18 re-validation plan, add CTAB verification and the SM4-source/NOT_IMPLEMENTED fail-loudly mappings as named steps, add a MojoShader harness for rung 3, and amend the phase doc with the explicit FNA evidence ladder + claimed-rungs honesty rule. With those in, Phase 39 strengthens THE PURPOSE rather than drifting from it.
