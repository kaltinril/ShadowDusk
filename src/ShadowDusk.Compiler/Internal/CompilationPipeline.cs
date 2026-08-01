#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using ShadowDusk.HLSL.Vkd3d;

namespace ShadowDusk.Compiler.Internal;

internal sealed class CompilationPipeline
{
    private readonly Func<IDxcShaderCompiler> _dxcCompilerFactory;
    private readonly Func<ISpirvToGlslTranspiler> _glslTranspilerFactory;
    private readonly Func<IShaderReflector>? _reflectorFactory;
    private readonly Func<IDxbcShaderCompiler>? _dxbcCompilerFactory;

    /// <summary>
    /// A sampler uniform declaration in the rewritten MonoGame-GL source, as
    /// <c>MonoGameGlslRewriter</c> emits it (<c>uniform sampler2D ps_s0;</c>). Used to
    /// cross-check the emitted GLSL against the <c>.mgfx</c> sampler table (SD0217).
    /// </summary>
    private static readonly Regex GlslSamplerDeclaration = new(
        @"^\s*uniform\s+sampler(?:1D|2D|3D|Cube)\s+\w+\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public CompilationPipeline(
        Func<IDxcShaderCompiler>? dxcCompilerFactory = null,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory = null,
        Func<IShaderReflector>? reflectorFactory = null,
        Func<IDxbcShaderCompiler>? dxbcCompilerFactory = null)
    {
        _dxcCompilerFactory    = dxcCompilerFactory    ?? (() => new DxcShaderCompiler());
        _glslTranspilerFactory = glslTranspilerFactory ?? (() => new SpirvCrossGlslTranspiler());
        // When non-null AND target == OpenGL, reflection is sourced from SPIR-V (the
        // browser/WASM path) instead of the native DXIL ID3D12ShaderReflection oracle.
        // Null (desktop default) keeps the DXIL path byte-for-byte unchanged.
        _reflectorFactory      = reflectorFactory;
        // When non-null, the DirectX AND FNA targets compile their D3D bytecode through
        // this factory instead of the desktop defaults below (the browser/WASM host
        // injects WasmVkd3dShaderCompiler — same pinned vkd3d, different call mechanism).
        // Null (desktop default) keeps both targets byte-for-byte unchanged.
        _dxbcCompilerFactory   = dxbcCompilerFactory;
    }

    // The SYNCHRONOUS pipeline core (issue #28). Every backend stage is synchronous
    // work on every host (desktop natives are direct in-process calls; the WASM
    // [JSImport] compiles are synchronous once their modules are loaded), so the whole
    // pipeline runs on the calling thread with no task to block on — which is what
    // makes IShaderCompiler.Compile safe from a synchronous call site on single-
    // threaded browser WASM. The async surface (EffectCompiler.CompileAsync) is a thin
    // shell over THIS method — one implementation, so sync and async output is
    // byte-identical by construction. Never add an await-able stage here; hoist any
    // genuinely-async work (module loads) into IShaderCompiler.InitializeAsync instead.
    public Result<CompiledShader, ShaderError[]> Run(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A CapabilityProfile fully specifies the output target, including the graphics backend, so
        // a set Profile's GraphicsTarget wins over Target (this is what lets the runtime-detection
        // advisory return ONE profile that picks both format and backend). Normalize up front so the
        // resolved backend flows through every downstream options.Target read. With no profile this
        // is a no-op, so Profile == null stays byte-identical.
        if (options.Profile is { } selectedProfile && selectedProfile.GraphicsTarget != options.Target)
            options = options.WithGraphicsTarget(selectedProfile.GraphicsTarget);

        if (options.Target == PlatformTarget.Metal)
        {
            return Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "SD0200",
                Message: "Metal target not yet supported"));
        }

        // FNA takes a fully separate path: D3D9-style source preserved verbatim, vkd3d
        // SM1–3 compiles, CTAB reflection, and the fx_2_0 container — nothing below
        // (DXC/SPIRV-Cross/MGFX) participates, which also guarantees the existing
        // MonoGame/KNI targets' output cannot change.
        if (options.Target == PlatformTarget.Fna)
            return RunFna(hlslSource, options, cancellationToken);

        string sourceFileName = options.SourceFileName ?? "<source>";

        // Stage 1: FX9 pre-parser.
        var parseResult = FxPreParser.Parse(hlslSource, sourceFileName);
        if (parseResult.IsFailure)
            return Fail(FromFxParseError(parseResult.Error));

        FxParseResult fxParsed = parseResult.Value;

        // Stage 2: Preprocessor — inject platform macros and flatten #includes.
        // Pre-check (no exception-as-control-flow): an unsupported target is reported
        // as a Result error, never caught from PlatformMacros.For.
        if (!PlatformMacros.IsSupported(options.Target))
        {
            return Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "X0010",
                Message: $"platform '{options.Target}' is not supported by ShadowDusk"));
        }

        // The output container (honoring a CapabilityProfile when set) is needed up front so the
        // macro set can define __KNIFX__ for a KNIFX-targeted compile (KNI's compiler always
        // does — see PlatformMacros.For(target, container)). Same value reused at Seam 5 below.
        EffectContainer effectiveContainer = options.Profile?.Container ?? options.Container;
        MacroSet macros = PlatformMacros.For(options.Target, effectiveContainer);
        if (options.Defines.Count > 0)
        {
            // mgfxc /Defines: parity (bug-hunt 2026-07-27 M9). User macros ride with the
            // platform macros through BOTH renderers (the #define prepend and the DXC -D
            // flags), so every backend sees them; previously they were dropped silently.
            macros = macros with { UserDefines = options.Defines };
        }

        IIncludeResolver includeResolver = options.IncludeResolver ?? new FileSystemIncludeResolver();
        var preprocessor = new Preprocessor();

        var preprocessResult = preprocessor.Flatten(
            fxParsed.StrippedHlsl,
            sourceFileName,
            macros,
            includeResolver,
            options.AdditionalIncludePaths);

        if (preprocessResult.IsFailure)
            return Fail(preprocessResult.Error);

        PreprocessedSource preprocessed = preprocessResult.Value;
        IReadOnlyList<ShaderError> preprocessWarnings = preprocessed.Warnings;

        // LAZY DXC instance, hoisted above the zero-technique fallback so the fallback's
        // preprocess pass and the GL reflection compile share one instance/disposal
        // (Phase 18 Track A: a DX11 compile must still never construct DXC). Materialized
        // only on first use — the macro-technique fallback below, or the GL/Vulkan compile.
        var dxcCompiler = new Lazy<IDxcShaderCompiler>(_dxcCompilerFactory);
        try
        {

        // Zero-technique fallback (Phase 41). The raw pre-parse (Stage 1) ran BEFORE macro
        // expansion and deliberately ignores macro-call technique forms, so the MonoGame
        // stock effects (BasicEffect.fx etc.) whose techniques come ONLY from the
        // TECHNIQUE(name, vs, ps) macro in Macros.fxh yield zero techniques here — today an
        // immediate SD0010. Recover by macro-expanding the (already #include-flattened)
        // source through DXC's preprocessor with the target's PlatformMacros, then re-parse
        // the EXPANDED text: the TECHNIQUE(...) calls are now literal `technique { ... }`
        // blocks the pre-parser reads. The default (techniques already found) path is
        // untouched. NOTE: GL/DX only — the FNA path (RunFna) does not apply this fallback;
        // tracked follow-up.
        //
        // GATE — modern macro branch only. The recovery runs ONLY when the target's macro
        // set selects the modern (SM4/SM6) branch of Macros.fxh (DirectX, Vulkan). The
        // OpenGL macro set is deliberately {MGFX, GLSL, OPENGL} with NO SM4/SM6 (it must
        // stay that way — changing it would regress every #if OPENGL / #if SM4 fixture), so
        // the stock effects expand to their LEGACY DX9/SM2 branch (sampler2D / tex2D /
        // vs_2_0). Feeding that legacy form to ShadowDusk's modern DXC -> SPIR-V GL backend
        // crashes DXC's native SPIR-V codegen (an uncatchable access violation), which is
        // strictly worse than the loud SD0010 the user already gets. So for a target whose
        // macros lack a modern shader model we DECLINE the recovery and keep the honest
        // SD0010 below. This is a documented GL macro-model gap (Phase 41 follow-up), NOT a
        // PlatformMacros change and NOT a special-case in the GL shader path. DirectX is the
        // primary, proven win for the stock TECHNIQUE() effects.
        bool macrosSelectModernBranch = macros.Macros.Any(m =>
            m.Name is "SM4" or "SM6");

        if (fxParsed.Techniques.Count == 0 && macrosSelectModernBranch)
        {
            var preprocessRequest = new DxcPreprocessRequest
            {
                HlslSource     = preprocessed.Text,
                SourceFileName = sourceFileName,
                Macros         = macros.Macros
                    .Select(m => (m.Name, (string?)m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList(),
            };

            Result<string, ShaderError> expandResult =
                dxcCompiler.Value.Preprocess(preprocessRequest, cancellationToken);
            if (expandResult.IsFailure)
                return Fail(expandResult.Error);

            var reparseResult = FxPreParser.Parse(expandResult.Value, sourceFileName);
            if (reparseResult.IsFailure)
                return Fail(FromFxParseError(reparseResult.Error));

            FxParseResult expandedParsed = reparseResult.Value;

            if (expandedParsed.Techniques.Count == 0)
                return Fail(new ShaderError(
                    File: sourceFileName,
                    Line: 0,
                    Column: 0,
                    Code: "SD0010",
                    Message: "Effect source contains no techniques"));

            // Adopt the re-parsed (expanded, technique-stripped) result. Its StrippedHlsl
            // already has #includes inlined and the macros consumed, so build the downstream
            // PreprocessedSource directly from it WITHOUT a second Flatten (re-flattening
            // would double-prepend the platform macros and re-trigger #include handling on
            // already-inlined text).
            fxParsed = expandedParsed;
            preprocessed = new PreprocessedSource(
                expandedParsed.StrippedHlsl,
                macros.ToDxcFlags(),
                sourceFileName);
        }

        // No techniques after the (possibly skipped) recovery — a genuinely technique-free
        // effect, or a macro-only-technique effect on a target whose macros select the
        // legacy branch (gated out above). Loud SD0010, identical to the prior behavior.
        if (fxParsed.Techniques.Count == 0)
            return Fail(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD0010",
                Message: "Effect source contains no techniques"));

        // MonoGame-compatible GLSL emission (MojoShader dialect) applies to EVERY GL
        // stage — pixel (ps_uniforms_vec4, gl_FragColor, ps_s{k}) AND vertex
        // (vs_uniforms_vec4, attribute/varying I/O, gl_Position) — so a VS-driven
        // effect links in MonoGame's GL runtime (Phase 28). The rewrite is keyed PER
        // STAGE inside MonoGameGlslRewriter; the pipeline just drives it on the OpenGL
        // target. (Phase 17 originally gated this to PS-only passes; the VS rewrite now
        // makes the gate stage-symmetric.) Non-GL targets keep the unmodified
        // SPIRV-Cross dialect.
        //
        // The dialect is the capability axis (Phase 35 auto-select seam 3): an explicit
        // CapabilityProfile may refine it, but with no profile (the default) it is derived
        // from the target exactly as before — LegacyMojoShader on OpenGL, NotApplicable
        // elsewhere — so Profile == null is byte-identical to the pre-seam behavior. The
        // Target == OpenGL guard is retained so a mismatched profile can never force the GL
        // rewrite onto a DirectX/FNA compile.
        ShaderDialect glDialect = options.Profile?.Dialect
            ?? (options.Target == PlatformTarget.OpenGL
                ? ShaderDialect.LegacyMojoShader
                : ShaderDialect.NotApplicable);
        bool monoGameGl = options.Target == PlatformTarget.OpenGL
            && glDialect == ShaderDialect.LegacyMojoShader;

        // Seam 5: the container/version axis. A CapabilityProfile (when set) names a full
        // (runtime, format) contract, so it selects the effect container and MGFX version too
        // (e.g. KniGL_4_02 -> KNIFX, MonoGameGL_3_8_5 -> MGFX v11). With no profile the existing
        // Container / MgfxVersion options apply unchanged, so Profile == null is byte-identical.
        // (effectiveContainer is computed up front, near the macro set, so __KNIFX__ can be
        // injected for a KNIFX compile.)
        int effectiveMgfxVersion = options.Profile?.MgfxVersion ?? options.MgfxVersion;

        // Seam 4: the feature axis. A profile may declare AllowedFeatures, but a feature is honored
        // only once a shipping runtime is render-proven to consume it; ShaderFeatureSupport rejects
        // any unsupported feature loudly (SD0201) so ShadowDusk never emits bytes no runtime can
        // load. Today no runtime consumes these, so every proven profile declares None and this
        // never fires (Profile == null is byte-identical).
        ShaderFeatures effectiveFeatures = options.Profile?.AllowedFeatures ?? ShaderFeatures.None;
        if (ShaderFeatureSupport.Validate(effectiveFeatures) is { } featureError)
            return Fail(featureError);

        // When a reflector factory is injected and the target is OpenGL, reflection is
        // sourced from the SPIR-V blob (pure-managed, WASM-safe) instead of compiling a
        // separate DXIL blob and reflecting it via the native ID3D12ShaderReflection
        // oracle. The SPIR-V compile + transpile remain identical, so .mgfx output is
        // byte-transparent — only the SOURCE of the ReflectedEffect changes.
        // Vulkan has no DXIL-oracle alternative at all (CompileEntryPoint's generic
        // else branch never compiles a companion DXIL blob for Vulkan), so it must
        // reflect from SPIR-V unconditionally, unlike OpenGL which only does so when a
        // reflector factory is injected.
        bool reflectFromSpirv = options.Target == PlatformTarget.Vulkan
            || (_reflectorFactory is not null && options.Target == PlatformTarget.OpenGL);

        // GAP-2 (GL-only): retarget a pixel shader's MRT struct-output ': COLOR<n>' semantics to
        // ': SV_Target<n>' for the OpenGL DXC compiles only. DXC's HLSL->SPIR-V (the GL backend)
        // rejects COLOR as a PS output (vkd3d/DX accepts it), and the pre-parse is shared by both
        // backends, so this rewrite runs HERE on a GL-private copy of the source and is fed ONLY to
        // the OpenGL CompileEntryPoint calls below; the DX/Vulkan/FNA paths keep the untouched
        // `preprocessed`, so their bytes are unchanged. The rewrite is a no-op (returns the same
        // text) for any shader whose pixel entry does not return a COLOR-member struct, so every
        // existing GL shader stays byte-identical. See GlStructOutputColorRewriter.
        //
        // Vulkan-only: force each texture+sampler pair onto matching explicit registers so
        // DXC's -fvk-t-shift/-fvk-s-shift co-locate them at the same raw SPIR-V binding — the
        // only pattern confirmed to draw correctly on real DesktopVK (see
        // VulkanTextureSamplerBindingRewriter). No-op for GL/DX/FNA and for shaders with no
        // Texture2D/SamplerState declarations.
        PreprocessedSource glCompileSource = monoGameGl
            ? preprocessed with { Text = GlStructOutputColorRewriter.Rewrite(preprocessed.Text, fxParsed.Techniques) }
            : options.Target == PlatformTarget.Vulkan
                ? preprocessed with { Text = VulkanTextureSamplerBindingRewriter.Rewrite(preprocessed.Text) }
                : preprocessed;

        // Stages 3–5: Compile each pass's entry points, reflect, and transpile.
        // The preprocessor has already flattened all #includes so no include handler is needed for DXC.
        // The dxcCompiler Lazy is hoisted above the zero-technique fallback (see Stage 2);
        // LAZY on purpose (Phase 18 Track A): the DX11 path never touches DXC (vkd3d /
        // d3dcompiler_47 emit the DXBC; reflection is the managed RdefReader), so a DX11
        // compile must not die constructing DXC on a host without the native (the
        // Phase 37 A macOS gap). GL/Vulkan materialize it on first use, as before.
        ISpirvToGlslTranspiler glslTranspiler = _glslTranspilerFactory();

        // DirectX (DX11) takes a separate backend: DXC only emits SM6 DXIL, which
        // MonoGame's DX11 runtime rejects. The DXBC comes from d3dcompiler_47 (the fxc
        // engine, Windows-only) or the cross-platform vkd3d-shader backend, and is
        // reflected by the pure-managed RdefReader (Phase 18 Track A) — so the DX11
        // pipeline end-to-end runs on any OS when the vkd3d backend is selected.
        bool directX = options.Target == PlatformTarget.DirectX;
        // Backend selection (default = the cross-platform vkd3d-shader backend, the
        // shipping backend on every OS — host-independent, so default-DX output is
        // cross-host byte-identical). The Windows-only d3dcompiler_47 correctness
        // oracle is opt-in via CompilerOptions.DxbcBackend. Both implement
        // IDxbcShaderCompiler and both feed the SAME DxbcReflectionExtractor.
        // An injected host backend (the WASM vkd3d backend) takes precedence over both —
        // a host-appropriate default, not a consumer choice (CompilerOptions.DxbcBackend
        // selects between desktop natives that do not exist in the browser).
        IDxbcShaderCompiler dxbcCompiler = _dxbcCompilerFactory?.Invoke() ?? options.DxbcBackend switch
        {
            DxbcBackend.D3DCompiler => new D3DCompilerShaderCompiler(),
            _                       => new Vkd3dShaderCompiler(),
        };
        var dxbcReflectionPipe  = new DxbcReflectionPipeline(new DxbcReflectionExtractor());

        var extractor          = new DxilReflectionExtractor();
        var reflectionPipeline = new ReflectionPipeline(extractor);
        var renderStateParser  = new RenderStateParser();

        var compiledShaderBlobs = new List<CompiledShaderBlob>();
        var techniques          = new List<MgfxTechniqueInfo>();
        var allParameters       = new List<ParameterReflection>();
        var allConstantBuffers  = new List<ConstantBufferReflection>();
        var seenParamNames      = new HashSet<string>(StringComparer.Ordinal);
        var seenCbufferNames    = new HashSet<string>(StringComparer.Ordinal);

        // Per-shader (by blob index) resource bindings captured during reflection,
        // used to emit MonoGame's shader record (sampler table + cbuffer-index list).
        var shaderTextures      = new Dictionary<int, IReadOnlyList<TextureReflection>>();
        var shaderSamplers      = new Dictionary<int, IReadOnlyList<SamplerReflection>>();
        var shaderCbufferNames  = new Dictionary<int, IReadOnlyList<string>>();

        // Per-shader (by blob index) SPIR-V, kept for the OpenGL sampler table: the GL records
        // must mirror the COMBINED samplers SPIRV-Cross declares (one per texture+sampler pair),
        // and the pair list plus its order is derivable only from the SPIR-V — see
        // SpirvCombinedSamplerPairs. Captured on every target that produces SPIR-V; only the GL
        // branch reads it.
        var shaderSpirv         = new Dictionary<int, ReadOnlyMemory<byte>>();

        // Per-shader (by blob index) GL uniform register layout returned by the
        // MonoGameGlslRewriter — the allocation the emitted GLSL actually indexes.
        // The GL .mgfx cbuffer records are built from THIS (one record per shader,
        // mgfxc's model — Phase 43 F4/F5), never from cross-stage name dedup.
        var shaderUniformLayouts = new Dictionary<int, IReadOnlyList<MonoGameGlslUniform>>();

        // AllowWarnings = true (Phase 53): mgfxc's fxc front end never passes /WX, so
        // forcing -WX here made the GL/Vulkan leg STRICTER than the reference
        // compiler — warning-grade HLSL (e.g. an implicit truncation) compiled for
        // DirectX but hard-failed for OpenGL, a confirmed "DX works, GL doesn't"
        // divergence class from the field reports. Warnings are captured verbatim
        // instead (PlatformBlob.Warnings) and surfaced via CompiledShader.Warnings —
        // visible by default, never fatal, never discarded.
        var compileOptions = new DxcCompileOptions
        {
            EmbedDebugInfo = options.Debug,
            AllowWarnings  = true,
        };

        // Recognized-profile validation (SD0013, Phase 48): the compile target token in
        // each pass must resolve — after macro expansion when it is a macro name — to a
        // profile fxc/mgfxc accepts. The pre-parser runs before macro expansion and is
        // deliberately lenient, so a typo ('compile A …') or an undefined '*_SHADERMODEL'
        // macro silently fell back to SM3 (a divergence from mgfxc, which hard-errors).
        // Cheap path: every literal known profile is accepted with no work; a profile-
        // SHAPED-but-bogus token ('ps_9_9') is rejected here. Only a non-literal token (a
        // macro name) pays for a DXC -P expansion, cached per token. Per the byte-identity
        // invariant this ONLY adds rejections — every currently-compiling shader (literal
        // profile or a macro defined to a real profile) still resolves to a known profile.
        //
        // Target profile-FLOOR validation (SD0015, Phase 51 A10) rides on the same walk.
        // A profile can be perfectly recognized (so SD0013 passes) and still be one the
        // requested target's reference compiler refuses: mgfxc's DirectX_11 profile rejects
        // every SM1–3 target with "must be SM 4.0 level 9.1 or higher!". ShadowDusk accepted
        // them, so a legacy SM3 effect compiled here and then failed the consumer's real
        // Content Pipeline build. DirectX only — see DirectX11FloorCheck's remarks for why
        // the OpenGL/Vulkan/DX12 equivalents are deliberately not enforced here.
        bool enforceDx11Floor = directX;
        var profileExpansionCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (TechniqueInfo technique in fxParsed.Techniques)
        {
            foreach (PassInfo pass in technique.Passes)
            {
                if (pass.VertexEntryPoint is not null)
                {
                    var v = ValidateCompileProfile(
                        pass.VertexProfile, pass.VertexProfileToken, pass.VertexProfileSpan, ShaderStage.Vertex,
                        dxcCompiler, macros, preprocessed, sourceFileName,
                        profileExpansionCache, enforceStagePrefix: true, cancellationToken,
                        enforceDirectX11Floor: enforceDx11Floor, entryPoint: pass.VertexEntryPoint);
                    if (v is { } vErr)
                        return Fail(vErr);
                }
                if (pass.PixelEntryPoint is not null)
                {
                    var p = ValidateCompileProfile(
                        pass.PixelProfile, pass.PixelProfileToken, pass.PixelProfileSpan, ShaderStage.Pixel,
                        dxcCompiler, macros, preprocessed, sourceFileName,
                        profileExpansionCache, enforceStagePrefix: true, cancellationToken,
                        enforceDirectX11Floor: enforceDx11Floor, entryPoint: pass.PixelEntryPoint);
                    if (p is { } pErr)
                        return Fail(pErr);
                }
            }
        }

        // Non-fatal diagnostics for the whole effect: the preprocessor's own findings
        // (SD0008 case-only #include mismatches), the underlying compilers' verbatim
        // warnings (deduped — VS and PS compile the same preprocessed source, so a
        // source-level warning re-surfaces once per entry point) plus the GL portability
        // lint findings (SD0400–SD0499). Returned on CompiledShader.Warnings; never gates
        // output.
        //
        // preprocessWarnings, not preprocessed.Warnings: the zero-technique recovery above
        // may have replaced `preprocessed` with a source rebuilt from the re-parse, which
        // carries no warnings of its own.
        var runWarnings  = new List<ShaderError>(preprocessWarnings);
        var seenWarnings = new HashSet<(string File, int Line, int Column, string Code, string Message)>();

        foreach (TechniqueInfo technique in fxParsed.Techniques)
        {
            var mgfxPasses = new List<MgfxPassInfo>();

            foreach (PassInfo pass in technique.Passes)
            {
                int vsIndex = -1;
                int psIndex = -1;

                ReadOnlyMemory<byte> vsDxilBlob  = default;
                ReadOnlyMemory<byte> vsSpirvBlob = default;
                ReadOnlyMemory<byte> psDxilBlob  = default;
                ReadOnlyMemory<byte> psSpirvBlob = default;

                if (pass.VertexEntryPoint is not null)
                {
                    var compileOutput = CompileEntryPoint(
                        dxcCompiler,
                        dxbcCompiler,
                        glslTranspiler,
                        glCompileSource,
                        pass.VertexEntryPoint,
                        ShaderStage.Vertex,
                        options.Target,
                        compileOptions,
                        // VS-bearing GL passes are now MonoGame-rewritten too (Phase 28):
                        // the rewrite is stage-symmetric, so the VS gets the vs_uniforms_vec4
                        // + attribute/varying contract that lets MonoGame's GL runtime link it.
                        applyMonoGameGlsl: monoGameGl,
                        reflectFromSpirv: reflectFromSpirv,
                        cancellationToken);

                    if (compileOutput.Blob.IsFailure)
                        return Fail(compileOutput.Blob.Error, runWarnings);

                    AccumulateWarnings(runWarnings, seenWarnings, compileOutput.Warnings);
                    if (monoGameGl)
                    {
                        // GL portability lint over the emitted vertex GLSL (loop
                        // shapes; the SpriteBatch/derivative checks are pixel-stage).
                        AccumulateWarnings(runWarnings, seenWarnings, GlslPortabilityAnalyzer.Analyze(
                            Encoding.UTF8.GetString(compileOutput.Blob.Value),
                            ShaderStage.Vertex,
                            passHasVertexShader: true,
                            options.SourceFileName ?? "<source>",
                            pass.VertexEntryPoint));
                    }

                    vsIndex     = compiledShaderBlobs.Count;
                    vsDxilBlob  = compileOutput.DxilBlob;
                    vsSpirvBlob = compileOutput.SpirvBlob;
                    shaderUniformLayouts[vsIndex] = compileOutput.Uniforms;
                    compiledShaderBlobs.Add(new CompiledShaderBlob(compileOutput.Blob.Value, ShaderStage.Vertex)
                    {
                        // The GL attribute table maps each vs_v{k} → VertexElementUsage+index
                        // so MonoGame binds the right vertex element. Empty for DX / non-GL.
                        Attributes = compileOutput.Attributes,
                        ShaderModel = ParseShaderModel(pass.VertexProfile),
                        // Diagnostic strings written only by MGFX v11+ (ignored by v10/KNIFX).
                        SourceFile = options.SourceFileName ?? "<unknown>",
                        Entrypoint = pass.VertexEntryPoint ?? "<unknown>",
                    });
                }

                if (pass.PixelEntryPoint is not null)
                {
                    var compileOutput = CompileEntryPoint(
                        dxcCompiler,
                        dxbcCompiler,
                        glslTranspiler,
                        glCompileSource,
                        pass.PixelEntryPoint,
                        ShaderStage.Pixel,
                        options.Target,
                        compileOptions,
                        applyMonoGameGlsl: monoGameGl,
                        reflectFromSpirv: reflectFromSpirv,
                        cancellationToken);

                    if (compileOutput.Blob.IsFailure)
                        return Fail(compileOutput.Blob.Error, runWarnings);

                    AccumulateWarnings(runWarnings, seenWarnings, compileOutput.Warnings);
                    if (monoGameGl)
                    {
                        // GL portability lint over the emitted pixel GLSL: gradient
                        // ops in divergent loops (SD0400), SpriteBatch-incompatible
                        // interpolants on a PS-only pass (SD0401), and non-Appendix-A
                        // loop shapes (SD0402) — the classes that otherwise surface
                        // only as the engine's generic draw-time exception.
                        AccumulateWarnings(runWarnings, seenWarnings, GlslPortabilityAnalyzer.Analyze(
                            Encoding.UTF8.GetString(compileOutput.Blob.Value),
                            ShaderStage.Pixel,
                            passHasVertexShader: pass.VertexEntryPoint is not null,
                            options.SourceFileName ?? "<source>",
                            pass.PixelEntryPoint));
                    }

                    psIndex     = compiledShaderBlobs.Count;
                    psDxilBlob  = compileOutput.DxilBlob;
                    psSpirvBlob = compileOutput.SpirvBlob;
                    shaderUniformLayouts[psIndex] = compileOutput.Uniforms;
                    compiledShaderBlobs.Add(new CompiledShaderBlob(compileOutput.Blob.Value, ShaderStage.Pixel)
                    {
                        ShaderModel = ParseShaderModel(pass.PixelProfile),
                        // Diagnostic strings written only by MGFX v11+ (ignored by v10/KNIFX).
                        SourceFile = options.SourceFileName ?? "<unknown>",
                        Entrypoint = pass.PixelEntryPoint ?? "<unknown>",
                    });
                }

                // Stage 4: Reflect each shader stage independently so parameters that are
                // only bound in PS (or only in VS) are not missed. seenParamNames/seenCbufferNames
                // deduplicate across stages and across passes.
                foreach (var (blobIndex, dxilBlob, spirvBlob) in new[]
                {
                    (vsIndex, vsDxilBlob, vsSpirvBlob),
                    (psIndex, psDxilBlob, psSpirvBlob),
                })
                {
                    // When reflecting from SPIR-V (WASM path) there is no DXIL blob —
                    // gate on the SPIR-V blob instead so empty (skipped) stages are
                    // dropped the same way.
                    if (reflectFromSpirv ? spirvBlob.IsEmpty : dxilBlob.IsEmpty)
                        continue;

                    Result<ReflectedEffect, ShaderError> reflectResult;
                    if (reflectFromSpirv)
                    {
                        // Pure-managed SPIR-V reflection: derive the base effect
                        // (cbuffers/textures/samplers) from the SPIR-V blob, then run the
                        // SAME ParameterListBuilder step the DXIL path uses so Parameters
                        // are populated identically. Output is byte-transparent.
                        // Vulkan reaches this branch unconditionally (see reflectFromSpirv
                        // above) with no injected factory on desktop, so fall back to a
                        // plain SpirvReflector rather than relying on caller injection.
                        Result<ReflectedEffect, ShaderError> baseResult =
                            (_reflectorFactory?.Invoke() ?? new SpirvReflector()).Reflect(spirvBlob);

                        if (baseResult.IsSuccess)
                        {
                            ReflectedEffect baseEffect = baseResult.Value;
                            IReadOnlyList<ParameterReflection> parameters =
                                ParameterListBuilder.Build(baseEffect, fxParsed.ParameterAnnotations);
                            reflectResult = Result<ReflectedEffect, ShaderError>.Ok(
                                baseEffect with { Parameters = parameters });
                        }
                        else
                        {
                            reflectResult = Result<ReflectedEffect, ShaderError>.Fail(baseResult.Error);
                        }
                    }
                    else if (directX)
                    {
                        // DirectX: dxilBlob actually carries SM5 DXBC — reflect via the
                        // managed RdefReader (DXC's DXIL reflection can't read DXBC).
                        reflectResult = dxbcReflectionPipe.Reflect(
                            dxilBlob,
                            fxParsed.ParameterAnnotations,
                            cancellationToken);
                    }
                    else
                    {
                        var reflectionInput = new ReflectionInput
                        {
                            DxilBlob      = dxilBlob,
                            FxAnnotations = fxParsed.ParameterAnnotations,
                        };

                        // DirectX12 folds a sampler+texture pair into the single texture
                        // parameter, matching DirectX11's DXBC path and the real mgfxc
                        // DirectX_12 golden (confirmed by decoding it directly, Phase 54
                        // follow-up) — real mgfxc's DX12 golden has no standalone sampler
                        // parameter. OpenGL/Vulkan keep the existing (Phase 17/32 proven)
                        // behavior of emitting one.
                        bool includeSamplerParameters = options.Target != PlatformTarget.DirectX12;

                        reflectResult = reflectionPipeline.Reflect(
                            reflectionInput, cancellationToken, includeSamplerParameters);
                    }

                    if (reflectResult.IsFailure)
                        return Fail(reflectResult.Error, runWarnings);

                    ReflectedEffect reflected = reflectResult.Value;

                    foreach (ConstantBufferReflection cb in reflected.ConstantBuffers)
                    {
                        if (seenCbufferNames.Add(cb.Name))
                            allConstantBuffers.Add(cb);
                    }

                    foreach (ParameterReflection param in reflected.Parameters)
                    {
                        if (seenParamNames.Add(param.Name))
                            allParameters.Add(param);
                    }

                    // Capture this shader's resource bindings for its .mgfx record.
                    shaderTextures[blobIndex]     = reflected.Textures;
                    shaderSamplers[blobIndex]     = reflected.Samplers;
                    shaderCbufferNames[blobIndex] = reflected.ConstantBuffers.Select(c => c.Name).ToList();
                    shaderSpirv[blobIndex]        = spirvBlob;
                }

                // Last assignment wins on a duplicated state key — fxc's semantics — instead
                // of ToDictionary's ArgumentException (no exception-as-control-flow).
                var renderStateKvp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (RenderStateEntry rs in pass.RenderStates)
                    renderStateKvp[rs.Key] = rs.Value;
                var renderStateResult = renderStateParser.Parse(renderStateKvp);
                if (renderStateResult.IsFailure)
                    return Fail(renderStateResult.Error, runWarnings);

                var passAnnotations = MapAnnotationEntries(pass.Annotations);

                mgfxPasses.Add(new MgfxPassInfo(
                    Name: pass.Name,
                    Annotations: passAnnotations,
                    VertexShaderIndex: vsIndex,
                    PixelShaderIndex: psIndex,
                    RenderState: renderStateResult.Value));
            }

            var techAnnotations = MapAnnotationEntries(technique.Annotations);

            techniques.Add(new MgfxTechniqueInfo(
                Name: technique.Name,
                Annotations: techAnnotations,
                Passes: mgfxPasses));
        }

        // GL (Phase 43 F4/F5): one cbuffer record PER SHADER, built from the uniform
        // register layout the GLSL rewriter returned for that shader, deduplicated
        // across shaders mgfxc-style (ConstantBufferData.SameAs). A cbuffer bound by
        // BOTH stages therefore yields a vs_uniforms_vec4 record AND a
        // ps_uniforms_vec4 record (the SkinnedEffect mgfxc golden carries several
        // records with the SAME name — MonoGame binds each shader to its records by
        // index, not by name). Multiple HLSL cbuffers in one stage are already merged
        // into that shader's single register space by the rewriter (MojoShader's
        // model: D3D9 has one float-constant file per stage).
        IReadOnlyList<ConstantBufferInfo> constantBufferInfoList;
        Dictionary<int, int>? glShaderCbRecord = null;
        if (monoGameGl)
        {
            var records = new List<ConstantBufferInfo>();
            glShaderCbRecord = new Dictionary<int, int>();
            for (int i = 0; i < compiledShaderBlobs.Count; i++)
            {
                if (!shaderUniformLayouts.TryGetValue(i, out var layout) || layout.Count == 0)
                    continue;

                string cbName = compiledShaderBlobs[i].Stage == ShaderStage.Vertex
                    ? "vs_uniforms_vec4"
                    : "ps_uniforms_vec4";

                var paramIndices = new List<int>(layout.Count);
                var paramOffsets = new List<ushort>(layout.Count);
                int sizeRegisters = 0;
                foreach (MonoGameGlslUniform u in layout)
                {
                    sizeRegisters = Math.Max(sizeRegisters, u.BaseRegister + u.RegisterCount);
                    // Primary: join by NAME. Every shader that compiles today resolves
                    // here, so it never reaches the fallback below and its bytes are
                    // unchanged (the byte-identity corpus stays green by construction).
                    int paramIndex = IndexOfParam(allParameters, u.Name);
                    if (paramIndex < 0)
                    {
                        // Fallback (B10): SPIRV-Cross renames a free uniform whose name
                        // collides with a GLSL reserved word (e.g. `noise` -> `_noise`,
                        // legal+required GLSL), so the GLSL layout's name no longer
                        // matches the reflected parameter (still `noise`) and the name
                        // join misses. Recover the parameter through an OFFSET BRIDGE:
                        // the GL uniform's BaseRegister * 16 is its byte offset, which
                        // the reflected cbuffer variable carries as StartOffset — so the
                        // variable's ORIGINAL name recovers the parameter without ever
                        // trusting the SPIRV-Cross-emitted spelling. The parameter stays
                        // exposed under its original name in the .mgfx; only the INDEX is
                        // needed here. See docs/glsl-uniform-naming.md "Design notes".
                        paramIndex = IndexOfParamByRegister(allConstantBuffers, allParameters, u.BaseRegister);
                    }
                    if (paramIndex < 0)
                        return Fail(new ShaderError(
                            File: sourceFileName,
                            Line: 0,
                            Column: 0,
                            Code: "SD0012",
                            Message: $"internal: GL uniform '{u.Name}' (shader #{i}) has no " +
                                     "matching effect parameter — the GLSL uniform layout and " +
                                     "the reflected parameter list diverged"), runWarnings);
                    paramIndices.Add(paramIndex);
                    paramOffsets.Add((ushort)(u.BaseRegister * 16));
                }

                var record = new ConstantBufferInfo(
                    Name:             cbName,
                    SizeInBytes:      sizeRegisters * 16,
                    ParameterIndices: paramIndices,
                    ParameterOffsets: paramOffsets);

                int existing = records.FindIndex(r => SameCbufferRecord(r, record));
                if (existing < 0)
                {
                    records.Add(record);
                    existing = records.Count - 1;
                }
                glShaderCbRecord[i] = existing;
            }
            constantBufferInfoList = records;
        }
        else
        {
            constantBufferInfoList = BuildConstantBufferInfoList(allConstantBuffers, allParameters, directX);
        }

        IReadOnlyList<EffectParameterInfo> effectParameterInfoList = BuildEffectParameterInfoList(allParameters);

        // Phase 43, F9: bake parsed sampler_state members (MinFilter/AddressU/…)
        // into per-sampler MGFX states, keyed by the .fx sampler name — the
        // declaration survives the SM4 rewrite verbatim ('SamplerState <name>;'),
        // so reflection reports the same name. mgfxc bakes these identically and
        // MonoGame applies them at EffectPass.Apply; dropping them silently
        // diverged (Point became Linear).
        var samplerStateByName = new Dictionary<string, MgfxSamplerStateInfo>(StringComparer.Ordinal);
        foreach (SamplerInfo parsedSampler in fxParsed.Samplers)
        {
            var resolved = MgfxSamplerStateResolver.Resolve(
                parsedSampler.Name,
                parsedSampler.StateEntries.Select(e => (e.Key, e.Value)),
                options.SourceFileName ?? "<source>");
            if (resolved.IsFailure)
                return Fail(resolved.Error, runWarnings);
            if (resolved.Value is { } samplerState)
                samplerStateByName[parsedSampler.Name] = samplerState;
        }

        // Attach each shader's sampler table + constant-buffer-index list so the
        // .mgfx shader record is complete and MonoGame can bind textures/uniforms.
        for (int i = 0; i < compiledShaderBlobs.Count; i++)
        {
            var samplers = new List<MgfxSamplerInfo>();
            if (shaderSamplers.TryGetValue(i, out var samplerRefs) &&
                shaderTextures.TryGetValue(i, out var textureRefs))
            {
                if (options.Target == PlatformTarget.Vulkan)
                {
                    // Two distinct textures on ONE raw SPIR-V binding = one combined
                    // descriptor serving two images: the second texture is never bound
                    // and silently samples the first's data on DesktopVK (bug-hunt
                    // 2026-07-27 M5 — the shape is two textures sampled through one
                    // shared SamplerState). Detected HERE, post-compile, because
                    // reflection sees only the SURVIVING #if branch — at binding-rewrite
                    // time the legal dual-branch shared-sampler pattern is textually
                    // indistinguishable from this invalid one.
                    var colocated = textureRefs
                        .GroupBy(t => t.RawBinding)
                        .FirstOrDefault(g => g.Select(t => t.Name).Distinct().Count() > 1);
                    if (colocated is not null)
                    {
                        return Fail(new ShaderError(
                            File: options.SourceFileName ?? "<source>",
                            Line: 0,
                            Column: 0,
                            Code: "SD0213",
                            Message: "Vulkan target: textures " +
                                     string.Join(" and ", colocated.Select(t => $"'{t.Name}'").Distinct()) +
                                     " share one sampler and were co-located onto a single " +
                                     "descriptor binding. Vulkan's combined-image-sampler " +
                                     "model needs a distinct descriptor per texture, so the " +
                                     "second texture would silently sample the first's " +
                                     "data. Give each texture its own SamplerState (or its " +
                                     "own explicit register) until per-texture descriptor " +
                                     "duplication ships."), runWarnings);
                    }
                }

                // The targets disagree about what a sampler record IS, so the table is built
                // three different ways — deliberately, and each way matches its own runtime's
                // binding model:
                //
                //  * DirectX / DirectX12 — one record per reflected TEXTURE, keyed on the
                //    texture's bind point. That is what mgfxc does (ShaderData.DX11 walks
                //    the RDEF texture bindings), and the goldens prove it: a sampler-driven
                //    table emits ONE record for N textures sharing a single `SamplerState`
                //    (the classic diffuse+lightmap shape), so MonoGame's ApplySamplers never
                //    binds textures 1..N-1 and `Parameters["Lightmap"].SetValue(tex)`
                //    silently does nothing. See tests/fixtures/golden/DirectX_11/
                //    PenumbraTexture.mgfx, which carries TWO records. DirectX12 was left out
                //    of this branch until Phase 51 A7 and silently had exactly that bug.
                //
                //  * OpenGL — one record per (texture, sampler) PAIR, in the order SPIRV-Cross
                //    declares the combined samplers it folds each pair into, because the GL
                //    runtime binds a texture unit to a sampler by GLSL uniform NAME
                //    (glGetUniformLocation("ps_s{k}")) and MonoGameGlslRewriter names those
                //    positionally in emitted-declaration order. NEITHER reflected list is that
                //    pair list: N textures through one shared SamplerState is N uniforms but one
                //    reflected sampler, and the mirror shape (one texture, N SamplerStates — the
                //    linear+point idiom) is N uniforms but one reflected texture. The pairs come
                //    from SpirvCombinedSamplerPairs, which derives them from the SPIR-V so the
                //    desktop and WASM hosts agree byte-for-byte.
                //
                //  * Vulkan — one record per reflected SAMPLER. Vulkan binds through the
                //    descriptor layout in VulkanShaderCodeWrapper (raw SPIR-V bindings), not by
                //    GLSL name, and VulkanTextureSamplerBindingRewriter has already forced each
                //    pair onto matching registers, so the reflected list IS the binding list.
                bool textureKeyed = directX || options.Target == PlatformTarget.DirectX12;
                if (textureKeyed)
                {
                    foreach (TextureReflection tex in textureRefs)
                    {
                        int slot = tex.BindSlot;
                        // The sampler paired with this texture, for the baked sampler_state.
                        // Slot first (the 1:1 modern shape), then the sole shared sampler.
                        SamplerReflection? matchedSamp =
                            samplerRefs.FirstOrDefault(s => s.BindSlot == slot)
                            ?? (samplerRefs.Count == 1 ? samplerRefs[0] : null);
                        // No Math.Max(0, …) clamp (bug-hunt 2026-07-27 N11): a failed
                        // name→parameter join used to be silently coerced to parameter 0 —
                        // the sampler then pointed at whatever parameter 0 happened to be.
                        // A -1 miss now flows to the writer's SD0022 byte-range guard and
                        // fails the compile loudly instead.
                        samplers.Add(new MgfxSamplerInfo(
                            // MonoGame SamplerType byte: 2D=0, Cube=1, Volume(3D)=2 (1D=3).
                            // Critical for binding — cube/3D won't bind at runtime if left 0.
                            Type:        SamplerTypeByte(tex.Dimension),
                            TextureSlot: (byte)slot,
                            SamplerSlot: (byte)slot,
                            // DX11 binds samplers via the DXBC resource table, not by name, so
                            // the name is empty — matching the mgfxc DirectX_11 goldens. DX12
                            // keeps the positional form it has been rung-4 proven with (Phase
                            // 54); real mgfxc writes the HLSL sampler name there instead, a
                            // separate divergence tracked in Phase 51, not changed here because
                            // it needs its own DX12 render re-proof.
                            Name:        directX ? string.Empty : $"ps_s{slot}",
                            Parameter:   IndexOfParam(allParameters, tex.Name),
                            State:       matchedSamp is null
                                             ? null
                                             : samplerStateByName.GetValueOrDefault(matchedSamp.Name)));
                    }
                }
                else if (options.Target == PlatformTarget.OpenGL)
                {
                    // One record per (texture, sampler) PAIR, in SPIRV-Cross's own
                    // combined-sampler declaration order — the only list that can be correct,
                    // because it is literally the list of uniforms the emitted GLSL declares
                    // and the GL runtime looks up by name. Derived from the SPIR-V so the
                    // desktop and WASM hosts produce identical bytes (Phase 51 A7).
                    if (!shaderSpirv.TryGetValue(i, out ReadOnlyMemory<byte> glSpirv) || glSpirv.IsEmpty)
                    {
                        return Fail(new ShaderError(
                            File: options.SourceFileName ?? "<source>",
                            Line: 0,
                            Column: 0,
                            Code: "SD0217",
                            Message: "OpenGL target: the SPIR-V for this shader stage is " +
                                     "unavailable, so the combined-sampler declaration order " +
                                     "the GL sampler table must mirror cannot be determined."),
                            runWarnings);
                    }

                    Result<IReadOnlyList<CombinedSamplerPair>, ShaderError> pairResult =
                        SpirvCombinedSamplerPairs.Extract(glSpirv);
                    if (pairResult.IsFailure)
                        return Fail(pairResult.Error with { File = options.SourceFileName ?? "<source>" }, runWarnings);

                    IReadOnlyList<CombinedSamplerPair> pairs = pairResult.Value;

                    for (int k = 0; k < pairs.Count; k++)
                    {
                        CombinedSamplerPair pair = pairs[k];

                        // Dimension comes from the REFLECTED texture (not from the SPIR-V walk)
                        // so it has a single source: both the native DXIL oracle and the
                        // pure-managed SpirvReflector report it identically, which is what keeps
                        // the sampler-type byte byte-transparent across hosts.
                        TextureReflection? tex =
                            textureRefs.FirstOrDefault(t => string.Equals(t.Name, pair.TextureName, StringComparison.Ordinal));
                        if (tex is null)
                        {
                            return Fail(new ShaderError(
                                File: options.SourceFileName ?? "<source>",
                                Line: 0,
                                Column: 0,
                                Code: "SD0217",
                                Message: $"OpenGL target: texture '{pair.TextureName}' is sampled " +
                                         $"through '{pair.SamplerName}' but is not in this stage's " +
                                         "reflected texture list, so its combined sampler cannot be " +
                                         "bound to an effect parameter."), runWarnings);
                        }

                        // No Math.Max(0, …) clamp — see the DirectX branch (bug-hunt N11); a -1
                        // miss reaches the writer's SD0022 guard and fails loudly.
                        samplers.Add(new MgfxSamplerInfo(
                            Type:        SamplerTypeByte(tex.Dimension),
                            // The record index IS the GL texture unit: MonoGame's GL runtime does
                            // glUniform1i(location("ps_s{k}"), TextureSlot) and then binds
                            // Parameters[Parameter]'s texture to that unit. Each pair needs its
                            // own unit even when several pairs share one texture or one sampler.
                            TextureSlot: (byte)k,
                            SamplerSlot: (byte)k,
                            Name:        $"ps_s{k}",
                            Parameter:   IndexOfParam(allParameters, pair.TextureName),
                            // Keyed on the .fx sampler identifier, which survives the SM4
                            // rewrite verbatim, so the baked sampler_state follows the SAMPLER
                            // half of the pair — two pairs sharing one texture but different
                            // SamplerStates (the linear+point idiom) get their own state each.
                            State:       samplerStateByName.GetValueOrDefault(pair.SamplerName)));
                    }

                    // Cross-check the model against the artifact it is predicting: the emitted
                    // GLSL is the authority on how many sampler uniforms exist, and every one
                    // must have a record naming it or it silently keeps texture unit 0. This can
                    // only fire if the declaration-order model in SpirvCombinedSamplerPairs has
                    // drifted from the pinned SPIRV-Cross, which is exactly the regression worth
                    // catching loudly rather than shipping a mis-bound table.
                    string emittedGlsl = Encoding.UTF8.GetString(compiledShaderBlobs[i].Bytes);
                    int declaredSamplers = GlslSamplerDeclaration.Matches(emittedGlsl).Count;
                    if (declaredSamplers != samplers.Count)
                    {
                        return Fail(new ShaderError(
                            File: options.SourceFileName ?? "<source>",
                            Line: 0,
                            Column: 0,
                            Code: "SD0217",
                            Message: $"OpenGL target: the emitted GLSL declares {declaredSamplers} " +
                                     $"sampler uniform(s) but {samplers.Count} combined " +
                                     "(texture, sampler) pair(s) were derived from the SPIR-V. The " +
                                     "two must agree exactly, so this is an internal " +
                                     "declaration-order mismatch, not a problem with the shader."),
                            runWarnings);
                    }
                }
                else
                {
                    // Vulkan: one record per reflected SAMPLER. Binding goes through the
                    // descriptor layout VulkanShaderCodeWrapper writes (raw SPIR-V bindings),
                    // never by GLSL name, and VulkanTextureSamplerBindingRewriter has already
                    // co-located each texture+sampler pair on matching registers — so the
                    // reflected sampler list IS the binding list here.
                    foreach (SamplerReflection samp in samplerRefs)
                    {
                        int slot = samp.BindSlot;
                        // Pair the sampler with its texture (by slot, then first) so the
                        // sampler-type byte can carry the texture's DIMENSION. The dimension
                        // is reflected identically by BOTH the DXIL oracle and the pure-managed
                        // SpirvReflector, so this stays byte-transparent across the desktop
                        // and WASM reflection paths.
                        TextureReflection? matchedTex =
                            textureRefs.FirstOrDefault(t => t.BindSlot == slot)
                            ?? textureRefs.FirstOrDefault();
                        // No Math.Max(0, …) clamp — see the DirectX branch (bug-hunt N11).
                        int paramIndex = matchedTex is null
                            ? 0
                            : IndexOfParam(allParameters, matchedTex.Name);
                        samplers.Add(new MgfxSamplerInfo(
                            Type:        SamplerTypeByte(matchedTex?.Dimension),
                            TextureSlot: (byte)slot,
                            SamplerSlot: (byte)slot,
                            Name:        $"ps_s{slot}",
                            Parameter:   paramIndex,
                            // The reflected sampler name is the .fx sampler identifier —
                            // the key the parsed sampler_state members were resolved under.
                            State:       samplerStateByName.GetValueOrDefault(samp.Name)));
                    }
                }
            }

            var cbIndices = new List<int>();
            if (glShaderCbRecord is not null)
            {
                // GL: the shader's single merged {vs,ps}_uniforms_vec4 record
                // (Phase 43 F4/F5) — by construction, never by reflection-name lookup.
                if (glShaderCbRecord.TryGetValue(i, out int recordIndex))
                    cbIndices.Add(recordIndex);
            }
            else if (shaderCbufferNames.TryGetValue(i, out var cbNames))
            {
                foreach (string name in cbNames)
                {
                    int gi = IndexOfCbuffer(allConstantBuffers, name);
                    if (gi >= 0)
                        cbIndices.Add(gi);
                }
            }

            // Vulkan: real mgfxc only supports one constant buffer per shader stage
            // (VulkanShaderProfile.CreateShader throws on a second one) — fail loudly
            // rather than silently drop or mis-bind a buffer the writer can't represent.
            if (options.Target == PlatformTarget.Vulkan && cbIndices.Count > 1)
                return Fail(new ShaderError(
                    File: options.SourceFileName ?? "",
                    Line: 0,
                    Column: 0,
                    Code: "SD0026",
                    Message: "Vulkan does not support more than one constant buffer per shader " +
                             "stage; consider merging globals into a single cbuffer."), runWarnings);

            byte[] blobBytes = compiledShaderBlobs[i].Bytes;
            if (options.Target == PlatformTarget.Vulkan)
            {
                blobBytes = VulkanShaderCodeWrapper.Wrap(
                    blobBytes,
                    compiledShaderBlobs[i].Stage,
                    constantBuffer: cbIndices.Count > 0 ? allConstantBuffers[cbIndices[0]] : null,
                    textures: shaderTextures.TryGetValue(i, out var vkTextures) ? vkTextures : [],
                    samplers: shaderSamplers.TryGetValue(i, out var vkSamplers) ? vkSamplers : []);
            }
            else if (options.Target == PlatformTarget.DirectX12)
            {
                blobBytes = DirectX12ShaderCodeWrapper.Wrap(
                    blobBytes,
                    textures: shaderTextures.TryGetValue(i, out var dxTextures) ? dxTextures : [],
                    samplers: shaderSamplers.TryGetValue(i, out var dxSamplers) ? dxSamplers : []);
            }

            compiledShaderBlobs[i] = compiledShaderBlobs[i] with
            {
                Bytes                 = blobBytes,
                Samplers              = samplers,
                ConstantBufferIndices = cbIndices,
            };
        }

        ShaderIR ir = ShaderIRBuilder.Build(
            compiledShaderBlobs,
            techniques,
            constantBufferInfoList,
            effectParameterInfoList);

        // Stage 6 (additive): KNIFX v11 container — opt-in, never the default. Same IR, a
        // different container; the default MGFX v10 path below is untouched. (Phase 35 B.)
        if (effectiveContainer == EffectContainer.Knifx)
        {
            // KNI ships no Vulkan platform, and KnifxBackend has no Vulkan value to map
            // to — without this guard a Vulkan+KNIFX request would silently fall through
            // to KnifxBackend.OpenGL and emit a wrong-shaped container.
            if (options.Target == PlatformTarget.Vulkan)
                return Fail(new ShaderError(
                    File: "",
                    Line: 0,
                    Column: 0,
                    Code: "SD0025",
                    Message: "The Vulkan target does not support the KNIFX container (KNI ships no Vulkan platform)."), runWarnings);

            // Same reasoning as Vulkan above: KNI ships no DX12 platform either.
            if (options.Target == PlatformTarget.DirectX12)
                return Fail(new ShaderError(
                    File: "",
                    Line: 0,
                    Column: 0,
                    Code: "SD0027",
                    Message: "The DirectX12 target does not support the KNIFX container (KNI ships no DX12 platform)."), runWarnings);

            KnifxBackend knifxBackend = options.Target switch
            {
                PlatformTarget.DirectX => KnifxBackend.DirectX11,
                _ => KnifxBackend.OpenGL,
            };
            var knifxResult = new KnifxWriter().Write(ir, new KnifxWriterOptions(knifxBackend));
            if (knifxResult.IsFailure)
                return Fail(knifxResult.Error, runWarnings);
            return Result<CompiledShader, ShaderError[]>.Ok(
                new CompiledShader(options.Target, knifxResult.Value) { Warnings = runWarnings });
        }

        // Stage 6: MGFX binary writer.
        MgfxProfile mgfxProfile = options.Target switch
        {
            PlatformTarget.DirectX   => MgfxProfile.DirectX11,
            PlatformTarget.OpenGL    => MgfxProfile.OpenGL,
            PlatformTarget.Vulkan    => MgfxProfile.Vulkan,
            PlatformTarget.DirectX12 => MgfxProfile.DirectX12,
            _ => MgfxProfile.OpenGL,
        };

        // Real MonoGame 3.8.5 hardcodes version 11 for every profile (SourceFile/
        // Entrypoint always written) — DesktopVK is new in 3.8.5 with no older-version
        // reader to preserve compatibility with, so Vulkan always writes the v11 shape
        // regardless of CompilerOptions.MgfxVersion. DirectX12 is new in 3.8.5 too, same
        // reasoning (Phase 54).
        if (options.Target is PlatformTarget.Vulkan or PlatformTarget.DirectX12)
            effectiveMgfxVersion = 11;

        // Guard the byte cast (like the writer's SD0020/SD0021 size guards): a
        // MgfxVersion outside 0..255 would silently truncate into a bogus header.
        if (effectiveMgfxVersion is < byte.MinValue or > byte.MaxValue)
            return Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "SD0023",
                Message: $"MgfxVersion {effectiveMgfxVersion} is outside the MGFX header's byte range (0-255)"), runWarnings);

        var mgfxWriter  = new MgfxWriter();
        var writeResult = mgfxWriter.Write(ir, new MgfxWriterOptions(
            Profile: mgfxProfile,
            MgfxVersion: (byte)effectiveMgfxVersion));

        if (writeResult.IsFailure)
            return Fail(writeResult.Error, runWarnings);

        byte[] mgfxBytes = writeResult.Value;

        return Result<CompiledShader, ShaderError[]>.Ok(
            new CompiledShader(options.Target, mgfxBytes) { Warnings = runWarnings });
        }
        finally
        {
            if (dxcCompiler.IsValueCreated)
                (dxcCompiler.Value as IDisposable)?.Dispose();
        }
    }

    // Parse a pass profile string ("vs_3_0", "ps_2_0") into (Major, Minor) for the KNIFX
    // per-shader ShaderVersion. MGFX v10 ignores this; KNIFX v11 records it (and a non-(0,0)
    // value selects KNI's GLSL-directory parse path). Defaults to (3,0) — the MojoShader GL
    // ceiling — when the profile is absent or unparseable.
    private static (int Major, int Minor) ParseShaderModel(string? profile)
    {
        if (!string.IsNullOrEmpty(profile))
        {
            var m = System.Text.RegularExpressions.Regex.Match(profile, @"_(\d)_(\d)");
            if (m.Success
                && int.TryParse(m.Groups[1].Value, out int major)
                && int.TryParse(m.Groups[2].Value, out int minor))
                return (major, minor);
        }
        return (3, 0);
    }

    // -------------------------------------------------------------------------
    // Recognized-profile validation (SD0013) — GL / DX / Vulkan path (Phase 48).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates one pass stage's <c>compile &lt;target&gt;</c> token against the
    /// recognized-profile set, returning a <see cref="ShaderError"/> when it is not a real
    /// profile (after macro expansion) and <c>null</c> when it is acceptable. mgfxc/fxc
    /// hard-error on an unrecognized target; ShadowDusk used to silently fall back to SM3.
    /// </summary>
    /// <remarks>
    /// CHEAP path: a literal known profile (<c>ps_3_0</c>, <c>ps_4_0_level_9_1</c>, …) is
    /// accepted with zero work; a profile-SHAPED-but-bogus token (<c>ps_9_9</c>) is rejected
    /// without expansion. EXPENSIVE path: a non-literal token (a macro name like
    /// <c>PS_SHADERMODEL</c>) is macro-expanded via DXC's <c>-P</c> preprocessor using the
    /// target's <see cref="MacroSet"/> so the correct <c>#if OPENGL</c> branch is selected,
    /// then re-checked. Expansions are cached per token so the common single-macro effect
    /// pays for one expansion total, not one per pass.
    /// </remarks>
    private static ShaderError? ValidateCompileProfile(
        string? profile,
        string? profileToken,
        SourceSpan? span,
        ShaderStage stage,
        Lazy<IDxcShaderCompiler> dxcCompiler,
        MacroSet macros,
        PreprocessedSource preprocessed,
        string sourceFileName,
        Dictionary<string, string?> expansionCache,
        bool enforceStagePrefix,
        CancellationToken cancellationToken,
        bool enforceDirectX11Floor = false,
        string? entryPoint = null)
    {
        // A missing profile cannot be validated (and never reaches a compile with a real
        // target) — leave the existing SM3 fallback behavior untouched.
        if (string.IsNullOrEmpty(profile))
            return null;

        // Cheap path: an already-known literal profile is accepted with no expansion.
        // (IsKnownProfile is case-insensitive, so the lowercased form is fine here.)
        if (FxPreParser.IsKnownProfile(profile))
            return ResolvedProfileChecks(
                profile, stage, span, sourceFileName, enforceStagePrefix, enforceDirectX11Floor, entryPoint);

        // Profile-SHAPED but NOT a known profile (e.g. 'ps_9_9', 'ps_2_5'): unconditionally
        // invalid — no macro could rescue a literal that already looks like a profile — so
        // reject here without paying for a preprocess.
        if (FxPreParser.LooksLikeProfile(profile))
            return ProfileError(profile, span, sourceFileName);

        // Expensive path: a macro NAME (e.g. 'PS_SHADERMODEL'). Macro-expand it with the
        // target's macros and re-check. C macros are CASE-SENSITIVE, so expand the token as
        // written ('PS_SHADERMODEL'), not the lowercased profile. Cache per raw token so
        // repeated passes (every pass uses the same *_SHADERMODEL macro) expand only once.
        string rawToken = profileToken ?? profile;
        if (!expansionCache.TryGetValue(rawToken, out string? expanded))
        {
            expanded = TryExpandProfileToken(
                rawToken, dxcCompiler, macros, preprocessed, sourceFileName, cancellationToken);
            expansionCache[rawToken] = expanded;
        }

        // The macro-token check is BEST-EFFORT and must never block a compile that would
        // otherwise succeed. When expansion could not run on this backend (the WASM DXC shim
        // has no preprocess-only '-P' export and throws — see JsShaderBackends.Preprocess),
        // we cannot tell a real profile from a typo, so we defer to the actual compile: this
        // restores the exact pre-Phase-48 behavior (lenient accept) for macro tokens on a
        // backend without '-P', while desktop (where '-P' works) still rejects bogus macros.
        if (ReferenceEquals(expanded, ExpansionUnavailable))
            return null;

        if (expanded is not null && FxPreParser.IsKnownProfile(expanded))
            return ResolvedProfileChecks(
                expanded, stage, span, sourceFileName, enforceStagePrefix, enforceDirectX11Floor, entryPoint);

        return ProfileError(profile, span, sourceFileName);
    }

    /// <summary>
    /// The checks that only make sense once a compile target has RESOLVED to a recognized
    /// profile: the stage-prefix cross-check (<c>SD0014</c>) and then the target's own
    /// profile floor (<c>SD0015</c>). Stage prefix runs first because it is the more
    /// specific diagnosis of the same token — a <c>ps_*</c> in a <c>VertexShader</c> slot
    /// is a slot error, not a shader-model one (mgfxc conflates the two and reports its
    /// floor message for both).
    /// </summary>
    private static ShaderError? ResolvedProfileChecks(
        string knownProfile,
        ShaderStage stage,
        SourceSpan? span,
        string sourceFileName,
        bool enforceStagePrefix,
        bool enforceDirectX11Floor,
        string? entryPoint)
    {
        ShaderError? stagePrefix = StagePrefixCheck(knownProfile, stage, span, sourceFileName, enforceStagePrefix);
        if (stagePrefix is not null)
            return stagePrefix;

        return enforceDirectX11Floor
            ? DirectX11FloorCheck(knownProfile, stage, span, sourceFileName, entryPoint)
            : null;
    }

    /// <summary>
    /// Sentinel cached by <see cref="TryExpandProfileToken"/> when macro expansion could not
    /// run on the active DXC backend (e.g. the WASM shim has no <c>-P</c> export). Distinct
    /// from a <c>null</c> expansion (which is a definitive "not a profile" → reject).
    /// </summary>
    private const string ExpansionUnavailable = "\0__sd_expansion_unavailable__";

    /// <summary>
    /// Macro-expands a compile-target token, returning the expansion, <c>null</c> when it does
    /// not resolve to a profile-shaped token, or <see cref="ExpansionUnavailable"/> when the
    /// backend cannot preprocess at all (the WASM DXC shim throws <see cref="NotSupportedException"/>
    /// for <c>-P</c>). Any non-cancellation failure is treated as "unavailable" so the
    /// best-effort profile check can never crash or block a compile.
    /// </summary>
    private static string? TryExpandProfileToken(
        string token,
        Lazy<IDxcShaderCompiler> dxcCompiler,
        MacroSet macros,
        PreprocessedSource preprocessed,
        string sourceFileName,
        CancellationToken cancellationToken)
    {
        try
        {
            var expandResult = ExpandProfileToken(
                token, dxcCompiler, macros, preprocessed, sourceFileName, cancellationToken);
            // A preprocess failure (e.g. a malformed #if the user wrote) is NOT surfaced here:
            // the real compile independently reports any genuine source error, so treating it
            // as "unavailable" keeps the check best-effort and avoids double/duplicate errors.
            return expandResult.IsFailure ? ExpansionUnavailable : expandResult.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Backend cannot preprocess (WASM DXC shim has no -P export, throws
            // NotSupportedException). Skip the macro-token check; defer to the actual compile.
            return ExpansionUnavailable;
        }
    }

    /// <summary>
    /// Zero-technique macro recovery (the FNA path's half of the Phase 41 GAP-1 fallback):
    /// macro-expand the already-<c>#include</c>-flattened source through DXC's <c>-P</c>
    /// preprocessor with <paramref name="macros"/>, then re-parse the expanded text in
    /// <paramref name="mode"/>, so techniques that exist only as a <c>TECHNIQUE(...)</c> macro
    /// call become visible. Tri-state result:
    /// <list type="bullet">
    /// <item><c>Ok(non-null)</c> — re-parsed; may itself have zero techniques (a genuinely
    /// technique-free source), which the caller treats as the honest SD0010.</item>
    /// <item><c>Ok(null)</c> — <c>-P</c> could not RUN on this backend (the WASM DXC shim
    /// throws, or the DXC native is absent); recovery is skipped best-effort and the caller
    /// keeps SD0010 (the WASM degrade-path).</item>
    /// <item><c>Fail(error)</c> — <c>-P</c> RAN and reported a genuine source error (e.g. a
    /// malformed <c>#if</c>), or the re-parse failed; the caller surfaces that exact diagnostic
    /// rather than a misleading SD0010 (no later compile would re-report it). CLAUDE.md #5.</item>
    /// </list>
    /// Never crashes. The GL/DX path in <see cref="Run"/> inlines an equivalent recovery with
    /// its own modern-branch gate.
    /// </summary>
    private Result<FxParseResult?, ShaderError> TryRecoverMacroTechniques(
        string flattenedSource,
        MacroSet macros,
        string sourceFileName,
        FxSourceMode mode,
        CancellationToken cancellationToken)
    {
        var request = new DxcPreprocessRequest
        {
            HlslSource     = flattenedSource,
            SourceFileName = sourceFileName,
            Macros         = macros.Macros
                .Select(m => (m.Name, (string?)m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToList(),
        };

        IDxcShaderCompiler? dxc = null;
        try
        {
            dxc = _dxcCompilerFactory();
            Result<string, ShaderError> expandResult = dxc.Preprocess(request, cancellationToken);
            // A real -P preprocess error (the expander RAN and reported a genuine source error,
            // e.g. a malformed #if) is SURFACED, not swallowed: unlike profile validation there
            // is no subsequent compile to re-report it (we would otherwise return a misleading
            // SD0010 'no techniques' instead of the actual diagnostic — CLAUDE.md constraint #5).
            if (expandResult.IsFailure)
                return Result<FxParseResult?, ShaderError>.Fail(expandResult.Error);

            var reparse = FxPreParser.Parse(expandResult.Value, sourceFileName, mode);
            if (reparse.IsFailure)
                return Result<FxParseResult?, ShaderError>.Fail(FromFxParseError(reparse.Error));

            // Ok(value) — value may legitimately have zero techniques (a genuinely
            // technique-free source), in which case the caller keeps the honest SD0010.
            return Result<FxParseResult?, ShaderError>.Ok(reparse.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // -P could not RUN on this backend (the WASM DXC shim throws NotSupportedException),
            // or the DXC native could not be constructed. Distinct from a -P *failure* above:
            // here we cannot tell whether techniques exist, so we skip recovery best-effort
            // (Ok(null)) and let the honest SD0010 stand — exactly the WASM degrade-path.
            return Result<FxParseResult?, ShaderError>.Ok(null);
        }
        finally
        {
            (dxc as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// W3 (GL/DX/Vulkan): once a compile target resolves to a recognized profile, verify its
    /// stage prefix matches the pass slot it is bound to — a <c>vs_*</c> profile in a
    /// <c>VertexShader =</c> slot and a <c>ps_*</c> in a <c>PixelShader =</c> slot. mgfxc/fxc
    /// reject a cross-stage binding (e.g. <c>VertexShader = compile ps_3_0 …</c>); ShadowDusk
    /// previously ignored the declared prefix and compiled by slot. Returns <c>null</c> when
    /// the prefix matches (or when <paramref name="enforceStagePrefix"/> is false — the FNA
    /// path keeps this check in <see cref="ResolveFnaProfile"/> as SD0300). A recognized
    /// profile is always <c>vs_</c> or <c>ps_</c> (KnownProfiles lists no other stages).
    /// </summary>
    private static ShaderError? StagePrefixCheck(
        string knownProfile, ShaderStage stage, SourceSpan? span, string sourceFileName, bool enforceStagePrefix)
    {
        if (!enforceStagePrefix)
            return null;

        bool profileIsVertex = knownProfile.StartsWith("vs_", StringComparison.Ordinal);
        bool slotIsVertex    = stage == ShaderStage.Vertex;
        if (profileIsVertex == slotIsVertex)
            return null;

        string slot = slotIsVertex ? "VertexShader" : "PixelShader";
        string want = slotIsVertex ? "vs_*" : "ps_*";
        return new ShaderError(
            File: sourceFileName,
            Line: span?.StartLine ?? 0,
            Column: span?.StartColumn ?? 0,
            Code: "SD0014",
            Message: $"compile target '{knownProfile}' is a {(profileIsVertex ? "vertex" : "pixel")} profile but " +
                     $"is bound to the pass's {slot} slot — the profile's stage must match the slot it compiles " +
                     $"(use a {want} profile)");
    }

    /// <summary>
    /// Phase 51 A10 (GL/DX/Vulkan path, DirectX only): once a compile target resolves to a
    /// recognized profile, verify it is one MonoGame's <c>DirectX_11</c> shader profile
    /// actually accepts. <c>mgfxc</c> hard-errors on anything else — <em>"Invalid profile
    /// 'vs_3_0'. Vertex shader 'VSMain' must be SM 4.0 level 9.1 or higher!"</em> — while
    /// ShadowDusk used to compile it happily, so a legacy SM2/SM3 effect (or one whose
    /// <c>#if OPENGL … #else …</c> header names SM3 in BOTH arms, which is what the
    /// ShaderToy converter used to emit) built here and then failed in the consumer's real
    /// Content Pipeline build. The accepted set is empirical; see
    /// <see cref="FxPreParser.IsDirectX11Profile"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to <see cref="PlatformTarget.DirectX"/> and no other target.
    /// The OpenGL ceiling (<c>mgfxc</c>: <em>"must be SM 3.0 or lower!"</em>) and the
    /// Vulkan floor (<em>"Invalid Vulkan vertex profile … Requires vs_6_0"</em>) are the
    /// same class and were measured at the same time, but each has its own reject-set
    /// blast radius and is tracked as its own gap row in <c>docs/validation-matrix.md</c>
    /// §8. <see cref="PlatformTarget.DirectX12"/> is excluded because its reference
    /// compiler is mgfxc <b>3.8.5</b>, which is not the pinned golden oracle and is not
    /// installed here, so its floor has never been measured — guessing it would be the
    /// opposite of the empirical rule this check is built on.
    /// </remarks>
    private static ShaderError? DirectX11FloorCheck(
        string knownProfile, ShaderStage stage, SourceSpan? span, string sourceFileName, string? entryPoint)
    {
        if (FxPreParser.IsDirectX11Profile(knownProfile))
            return null;

        string stageWord = stage == ShaderStage.Vertex ? "Vertex" : "Pixel";
        string prefix    = stage == ShaderStage.Vertex ? "vs" : "ps";
        string named     = entryPoint is null ? string.Empty : $" '{entryPoint}'";
        return new ShaderError(
            File: sourceFileName,
            Line: span?.StartLine ?? 0,
            Column: span?.StartColumn ?? 0,
            Code: "SD0015",
            Message: $"compile target '{knownProfile}' is below the DirectX target's floor — mgfxc rejects it " +
                     $"with \"Invalid profile '{knownProfile}'. {stageWord} shader{named} must be SM 4.0 level 9.1 " +
                     $"or higher!\". MonoGame's DirectX_11 profile accepts only {prefix}_4_0_level_9_1, " +
                     $"{prefix}_4_0_level_9_3, {prefix}_4_0, {prefix}_4_1, and {prefix}_5_0 (note that SM6 " +
                     $"profiles such as {prefix}_6_0 are refused too). Use the standard " +
                     "'#if OPENGL … #else …' header so the DirectX arm defines " +
                     "VS_SHADERMODEL/PS_SHADERMODEL as the *_4_0_level_9_1 pair");
    }

    /// <summary>
    /// Macro-expands a single compile-target token using DXC's <c>-P</c> preprocessor with
    /// the target's macros, returning the lowercased expansion (or <c>null</c> when it does
    /// not expand to a profile-shaped token). A unique sentinel wraps the probe so the
    /// expansion is recovered unambiguously from the preprocessed output.
    /// </summary>
    private static Result<string?, ShaderError> ExpandProfileToken(
        string token,
        Lazy<IDxcShaderCompiler> dxcCompiler,
        MacroSet macros,
        PreprocessedSource preprocessed,
        string sourceFileName,
        CancellationToken cancellationToken)
    {
        const string sentinel = "__SD_PROFILE_PROBE__";

        // preprocessed.Text still carries every '#define'/'#if' line (ShadowDusk's
        // Preprocessor flattens #includes and prepends platform macros but leaves the
        // conditionals for DXC). Append a probe that references the original token so DXC
        // expands it through exactly the macros the real compile would see — including the
        // correct '#if OPENGL' branch driven by the target's PlatformMacros below.
        string probeSource = preprocessed.Text + $"\n{sentinel} {token} {sentinel}\n";

        var request = new DxcPreprocessRequest
        {
            HlslSource     = probeSource,
            SourceFileName = sourceFileName,
            Macros         = macros.Macros
                .Select(m => (m.Name, (string?)m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToList(),
        };

        Result<string, ShaderError> result = dxcCompiler.Value.Preprocess(request, cancellationToken);
        if (result.IsFailure)
            return Result<string?, ShaderError>.Fail(result.Error);

        // Pull the text between the probe's two sentinels. The sentinel is a unique token
        // that appears nowhere but the appended probe, so the FIRST occurrence opens the
        // probe and the NEXT closes it. Trim + lowercase to compare against KnownProfiles
        // (case-insensitive) and to keep the cache key canonical.
        string text = result.Value;
        int open = text.IndexOf(sentinel, StringComparison.Ordinal);
        if (open < 0)
            return Result<string?, ShaderError>.Ok(null);
        int valueStart = open + sentinel.Length;
        int close = text.IndexOf(sentinel, valueStart, StringComparison.Ordinal);
        if (close < 0)
            return Result<string?, ShaderError>.Ok(null);

        string expanded = text.Substring(valueStart, close - valueStart).Trim().ToLowerInvariant();
        // An unexpanded macro re-emits its own name (DXC leaves an undefined identifier
        // verbatim); a defined macro yields its value. Either way, if it is not a
        // recognized profile the caller rejects it. Return null for an empty expansion.
        return Result<string?, ShaderError>.Ok(expanded.Length == 0 ? null : expanded);
    }

    private static ShaderError ProfileError(string profile, SourceSpan? span, string sourceFileName) =>
        new(
            File: sourceFileName,
            Line: span?.StartLine ?? 0,
            Column: span?.StartColumn ?? 0,
            Code: "SD0013",
            Message: $"compile target '{profile}' is not a recognized shader profile " +
                     "(did you forget to #define VS_SHADERMODEL / PS_SHADERMODEL, e.g. via the " +
                     "standard '#if OPENGL ... #else ...' header?)");

    // -------------------------------------------------------------------------
    // FNA (fx_2_0) pipeline — Phase 39. HLSL (D3D9 style, preserved by the
    // PreserveSm3 pre-parse mode) → vkd3d D3D_BYTECODE at SM1–3 → CTAB reflection →
    // Fx2EffectBuilder → Fx2EffectWriter. Always vkd3d on every host (never the
    // d3dcompiler oracle) so output is host-independent; CompilerOptions.DxbcBackend
    // and MgfxVersion are ignored by design.
    // -------------------------------------------------------------------------

    private Result<CompiledShader, ShaderError[]> RunFna(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken)
    {
        string sourceFileName = options.SourceFileName ?? "<source>";

        // Stage 1: FX9 pre-parse, preserving the D3D9 constructs vkd3d compiles natively.
        var parseResult = FxPreParser.Parse(hlslSource, sourceFileName, FxSourceMode.PreserveSm3);
        if (parseResult.IsFailure)
            return Fail(FromFxParseError(parseResult.Error));

        FxParseResult fxParsed = parseResult.Value;

        IIncludeResolver includeResolver = options.IncludeResolver ?? new FileSystemIncludeResolver();
        MacroSet fnaPlatformMacros = PlatformMacros.For(PlatformTarget.Fna);
        if (options.Defines.Count > 0)
        {
            // mgfxc /Defines: parity on the FNA path too (bug-hunt 2026-07-27 M9).
            fnaPlatformMacros = fnaPlatformMacros with { UserDefines = options.Defines };
        }

        // Zero-technique FNA macro fallback (Phase 41 GAP-1, extended to FNA). Mirrors the
        // GL/DX recovery in Run(): an effect whose techniques come ONLY from a TECHNIQUE(...)
        // macro — the MonoGame stock effects (BasicEffect.fx etc., via Macros.fxh) and the
        // FlatRedBall/Gum FNA sample (its own #define TECHNIQUE) — yields zero literal
        // techniques from the raw pre-parse, which used to be an immediate SD0010. Recover by
        // #include-flattening + macro-expanding through DXC's -P preprocessor with the FNA
        // macro set, then re-parsing the EXPANDED text in PreserveSm3 mode (FNA keeps the
        // D3D9 constructs vkd3d compiles natively). Differences from the GL/DX path:
        //   * NO modern-branch gate. FNA's vkd3d SM1-3 backend compiles the LEGACY
        //     (vs_2_0 / ps_2_0) macro branch directly and never uses DXC for codegen, so the
        //     GL legacy-branch SPIR-V crash that forces the GL gate cannot occur here.
        //   * PreserveSm3 re-parse, so the recovered StrippedHlsl keeps the D3D9 forms.
        // Best-effort on the WASM degrade-path: if -P cannot RUN (the WASM DXC shim throws),
        // recovery is skipped and the honest SD0010 below stands. But a GENUINE source error
        // (a bad #include, a malformed #if) IS surfaced with its real diagnostic rather than a
        // misleading SD0010. The default (techniques already found) path never enters this
        // block, so every FNA effect that compiles today is untouched.
        // Note: a recovered StrippedHlsl carries DXC -P #line markers into the downstream
        // Sm3StageReservationRewriter / vkd3d SM1-3 compile; vkd3d tolerates them (proven by the
        // Phase 41 FNA macro-technique corpus), the same way the GL/DX recovery feeds -P output on.
        PreprocessedSource? recoveredPreprocessed = null;
        IReadOnlyList<ShaderError> fnaPreprocessWarnings = [];
        if (fxParsed.Techniques.Count == 0)
        {
            var flattenForExpand = new Preprocessor().Flatten(
                fxParsed.StrippedHlsl, sourceFileName, fnaPlatformMacros,
                includeResolver, options.AdditionalIncludePaths);
            if (flattenForExpand.IsFailure)
                return Fail(flattenForExpand.Error);   // a real #include error — surface it, not SD0010

            // The recovery path rebuilds PreprocessedSource from the re-parse below, so the
            // flatten's own warnings have to be carried across explicitly or they are lost.
            fnaPreprocessWarnings = flattenForExpand.Value.Warnings;

            var recovered = TryRecoverMacroTechniques(
                flattenForExpand.Value.Text, fnaPlatformMacros, sourceFileName,
                FxSourceMode.PreserveSm3, cancellationToken);
            if (recovered.IsFailure)
                return Fail(recovered.Error);          // a real -P / re-parse error — surface it

            FxParseResult? expandedParsed = recovered.Value;
            if (expandedParsed is { Techniques.Count: > 0 })
            {
                fxParsed = expandedParsed;
                recoveredPreprocessed = new PreprocessedSource(
                    expandedParsed.StrippedHlsl, fnaPlatformMacros.ToDxcFlags(), sourceFileName);
            }
        }

        if (fxParsed.Techniques.Count == 0)
            return Fail(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD0010",
                Message: "Effect source contains no techniques"));

        // Stage 2: preprocess (flatten #includes, prepend the FNA macro set) — unless the
        // fallback above already produced the expanded, technique-stripped source.
        PreprocessedSource preprocessed;
        if (recoveredPreprocessed is not null)
        {
            preprocessed = recoveredPreprocessed;
        }
        else
        {
            var preprocessResult = new Preprocessor().Flatten(
                fxParsed.StrippedHlsl,
                sourceFileName,
                fnaPlatformMacros,
                includeResolver,
                options.AdditionalIncludePaths);

            if (preprocessResult.IsFailure)
                return Fail(preprocessResult.Error);

            preprocessed = preprocessResult.Value;
            fnaPreprocessWarnings = preprocessed.Warnings;
        }

        // Per-stage source: vkd3d 1.17 rejects D3D9 stage-scoped register reservations
        // (register(vs, c0)) — rewrite them per compiling stage. Lazy: most effects
        // have none and many are PS-only.
        string? vsSource = null;
        string? psSource = null;

        // Stage 3: compile each pass's entry points to SM1–3 D3D bytecode and reflect
        // each blob's CTAB (the constant table MojoShader itself binds against).
        // Always vkd3d (never the d3dcompiler oracle); an injected host backend (the
        // WASM vkd3d backend) is the same vkd3d behind a different call mechanism.
        IDxbcShaderCompiler fnaCompiler = _dxbcCompilerFactory?.Invoke() ?? new Vkd3dShaderCompiler();
        var renderStateParser = new RenderStateParser();
        var shaders = new List<Fx2Shader>();
        var ctabs = new List<CtabTable>();
        var techniqueSources = new List<Fx2TechniqueSource>();

        // Recognized-profile validation (SD0013, Phase 48) for FNA. A compile target that
        // does not resolve (after macro expansion) to a real profile — 'compile A …', an
        // undefined '*_SHADERMODEL', or a profile-shaped typo like 'ps_9_9' — is rejected
        // here, matching mgfxc/fxc. A token that DOES resolve to a real profile then flows
        // into ResolveFnaProfile unchanged, which applies the MojoShader SM2–3 ceiling.
        // Expansion needs the C preprocessor only; DXC's -P is reused (lazy, macro-tokens
        // only) — it never compiles, so the FNA path stays vkd3d-only for codegen.
        var fnaDxcCompiler = new Lazy<IDxcShaderCompiler>(_dxcCompilerFactory);
        var fnaProfileExpansionCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (TechniqueInfo technique in fxParsed.Techniques)
            {
                foreach (PassInfo pass in technique.Passes)
                {
                    if (pass.VertexEntryPoint is not null)
                    {
                        // enforceStagePrefix: false — the FNA path's stage/profile prefix
                        // cross-check stays in ResolveFnaProfile (SD0300, FNA range), unchanged.
                        var v = ValidateCompileProfile(
                            pass.VertexProfile, pass.VertexProfileToken, pass.VertexProfileSpan, ShaderStage.Vertex,
                            fnaDxcCompiler, fnaPlatformMacros, preprocessed, sourceFileName,
                            fnaProfileExpansionCache, enforceStagePrefix: false, cancellationToken);
                        if (v is { } vErr)
                            return Fail(vErr);
                    }
                    if (pass.PixelEntryPoint is not null)
                    {
                        var p = ValidateCompileProfile(
                            pass.PixelProfile, pass.PixelProfileToken, pass.PixelProfileSpan, ShaderStage.Pixel,
                            fnaDxcCompiler, fnaPlatformMacros, preprocessed, sourceFileName,
                            fnaProfileExpansionCache, enforceStagePrefix: false, cancellationToken);
                        if (p is { } pErr)
                            return Fail(pErr);
                    }
                }
            }
        }
        finally
        {
            if (fnaDxcCompiler.IsValueCreated)
                (fnaDxcCompiler.Value as IDisposable)?.Dispose();
        }

        // The preprocessor's own findings (SD0008) plus verbatim vkd3d warnings for the
        // whole effect, deduped across entry points (same policy as the GL/DX path's
        // runWarnings) — returned on CompiledShader.Warnings.
        var fnaWarnings     = new List<ShaderError>(fnaPreprocessWarnings);
        var fnaSeenWarnings = new HashSet<(string File, int Line, int Column, string Code, string Message)>();

        foreach (TechniqueInfo technique in fxParsed.Techniques)
        {
            var passSources = new List<Fx2PassSource>();

            foreach (PassInfo pass in technique.Passes)
            {
                int vsIndex = -1;
                int psIndex = -1;

                if (pass.VertexEntryPoint is not null)
                {
                    vsSource ??= Sm3StageReservationRewriter.Rewrite(preprocessed.Text, ShaderStage.Vertex);
                    var compiled = CompileFnaStage(
                        fnaCompiler, vsSource, preprocessed.OriginalFilePath,
                        pass.VertexEntryPoint, pass.VertexProfile, ShaderStage.Vertex,
                        cancellationToken);
                    if (compiled.IsFailure)
                        return Fail(compiled.Error, fnaWarnings);

                    AccumulateWarnings(fnaWarnings, fnaSeenWarnings, compiled.Value.Warnings);
                    vsIndex = shaders.Count;
                    shaders.Add(compiled.Value.Shader);
                    ctabs.Add(compiled.Value.Ctab);
                }

                if (pass.PixelEntryPoint is not null)
                {
                    psSource ??= Sm3StageReservationRewriter.Rewrite(preprocessed.Text, ShaderStage.Pixel);
                    var compiled = CompileFnaStage(
                        fnaCompiler, psSource, preprocessed.OriginalFilePath,
                        pass.PixelEntryPoint, pass.PixelProfile, ShaderStage.Pixel,
                        cancellationToken);
                    if (compiled.IsFailure)
                        return Fail(compiled.Error, fnaWarnings);

                    AccumulateWarnings(fnaWarnings, fnaSeenWarnings, compiled.Value.Warnings);
                    psIndex = shaders.Count;
                    shaders.Add(compiled.Value.Shader);
                    ctabs.Add(compiled.Value.Ctab);
                }

                // Last assignment wins on a duplicated state key — fxc's semantics — instead
                // of ToDictionary's ArgumentException (no exception-as-control-flow).
                var renderStateKvp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (RenderStateEntry rs in pass.RenderStates)
                    renderStateKvp[rs.Key] = rs.Value;
                var renderStateResult = renderStateParser.Parse(renderStateKvp);
                if (renderStateResult.IsFailure)
                    return Fail(renderStateResult.Error, fnaWarnings);

                passSources.Add(new Fx2PassSource(
                    Name: pass.Name,
                    VertexShaderIndex: vsIndex,
                    PixelShaderIndex: psIndex,
                    RenderState: renderStateResult.Value));
            }

            techniqueSources.Add(new Fx2TechniqueSource(technique.Name, passSources));
        }

        // Stage 4: assemble the effect description and write the fx_2_0 container.
        var buildResult = Fx2EffectBuilder.Build(
            techniqueSources, shaders, ctabs, fxParsed.Samplers, sourceFileName);
        if (buildResult.IsFailure)
            return Fail(buildResult.Error, fnaWarnings);

        var writeResult = new Fx2EffectWriter().Write(buildResult.Value);
        if (writeResult.IsFailure)
            return Fail(writeResult.Error, fnaWarnings);

        return Result<CompiledShader, ShaderError[]>.Ok(
            new CompiledShader(PlatformTarget.Fna, writeResult.Value) { Warnings = fnaWarnings });
    }

    private static Result<(Fx2Shader Shader, CtabTable Ctab, IReadOnlyList<ShaderError> Warnings), ShaderError>
        CompileFnaStage(
            IDxbcShaderCompiler compiler,
            string source,
            string sourceFileName,
            string entryPoint,
            string? declaredProfile,
            ShaderStage stage,
            CancellationToken ct)
    {
        Result<string, ShaderError> profileResult =
            ResolveFnaProfile(declaredProfile, stage, sourceFileName);
        if (profileResult.IsFailure)
            return Result<(Fx2Shader, CtabTable, IReadOnlyList<ShaderError>), ShaderError>.Fail(profileResult.Error);

        var request = new D3DCompileRequest
        {
            HlslSource      = source,
            SourceFileName  = sourceFileName,
            EntryPoint      = entryPoint,
            Stage           = stage,
            // CompilerOptions.Debug is a deliberate no-op on the FNA path: vkd3d's d3dbc
            // target has no debug-info knob we pass, and fxc debug-style codegen trips
            // MojoShader strictness — Debug must never produce a .fxb MojoShader rejects.
            EmbedDebugInfo  = false,
            AllowWarnings   = false,
            ProfileOverride = profileResult.Value,
        };

        var compileResult = compiler.Compile(request, ct);
        if (compileResult.IsFailure)
            return Result<(Fx2Shader, CtabTable, IReadOnlyList<ShaderError>), ShaderError>.Fail(compileResult.Error);

        // Canonicalize the instruction forms MojoShader rejects but vkd3d emits
        // (texkill partial writemask; texld src0 swizzle below SM3) — found by the
        // rung-3/4 real-FNA harness. Semantics-preserving; no-op for clean blobs.
        var patchResult = D3d9BytecodePatcher.PatchForMojoShader(
            compileResult.Value.Bytes.ToArray(), sourceFileName);
        if (patchResult.IsFailure)
            return Result<(Fx2Shader, CtabTable, IReadOnlyList<ShaderError>), ShaderError>.Fail(patchResult.Error);

        byte[] bytecode = patchResult.Value;

        var ctabResult = CtabReader.Read(bytecode, sourceFileName);
        if (ctabResult.IsFailure)
            return Result<(Fx2Shader, CtabTable, IReadOnlyList<ShaderError>), ShaderError>.Fail(ctabResult.Error);

        return Result<(Fx2Shader, CtabTable, IReadOnlyList<ShaderError>), ShaderError>.Ok(
            // vkd3d populates its message buffer on success too — carry the verbatim
            // warnings up so the FNA path surfaces them like every other target.
            (new Fx2Shader(stage, bytecode), ctabResult.Value, compileResult.Value.Warnings));
    }

    /// <summary>
    /// FNA profile policy: a literal SM 2–3 profile in the pass's compile statement is
    /// honored as written (fxc fidelity), provided its vs_/ps_ prefix matches the stage
    /// it compiles; a literal SM4+ profile fails loudly (MojoShader's hard ceiling is
    /// vs_3_0/ps_3_0); a literal SM1 profile fails loudly too (vkd3d 1.17's ps_1_x
    /// backend has known instruction gaps and MojoShader's ps_1_x rules differ wholesale
    /// from SM2+ — never validated here, so refuse rather than risk silently-wrong
    /// output); anything else — no profile, or an unexpanded macro name like
    /// <c>PS_SHADERMODEL</c> (the pre-parser runs before macro expansion, and our
    /// preprocessor does not evaluate conditionals, so the macro's value is unknowable
    /// here) — defaults to the SM3 ceiling for the stage. Write a literal profile
    /// (<c>compile ps_2_0 …</c>) to pin codegen to a specific fxc baseline.
    /// </summary>
    internal static Result<string, ShaderError> ResolveFnaProfile(
        string? declaredProfile, ShaderStage stage, string sourceFileName)
    {
        string fallback = stage == ShaderStage.Vertex ? "vs_3_0" : "ps_3_0";

        // Anything that does not LOOK like a literal profile (vs_/ps_ + SM major digit) is
        // an unexpanded macro name (e.g. PS_SHADERMODEL) — default to the SM3 ceiling.
        // Deliberately a shape test, not a KnownProfiles lookup, so literal SM4+ variants
        // outside that list (ps_4_0_level_9_1, the MonoGame Reach profile) still classify
        // as SM4 and fail loudly below instead of silently downgrading.
        bool looksLikeProfile = declaredProfile is { Length: >= 4 }
            && (declaredProfile.StartsWith("vs_", StringComparison.Ordinal) ||
                declaredProfile.StartsWith("ps_", StringComparison.Ordinal))
            && declaredProfile[3] is >= '0' and <= '9';

        if (!looksLikeProfile)
            return Result<string, ShaderError>.Ok(fallback);

        // Cross-stage misuse: `VertexShader = compile ps_3_0 …` would compile a pixel
        // shader and bind it as the pass's vertex shader. fxc rejects this at compile
        // time; shipping it would break only inside the consumer's FNA at load/draw.
        bool isVertexProfile = declaredProfile!.StartsWith("vs_", StringComparison.Ordinal);
        if (isVertexProfile != (stage == ShaderStage.Vertex))
        {
            string want = stage == ShaderStage.Vertex ? "vs_2_0/vs_3_0" : "ps_2_0/ps_3_0";
            return Result<string, ShaderError>.Fail(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD0300",
                Message: $"Pass compiles its {stage} shader with profile '{declaredProfile}' — " +
                         $"the profile's stage prefix must match the shader it compiles (use {want})"));
        }

        if (declaredProfile[3] > '3')
        {
            return Result<string, ShaderError>.Fail(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD0300",
                Message: $"Pass compiles with profile '{declaredProfile}', but the FNA target " +
                         "(MojoShader) supports Shader Model 2–3 only — use vs_2_0/vs_3_0 or " +
                         "ps_2_0/ps_3_0 in the technique's compile statements"));
        }

        if (declaredProfile[3] < '2')
        {
            return Result<string, ShaderError>.Fail(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD0300",
                Message: $"Pass compiles with profile '{declaredProfile}', but the FNA target " +
                         "supports Shader Model 2–3 here: vkd3d 1.17's SM1 backend has known " +
                         "instruction gaps and the SM1 output path has never been validated " +
                         "against real FNA — use vs_2_0/ps_2_0 (FNA's own guidance: ps_2_0 is " +
                         "the safest profile) or vs_3_0/ps_3_0"));
        }

        // Literal SM 2–3 profile: honored as written (fxc fidelity). An unusual-but-shaped
        // token (e.g. ps_3_9) passes through and vkd3d rejects it with its own diagnostic.
        return Result<string, ShaderError>.Ok(declaredProfile);
    }

    /// <summary>
    /// Appends <paramref name="incoming"/> diagnostics to <paramref name="accumulated"/>,
    /// skipping entries already seen. VS and PS entry points compile the same
    /// preprocessed source, so a source-level compiler warning re-surfaces once per
    /// entry-point compile — the consumer should read it once.
    /// </summary>
    private static void AccumulateWarnings(
        List<ShaderError> accumulated,
        HashSet<(string File, int Line, int Column, string Code, string Message)> seen,
        IReadOnlyList<ShaderError> incoming)
    {
        foreach (ShaderError w in incoming)
        {
            if (seen.Add((w.File, w.Line, w.Column, w.Code, w.Message)))
                accumulated.Add(w);
        }
    }

    private static (Result<byte[], ShaderError> Blob, ReadOnlyMemory<byte> DxilBlob, ReadOnlyMemory<byte> SpirvBlob, IReadOnlyList<MgfxVertexAttributeInfo> Attributes, IReadOnlyList<MonoGameGlslUniform> Uniforms, IReadOnlyList<ShaderError> Warnings)
        CompileEntryPoint(
            Lazy<IDxcShaderCompiler> dxcCompiler,
            IDxbcShaderCompiler dxbcCompiler,
            ISpirvToGlslTranspiler glslTranspiler,
            PreprocessedSource preprocessed,
            string entryPoint,
            ShaderStage stage,
            PlatformTarget platform,
            DxcCompileOptions compileOptions,
            bool applyMonoGameGlsl,
            bool reflectFromSpirv,
            CancellationToken ct)
    {
        IReadOnlyList<MgfxVertexAttributeInfo> noAttributes = Array.Empty<MgfxVertexAttributeInfo>();
        IReadOnlyList<MonoGameGlslUniform>     noUniforms   = Array.Empty<MonoGameGlslUniform>();
        IReadOnlyList<ShaderError>             noWarnings   = Array.Empty<ShaderError>();

        if (platform == PlatformTarget.DirectX)
        {
            // DX11: compile SM5 DXBC via d3dcompiler_47 (the fxc oracle). DXC's
            // DirectX target only emits SM6 DXIL, which MonoGame's DX11 runtime
            // rejects. The DXBC bytes ARE the shader payload AND the reflection
            // source (carried in the dxilBlob slot — reflected as DXBC upstream).
            // DX binds vertex inputs via the DXBC input signature, not a GL attribute
            // table — so no attributes here.
            var dxbcRequest = new D3DCompileRequest
            {
                HlslSource     = preprocessed.Text,
                SourceFileName = preprocessed.OriginalFilePath,
                EntryPoint     = entryPoint,
                Stage          = stage,
                EmbedDebugInfo = compileOptions.EmbedDebugInfo,
                AllowWarnings  = compileOptions.AllowWarnings,
            };

            var dxbcResult = dxbcCompiler.Compile(dxbcRequest, ct);
            if (dxbcResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(dxbcResult.Error), default, default, noAttributes, noUniforms, noWarnings);

            ReadOnlyMemory<byte> dxbc = dxbcResult.Value.Bytes;
            return (Result<byte[], ShaderError>.Ok(dxbc.ToArray()), dxbc, default, noAttributes, noUniforms, dxbcResult.Value.Warnings);
        }

        if (platform == PlatformTarget.OpenGL)
        {
            // Desktop default reflects from DXIL, so compile a DirectX-target blob solely
            // for reflection. The WASM path (reflectFromSpirv) reflects the SPIR-V blob
            // directly, so this DXIL compile is skipped entirely — DxilBlob stays default
            // and the reflection loop gates on the SPIR-V blob instead.
            ReadOnlyMemory<byte> dxilBlob = default;
            if (!reflectFromSpirv)
            {
                // Compile with DirectX target to get DXIL for reflection.
                var dxilRequest = new DxcCompileRequest
                {
                    HlslSource     = preprocessed.Text,
                    SourceFileName = preprocessed.OriginalFilePath,
                    EntryPoint     = entryPoint,
                    Stage          = stage,
                    Platform       = PlatformTarget.DirectX,
                    // AllowWarnings = true so this reflection-only compile never fails on
                    // warnings-as-errors — the OpenGL compile below is the authoritative failure
                    // signal. SkipValidation = true (-Vd) because this blob is discarded after
                    // reflection, never shipped: a hosted CI runner's own preinstalled Windows
                    // SDK can put a dxil.dll on the native search path that is version-skewed
                    // against the dxcompiler.dll this library is pinned to, and DXC's validator
                    // rejects the (correctly-compiled) module with a "DXIL container mismatch"
                    // error purely from that skew. Skipping validation on this reflection-only
                    // compile sidesteps the skew entirely; the SHIPPED SPIR-V/DXBC/DXIL from
                    // every other compile in this file is still fully validated.
                    Options        = new DxcCompileOptions
                    {
                        EmbedDebugInfo  = compileOptions.EmbedDebugInfo,
                        AllowWarnings   = true,
                        SkipValidation  = true,
                    },
                };

                var dxilResult = dxcCompiler.Value.Compile(dxilRequest, ct);
                if (dxilResult.IsFailure)
                    return (Result<byte[], ShaderError>.Fail(dxilResult.Error), default, default, noAttributes, noUniforms, noWarnings);

                dxilBlob = dxilResult.Value.Bytes;
            }

            // Compile with OpenGL target to get SPIR-V for transpilation.
            var spirvRequest = new DxcCompileRequest
            {
                HlslSource     = preprocessed.Text,
                SourceFileName = preprocessed.OriginalFilePath,
                EntryPoint     = entryPoint,
                Stage          = stage,
                Platform       = PlatformTarget.OpenGL,
                Options        = compileOptions,
            };

            var spirvResult = dxcCompiler.Value.Compile(spirvRequest, ct);
            if (spirvResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(spirvResult.Error), default, default, noAttributes, noUniforms, noWarnings);

            // Transpile SPIR-V → GLSL.
            var transpileResult = glslTranspiler.Transpile(spirvResult.Value.Bytes, ct);
            if (transpileResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(transpileResult.Error), default, default, noAttributes, noUniforms, noWarnings);

            // Rewrite SPIRV-Cross GLSL into MonoGame/MojoShader-compatible GLSL so it
            // links with MonoGame's GL runtime. Per-stage (Phase 28): the PIXEL stage
            // gets varying reads, the ps_oC0 fragment-output alias, ps_sN samplers, and
            // ps_uniforms_vec4; the VERTEX stage gets attribute inputs, varying writes,
            // gl_Position, and vs_uniforms_vec4 (its attribute table is returned for the
            // .mgfx shader record). The rewriter fails loudly (MonoGameGlslRewrite-
            // Exception) on constructs that can't be lowered to a profile-agnostic GLSL
            // payload (e.g. LOD/proj/grad sampling, an unmodelled vertex semantic) —
            // surface that as a compile error rather than letting it crash.
            string glslText;
            IReadOnlyList<MgfxVertexAttributeInfo> attributes = noAttributes;
            IReadOnlyList<MonoGameGlslUniform>     uniforms   = noUniforms;
            if (applyMonoGameGlsl)
            {
                try
                {
                    MonoGameGlslResult rewritten = MonoGameGlslRewriter.Rewrite(transpileResult.Value.Text, stage);
                    glslText = rewritten.Glsl;
                    // The shader's uniform register layout — the pipeline builds the
                    // per-shader {vs,ps}_uniforms_vec4 cbuffer record from THIS, so
                    // the .mgfx offsets and the GLSL indices share one allocation
                    // (Phase 43 F4/F5/F6).
                    uniforms = rewritten.Uniforms;
                    if (stage == ShaderStage.Vertex && rewritten.Attributes.Count > 0)
                    {
                        // Map the rewriter's discovered attributes (vs_v{k} + usage/index)
                        // to the .mgfx attribute-table record. Location is 0 for every
                        // attribute — matching mgfxc's goldens; MonoGame's GL runtime
                        // binds by the (usage,index) pair and the attribute NAME, not by
                        // this field.
                        attributes = rewritten.Attributes
                            .Select(a => new MgfxVertexAttributeInfo(
                                Name:     a.Name,
                                Usage:    a.Usage,
                                Index:    a.Index,
                                Location: 0))
                            .ToList();
                    }
                }
                catch (MonoGameGlslRewriteException ex)
                {
                    return (Result<byte[], ShaderError>.Fail(new ShaderError(
                        File:    preprocessed.OriginalFilePath,
                        Line:    0,
                        Column:  0,
                        Code:    "SD0210",
                        Message: ex.Message)), default, default, noAttributes, noUniforms, noWarnings);
                }
            }
            else
            {
                glslText = transpileResult.Value.Text;
            }
            byte[] glslBytes = Encoding.UTF8.GetBytes(glslText);

            return (
                Result<byte[], ShaderError>.Ok(glslBytes),
                dxilBlob,
                spirvResult.Value.Bytes,
                attributes,
                uniforms,
                // The SPIR-V compile's warnings only — the DXIL-for-reflection compile
                // sees the same source, so its warnings would be duplicates.
                spirvResult.Value.Warnings);
        }
        else
        {
            // Vulkan (and any future DX12/KNI SM6 profile): single DXC compile.
            // DX11 no longer reaches here — it takes the DXBC oracle branch above.
            //
            // Vulkan-only: real mgfxc renames every entry point to literally "main"
            // before compiling (VulkanShaderProfile.CreateShader). Confirmed by a
            // minimal repro against real DesktopVK (2026-07-18): the ONLY byte
            // difference between a real-mgfxc-compiled SPIR-V module and ShadowDusk's
            // (once the container/binding fixes above are applied) was the
            // OpEntryPoint name — "main" vs the shader's real name (e.g. "MainPS") —
            // and shipping the real name crashes MonoGame's native Vulkan pipeline
            // creation (it evidently expects "main" unconditionally). GL/DX/FNA keep
            // the shader's real entry name; this rename is Vulkan-only.
            string hlslSource = preprocessed.Text;
            string effectiveEntryPoint = entryPoint;
            if (platform == PlatformTarget.Vulkan)
            {
                hlslSource = RenameEntryPointToMain(hlslSource, entryPoint);
                effectiveEntryPoint = "main";
            }

            var request = new DxcCompileRequest
            {
                HlslSource     = hlslSource,
                SourceFileName = preprocessed.OriginalFilePath,
                EntryPoint     = effectiveEntryPoint,
                Stage          = stage,
                Platform       = platform,
                Options        = compileOptions,
            };

            var result = dxcCompiler.Value.Compile(request, ct);
            if (result.IsFailure)
                return (Result<byte[], ShaderError>.Fail(result.Error), default, default, noAttributes, noUniforms, noWarnings);

            ReadOnlyMemory<byte> blob      = result.Value.Bytes;
            // DirectX12 ships raw SM6 DXIL directly (no transpile) and reflects from it via
            // the same DXIL reflection path DirectX11's companion compile already uses (Phase
            // 54) — so it belongs on the dxilBlob side, not the spirvBlob side, alongside the
            // (dead, DX11 no longer reaches here) DirectX case above.
            bool isDxilOutput = platform is PlatformTarget.DirectX or PlatformTarget.DirectX12;
            ReadOnlyMemory<byte> dxilBlob  = isDxilOutput ? blob : default;
            ReadOnlyMemory<byte> spirvBlob = isDxilOutput ? default : blob;

            // Vulkan vertex shaders carry an attribute table in the .mgfx shader record, built
            // from the SPIR-V input semantics exactly as mgfxc builds it (issue #145, S1).
            //
            // DirectX12 vertex shaders MUST carry this table too — it is NOT cosmetic on the new
            // native backend. MonoGame's managed VertexInputLayout.GenerateInputElements (shared
            // by every native backend) iterates this exact table to build the D3D12 input layout;
            // an empty table silently produces a zero-element input layout (its "missing input"
            // check only runs inside the per-attribute loop, so it never fires when the table is
            // empty), which then fails CreateGraphicsPipelineState — called lazily right before
            // the first Draw — with E_INVALIDARG. Confirmed root cause by reading MonoGame's real
            // v3.8.5 source directly (Phase 54 follow-up, 2026-07-23): VertexInputLayout.Native.cs
            // and Shader.Native.cs's GetOrCreateLayout.
            IReadOnlyList<MgfxVertexAttributeInfo> vertexAttributes;
            // SD0104 (bug-hunt 2026-07-27 N5): mgfxc prints a warning when an input semantic
            // it does not recognise falls through to the TextureCoordinate default, and a
            // drop-in replacement has to as well — a typo'd semantic otherwise silently mints
            // a phantom TEXCOORD attribute the consumer's vertex declaration must supply.
            // The fallback VALUE is unchanged (mgfxc defaults the same way); only the
            // diagnostic is new, and warnings never gate output.
            IReadOnlyList<ShaderError> attributeWarnings = noWarnings;
            if (stage != ShaderStage.Vertex)
            {
                vertexAttributes = noAttributes;
            }
            else
            {
                // A reflection FAILURE here is a compile-time error (bug-hunt 2026-07-27
                // M11): the old empty-table fallback shipped exactly the delayed
                // E_INVALIDARG-at-first-Draw crash described above, with no pointer back
                // to the shader. A shader that genuinely declares no vertex inputs still
                // gets an empty table (Ok) — a zero-element layout is valid when nothing
                // is consumed.
                Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError> attrResult;
                if (platform == PlatformTarget.Vulkan)
                    attrResult = SpirvVertexInputReflector.Read(spirvBlob, out attributeWarnings);
                else if (platform == PlatformTarget.DirectX12)
                    attrResult = DxilVertexInputReflector.Read(dxilBlob, new DxilReflectionExtractor(), out attributeWarnings);
                else
                    attrResult = Result<IReadOnlyList<MgfxVertexAttributeInfo>, ShaderError>.Ok(noAttributes);

                if (attrResult.IsFailure)
                    return (Result<byte[], ShaderError>.Fail(attrResult.Error), default, default, noAttributes, noUniforms, noWarnings);
                vertexAttributes = attrResult.Value;
            }

            IReadOnlyList<ShaderError> stageWarnings = result.Value.Warnings;
            if (attributeWarnings.Count > 0)
            {
                // The reflectors have no source path; stamp the one the compile was given so
                // an MGCB build with many effects can tell WHICH effect warned (the same
                // reason the GL portability lint carries a file and no line).
                var merged = new List<ShaderError>(stageWarnings.Count + attributeWarnings.Count);
                merged.AddRange(stageWarnings);
                foreach (ShaderError w in attributeWarnings)
                    merged.Add(w with { File = preprocessed.OriginalFilePath });
                stageWarnings = merged;
            }

            return (Result<byte[], ShaderError>.Ok(blob.ToArray()), dxilBlob, spirvBlob, vertexAttributes, noUniforms, stageWarnings);
        }
    }

    /// <summary>
    /// Renames the top-level function definition named <paramref name="entryPoint"/> to
    /// <c>main</c> — mirrors mgfxc's own <c>Regex.Replace(fileContent, entryPoint, "main")</c>
    /// (<c>ShaderProfile.Vulkan.cs</c>). Matches only <c>&lt;whitespace&gt;entryPoint(</c>
    /// (a function definition or call), never a substring inside another identifier.
    /// </summary>
    private static string RenameEntryPointToMain(string hlsl, string entryPoint) =>
        Regex.Replace(hlsl, $@"(?<=\s){Regex.Escape(entryPoint)}(?=\s*\()", "main");

    /// <summary>
    /// mgfxc's <c>ConstantBufferData.SameAs</c> equivalence for the GL per-shader
    /// record dedup: same name, same size, and the same parameter index/offset
    /// sequences. (mgfxc compares the parameter shapes; here the indices point into
    /// the single global parameter list, so index equality subsumes shape equality.)
    /// </summary>
    private static bool SameCbufferRecord(ConstantBufferInfo a, ConstantBufferInfo b) =>
        a.Name == b.Name &&
        a.SizeInBytes == b.SizeInBytes &&
        a.ParameterIndices.SequenceEqual(b.ParameterIndices) &&
        a.ParameterOffsets.SequenceEqual(b.ParameterOffsets);

    // NON-GL targets only (DirectX / Vulkan): one record per reflected cbuffer with
    // the HLSL byte packing. The GL records are built per shader from the GLSL
    // rewriter's register layout in Run() (Phase 43 F4/F5).
    private static IReadOnlyList<ConstantBufferInfo> BuildConstantBufferInfoList(
        IReadOnlyList<ConstantBufferReflection> constantBuffers,
        IReadOnlyList<ParameterReflection> parameters,
        bool directX)
    {
        var result = new List<ConstantBufferInfo>(constantBuffers.Count);

        foreach (ConstantBufferReflection cb in constantBuffers)
        {
            var paramIndices = new List<int>();
            var paramOffsets = new List<ushort>();

            foreach (VariableReflection variable in cb.Variables)
            {
                for (int idx = 0; idx < parameters.Count; idx++)
                {
                    if (parameters[idx].Name == variable.Name)
                    {
                        paramIndices.Add(idx);
                        paramOffsets.Add((ushort)variable.StartOffset);
                        break;
                    }
                }
            }

            // DX cbuffer record carries an empty name (MonoGame's DX11 runtime binds
            // the cbuffer by slot, not by name).
            string cbName = directX ? string.Empty : cb.Name;

            result.Add(new ConstantBufferInfo(
                Name:             cbName,
                SizeInBytes:      cb.SizeBytes,
                ParameterIndices: paramIndices,
                ParameterOffsets: paramOffsets));
        }

        return result;
    }

    // MonoGame's per-sampler SamplerType byte (read by Shader.cs as
    // (SamplerType)reader.ReadByte()): Sampler2D=0, SamplerCube=1, SamplerVolume(3D)=2,
    // Sampler1D=3. Verified against an mgfxc cube golden — see PHASE34-INVESTIGATION.md.
    // An unknown/unmatched dimension falls back to 2D (0), the prior behaviour.
    private static byte SamplerTypeByte(TextureDimension? dimension) => dimension switch
    {
        TextureDimension.TextureCube => 1,
        TextureDimension.Texture3D   => 2,
        TextureDimension.Texture1D   => 3,
        _                            => 0, // Texture2D / Unknown / null
    };

    private static int IndexOfParam(IReadOnlyList<ParameterReflection> parameters, string name)
    {
        for (int i = 0; i < parameters.Count; i++)
            if (parameters[i].Name == name)
                return i;
        return -1;
    }

    /// <summary>
    /// The OFFSET BRIDGE for the GL cbuffer/parameter join (B10), used ONLY when the
    /// primary name match fails — which happens exactly when SPIRV-Cross renamed a
    /// free uniform whose name collides with a GLSL reserved word (e.g. <c>noise</c> →
    /// <c>_noise</c>). The GL uniform's byte offset is <c>baseRegister * 16</c>; the
    /// reflected cbuffer variable at that <see cref="VariableReflection.StartOffset"/>
    /// carries the ORIGINAL HLSL name, which recovers the effect-parameter index
    /// without ever trusting the SPIRV-Cross-emitted spelling. Returns the parameter
    /// index, or -1 if it cannot be resolved unambiguously (caller then keeps the loud
    /// <c>SD0012</c> guard).
    ///
    /// <para><b>Single-cbuffer only, by design (correctness over coverage).</b> The
    /// reserved-word case is always a free global, which DXC reflects as a single
    /// <c>$Globals</c> cbuffer — there a variable's effective byte offset is exactly
    /// its <c>StartOffset</c>, so <c>StartOffset == baseRegister * 16</c> holds
    /// directly. With MULTIPLE cbuffers the rewriter merges them into one packed
    /// register space in GLSL-declaration order, which is NOT guaranteed to match the
    /// reflection's cbuffer order, so the per-cbuffer base offset cannot be reliably
    /// reconstructed here. Rather than risk MIS-mapping to the wrong parameter, this
    /// declines (returns -1) for the multi-cbuffer case and lets <c>SD0012</c> stand.</para>
    /// </summary>
    private static int IndexOfParamByRegister(
        IReadOnlyList<ConstantBufferReflection> constantBuffers,
        IReadOnlyList<ParameterReflection> parameters,
        int baseRegister)
    {
        // Only the unambiguous single-cbuffer ($Globals) case is bridged — see the
        // remarks above for why the multi-cbuffer merge order is not reconstructed.
        if (constantBuffers.Count != 1)
            return -1;

        int targetOffset = baseRegister * 16;
        foreach (VariableReflection variable in constantBuffers[0].Variables)
        {
            if (variable.StartOffset == targetOffset)
                return IndexOfParam(parameters, variable.Name);
        }

        return -1;
    }

    private static int IndexOfCbuffer(IReadOnlyList<ConstantBufferReflection> cbuffers, string name)
    {
        for (int i = 0; i < cbuffers.Count; i++)
            if (cbuffers[i].Name == name)
                return i;
        return -1;
    }

    private static IReadOnlyList<EffectParameterInfo> BuildEffectParameterInfoList(
        IReadOnlyList<ParameterReflection> parameters)
    {
        var result = new List<EffectParameterInfo>(parameters.Count);

        foreach (ParameterReflection param in parameters)
        {
            var annotations = param.Annotations?
                .Select(MapAnnotation)
                .ToList() ?? new List<AnnotationInfo>();

            result.Add(new EffectParameterInfo(
                Class: (byte)param.Class,
                Type: (byte)param.Type,
                Name: param.Name,
                Semantic: param.Semantic,
                Annotations: annotations,
                RowCount: (byte)param.Rows,
                ColumnCount: (byte)param.Columns,
                Members: Array.Empty<EffectParameterInfo>(),
                Elements: BuildElementParameters(param)));
        }

        return result;
    }

    /// <summary>
    /// Phase 43 F6: the element sub-parameter records for an ARRAY parameter, on
    /// EVERY target. MonoGame's <c>Effect.ReadParameters</c> reads array elements as
    /// a recursive parameter collection, and <c>EffectParameter.SetValue</c> for an
    /// array (or <c>Elements[i]</c> indexing) requires them — with <c>Elements</c>
    /// empty, an array parameter was un-settable beyond element 0 even on DirectX.
    /// Shape mirrors mgfxc (<c>ConstantBufferData.GetParameterFromSymbol</c>): each
    /// element carries an EMPTY name/semantic, the parent's class/type/rows/columns,
    /// no annotations, and a zero default-value blob (written by the leaf data rule).
    /// </summary>
    private static IReadOnlyList<EffectParameterInfo> BuildElementParameters(ParameterReflection param)
    {
        if (param.Elements <= 1)
            return Array.Empty<EffectParameterInfo>();

        var elements = new List<EffectParameterInfo>(param.Elements);
        for (int i = 0; i < param.Elements; i++)
        {
            elements.Add(new EffectParameterInfo(
                Class: (byte)param.Class,
                Type: (byte)param.Type,
                Name: "",
                Semantic: "",
                Annotations: Array.Empty<AnnotationInfo>(),
                RowCount: (byte)param.Rows,
                ColumnCount: (byte)param.Columns,
                Members: Array.Empty<EffectParameterInfo>(),
                Elements: Array.Empty<EffectParameterInfo>()));
        }
        return elements;
    }

    // Annotation Type tags are the MGFX EffectParameterType ordinals the reader uses to
    // pick the value field: Int32 = 2, Single = 3, String = 4.
    private const byte AnnotationTypeInt32  = 2;
    private const byte AnnotationTypeSingle = 3;
    private const byte AnnotationTypeString = 4;

    private static AnnotationInfo MapAnnotation(AnnotationReflection annotation)
    {
        if (float.TryParse(
                annotation.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float floatVal))
        {
            return new AnnotationInfo(
                Name: annotation.Name,
                Type: AnnotationTypeSingle,
                StringValue: null,
                FloatValue: floatVal,
                IntValue: null,
                BoolValue: null);
        }

        if (int.TryParse(annotation.Value, out int intVal))
        {
            return new AnnotationInfo(
                Name: annotation.Name,
                Type: AnnotationTypeInt32,
                StringValue: null,
                FloatValue: null,
                IntValue: intVal,
                BoolValue: null);
        }

        return new AnnotationInfo(
            Name: annotation.Name,
            Type: AnnotationTypeString,
            StringValue: annotation.Value,
            FloatValue: null,
            IntValue: null,
            BoolValue: null);
    }

    private static IReadOnlyList<AnnotationInfo> MapAnnotationEntries(
        IReadOnlyList<AnnotationEntry> entries)
    {
        var result = new List<AnnotationInfo>(entries.Count);
        foreach (AnnotationEntry entry in entries)
        {
            result.Add(new AnnotationInfo(
                Name: entry.Name,
                Type: AnnotationTypeString,
                StringValue: entry.Value,
                FloatValue: null,
                IntValue: null,
                BoolValue: null));
        }
        return result;
    }

    private static Result<CompiledShader, ShaderError[]> Fail(ShaderError error) =>
        Result<CompiledShader, ShaderError[]>.Fail(new ShaderError[] { error });

    // Fails with the fatal error PLUS any warnings already accumulated from earlier,
    // successfully-compiled stages in the SAME effect (e.g. an earlier technique's
    // pass compiled fine but with a warning; a later technique's pass then hard-
    // failed) — otherwise those warnings would just be dropped on the floor. The
    // error stays first (the actionable line); accumulated warnings ride along after
    // it in the same array a caller already iterates severity-aware (CLI/MGCB
    // formatting, ShaderValidationReport). Skips the allocation entirely when there
    // are no accumulated warnings, so the ordinary single-error path is unchanged.
    private static Result<CompiledShader, ShaderError[]> Fail(
        ShaderError error, IReadOnlyList<ShaderError> accumulatedWarnings)
    {
        if (accumulatedWarnings.Count == 0)
            return Fail(error);

        var all = new ShaderError[accumulatedWarnings.Count + 1];
        all[0] = error;
        for (int i = 0; i < accumulatedWarnings.Count; i++)
            all[i + 1] = accumulatedWarnings[i];
        return Result<CompiledShader, ShaderError[]>.Fail(all);
    }

    // Maps an FX9 pre-parser error to the pipeline's ShaderError, formatting the FX
    // diagnostic code as the four-digit "FXnnnn" string. Shared by every FxPreParser
    // call site so the mapping stays identical.
    private static ShaderError FromFxParseError(FxParseError err) =>
        new(
            File: err.SourceFile,
            Line: err.Line,
            Column: err.Column,
            Code: $"FX{(int)err.Code:D4}",
            Message: err.Message);
}
