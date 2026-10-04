#nullable enable

using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Raylib;

/// <summary>Options for <see cref="RaylibConverter.Convert"/>.</summary>
public sealed class RaylibConvertOptions
{
    /// <summary>The logical source name used in diagnostics. Defaults to <c>"&lt;memory&gt;.fx"</c>.</summary>
    public string SourceName { get; init; } = "<memory>.fx";

    /// <summary>Optional custom <c>#include</c> resolver (defaults to the file system).</summary>
    public IIncludeResolver? IncludeResolver { get; init; }

    /// <summary>Additional directories searched when resolving <c>#include</c> directives.</summary>
    public IReadOnlyList<string> AdditionalIncludePaths { get; init; } = [];
}

/// <summary>
/// The successful product: a raylib fragment shader plus the binding contract the consumer's
/// draw code needs. Deliberately NOT a <see cref="CompiledShader"/>: that type's
/// <c>Data</c> is an effect container every existing consumer hands to <c>new Effect(...)</c>,
/// and a GLSL string in that slot would load as garbage instead of failing.
/// </summary>
/// <param name="FragmentShader">
/// <c>#version 330</c> GLSL (raylib's <c>glsl330</c> profile) for
/// <c>Raylib.LoadShaderFromMemory(null, FragmentShader)</c>. The <c>null</c> vertex shader is
/// the point: raylib's built-in vertex shader supplies <c>fragTexCoord</c> and
/// <c>fragColor</c>, which is the only interface this slice emits against.
/// </param>
/// <param name="Uniforms">Every uniform the shader declares, under the GLSL name to pass to
/// <c>GetShaderLocation</c>. Each is the HLSL global of the same name.</param>
/// <param name="Samplers">Every sampler the shader declares (see <see cref="RaylibSampler"/>).</param>
/// <param name="Warnings">Non-fatal findings, e.g. baked sampler states the consumer must apply (<c>SD0637</c>).</param>
public sealed record RaylibShader(
    string FragmentShader,
    IReadOnlyList<RaylibUniform> Uniforms,
    IReadOnlyList<RaylibSampler> Samplers,
    IReadOnlyList<ShaderError> Warnings);

/// <summary>One non-sampler uniform of a <see cref="RaylibShader"/>.</summary>
/// <param name="Name">The GLSL uniform name (the HLSL global's name).</param>
/// <param name="GlslType">The GLSL type, e.g. <c>vec4</c>; pick the matching <c>ShaderUniformDataType</c>.</param>
/// <param name="ArrayLength">Element count for an array uniform, or <c>0</c> for a non-array.</param>
public sealed record RaylibUniform(string Name, string GlslType, int ArrayLength);

/// <summary>One <c>sampler2D</c> of a <see cref="RaylibShader"/>.</summary>
/// <param name="UniformName">
/// The GLSL uniform. <c>texture0</c> for the sampler on texture unit 0 (the one MonoGame's
/// <c>SpriteBatch.Draw</c> binds, and the one raylib's draw call binds); any other sampler keeps
/// its HLSL texture's name and is bound with <c>SetShaderValueTexture</c>.
/// </param>
/// <param name="HlslTextureName">The HLSL texture behind the sampler.</param>
/// <param name="HlslSamplerName">The HLSL sampler object.</param>
/// <param name="BoundByDrawCall">True for <c>texture0</c>: the texture passed to the draw call.</param>
/// <param name="BakedSamplerState">
/// The <c>sampler_state</c> entries the <c>.fx</c> bakes (e.g. <c>Filter = Point</c>). A raylib
/// shader cannot carry them, because raylib keeps filtering and addressing on the
/// <c>Texture2D</c>; apply them with <c>SetTextureFilter</c>/<c>SetTextureWrap</c>.
/// </param>
public sealed record RaylibSampler(
    string UniformName,
    string HlslTextureName,
    string HlslSamplerName,
    bool BoundByDrawCall,
    IReadOnlyDictionary<string, string> BakedSamplerState);

/// <summary>
/// Converts an HLSL <c>.fx</c> pixel shader to a <b>raylib fragment shader</b> for Raylib-cs
/// (Phase 59, the fragment-only slice). The route is the same seam the SkSL converter uses:
/// <c>HLSL → [DXC] → SPIR-V → [SPIRV-Cross] → modern GLSL → [raylib convention mapper]</c>,
/// branching before the MonoGame GLSL rewriter and the MGFX writer, so no substitute compiler is
/// involved and no existing target's output changes.
///
/// <para><b>Evidence model:</b> rendered-image fidelity (owner decision 2026-10-01, the SkSL
/// target's model): raylib has no reference compiler, so this is never an <c>mgfxc</c>-equivalence
/// claim. The proof is <c>validation/RaylibRoute</c>, which renders each converted shader in real
/// Raylib-cs and pixel-diffs it against the same <c>.fx</c> built for OpenGL and rendered in real
/// MonoGame DesktopGL.</para>
///
/// <para><b>Input languages:</b> anything that becomes <c>.fx</c> text. A <c>.slang</c> file goes
/// through <c>SlangFrontend.ConvertToFx</c> (or the <c>ShadowDusk.Slang</c> package) first; the
/// HLSL it yields is compiled by the same DXC as every <c>.fx</c>, so this converter needs nothing
/// Slang-specific.</para>
///
/// <para>Everything raylib's fixed-function model cannot hold is rejected loudly
/// (<c>SD0630</c>–<c>SD0636</c>), never emitted as a shader that loads and renders wrong.</para>
/// </summary>
public static class RaylibConverter
{
    /// <summary>
    /// Converts the pixel shader of <paramref name="fxSource"/>'s single technique/pass to a
    /// raylib <c>glsl330</c> fragment shader, or returns the loud diagnostics for anything
    /// raylib's model cannot hold.
    /// </summary>
    /// <param name="fxSource">The HLSL <c>.fx</c> effect source.</param>
    /// <param name="options">Conversion options; see <see cref="RaylibConvertOptions"/>.</param>
    /// <param name="cancellationToken">Observed between pipeline stages.</param>
    public static Result<RaylibShader, ShaderError[]> Convert(
        string fxSource,
        RaylibConvertOptions options,
        CancellationToken cancellationToken = default)
    {
        string file = options.SourceName;

        var parse = FxPreParser.Parse(fxSource, file);
        if (parse.IsFailure)
        {
            return Result<RaylibShader, ShaderError[]>.Fail(
            [
                new ShaderError(
                    File: parse.Error.SourceFile,
                    Line: parse.Error.Line,
                    Column: parse.Error.Column,
                    Code: $"FX{(int)parse.Error.Code:D4}",
                    Message: parse.Error.Message),
            ]);
        }

        Result<RaylibShader, ShaderError[]> first = ConvertCore(
            fxSource, parse.Value, options, recovered: null, out bool shaderCompileFailed, cancellationToken);
        if (first.IsSuccess || !shaderCompileFailed)
            return first;

        // Legacy-sampler recovery (issues #308, #327): the same one CompilationPipeline.Run uses.
        // The DXC compile failed; if the text it was given still holds legacy D3D9 sampler syntax
        // once preprocessed (a declaration in an #include'd file, or one that comes out of a
        // macro, which the raw pre-parse cannot see), repeat the pre-parse on the PREPROCESSED
        // source and convert from that. Only ever reached on a failure, so an effect that
        // converts from its raw source is converted from exactly that source, as before.
        LegacySamplerRecovery.Outcome outcome = LegacySamplerRecovery.Evaluate(
            fxSource,
            file,
            PlatformMacros.For(PlatformTarget.OpenGL),
            options.IncludeResolver ?? new FileSystemIncludeResolver(),
            options.AdditionalIncludePaths);
        return LegacySamplerRecovery.Apply(
            first, outcome,
            retry => ConvertCore(fxSource, retry.Parsed.Parsed, options, retry, out _, cancellationToken));
    }

    /// <summary>
    /// One pass of the conversion over a pre-parse: the raw one (every conversion's first and
    /// normally only pass), or, with <paramref name="recovered"/>, the pre-parse of the
    /// preprocessed source (issue #308). <paramref name="shaderCompileFailed"/> is set when the
    /// DXC compile (or the transpile behind it) is what failed on a first pass: the one failure
    /// the recovery can be consulted for.
    /// </summary>
    private static Result<RaylibShader, ShaderError[]> ConvertCore(
        string fxSource,
        FxParseResult parsed,
        RaylibConvertOptions options,
        LegacySamplerRecovery.Outcome.Retry? recovered,
        out bool shaderCompileFailed,
        CancellationToken cancellationToken)
    {
        shaderCompileFailed = false;
        string file = options.SourceName;

        IReadOnlyList<TechniqueInfo> techniques = parsed.Techniques;
        if (techniques.Count == 0)
            return Fail(file, 0, 0, "SD0010", "Effect source contains no techniques.");

        // raylib has no technique/pass concept: a shader is one program bound for a draw, so
        // picking one pass of several would be a silent guess about which one the author meant.
        if (techniques.Count > 1)
        {
            SourceSpan extra = techniques[1].Span;
            return Fail(file, extra.StartLine, extra.StartColumn, "SD0630",
                $"the effect declares {techniques.Count} techniques ('{techniques[1].Name}' is the " +
                "second). A raylib shader is a single program with no technique concept, so " +
                "converting one and dropping the rest would be a silent guess. Split the effect " +
                "into one .fx per technique.");
        }
        if (techniques[0].Passes.Count > 1)
        {
            SourceSpan extra = techniques[0].Passes[1].Span;
            return Fail(file, extra.StartLine, extra.StartColumn, "SD0630",
                $"technique '{techniques[0].Name}' declares {techniques[0].Passes.Count} passes " +
                $"('{techniques[0].Passes[1].Name}' is the second). raylib draws with one shader " +
                "at a time and has no pass concept, so a multi-pass effect cannot be one raylib " +
                "shader. Split each pass into its own .fx and chain them through render textures.");
        }

        PassInfo pass = techniques[0].Passes[0];

        if (pass.VertexEntryPoint is not null)
        {
            SourceSpan at = pass.VertexProfileSpan ?? pass.Span;
            return Fail(file, at.StartLine, at.StartColumn, "SD0631",
                $"pass '{pass.Name}' compiles a vertex shader ('{pass.VertexEntryPoint}'). This " +
                "converter emits fragment shaders that run behind raylib's built-in vertex shader " +
                "(LoadShader(null, fs)); a custom vertex stage needs raylib's vertex conventions " +
                "(vertexPosition, mvp, ...), which are not mapped. Refusing rather than silently " +
                "dropping the vertex work.");
        }
        if (pass.PixelEntryPoint is null)
        {
            return Fail(file, pass.Span.StartLine, pass.Span.StartColumn, "SD0631",
                $"pass '{pass.Name}' has no pixel shader, so there is no fragment shader to emit.");
        }

        // MonoGame applies these from the effect on EffectPass.Apply; a raylib shader string has
        // nowhere to carry them, so emitting it would render with whatever state raylib has set.
        if (pass.RenderStates.Count > 0)
        {
            RenderStateEntry first = pass.RenderStates[0];
            string names = string.Join(", ", pass.RenderStates.Select(s => $"{s.Key} = {s.Value}"));
            return Fail(file, first.Span.StartLine, first.Span.StartColumn, "SD0632",
                $"pass '{pass.Name}' sets render state ({names}). A raylib shader is GLSL text only; " +
                "blend, depth, stencil, and rasterizer state are set by the draw code " +
                "(BeginBlendMode, rlDisableDepthTest, ...), so this state would be silently lost. " +
                "Remove it from the pass and set the equivalent state around the raylib draw.");
        }

        // A recovery's text already has its #includes inlined and its macros expanded (flattening
        // it again would prepend the macro block a second time); a raw pre-parse is flattened here.
        string compilerInput;
        if (recovered is not null)
        {
            compilerInput = parsed.StrippedHlsl;
        }
        else
        {
            var flattened = ModernGlslSeam.Flatten(
                parsed.StrippedHlsl, file, options.IncludeResolver, options.AdditionalIncludePaths);
            if (flattened.IsFailure)
                return Result<RaylibShader, ShaderError[]>.Fail([flattened.Error]);
            compilerInput = flattened.Value.Text;
        }

        var seam = ModernGlslSeam.CompilePixel(compilerInput, pass.PixelEntryPoint, file, cancellationToken);
        if (seam.IsFailure)
        {
            shaderCompileFailed = recovered is null;
            return Result<RaylibShader, ShaderError[]>.Fail(seam.Error);
        }

        // A wrong pair order would bind the draw call's texture to the wrong sampler, so an
        // unmodeled shape is fatal here (the SkSL converter can tolerate it; this one cannot).
        if (seam.Value.SamplerPairs.IsFailure)
            return Result<RaylibShader, ShaderError[]>.Fail([seam.Value.SamplerPairs.Error]);

        IReadOnlyList<CombinedSamplerPair> pairs = seam.Value.SamplerPairs.Value;

        // Reservations (issue #283) and a legacy sampler's explicit register (issue #299) both
        // come from the preprocessed source with the OpenGL macro set the seam compiled with,
        // exactly as on the OpenGL target. DXC has already accepted the source, so a view that
        // cannot be built is our preprocessor's fault: SD0009. A recovery pass has the flattened
        // raw source in hand already, and its pre-parse names samplers as the preprocessed view does.
        var samplerSlots = recovered is not null
            ? FxPreParser.CollectGlSamplerSlots(recovered.FlattenedRawSource, file, parsed)
            : GlSamplerReservation.Collect(
                fxSource,
                file,
                PlatformMacros.For(PlatformTarget.OpenGL),
                options.IncludeResolver ?? new FileSystemIncludeResolver(),
                options.AdditionalIncludePaths,
                parsed);
        if (samplerSlots.IsFailure)
            return Result<RaylibShader, ShaderError[]>.Fail([samplerSlots.Error]);

        // The same allocator the OpenGL target uses, so "the sampler SpriteBatch binds" (unit 0)
        // and "the sampler raylib's draw binds" (texture0) are the same HLSL sampler by construction.
        IReadOnlyList<int> slots = SpirvCombinedSamplerPairs.ResolveSlots(
            pairs, samplerSlots.Value.Explicit, samplerSlots.Value.Reserved,
            legacyTextures: samplerSlots.Value.LegacyTextures);

        var bakedStates = parsed.Samplers
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, string>)g.First().StateEntries
                    .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(e => e.Key, e => e.Last().Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);

        var samplerInputs = pairs
            .Select((p, i) => new RaylibGlslMapper.SamplerInput(
                p.TextureName,
                p.SamplerName,
                slots[i],
                bakedStates.TryGetValue(p.SamplerName, out var state)
                    ? state
                    : new Dictionary<string, string>()))
            .ToList();

        return RaylibGlslMapper.Map(seam.Value.Glsl, samplerInputs, parsed.StrippedHlsl, file);
    }

    private static Result<RaylibShader, ShaderError[]> Fail(
        string file, int line, int column, string code, string message) =>
        Result<RaylibShader, ShaderError[]>.Fail(
            [new ShaderError(File: file, Line: line, Column: column, Code: code, Message: message)]);
}
