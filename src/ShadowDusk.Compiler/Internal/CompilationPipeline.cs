#nullable enable

using System.Text;
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

        MacroSet macros = PlatformMacros.For(options.Target);

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
        EffectContainer effectiveContainer = options.Profile?.Container ?? options.Container;
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
        bool reflectFromSpirv = _reflectorFactory is not null && options.Target == PlatformTarget.OpenGL;

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

        // Per-shader (by blob index) GL uniform register layout returned by the
        // MonoGameGlslRewriter — the allocation the emitted GLSL actually indexes.
        // The GL .mgfx cbuffer records are built from THIS (one record per shader,
        // mgfxc's model — Phase 43 F4/F5), never from cross-stage name dedup.
        var shaderUniformLayouts = new Dictionary<int, IReadOnlyList<MonoGameGlslUniform>>();

        var compileOptions = new DxcCompileOptions
        {
            EmbedDebugInfo = options.Debug,
            AllowWarnings  = false,
        };

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
                        preprocessed,
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
                        return Fail(compileOutput.Blob.Error);

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
                        preprocessed,
                        pass.PixelEntryPoint,
                        ShaderStage.Pixel,
                        options.Target,
                        compileOptions,
                        applyMonoGameGlsl: monoGameGl,
                        reflectFromSpirv: reflectFromSpirv,
                        cancellationToken);

                    if (compileOutput.Blob.IsFailure)
                        return Fail(compileOutput.Blob.Error);

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
                        Result<ReflectedEffect, ShaderError> baseResult =
                            _reflectorFactory!().Reflect(spirvBlob);

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

                        reflectResult = reflectionPipeline.Reflect(reflectionInput, cancellationToken);
                    }

                    if (reflectResult.IsFailure)
                        return Fail(reflectResult.Error);

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
                }

                // Last assignment wins on a duplicated state key — fxc's semantics — instead
                // of ToDictionary's ArgumentException (no exception-as-control-flow).
                var renderStateKvp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (RenderStateEntry rs in pass.RenderStates)
                    renderStateKvp[rs.Key] = rs.Value;
                var renderStateResult = renderStateParser.Parse(renderStateKvp);
                if (renderStateResult.IsFailure)
                    return Fail(renderStateResult.Error);

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
                    int paramIndex = IndexOfParam(allParameters, u.Name);
                    if (paramIndex < 0)
                        return Fail(new ShaderError(
                            File: sourceFileName,
                            Line: 0,
                            Column: 0,
                            Code: "SD0012",
                            Message: $"internal: GL uniform '{u.Name}' (shader #{i}) has no " +
                                     "matching effect parameter — the GLSL uniform layout and " +
                                     "the reflected parameter list diverged"));
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
                return Fail(resolved.Error);
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
                foreach (SamplerReflection samp in samplerRefs)
                {
                    int slot = samp.BindSlot;
                    // Pair the sampler with its texture (by slot, then by name fallback)
                    // so the sampler-type byte can carry the texture's DIMENSION. The
                    // dimension is reflected identically by BOTH the DXIL oracle and the
                    // pure-managed SpirvReflector, so this stays byte-transparent across
                    // the desktop and WASM reflection paths.
                    TextureReflection? matchedTex =
                        textureRefs.FirstOrDefault(t => t.BindSlot == slot)
                        ?? (samp.TextureName is not null
                                ? textureRefs.FirstOrDefault(t => t.Name == samp.TextureName)
                                : null)
                        ?? textureRefs.FirstOrDefault();
                    string? texName = samp.TextureName ?? matchedTex?.Name;
                    int paramIndex = texName is null ? 0 : Math.Max(0, IndexOfParam(allParameters, texName));
                    samplers.Add(new MgfxSamplerInfo(
                        // MonoGame SamplerType byte: 2D=0, Cube=1, Volume(3D)=2 (1D=3).
                        // Critical for binding — cube/3D won't bind at runtime if left 0.
                        Type:        SamplerTypeByte(matchedTex?.Dimension),
                        TextureSlot: (byte)slot,
                        SamplerSlot: (byte)slot,
                        // DX binds samplers via the DXBC resource table, not by GLSL
                        // uniform name, so the sampler name is empty for DirectX.
                        Name:        directX ? string.Empty : $"ps_s{slot}",
                        Parameter:   paramIndex,
                        // The reflected sampler name is the .fx sampler identifier —
                        // the key the parsed sampler_state members were resolved under.
                        State:       samplerStateByName.GetValueOrDefault(samp.Name)));
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

            compiledShaderBlobs[i] = compiledShaderBlobs[i] with
            {
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
            KnifxBackend knifxBackend = options.Target switch
            {
                PlatformTarget.DirectX => KnifxBackend.DirectX11,
                _ => KnifxBackend.OpenGL,
            };
            var knifxResult = new KnifxWriter().Write(ir, new KnifxWriterOptions(knifxBackend));
            if (knifxResult.IsFailure)
                return Fail(knifxResult.Error);
            return Result<CompiledShader, ShaderError[]>.Ok(
                new CompiledShader(options.Target, knifxResult.Value));
        }

        // Stage 6: MGFX binary writer.
        MgfxProfile mgfxProfile = options.Target switch
        {
            PlatformTarget.DirectX => MgfxProfile.DirectX11,
            PlatformTarget.OpenGL  => MgfxProfile.OpenGL,
            PlatformTarget.Vulkan  => MgfxProfile.Vulkan,
            _ => MgfxProfile.OpenGL,
        };

        // Guard the byte cast (like the writer's SD0020/SD0021 size guards): a
        // MgfxVersion outside 0..255 would silently truncate into a bogus header.
        if (effectiveMgfxVersion is < byte.MinValue or > byte.MaxValue)
            return Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "SD0023",
                Message: $"MgfxVersion {effectiveMgfxVersion} is outside the MGFX header's byte range (0-255)"));

        var mgfxWriter  = new MgfxWriter();
        var writeResult = mgfxWriter.Write(ir, new MgfxWriterOptions(
            Profile: mgfxProfile,
            MgfxVersion: (byte)effectiveMgfxVersion));

        if (writeResult.IsFailure)
            return Fail(writeResult.Error);

        byte[] mgfxBytes = writeResult.Value;

        return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, mgfxBytes));
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

        // NOTE: the GL/DX macro-technique fallback (Phase 41 — DXC -P expand + re-parse for
        // effects whose techniques come only from the TECHNIQUE(...) macro) is intentionally
        // NOT applied here. FNA uses the SM1–3 vkd3d path with no DXC -P step; a tracked
        // follow-up if a stock effect ever needs the FNA target.
        if (fxParsed.Techniques.Count == 0)
            return Fail(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "SD0010",
                Message: "Effect source contains no techniques"));

        // Stage 2: preprocess (flatten #includes, prepend the FNA macro set).
        IIncludeResolver includeResolver = options.IncludeResolver ?? new FileSystemIncludeResolver();
        var preprocessResult = new Preprocessor().Flatten(
            fxParsed.StrippedHlsl,
            sourceFileName,
            PlatformMacros.For(PlatformTarget.Fna),
            includeResolver,
            options.AdditionalIncludePaths);

        if (preprocessResult.IsFailure)
            return Fail(preprocessResult.Error);

        PreprocessedSource preprocessed = preprocessResult.Value;

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
                        return Fail(compiled.Error);

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
                        return Fail(compiled.Error);

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
                    return Fail(renderStateResult.Error);

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
            return Fail(buildResult.Error);

        var writeResult = new Fx2EffectWriter().Write(buildResult.Value);
        if (writeResult.IsFailure)
            return Fail(writeResult.Error);

        return Result<CompiledShader, ShaderError[]>.Ok(
            new CompiledShader(PlatformTarget.Fna, writeResult.Value));
    }

    private static Result<(Fx2Shader Shader, CtabTable Ctab), ShaderError>
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
            return Result<(Fx2Shader, CtabTable), ShaderError>.Fail(profileResult.Error);

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
            return Result<(Fx2Shader, CtabTable), ShaderError>.Fail(compileResult.Error);

        // Canonicalize the instruction forms MojoShader rejects but vkd3d emits
        // (texkill partial writemask; texld src0 swizzle below SM3) — found by the
        // rung-3/4 real-FNA harness. Semantics-preserving; no-op for clean blobs.
        var patchResult = D3d9BytecodePatcher.PatchForMojoShader(
            compileResult.Value.Bytes.ToArray(), sourceFileName);
        if (patchResult.IsFailure)
            return Result<(Fx2Shader, CtabTable), ShaderError>.Fail(patchResult.Error);

        byte[] bytecode = patchResult.Value;

        var ctabResult = CtabReader.Read(bytecode, sourceFileName);
        if (ctabResult.IsFailure)
            return Result<(Fx2Shader, CtabTable), ShaderError>.Fail(ctabResult.Error);

        return Result<(Fx2Shader, CtabTable), ShaderError>.Ok(
            (new Fx2Shader(stage, bytecode), ctabResult.Value));
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

    private static (Result<byte[], ShaderError> Blob, ReadOnlyMemory<byte> DxilBlob, ReadOnlyMemory<byte> SpirvBlob, IReadOnlyList<MgfxVertexAttributeInfo> Attributes, IReadOnlyList<MonoGameGlslUniform> Uniforms)
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
                return (Result<byte[], ShaderError>.Fail(dxbcResult.Error), default, default, noAttributes, noUniforms);

            ReadOnlyMemory<byte> dxbc = dxbcResult.Value.Bytes;
            return (Result<byte[], ShaderError>.Ok(dxbc.ToArray()), dxbc, default, noAttributes, noUniforms);
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
                    // Use AllowWarnings = true so the DXIL reflection compile never fails due to
                    // warnings-as-errors — the OpenGL compile below is the authoritative failure signal.
                    Options        = new DxcCompileOptions { EmbedDebugInfo = compileOptions.EmbedDebugInfo, AllowWarnings = true },
                };

                var dxilResult = dxcCompiler.Value.Compile(dxilRequest, ct);
                if (dxilResult.IsFailure)
                    return (Result<byte[], ShaderError>.Fail(dxilResult.Error), default, default, noAttributes, noUniforms);

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
                return (Result<byte[], ShaderError>.Fail(spirvResult.Error), default, default, noAttributes, noUniforms);

            // Transpile SPIR-V → GLSL.
            var transpileResult = glslTranspiler.Transpile(spirvResult.Value.Bytes, ct);
            if (transpileResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(transpileResult.Error), default, default, noAttributes, noUniforms);

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
                        Message: ex.Message)), default, default, noAttributes, noUniforms);
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
                uniforms);
        }
        else
        {
            // Vulkan (and any future DX12/KNI SM6 profile): single DXC compile.
            // DX11 no longer reaches here — it takes the DXBC oracle branch above.
            var request = new DxcCompileRequest
            {
                HlslSource     = preprocessed.Text,
                SourceFileName = preprocessed.OriginalFilePath,
                EntryPoint     = entryPoint,
                Stage          = stage,
                Platform       = platform,
                Options        = compileOptions,
            };

            var result = dxcCompiler.Value.Compile(request, ct);
            if (result.IsFailure)
                return (Result<byte[], ShaderError>.Fail(result.Error), default, default, noAttributes, noUniforms);

            ReadOnlyMemory<byte> blob      = result.Value.Bytes;
            ReadOnlyMemory<byte> dxilBlob  = platform == PlatformTarget.DirectX ? blob : default;
            ReadOnlyMemory<byte> spirvBlob = platform != PlatformTarget.DirectX ? blob : default;

            return (Result<byte[], ShaderError>.Ok(blob.ToArray()), dxilBlob, spirvBlob, noAttributes, noUniforms);
        }
    }

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
