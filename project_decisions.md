# Project Decisions

Decisions made, with the why - consult before re-litigating anything. One per line: decided X (not Y) because Z.

## Pipeline and backends

- Chose the Option B pipeline (DXC -> SPIR-V -> SPIRV-Cross -> GLSL/MSL), not Option A (FXC/MojoShader), because Option A requires Wine on Linux and macOS and so cannot deliver the reach that justifies the project.
- Chose vkd3d-shader for the DX11 DXBC backend, not DXC, because DXC only emits SM6 DXIL and D3D11 rejects DXIL unconditionally; DXC's DXIL path is retained only for DX12 and KNI.
- Chose vkd3d as the shipping DXBC backend on every OS (default since 0.5.0), not `d3dcompiler_47`, because it makes output host-independent; `d3dcompiler_47` stays an opt-in Windows-only correctness oracle and never ships.
- Chose to keep vkd3d pinned at 1.17, not to track upstream, because output byte-stability is a product promise and a bump re-baselines every golden and re-runs rung 4. Upstream's stubbed fx_2_0 writer stays a background watch, not a dependency.
- Chose our own pinned macOS `libdxcompiler.dylib` loaded via Vortice's `Dxc.ResolveLibrary` event, not `SetDllImportResolver`, because Vortice's assembly already registers a resolver and a second registration throws.
- Chose the pure-managed `RdefReader` for DXBC reflection, not P/Invoking `D3DReflect`, because `D3DReflect` is Windows-only and blocked DX11 end-to-end on Linux and macOS; proven deeply equal to the D3DReflect oracle for both backends with zero `.mgfx` byte change.
- Chose the pure-managed `SpirvReflector` over the DXIL reflection oracle, because the DXIL path was Windows-only and blocked the WASM host; proven equivalent 10/10 with byte-identical `.mgfx` output.
- Chose to un-park Vulkan only when MonoGame 3.8.5 shipped a stable `DesktopVK`, not earlier, because a backend with no validatable consumer runtime cannot reach rung 4; Metal stays parked for exactly that reason, not because the compilation is hard.
- Chose to build DX12 as a genuine new backend, not treat it as a render-validation rung on existing work, because source inspection tripped Phase 52's own decision gate: no `PlatformTarget.DirectX12` existed.
- Chose `RELAX_NAN_CHECKS` on the OpenGL SPIRV-Cross profile to fix issue #149, not a managed rewrite stripping `isnan()`, because flipping the upstream option produced zero byte changes anywhere else in the corpus.
- Chose to own only the container writers, the runtime-dialect adapters, and real-runtime validation, not compiler internals, because HLSL compilation and cross-compilation are multi-year efforts maintained by the people who own the formats, and the validation is the actual moat. (operator, 2026-06-09)
- Chose to treat the browser as a full export station (any host may emit any target), not a WebGL-only compiler, because compile-target and render-backend are independent; the in-browser artifact must be byte-identical to the desktop one, which is the bar for every future host. (operator, 2026-06-09)

## Output format and compatibility

- Chose MGFX v10 as the default, not v11 or KNIFX, because v10 is the one format both MonoGame 3.8.2+ and KNI load, making it the most backwards-compatible choice.
- Chose to ship MGFX v11 and KNIFX as additive opt-ins (`CompilerOptions.MgfxVersion`, `CompilerOptions.Container`), not as a default or a required flag, because the consumer must never pick a version to get correct output. The old `--mgfx-version 11` was corrupt (v10 body with a v11 version byte) and was fixed rather than kept.
- Chose to emit raw `.mgfx` from core, not `.xnb`, because wrapping is the content-pipeline layer's job; a hand-rolled XNB writer would be the first divergence from `mgfxc` parity and would duplicate and risk desyncing the Phase 29 plugin's validated envelope. Re-open only as a deliberate, explicitly-scoped feature if a downstream (e.g. XnaFiddle project export) needs pre-wrapped `.xnb`. (2026-06-08)
- Chose to emit `mgfxc`'s `#define ps_oC0 gl_FragColor` form, not a raw `gl_FragColor` write, because KNI's HiDef runtime converter only rewrites that form, so one artifact serves Reach and HiDef with no consumer flag and is strictly more `mgfxc`-faithful.
- Chose to emit the correct full uniform-array layout, not `mgfxc`'s compacted MojoShader layout, because `mgfxc` + MonoGame GL is itself broken for statically-partially-read arrays.
- Chose `PlatformTarget.Fna` as a fully separate `RunFnaAsync` pipeline emitting a raw `.fxb`, not a variant of the `.mgfx` path, because a separate pipeline cannot change existing GL or DX output; the additive-and-cannot-regress property is the point.
- Chose vkd3d on every host for FNA output, never the `d3dcompiler` oracle, because that keeps the shipped bytes host-independent; `fxc /T fx_2_0` is a Windows test oracle only.
- Chose MGCB Tier 1 (a PATH-based drop-in binary named `mgfxc`), not a Tier 2 content-processor plugin first, because Tier 1 delivers the drop-in promise with no MonoGame integration; Tier 2 remains a future convenience, not a requirement.

## Scope and product shape

- Chose to treat `.fx` compilation as same-trust-domain, not untrusted input, because compiling a shader you chose to run is like compiling copied C++ or C#; the earlier path-traversal, size-limit, and macro-validation findings were removed as not-real-harm rather than answered with input-validation theater. (2026-06-12, closed by `SECURITY.md` 2026-06-15)
- Chose a synchronous `CompilationPipeline.Run` core with `CompileAsync` as a thin shell over it, not two pipelines, because sync and async output must be byte-identical by construction rather than by test; the only genuinely async work is the one-time WASM module load.
- Chose to publish `ShadowDusk.ShaderToy` as a standalone NuGet from 0.9.0, superseding the earlier "packaging deferred" decision, because it is pure-managed with zero native dependencies and purely additive to the pipeline.
- Chose to judge the ShaderToy frontend by pixel-fidelity against the original GLSL, not by mgfxc-equivalence, because `mgfxc` never compiles ShaderToy GLSL and so provides no oracle; calling it an mgfxc-equivalence proof would be dishonest.
- Chose build-time precompile to `.mgfx` as the documented default for shipping Android games, with on-device compile as additive reach, because build-time works today with zero Android-specific work.
- Chose to vendor Gum and Apos.Shapes shaders verbatim as fixtures, not to synthesize approximations, because the real third-party shaders surfaced real gaps our own corpus could not (the Gum FnaSample shader is what exposed Phase 41 GAP-1).

## Process and infrastructure

- Chose a single `<Version>` in `Directory.Build.props`, not per-csproj `PackageVersion` properties, because per-project versions desync and collide by name with Central Package Management items.
- Chose dispatch-only release triggering with a version-input guard, not tag-push triggering, because publishing must be a deliberate human action and a mismatched version should fail before anything is packed.
- Chose to fail the release red when a packed nupkg is missing a native, not to warn, because a stopped release beats shipping the FNA target and `DxbcBackend.Vkd3d` broken for a consumer RID.
- Chose a hard `SHADOWDUSK_REQUIRE_GL` gate over letting headless hosts soft-skip GL tests, because a skip reported as a pass is indistinguishable from real coverage and had already masked three latent failures.
- Chose to gate the PR integration lane behind a `run-integration` label, not run it on every PR, because it is heavyweight and antivirus-scan sensitive.
- Chose to reuse the normal build's CLI binary in `CliBinaryFixture`, not a per-construction `dotnet publish -c Release`, because the cold Release build plus antivirus scan made the suite take 21m43s.
- Chose one collector phase (51) for leftover tails, not leaving parent phases open at 95%, because a phase open for one or two items obscures what is actually done.
- Chose to keep the Phase-41 structural-divergence appendix outside `plan/DONE/`, not archive it with its phase, because `dotnet test` regenerates it on every run, making it a live artifact rather than a record.
- Chose to make the local Windows render gate a hard pre-release requirement, not advisory, because CI structurally cannot produce that evidence and `release.yml` does not check it either.
