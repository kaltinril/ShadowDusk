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

    public CompilationPipeline(
        Func<IDxcShaderCompiler>? dxcCompilerFactory = null,
        Func<ISpirvToGlslTranspiler>? glslTranspilerFactory = null,
        Func<IShaderReflector>? reflectorFactory = null)
    {
        _dxcCompilerFactory    = dxcCompilerFactory    ?? (() => new DxcShaderCompiler());
        _glslTranspilerFactory = glslTranspilerFactory ?? (() => new SpirvCrossGlslTranspiler());
        // When non-null AND target == OpenGL, reflection is sourced from SPIR-V (the
        // browser/WASM path) instead of the native DXIL ID3D12ShaderReflection oracle.
        // Null (desktop default) keeps the DXIL path byte-for-byte unchanged.
        _reflectorFactory      = reflectorFactory;
    }

    public async Task<Result<CompiledShader, ShaderError[]>> RunAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.Target == PlatformTarget.Metal)
        {
            return Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "SD0200",
                Message: "Metal target not yet supported"));
        }

        string sourceFileName = options.SourceFileName ?? "<source>";

        // Stage 1: FX9 pre-parser.
        var parseResult = FxPreParser.Parse(hlslSource, sourceFileName);
        if (parseResult.IsFailure)
        {
            FxParseError err = parseResult.Error;
            return Fail(new ShaderError(
                File: err.SourceFile,
                Line: err.Line,
                Column: err.Column,
                Code: $"FX{(int)err.Code:D4}",
                Message: err.Message));
        }

        FxParseResult fxParsed = parseResult.Value;

        // Stage 2: Preprocessor — inject platform macros and flatten #includes.
        MacroSet macros;
        try
        {
            macros = PlatformMacros.For(options.Target);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Fail(new ShaderError(
                File: "",
                Line: 0,
                Column: 0,
                Code: "X0010",
                Message: $"platform '{options.Target}' is not supported by ShadowDusk"));
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
        bool monoGameGl = options.Target == PlatformTarget.OpenGL;

        // When a reflector factory is injected and the target is OpenGL, reflection is
        // sourced from the SPIR-V blob (pure-managed, WASM-safe) instead of compiling a
        // separate DXIL blob and reflecting it via the native ID3D12ShaderReflection
        // oracle. The SPIR-V compile + transpile remain identical, so .mgfx output is
        // byte-transparent — only the SOURCE of the ReflectedEffect changes.
        bool reflectFromSpirv = _reflectorFactory is not null && options.Target == PlatformTarget.OpenGL;

        // Stages 3–5: Compile each pass's entry points, reflect, and transpile.
        // The preprocessor has already flattened all #includes so no include handler is needed for DXC.
        IDxcShaderCompiler dxcCompiler = _dxcCompilerFactory();
        try
        {
        ISpirvToGlslTranspiler glslTranspiler = _glslTranspilerFactory();

        // DirectX (DX11) takes a separate backend: DXC only emits SM6 DXIL, which
        // MonoGame's DX11 runtime rejects. d3dcompiler_47 (the fxc engine) emits the
        // SM5 DXBC MonoGame loads — and the matching ID3D11ShaderReflection is reflected
        // here. Windows-only at runtime (guarded inside the backend). A cross-platform
        // vkd3d backend can replace D3DCompilerShaderCompiler behind IDxbcShaderCompiler.
        bool directX = options.Target == PlatformTarget.DirectX;
        // Backend selection (default = the proven d3dcompiler_47 oracle). The
        // cross-platform vkd3d-shader backend is opt-in via CompilerOptions.DxbcBackend.
        // Both implement IDxbcShaderCompiler and both feed the SAME DxbcReflectionExtractor.
        IDxbcShaderCompiler dxbcCompiler = options.DxbcBackend switch
        {
            DxbcBackend.Vkd3d => new Vkd3dShaderCompiler(),
            _                 => new D3DCompilerShaderCompiler(),
        };
        var dxbcReflectionPipe  = new DxbcReflectionPipeline(new DxbcReflectionExtractor());

        var extractor          = new DxilReflectionExtractor();
        var verifier           = new SpvReflectionVerifier();
        var reflectionPipeline = new ReflectionPipeline(extractor, verifier);
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

        // cbuffer name -> which stages bind it. Drives the GL cbuffer name
        // (vs_uniforms_vec4 for a VS-bound cbuffer, ps_uniforms_vec4 otherwise).
        var cbufferStages       = new Dictionary<string, (bool Vs, bool Ps)>(StringComparer.Ordinal);

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
                    var compileOutput = await CompileEntryPointAsync(
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
                        cancellationToken).ConfigureAwait(false);

                    if (compileOutput.Blob.IsFailure)
                        return Fail(compileOutput.Blob.Error);

                    vsIndex     = compiledShaderBlobs.Count;
                    vsDxilBlob  = compileOutput.DxilBlob;
                    vsSpirvBlob = compileOutput.SpirvBlob;
                    compiledShaderBlobs.Add(new CompiledShaderBlob(compileOutput.Blob.Value, ShaderStage.Vertex)
                    {
                        // The GL attribute table maps each vs_v{k} → VertexElementUsage+index
                        // so MonoGame binds the right vertex element. Empty for DX / non-GL.
                        Attributes = compileOutput.Attributes,
                    });
                }

                if (pass.PixelEntryPoint is not null)
                {
                    var compileOutput = await CompileEntryPointAsync(
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
                        cancellationToken).ConfigureAwait(false);

                    if (compileOutput.Blob.IsFailure)
                        return Fail(compileOutput.Blob.Error);

                    psIndex     = compiledShaderBlobs.Count;
                    psDxilBlob  = compileOutput.DxilBlob;
                    psSpirvBlob = compileOutput.SpirvBlob;
                    compiledShaderBlobs.Add(new CompiledShaderBlob(compileOutput.Blob.Value, ShaderStage.Pixel));
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
                        // DirectX: dxilBlob actually carries SM5 DXBC — reflect via
                        // ID3D11ShaderReflection (DXC's DXIL reflection can't read DXBC).
                        reflectResult = await dxbcReflectionPipe.ReflectAsync(
                            dxilBlob,
                            fxParsed.ParameterAnnotations,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var reflectionInput = new ReflectionInput
                        {
                            DxilBlob      = dxilBlob,
                            SpirVBlob     = spirvBlob,
                            FxAnnotations = fxParsed.ParameterAnnotations,
                        };

                        reflectResult = await reflectionPipeline.ReflectAsync(reflectionInput, cancellationToken).ConfigureAwait(false);
                    }

                    if (reflectResult.IsFailure)
                        return Fail(reflectResult.Error);

                    ReflectedEffect reflected = reflectResult.Value;

                    foreach (ConstantBufferReflection cb in reflected.ConstantBuffers)
                    {
                        if (seenCbufferNames.Add(cb.Name))
                            allConstantBuffers.Add(cb);

                        // Record which stage(s) bind this cbuffer so the GL writer can name
                        // it ps_uniforms_vec4 / vs_uniforms_vec4 from reflection rather than
                        // the PS-only assumption. blobIndex's stage is authoritative here.
                        ShaderStage cbStage = compiledShaderBlobs[blobIndex].Stage;
                        if (!cbufferStages.TryGetValue(cb.Name, out var stages))
                            stages = (Vs: false, Ps: false);
                        cbufferStages[cb.Name] = cbStage == ShaderStage.Vertex
                            ? (Vs: true, stages.Ps)
                            : (stages.Vs, Ps: true);
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

                var renderStateKvp = pass.RenderStates
                    .ToDictionary(rs => rs.Key, rs => rs.Value, StringComparer.OrdinalIgnoreCase);
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

        IReadOnlyList<ConstantBufferInfo>  constantBufferInfoList  = BuildConstantBufferInfoList(allConstantBuffers, allParameters, monoGameGl, directX, cbufferStages);
        IReadOnlyList<EffectParameterInfo> effectParameterInfoList = BuildEffectParameterInfoList(allParameters);

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
                        Parameter:   paramIndex));
                }
            }

            var cbIndices = new List<int>();
            if (shaderCbufferNames.TryGetValue(i, out var cbNames))
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

        // Stage 6: MGFX binary writer.
        MgfxProfile mgfxProfile = options.Target switch
        {
            PlatformTarget.DirectX => MgfxProfile.DirectX11,
            PlatformTarget.OpenGL  => MgfxProfile.OpenGL,
            PlatformTarget.Vulkan  => MgfxProfile.Vulkan,
            _ => MgfxProfile.OpenGL,
        };

        var mgfxWriter  = new MgfxWriter();
        var writeResult = mgfxWriter.Write(ir, new MgfxWriterOptions(
            Profile: mgfxProfile,
            MgfxVersion: (byte)options.MgfxVersion));

        if (writeResult.IsFailure)
            return Fail(writeResult.Error);

        byte[] mgfxBytes = writeResult.Value;

        return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, mgfxBytes));
        }
        finally
        {
            (dxcCompiler as IDisposable)?.Dispose();
        }
    }

    private static async Task<(Result<byte[], ShaderError> Blob, ReadOnlyMemory<byte> DxilBlob, ReadOnlyMemory<byte> SpirvBlob, IReadOnlyList<MgfxVertexAttributeInfo> Attributes)>
        CompileEntryPointAsync(
            IDxcShaderCompiler dxcCompiler,
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

            var dxbcResult = await dxbcCompiler.CompileAsync(dxbcRequest, ct).ConfigureAwait(false);
            if (dxbcResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(dxbcResult.Error), default, default, noAttributes);

            ReadOnlyMemory<byte> dxbc = dxbcResult.Value.Bytes;
            return (Result<byte[], ShaderError>.Ok(dxbc.ToArray()), dxbc, default, noAttributes);
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

                var dxilResult = await dxcCompiler.CompileAsync(dxilRequest, ct).ConfigureAwait(false);
                if (dxilResult.IsFailure)
                    return (Result<byte[], ShaderError>.Fail(dxilResult.Error), default, default, noAttributes);

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

            var spirvResult = await dxcCompiler.CompileAsync(spirvRequest, ct).ConfigureAwait(false);
            if (spirvResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(spirvResult.Error), default, default, noAttributes);

            // Transpile SPIR-V → GLSL.
            var transpileResult = glslTranspiler.Transpile(spirvResult.Value.Bytes, ct);
            if (transpileResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(transpileResult.Error), default, default, noAttributes);

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
            if (applyMonoGameGlsl)
            {
                try
                {
                    MonoGameGlslResult rewritten = MonoGameGlslRewriter.Rewrite(transpileResult.Value.Text, stage);
                    glslText = rewritten.Glsl;
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
                        Message: ex.Message)), default, default, noAttributes);
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
                attributes);
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

            var result = await dxcCompiler.CompileAsync(request, ct).ConfigureAwait(false);
            if (result.IsFailure)
                return (Result<byte[], ShaderError>.Fail(result.Error), default, default, noAttributes);

            ReadOnlyMemory<byte> blob      = result.Value.Bytes;
            ReadOnlyMemory<byte> dxilBlob  = platform == PlatformTarget.DirectX ? blob : default;
            ReadOnlyMemory<byte> spirvBlob = platform != PlatformTarget.DirectX ? blob : default;

            return (Result<byte[], ShaderError>.Ok(blob.ToArray()), dxilBlob, spirvBlob, noAttributes);
        }
    }

    private static IReadOnlyList<ConstantBufferInfo> BuildConstantBufferInfoList(
        IReadOnlyList<ConstantBufferReflection> constantBuffers,
        IReadOnlyList<ParameterReflection> parameters,
        bool monoGameGl,
        bool directX,
        IReadOnlyDictionary<string, (bool Vs, bool Ps)> cbufferStages)
    {
        // For MonoGame's GL runtime, free uniforms bind as a single vec4[] array
        // named after the cbuffer (ps_uniforms_vec4) via glUniform4fv. Each free
        // param is register-aligned (16 bytes), occupying ceil(size/16) registers —
        // scalars padded to a full register, a float4x4 spanning four — matching
        // mgfxc's MojoShader layout and the ps_uniforms_vec4[reg] indexing the GLSL
        // rewriter emits. Otherwise (DirectX, or VS-driven GL) keep HLSL packing.
        bool gl = monoGameGl;
        var result = new List<ConstantBufferInfo>(constantBuffers.Count);

        foreach (ConstantBufferReflection cb in constantBuffers)
        {
            var paramIndices = new List<int>();
            var paramOffsets = new List<ushort>();
            int glByteOffset = 0;

            foreach (VariableReflection variable in cb.Variables)
            {
                int thisOffset = glByteOffset;
                glByteOffset += Math.Max(1, (variable.SizeBytes + 15) / 16) * 16;

                for (int idx = 0; idx < parameters.Count; idx++)
                {
                    if (parameters[idx].Name == variable.Name)
                    {
                        paramIndices.Add(idx);
                        paramOffsets.Add(gl ? (ushort)thisOffset : (ushort)variable.StartOffset);
                        break;
                    }
                }
            }

            // DX cbuffer record carries an empty name (MonoGame's DX11 runtime binds
            // the cbuffer by slot, not by name); GL names it after the binding stage —
            // vs_uniforms_vec4 for a VS-bound cbuffer, ps_uniforms_vec4 otherwise (the
            // name MonoGame's GL runtime keys glUniform4fv on). A cbuffer bound by BOTH
            // stages is named ps_uniforms_vec4 (the pixel stage's view), matching the
            // PS-only corpus's prior behaviour. Attribution comes from reflection
            // (cbufferStages), not the old PS-only assumption.
            string cbName;
            if (gl)
            {
                bool vsBound = cbufferStages.TryGetValue(cb.Name, out var stages) && stages.Vs && !stages.Ps;
                cbName = vsBound ? "vs_uniforms_vec4" : "ps_uniforms_vec4";
            }
            else
            {
                cbName = directX ? string.Empty : cb.Name;
            }

            result.Add(new ConstantBufferInfo(
                Name:             cbName,
                SizeInBytes:      gl ? glByteOffset : cb.SizeBytes,
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
                MemberIndices: Array.Empty<int>(),
                ElementIndices: Array.Empty<int>()));
        }

        return result;
    }

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
                Type: 3,
                StringValue: null,
                FloatValue: floatVal,
                IntValue: null,
                BoolValue: null);
        }

        if (int.TryParse(annotation.Value, out int intVal))
        {
            return new AnnotationInfo(
                Name: annotation.Name,
                Type: 2,
                StringValue: null,
                FloatValue: null,
                IntValue: intVal,
                BoolValue: null);
        }

        return new AnnotationInfo(
            Name: annotation.Name,
            Type: 4,
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
                Type: 4,
                StringValue: entry.Value,
                FloatValue: null,
                IntValue: null,
                BoolValue: null));
        }
        return result;
    }

    private static Result<CompiledShader, ShaderError[]> Fail(ShaderError error) =>
        Result<CompiledShader, ShaderError[]>.Fail(new ShaderError[] { error });
}
