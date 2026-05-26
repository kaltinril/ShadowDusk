#nullable enable

using System.Text;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;

namespace ShadowDusk.Compiler.Internal;

internal sealed class CompilationPipeline
{
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

        // Stages 3–5: Compile each pass's entry points, reflect, and transpile.
        // The preprocessor has already flattened all #includes so no include handler is needed for DXC.
        using var dxcCompiler = new DxcShaderCompiler();
        var glslTranspiler = new SpirvCrossGlslTranspiler();

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
                        glslTranspiler,
                        preprocessed,
                        pass.VertexEntryPoint,
                        ShaderStage.Vertex,
                        options.Target,
                        compileOptions,
                        cancellationToken).ConfigureAwait(false);

                    if (compileOutput.Blob.IsFailure)
                        return Fail(compileOutput.Blob.Error);

                    vsIndex     = compiledShaderBlobs.Count;
                    vsDxilBlob  = compileOutput.DxilBlob;
                    vsSpirvBlob = compileOutput.SpirvBlob;
                    compiledShaderBlobs.Add(new CompiledShaderBlob(compileOutput.Blob.Value, ShaderStage.Vertex));
                }

                if (pass.PixelEntryPoint is not null)
                {
                    var compileOutput = await CompileEntryPointAsync(
                        dxcCompiler,
                        glslTranspiler,
                        preprocessed,
                        pass.PixelEntryPoint,
                        ShaderStage.Pixel,
                        options.Target,
                        compileOptions,
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
                foreach (var (dxilBlob, spirvBlob) in new[]
                {
                    (vsDxilBlob, vsSpirvBlob),
                    (psDxilBlob, psSpirvBlob),
                })
                {
                    if (dxilBlob.IsEmpty)
                        continue;

                    var reflectionInput = new ReflectionInput
                    {
                        DxilBlob      = dxilBlob,
                        SpirVBlob     = spirvBlob,
                        FxAnnotations = fxParsed.ParameterAnnotations,
                    };

                    var reflectResult = await reflectionPipeline.ReflectAsync(reflectionInput, cancellationToken).ConfigureAwait(false);
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

        IReadOnlyList<ConstantBufferInfo>  constantBufferInfoList  = BuildConstantBufferInfoList(allConstantBuffers, allParameters);
        IReadOnlyList<EffectParameterInfo> effectParameterInfoList = BuildEffectParameterInfoList(allParameters);

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

    private static async Task<(Result<byte[], ShaderError> Blob, ReadOnlyMemory<byte> DxilBlob, ReadOnlyMemory<byte> SpirvBlob)>
        CompileEntryPointAsync(
            DxcShaderCompiler dxcCompiler,
            SpirvCrossGlslTranspiler glslTranspiler,
            PreprocessedSource preprocessed,
            string entryPoint,
            ShaderStage stage,
            PlatformTarget platform,
            DxcCompileOptions compileOptions,
            CancellationToken ct)
    {
        if (platform == PlatformTarget.OpenGL)
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
                return (Result<byte[], ShaderError>.Fail(dxilResult.Error), default, default);

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
                return (Result<byte[], ShaderError>.Fail(spirvResult.Error), default, default);

            // Transpile SPIR-V → GLSL.
            var transpileResult = glslTranspiler.Transpile(spirvResult.Value.Bytes, ct);
            if (transpileResult.IsFailure)
                return (Result<byte[], ShaderError>.Fail(transpileResult.Error), default, default);

            byte[] glslBytes = Encoding.UTF8.GetBytes(transpileResult.Value.Text);

            return (
                Result<byte[], ShaderError>.Ok(glslBytes),
                dxilResult.Value.Bytes,
                spirvResult.Value.Bytes);
        }
        else
        {
            // DirectX / Vulkan: single compile.
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
                return (Result<byte[], ShaderError>.Fail(result.Error), default, default);

            ReadOnlyMemory<byte> blob      = result.Value.Bytes;
            ReadOnlyMemory<byte> dxilBlob  = platform == PlatformTarget.DirectX ? blob : default;
            ReadOnlyMemory<byte> spirvBlob = platform != PlatformTarget.DirectX ? blob : default;

            return (Result<byte[], ShaderError>.Ok(blob.ToArray()), dxilBlob, spirvBlob);
        }
    }

    private static IReadOnlyList<ConstantBufferInfo> BuildConstantBufferInfoList(
        IReadOnlyList<ConstantBufferReflection> constantBuffers,
        IReadOnlyList<ParameterReflection> parameters)
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

            result.Add(new ConstantBufferInfo(
                Name: cb.Name,
                SizeInBytes: cb.SizeBytes,
                ParameterIndices: paramIndices,
                ParameterOffsets: paramOffsets));
        }

        return result;
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
