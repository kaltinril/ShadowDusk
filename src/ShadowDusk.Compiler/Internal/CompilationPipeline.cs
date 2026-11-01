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
        var helpers = new List<RestructuredHelperDefinition>();
        Result<CompiledShader, ShaderError[]> first = RunCore(
            hlslSource, options, recovered: null, out ShaderCompileFailure? compileFailure, cancellationToken,
            helpers);
        if (first.IsSuccess || compileFailure is null)
            return NameRestructuredHelpersAsWritten(first, helpers, options);

        // Vulkan: a texture or sampler the binding rewrite could not see on the raw text (its
        // declaration comes out of a macro) was auto-numbered below binding 32 (SD0228), a register space it could not see put a
        // resource outside the stage's descriptor set (SD0229), or a sampler ended up with no texture on
        // its binding because the pairing was hidden by a macro or an inactive #if (SD0232). Compile
        // once more from the preprocessed source, where the declaration is literal. Only after
        // that failure, so an effect that compiles from its raw source keeps exactly its bytes.
        if (compileFailure.UnpinnedVulkanResource)
        {
            LegacySamplerRecovery.Outcome.Retry? retry = LegacySamplerRecovery.PreprocessedRetry(
                hlslSource,
                compileFailure.SourceFileName,
                compileFailure.Macros,
                compileFailure.IncludeResolver,
                options.AdditionalIncludePaths);
            if (retry is null)
                return first;
            Result<CompiledShader, ShaderError[]> second = RunCore(hlslSource, options, retry, out _, cancellationToken);
            // A retry that still fails with one of VulkanResourceSlots' codes names what the preprocessed source could not pin
            // either, located through its #line map (the first attempt may name a resource the
            // retry already pinned, a macro-declared one, with no line). Any other retry failure is
            // the retry's own, so the first attempt's diagnostics stand.
            if (second.IsSuccess || second.Error.Any(e => VulkanResourceSlots.Codes.Contains(e.Code)))
                return second;
            return first;
        }

        // Legacy-sampler recovery (issue #308). A DXC shader compile failed. If the text DXC was
        // given still holds legacy D3D9 sampler syntax once preprocessed (a declaration in an
        // #include'd file, or one that comes out of a macro, which the raw pre-parse cannot
        // see), repeat the pre-parse on the PREPROCESSED source and compile that. This runs only
        // on a failure, so an effect that compiles from its raw source is compiled from exactly
        // that source, byte for byte as before.
        LegacySamplerRecovery.Outcome outcome = LegacySamplerRecovery.Evaluate(
            hlslSource,
            compileFailure.SourceFileName,
            compileFailure.Macros,
            compileFailure.IncludeResolver,
            options.AdditionalIncludePaths);

        // The outcome handling is LegacySamplerRecovery.Apply, shared with the raylib and SkSL
        // converters (issue #327). A retry that still fails with legacy syntax left in the
        // compiler's input gets SD0016 appended after the compiler's own verbatim diagnostics.
        // DirectX 11 sent a deferred tex2Dlod-family call here before compiling (see RunCore). When
        // the recovery does not apply, nothing it could rewrite is live: the include does not
        // resolve (whose error is the real one), or the call sits in a branch this configuration
        // does not compile. Either way the normal raw path is the answer, as it was before.
        if (compileFailure.DeferredLegacyCall && outcome is LegacySamplerRecovery.Outcome.NotApplicable)
        {
            return NameRestructuredHelpersAsWritten(
                RunCore(hlslSource, options, recovered: null, out _, cancellationToken, helpers, deferToRecovery: false),
                helpers, options);
        }

        return NameRestructuredHelpersAsWritten(
            LegacySamplerRecovery.Apply(
                first, outcome, retry => RunCore(hlslSource, options, retry, out _, cancellationToken, helpers)),
            helpers, options);
    }

    /// <summary>
    /// FxPreParser rewrites tex2Dlod / tex2Dbias / tex2Dproj to a call of a helper it names
    /// <c>tex2Dlod_SD&lt;n&gt;</c>, so a compiler error about a CALL names a function the author
    /// never wrote ("no matching function for call to 'tex2Dproj_SD0'"). This names the intrinsic
    /// instead in <see cref="ShaderError.Message"/>, keeping the compiler's own text, unchanged, in
    /// <see cref="ShaderError.RawDiagnostics"/>. Only the helpers the pre-parse that produced the
    /// compiled text defined are renamed (never an identifier an <c>#include</c> or the author
    /// wrote), only in an error located in the main file, and never one located inside a helper's
    /// own definition: an error there is about the helper's placement, and the intrinsic's name
    /// would mislead.
    /// </summary>
    private static Result<CompiledShader, ShaderError[]> NameRestructuredHelpersAsWritten(
        Result<CompiledShader, ShaderError[]> result,
        IReadOnlyList<RestructuredHelperDefinition> helpers,
        CompilerOptions options)
    {
        if (result.IsSuccess || helpers.Count == 0)
            return result;

        string mainFile = (options.SourceFileName ?? "<source>").Replace('\\', '/');
        var pattern = new Regex(@"\b(?:" + string.Join("|", helpers.Select(h => Regex.Escape(h.Name))) + @")\b");
        var intrinsicOf = helpers.ToDictionary(h => h.Name, h => h.Intrinsic, StringComparer.Ordinal);

        return Result<CompiledShader, ShaderError[]>.Fail(result.Error.Select(e =>
        {
            if (!pattern.IsMatch(e.Message) ||
                !string.Equals(e.File.Replace('\\', '/'), mainFile, StringComparison.OrdinalIgnoreCase) ||
                helpers.Any(h => h.Line == e.Line && h.StartColumn <= e.Column && e.Column < h.EndColumn))
            {
                return e;
            }
            string renamed = pattern.Replace(e.Message, m => intrinsicOf[m.Value]);
            return e with { Message = renamed, RawDiagnostics = e.RawDiagnostics ?? e.FxcFormattedMessage };
        }).ToArray());
    }

    /// <summary>
    /// What <see cref="Run"/> needs to consider the legacy-sampler recovery after a DXC shader
    /// compile failed: the inputs that compile was built from.
    /// </summary>
    private sealed record ShaderCompileFailure(
        string SourceFileName,
        MacroSet Macros,
        IIncludeResolver IncludeResolver,
        bool DeferredLegacyCall = false)
    {
        /// <summary>The failure is a Vulkan <c>SD0228</c> or <c>SD0229</c> (see <see cref="Run"/>), not a DXC compile failure.</summary>
        public bool UnpinnedVulkanResource { get; init; }
    }

    /// <summary>
    /// A failed shader compile is a candidate for the legacy-sampler recovery only on a target
    /// that compiles through DXC (OpenGL, Vulkan, DirectX 12), which has no D3D9 sampler syntax.
    /// DirectX 11 compiles through vkd3d / d3dcompiler, which parse that syntax themselves, and a
    /// recovery pass is never recovered a second time.
    /// </summary>
    private static ShaderCompileFailure? LegacySamplerRecoveryCandidate(
        PlatformTarget target,
        LegacySamplerRecovery.Outcome.Retry? recovered,
        string sourceFileName,
        MacroSet macros,
        IIncludeResolver includeResolver) =>
        recovered is null && target != PlatformTarget.DirectX
            ? new ShaderCompileFailure(sourceFileName, macros, includeResolver)
            : null;

    /// <summary>
    /// One pass of the pipeline. With <paramref name="recovered"/> null it pre-parses the raw
    /// source (every compile's first and normally only pass); with a recovery it compiles from
    /// the pre-parse of the preprocessed source instead (issue #308).
    /// </summary>
    private Result<CompiledShader, ShaderError[]> RunCore(
        string hlslSource,
        CompilerOptions options,
        LegacySamplerRecovery.Outcome.Retry? recovered,
        out ShaderCompileFailure? compileFailure,
        CancellationToken cancellationToken,
        List<RestructuredHelperDefinition>? helpers = null,
        bool deferToRecovery = true)
    {
        compileFailure = null;
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

        // Stage 1: FX9 pre-parser. A recovery pass (issue #308) arrives with the pre-parse of the
        // preprocessed source already done.
        FxParseResult fxParsed;
        if (recovered is not null)
        {
            fxParsed = recovered.Parsed.Parsed;
        }
        else
        {
            var parseResult = FxPreParser.Parse(hlslSource, sourceFileName);
            if (parseResult.IsFailure)
                return Fail(FromFxParseError(parseResult.Error));

            fxParsed = parseResult.Value;
        }
        if (helpers is not null)
        {
            helpers.Clear();
            helpers.AddRange(fxParsed.RestructuredHelpers);
        }

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

        // Memoized for this compile: the source is flattened more than once (the compile, the
        // preprocessed sampler views, the legacy-sampler recovery), and the consumer's resolver
        // must see one call per include, not one per pass.
        IIncludeResolver includeResolver = MemoizingIncludeResolver.Wrap(options.IncludeResolver ?? new FileSystemIncludeResolver());
        var preprocessor = new Preprocessor();

        // A tex2Dlod / tex2Dbias / tex2Dproj call the raw pre-parse left verbatim because its
        // rewrite depends on the #if branch that compiles. The DXC targets fail on it and recover
        // from the preprocessed source as for any legacy residue; DirectX 11's compiler would take
        // the unrewritten call silently (measured: vkd3d compiled one to an effect with no texture
        // parameter), so it goes to the same recovery up front. Its FX0012 is the error when the
        // recovery cannot apply.
        if (deferToRecovery && recovered is null && options.Target == PlatformTarget.DirectX &&
            fxParsed.DeferredLegacyIntrinsicCalls.Count > 0)
        {
            compileFailure = new ShaderCompileFailure(sourceFileName, macros, includeResolver, DeferredLegacyCall: true);
            return Fail(FromFxParseError(fxParsed.DeferredLegacyIntrinsicCalls[0]));
        }

        PreprocessedSource preprocessed;
        if (recovered is not null)
        {
            // The recovery's text already has its #includes inlined and its macros expanded, and
            // carries the flattener's own #line directives, so it is the compiler input as is
            // (flattening it again would prepend the macro block a second time).
            preprocessed = new PreprocessedSource(
                fxParsed.StrippedHlsl, macros.ToDxcFlags(), sourceFileName, recovered.Warnings);
        }
        else
        {
            var preprocessResult = preprocessor.Flatten(
                fxParsed.StrippedHlsl,
                sourceFileName,
                macros,
                includeResolver,
                options.AdditionalIncludePaths);

            if (preprocessResult.IsFailure)
                return Fail(preprocessResult.Error);

            preprocessed = preprocessResult.Value;
        }

        // Issue #407: the techniques (and so the passes, entry points and compile profiles) come
        // from the PREPROCESSED translation unit, like mgfxc: a technique in an inactive #if does
        // not exist, one in an #include'd file does, and `compile VS_SHADERMODEL V()` records the
        // profile the macro expands to. A recovery pass's pre-parse already ran on the
        // preprocessed text, so its techniques are that list already.
        ShaderError? techniqueViewWarning = null;
        bool techniquesFromView = false;
        if (recovered is null)
        {
            Result<EffectTechniques.Resolution, ShaderError> resolvedTechniques = EffectTechniques.Resolve(
                hlslSource, sourceFileName, macros, includeResolver, options.AdditionalIncludePaths, fxParsed);
            if (resolvedTechniques.IsFailure)
                return Fail(resolvedTechniques.Error);

            fxParsed = fxParsed with { Techniques = resolvedTechniques.Value.Techniques };
            techniqueViewWarning = resolvedTechniques.Value.ViewWarning;
            techniquesFromView = techniqueViewWarning is null;
        }

        // A sampler_state texture the main file never declares (`Texture = <T>` and no `T`): mgfxc
        // reads the name off the block and emits a `T` parameter. Declare it beside the rewritten
        // sampler, but only when the PREPROCESSED source declares no `T` either: a header or a
        // macro (`DECL_TEX(Mask)` giving `Texture2D MaskTex;`) can, and a second declaration is a
        // redefinition error.
        if (fxParsed.UndeclaredStateTextures.Count > 0)
        {
            Result<string, ShaderError> declared =
                FxPreParser.DeclareUndeclaredStateTextures(preprocessed.Text, sourceFileName, fxParsed);
            if (declared.IsFailure)
                return Fail(declared.Error);
            preprocessed = preprocessed with { Text = declared.Value };
        }

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

        // The pre-parse stripped the main file's technique blocks; one in an #include'd file is
        // still in the compiler's input, where it is effect syntax the compiler does not take
        // (DXC warns, -Weffects-syntax). Its techniques now come from the preprocessed view
        // (issue #407), so blank the block. A no-op for an effect with no such block.
        if (techniquesFromView)
            preprocessed = preprocessed with { Text = FxPreParser.StripTechniqueBlocks(preprocessed.Text, sourceFileName) };

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
        //
        // DirectX 12 (here) and Vulkan (VulkanShaderSource below), issue #446: a vertex shader's
        // legacy `: POSITION[0]` OUTPUT becomes `: SV_Position` for DXC, as fxc and vkd3d map it on
        // DirectX 11; DXC has no such mapping, so the shader had no position (WindowsDX12 threw
        // creating the pipeline, DesktopVK drew nothing). Vertex inputs are never touched, and the
        // DirectX 11 / OpenGL / FNA sources are not this copy. See DxcVertexPositionOutputRewriter.
        PreprocessedSource glCompileSource = monoGameGl
            ? preprocessed with { Text = GlStructOutputColorRewriter.Rewrite(preprocessed.Text, fxParsed.Techniques) }
            : options.Target == PlatformTarget.DirectX12
                ? preprocessed with { Text = DxcVertexPositionOutputRewriter.Rewrite(preprocessed.Text, fxParsed.Techniques) }
                : preprocessed;

        // Vulkan: the binding rewrite runs PER COMPILED SHADER (VulkanShaderSource below), because
        // each shader is its own descriptor layout: a texture read through sampler A in one pass
        // and B in another is one combined descriptor in each, never an alias.
        bool vulkan = options.Target == PlatformTarget.Vulkan;
        if (vulkan && VulkanReservedName(preprocessed.Text, sourceFileName) is { } reservedName)
            return Fail(reservedName);

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
        // Memoized per run (issue #255): an entry point named by several passes is compiled
        // once, not once per pass. Same request, deterministic backend, so the same bytes.
        IDxbcShaderCompiler dxbcCompiler = WithDxbcMemo(
            _dxbcCompilerFactory?.Invoke() ?? options.DxbcBackend switch
            {
                DxbcBackend.D3DCompiler => new D3DCompilerShaderCompiler(),
                _                       => new Vkd3dShaderCompiler(),
            },
            options);
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

        // Per-shader (by blob index) reflected NUMERIC parameters (scalar/vector/matrix),
        // in reflection order. The GL branch uses this to synthesize register backing for
        // a parameter reflection reports but the shipped GLSL never declares (issue #187:
        // DXC's -spirv backend can fold a uniform read fxc keeps, dropping the whole
        // cbuffer from the SPIR-V, while the reflection source still carries the
        // parameter). Without backing, the parameter exists but SetValue reaches nothing.
        var shaderNumericParams = new Dictionary<int, IReadOnlyList<ParameterReflection>>();

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
        //
        // A KNIFX container on DirectX takes KNI's floor instead of mgfxc's: KNI's own DirectX
        // pipeline also accepts *_2_0 and *_3_0, compiling them as *_4_0_level_9_1 / _9_3 (see
        // KniDirectXProfile). Choosing the KNIFX container is choosing KNI's pipeline; MGFX keeps
        // mgfxc's refusal.
        bool enforceDx11Floor = directX;
        bool kniDirectXFloor = directX && effectiveContainer == EffectContainer.Knifx;
        // A profile a macro produced is validated through the macro name the author wrote, so DXC
        // expands it with -P as before issue #407. (The Android emulator hangs the first SPIR-V
        // compile after the corpus' effects stop making those -P calls: a DXC-spawned thread is
        // blocked on a pipe read before IDxcResult.GetStatus returns. The cause is unexplained;
        // this restores the old call pattern.)
        var profileExpansionCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (TechniqueInfo technique in fxParsed.Techniques)
        {
            foreach (PassInfo pass in technique.Passes)
            {
                if (pass.VertexEntryPoint is not null)
                {
                    var v = ValidateCompileProfile(
                        pass.VertexProfileMacroName?.ToLowerInvariant() ?? pass.VertexProfile,
                        pass.VertexProfileMacroName ?? pass.VertexProfileToken, pass.VertexProfileSpan, ShaderStage.Vertex,
                        dxcCompiler, macros, preprocessed, sourceFileName,
                        profileExpansionCache, enforceStagePrefix: true, cancellationToken,
                        enforceDirectX11Floor: enforceDx11Floor, entryPoint: pass.VertexEntryPoint,
                        kniDirectXFloor: kniDirectXFloor);
                    if (v is { } vErr)
                        return Fail(vErr);
                }
                if (pass.PixelEntryPoint is not null)
                {
                    var p = ValidateCompileProfile(
                        pass.PixelProfileMacroName?.ToLowerInvariant() ?? pass.PixelProfile,
                        pass.PixelProfileMacroName ?? pass.PixelProfileToken, pass.PixelProfileSpan, ShaderStage.Pixel,
                        dxcCompiler, macros, preprocessed, sourceFileName,
                        profileExpansionCache, enforceStagePrefix: true, cancellationToken,
                        enforceDirectX11Floor: enforceDx11Floor, entryPoint: pass.PixelEntryPoint,
                        kniDirectXFloor: kniDirectXFloor);
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

        var vulkanSources = new Dictionary<(string, ShaderStage), PreprocessedSource>();
        // The lines an untaken #if drops from the text DXC compiles, and the macro-expanded view the
        // rewrite reads each function's calls from, both from the managed preprocessor with this
        // compile's macros (they head the flattened text) and the values DXC was measured to predefine
        // for this stage's Vulkan compile (issue #442: __HLSL_VERSION, __spirv__, __SHADER_TARGET_*,
        // ...; DxcSpirvPredefinedMacros), so a conditional on one of those is evaluated as DXC does.
        // Per stage: __SHADER_TARGET_STAGE differs. Unknown (null) when the view cannot be built or a
        // conditional tests a macro DXC defines with no host-independent measured value
        // (__DXC_VERSION_*, __GNUC__, __LINE__, ...; the view may then not be the code DXC compiles,
        // and the preprocessed retry that backs the per-shader pairing is unavailable too): the
        // rewrite then pairs over the whole file, every line and every function counting, as the
        // released rewrite did.
        var vulkanLiveCode = new Dictionary<ShaderStage, (IReadOnlySet<int> Lines, string View)?>();
        // Keyed by stage too: a vertex shader has fewer texture slots than a pixel shader
        // (VulkanTextureSamplerBindingRewriter.MaxVertexTextureSlots), and no alias goes past them.
        PreprocessedSource VulkanShaderSource(string entryPoint, ShaderStage stage)
        {
            if (vulkanSources.TryGetValue((entryPoint, stage), out PreprocessedSource? cached))
                return cached;
            if (!vulkanLiveCode.TryGetValue(stage, out (IReadOnlySet<int> Lines, string View)? liveCode))
            {
                var skipped = ShadowDusk.HLSL.Preprocessing.FxMacroPreprocessor.SkippedLines(
                    preprocessed.Text, sourceFileName, ShadowDusk.HLSL.Preprocessing.DxcSpirvPredefinedMacros.Vulkan(stage));
                liveCode = skipped.IsSuccess && skipped.Value.PredefinedMacroUse is null
                    ? (skipped.Value.Lines, skipped.Value.View)
                    : null;
                vulkanLiveCode[stage] = liveCode;
            }
            IReadOnlyList<VulkanTextureSamplerBindingRewriter.Alias> aliases;
            int textureSlots = stage == ShaderStage.Vertex
                ? VulkanTextureSamplerBindingRewriter.MaxVertexTextureSlots
                : VulkanTextureSamplerBindingRewriter.MaxPixelTextureSlots;
            string text = liveCode is { } live
                ? VulkanTextureSamplerBindingRewriter.Rewrite(preprocessed.Text, entryPoint, live.Lines, live.View, out aliases, textureSlots)
                : VulkanTextureSamplerBindingRewriter.Rewrite(preprocessed.Text, entryPoint: null, skippedLines: null, out aliases, textureSlots);
            foreach (var alias in aliases)
            {
                var (file, line, column) = VulkanTextureSamplerBindingRewriter.Locate(text, alias.Offset);
                AccumulateWarnings(runWarnings, seenWarnings, [new ShaderError(
                    File:     string.IsNullOrEmpty(file) ? sourceFileName : file,
                    Line:     line,
                    Column:   column,
                    Code:     "SD0235",
                    Message:  $"Vulkan target: texture '{alias.Texture}' is sampled through a second sampler, '{alias.Sampler}', " +
                              $"in shader '{entryPoint}'. MonoGame's DesktopVK binds a texture and a sampler as one combined " +
                              "descriptor, so ShadowDusk gives this pairing a second descriptor holding the same texture, bound " +
                              $"to the '{alias.Texture}' effect parameter. Set '{alias.Texture}' through Effect.Parameters: " +
                              "SpriteBatch fills only the first texture slot, so a sprite texture read this way reaches only " +
                              "the first sampler.",
                    Severity: ShaderErrorSeverity.Warning)]);
            }
            // Issue #446, after the binding rewrite (whose alias offsets index `text` above): the
            // same POSITION -> SV_Position output mapping DirectX 12 gets. Same-line edits only, so
            // the dead-line view still lines up.
            text = DxcVertexPositionOutputRewriter.Rewrite(text, fxParsed.Techniques);
            return vulkanSources[(entryPoint, stage)] = preprocessed with { Text = text };
        }

        // OpenGL sampler-register reservations (issue #283): which modern
        // `SamplerState X : register(sN)` declarations take a GL sampler register out of
        // circulation is decided on the PREPROCESSED source, like mgfxc — not on the raw tokens
        // the pre-parser saw (which counted dead #if branches, missed macro-spelled registers,
        // and never saw #include'd files). Built from the RAW source (the pre-parser's rewrite
        // turns legacy samplers into SamplerState, which must not start reserving), flattened
        // with this compile's macros. A failure to build the view is DEFERRED until the DXC
        // compiles below have accepted the source, so a genuinely malformed shader still
        // reports DXC's own diagnostic; SD0009 only fires when our preprocessor is the one at
        // fault.
        //
        // The sibling map rides on the same view (issue #299): the register an explicit
        // `register(sN)` on a LEGACY `sampler` declaration pins its texture to. Read from the
        // raw tokens it had the same two faults (`#if OPENGL sampler S = …; #else
        // sampler S : register(s1) = …; #endif` pinned unit 1 where mgfxc emits ps_s0, and
        // `register(REG)` through a macro was missed).
        IReadOnlyDictionary<string, int> explicitGlSamplerSlots = fxParsed.ExplicitGlSamplerSlots;
        IReadOnlySet<int> reservedGlSamplerSlots = fxParsed.ReservedGlSamplerSlots;
        IReadOnlySet<string> legacyGlTextures = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> legacySamplerTextureView = new Dictionary<string, string>(StringComparer.Ordinal);
        ShaderError? reservationError = null;
        IReadOnlyDictionary<string, EntryPointMatrixInterpolants>? glEntryInterpolants = null;
        IReadOnlyDictionary<string, int> legacySamplerRegisters = new Dictionary<string, int>(StringComparer.Ordinal);
        DxTextureSlotInputs? dxTextureSlots = null;
        if (options.Target == PlatformTarget.OpenGL)
        {
            // A recovery pass (issue #308) has the flattened raw source in hand already, and its
            // pre-parse names samplers as the preprocessed view does.
            Result<GlSamplerSlots, ShaderError> reservation = recovered is not null
                ? FxPreParser.CollectGlSamplerSlots(recovered.FlattenedRawSource, sourceFileName, fxParsed)
                : GlSamplerReservation.Collect(
                    hlslSource, sourceFileName, macros, includeResolver, options.AdditionalIncludePaths, fxParsed);
            if (reservation.IsSuccess)
            {
                explicitGlSamplerSlots = reservation.Value.Explicit;
                reservedGlSamplerSlots = reservation.Value.Reserved;
                legacySamplerRegisters = reservation.Value.LegacySamplerRegisters;
                legacyGlTextures = reservation.Value.LegacyTextures;
                legacySamplerTextureView = reservation.Value.LegacySamplerTextures;
                glEntryInterpolants = reservation.Value.EntryPointInterpolants;
            }
            else
            {
                reservationError = reservation.Error;
            }

            // The real-slangc route's combined samplers (Sampler2D X : register(sN)) pin their
            // texture like a legacy sampler does; see CompilerOptions.CombinedSamplerGlSlots.
            if (options.CombinedSamplerGlSlots.Count > 0)
            {
                var merged = new Dictionary<string, int>(explicitGlSamplerSlots, StringComparer.Ordinal);
                foreach ((string texture, int slot) in options.CombinedSamplerGlSlots)
                    merged.TryAdd(texture, slot);
                explicitGlSamplerSlots = merged;
            }
        }
        else if (fxParsed.Samplers.Select(s => s.Name)
                     .Concat(fxParsed.SynthesizedSamplerTextures.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Skip(directX ? 0 : 1).Any())
        {
            // The other targets need the same preprocessed view for one thing only: two legacy
            // samplers on one explicit register (SD0227, below). It is read only when two legacy
            // samplers exist at all. A view that cannot be built skips the check rather than
            // failing a compile that never needed the view before; OpenGL, where the view
            // decides the units, keeps failing loudly (SD0009).
            Result<GlSamplerSlots, ShaderError> view = recovered is not null
                ? FxPreParser.CollectGlSamplerSlots(recovered.FlattenedRawSource, sourceFileName, fxParsed)
                : GlSamplerReservation.Collect(
                    hlslSource, sourceFileName, macros, includeResolver, options.AdditionalIncludePaths, fxParsed);
            if (view.IsSuccess)
            {
                legacySamplerRegisters = view.Value.LegacySamplerRegisters;
                legacySamplerTextureView = view.Value.LegacySamplerTextures;
                // DirectX 11 texture slots (DxTextureSlotOrder): fxc puts a legacy sampler's
                // texture on the slot of its first read, ahead of every unregistered modern
                // texture, around every explicit texture register; the backend numbers the SM4
                // rewrite in declaration order. Each DX11 shader whose slots differ is recompiled
                // with them pinned. Left null on every other target and for an effect with no
                // legacy sampler, so those bytes cannot move. The evaluation order is read from
                // the source (HlslReadOrder), the same on every backend and host.
                if (directX && view.Value.LegacySamplerTextures.Count > 0)
                {
                    dxTextureSlots = new DxTextureSlotInputs(
                        new HashSet<string>(view.Value.LegacySamplerTextures.Values, StringComparer.Ordinal),
                        view.Value.LegacySamplerTextures,
                        view.Value.ExplicitTextureRegisters,
                        view.Value.ModernMethodTextures);
                }
            }
            else if (directX && fxParsed.LegacySamplerTextures.Count > 0)
            {
                // Fail loudly: without the view the DirectX 11 texture slots cannot be checked.
                runWarnings.Add(new ShaderError(
                    File: sourceFileName,
                    Line: 0,
                    Column: 0,
                    Code: "SD0230",
                    Message: "DirectX 11: the legacy samplers' textures could not be checked against the slots fxc (and so " +
                             $"mgfxc) gives them: the preprocessed view could not be built ({view.Error.Code}: {view.Error.Message}). " +
                             "They keep the backend's declaration-order slots, so GraphicsDevice.Textures[N] and SpriteBatch's " +
                             "texture (slot 0) can reach a different texture than in mgfxc's build.",
                    Severity: ShaderErrorSeverity.Warning));
            }
        }

        // DirectX 11: a LEGACY `sampler A : register(sN)` binds its SAMPLER at sN in fxc's DXBC
        // (mgfxc 3.8.4.1 /Profile:DirectX_11: `t0 s1` for `register(s1)`), but the SM4 rewrite
        // drops the clause, so the sampler fell to s0 and a state the game set on
        // GraphicsDevice.SamplerStates[N] never reached it. Put the register back on the
        // rewritten `SamplerState A;` for this target only (registers read off the preprocessed
        // view, so a dead #if branch does not count; every rewritten declaration of the name
        // gets the live register, which is harmless in the branch that does not compile). The
        // texture slots are fxc's too (DxTextureSlotOrder, per compiled shader, after the
        // compile). OpenGL decides units from the
        // register itself (SpirvCombinedSamplerPairs.ResolveSlots); DirectX 12 and Vulkan are
        // left as they were: mgfxc 3.8.5 offers no reference for legacy samplers there.
        if (directX && legacySamplerRegisters.Count > 0)
        {
            glCompileSource = glCompileSource with
            {
                Text = PinLegacySamplerRegisters(glCompileSource.Text, legacySamplerRegisters),
            };
        }

        // The pinned register can collide with a MODERN `SamplerState S : register(sN)` the same
        // shader reads; fxc (mgfxc 3.8.4.1 /Profile:DirectX_11) refuses that with X4500 at the
        // modern declaration, and vkd3d with its own E5015 ("Multiple variables bound to space
        // 115"). Report it as SD0227, naming both samplers, with vkd3d's text kept raw.
        ShaderError ExplainDirectXRegisterClash(ShaderError error)
        {
            if (!directX || legacySamplerRegisters.Count == 0 || !string.Equals(error.Code, "E5015", StringComparison.Ordinal))
                return error;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         glCompileSource.Text,
                         @"(?<![A-Za-z0-9_])Sampler(?:Comparison)?State\s+(\w+)\s*:\s*register\s*\(\s*[sS](\d+)\s*\)"))
            {
                string modern = m.Groups[1].Value;
                if (legacySamplerRegisters.ContainsKey(modern)
                    || !int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.None,
                                     System.Globalization.CultureInfo.InvariantCulture, out int slot))
                {
                    continue;
                }
                string? legacy = legacySamplerRegisters
                    .Where(r => r.Value == slot)
                    .Select(r => r.Key)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (legacy is null)
                    continue;
                return error with
                {
                    Code = "SD0227",
                    Message = $"legacy sampler '{legacy}' and sampler '{modern}' are both bound to register s{slot} and both " +
                              "read by the same shader. fxc (and so mgfxc) refuses this on DirectX 11 (X4500: overlapping " +
                              $"register semantics not yet implemented 's{slot}'); give each sampler its own register.",
                    RawDiagnostics = error.RawDiagnostics ?? error.FxcFormattedMessage,
                };
            }
            return error;
        }

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
                        vulkan ? VulkanShaderSource(pass.VertexEntryPoint, ShaderStage.Vertex) : glCompileSource,
                        pass.VertexEntryPoint,
                        ShaderStage.Vertex,
                        options.Target,
                        compileOptions,
                        // VS-bearing GL passes are now MonoGame-rewritten too (Phase 28):
                        // the rewrite is stage-symmetric, so the VS gets the vs_uniforms_vec4
                        // + attribute/varying contract that lets MonoGame's GL runtime link it.
                        applyMonoGameGlsl: monoGameGl,
                        reflectFromSpirv: reflectFromSpirv,
                        // The explicit register(sN) indices of legacy samplers (which the SM4 rewrite
                        // drops), read off the preprocessed view, so the GLSL rewriter numbers ps_s{slot} the
                        // same way the .mgfx sampler table will (issues #189, #299).
                        explicitGlSamplerSlots: explicitGlSamplerSlots,
                        reservedGlSamplerSlots: reservedGlSamplerSlots,
                        glTextureDeclarationOrder: options.GlTextureDeclarationOrder,
                        legacyGlTextures: legacyGlTextures,
                        glInterpolants: GlInterpolantPacking(glEntryInterpolants, pass.VertexEntryPoint, ShaderStage.Vertex),
                        dxTextureSlots: dxTextureSlots,
                        cancellationToken);

                    if (compileOutput.Blob.IsFailure)
                    {
                        compileFailure = LegacySamplerRecoveryCandidate(
                            options.Target, recovered, sourceFileName, macros, includeResolver);
                        return Fail(ExplainDirectXRegisterClash(compileOutput.Blob.Error), runWarnings);
                    }

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
                        ShaderModel = KnifxShaderModel.Parse(pass.VertexProfile),
                        // Diagnostic strings written only by MGFX v11+ (ignored by v10/KNIFX).
                        SourceFile = EmbeddedSourceFile(options),
                        Entrypoint = pass.VertexEntryPoint ?? "<unknown>",
                    });
                }

                if (pass.PixelEntryPoint is not null)
                {
                    var compileOutput = CompileEntryPoint(
                        dxcCompiler,
                        dxbcCompiler,
                        glslTranspiler,
                        vulkan ? VulkanShaderSource(pass.PixelEntryPoint, ShaderStage.Pixel) : glCompileSource,
                        pass.PixelEntryPoint,
                        ShaderStage.Pixel,
                        options.Target,
                        compileOptions,
                        applyMonoGameGlsl: monoGameGl,
                        reflectFromSpirv: reflectFromSpirv,
                        // The explicit register(sN) indices of legacy samplers (which the SM4 rewrite
                        // drops), read off the preprocessed view, so the GLSL rewriter numbers ps_s{slot} the
                        // same way the .mgfx sampler table will (issues #189, #299).
                        explicitGlSamplerSlots: explicitGlSamplerSlots,
                        reservedGlSamplerSlots: reservedGlSamplerSlots,
                        glTextureDeclarationOrder: options.GlTextureDeclarationOrder,
                        legacyGlTextures: legacyGlTextures,
                        glInterpolants: GlInterpolantPacking(glEntryInterpolants, pass.PixelEntryPoint, ShaderStage.Pixel),
                        dxTextureSlots: dxTextureSlots,
                        cancellationToken);

                    if (compileOutput.Blob.IsFailure)
                    {
                        compileFailure = LegacySamplerRecoveryCandidate(
                            options.Target, recovered, sourceFileName, macros, includeResolver);
                        return Fail(ExplainDirectXRegisterClash(compileOutput.Blob.Error), runWarnings);
                    }

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
                        ShaderModel = KnifxShaderModel.Parse(pass.PixelProfile),
                        // Diagnostic strings written only by MGFX v11+ (ignored by v10/KNIFX).
                        SourceFile = EmbeddedSourceFile(options),
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

                    // The text this stage was compiled from (on Vulkan its own per-shader rewrite), for locating diagnostics.
                    string stageSource = vulkan
                        ? VulkanShaderSource(blobIndex == vsIndex ? pass.VertexEntryPoint! : pass.PixelEntryPoint!, blobIndex == vsIndex ? ShaderStage.Vertex : ShaderStage.Pixel).Text
                        : glCompileSource.Text;

                    // Issue #324: an ARRAY of textures or samplers has no representation in
                    // MonoGame's Vulkan effect format (real mgfxc 3.8.5 reflects it as nothing
                    // at all), and used to be silently dropped here too. Refuse it by name. On
                    // DirectX 12 the table is mgfxc's (one parameter on the first slot) and ships
                    // unchanged, but elements beyond [0] read as zero in the real engine, so the
                    // consumer is warned.
                    //
                    // Issue #340: an ARRAY of samplers is refused by real mgfxc on every profile
                    // in its own parser, so no reference table exists to match; DirectX 11 and
                    // DirectX 12 refuse it by name too (SD0224) instead of compiling an effect
                    // mgfxc never builds. (On DirectX 11 the per-element RDEF records of an
                    // array have already been folded into one binding by the DXBC extractor,
                    // issue #339, so a texture array there is mgfxc's one `Tex` parameter.)
                    if ((directX || options.Target == PlatformTarget.DirectX12)
                        && ResourceArrayDiagnostics.DirectXSamplerArrayError(reflected, glCompileSource.Text, sourceFileName, options.Target,
                                                                              options.SamplerArraysFromCombinedSamplers) is { } samplerArrayError)
                    {
                        return Fail(samplerArrayError, runWarnings);
                    }
                    if (options.Target == PlatformTarget.Vulkan
                        && ResourceArrayDiagnostics.VulkanError(reflected, stageSource, sourceFileName) is { } arrayError)
                    {
                        return Fail(arrayError, runWarnings);
                    }

                    // Vulkan: a texture's or sampler's slot is its SPIR-V binding minus 32, as
                    // mgfxc writes it and as MonoGame's DesktopVK descriptor writer reads it
                    // (binding - 32). The reflector's ordinal numbering only coincides with that
                    // when the registers have no gap from 0, so an explicit `register(t1)` with
                    // nothing at t0 used to land its parameter on the wrong device slot.
                    // Every host reflects Vulkan from SPIR-V, so desktop, browser and Android
                    // all go through this one rule.
                    if (options.Target == PlatformTarget.Vulkan)
                    {
                        Result<ReflectedEffect, ShaderError> slotted =
                            VulkanResourceSlots.FromBindings(reflected, compiledShaderBlobs[blobIndex].Stage, stageSource, sourceFileName);
                        if (slotted.IsFailure)
                        {
                            // A first pass only: the retry from the preprocessed source (Run)
                            // is never retried again.
                            if (recovered is null)
                                compileFailure = new ShaderCompileFailure(sourceFileName, macros, includeResolver) { UnpinnedVulkanResource = true };
                            return Fail(slotted.Error, runWarnings);
                        }
                        reflected = slotted.Value;
                    }
                    if (options.Target == PlatformTarget.DirectX12
                        && ResourceArrayDiagnostics.DirectX12Warning(reflected, glCompileSource.Text, sourceFileName) is { } arrayWarning)
                    {
                        AccumulateWarnings(runWarnings, seenWarnings, [arrayWarning]);
                    }

                    foreach (ConstantBufferReflection cb in reflected.ConstantBuffers)
                    {
                        if (seenCbufferNames.Add(cb.Name))
                            allConstantBuffers.Add(cb);
                    }

                    foreach (ParameterReflection param in reflected.Parameters)
                    {
                        // A Vulkan texture ALIAS (a second declaration of one texture, for its
                        // second sampler; VulkanTextureSamplerBindingRewriter) is not an effect
                        // parameter of its own: its record binds the original's.
                        if (options.Target == PlatformTarget.Vulkan && IsVulkanTextureAlias(param.Name))
                        {
                            // Normally the original is in the same shader and owns the parameter.
                            // If DXC removed it (only the alias is live), the alias IS the texture:
                            // keep it under the original's name so its record still has a parameter.
                            string baseName = VulkanTextureAliasBase(param.Name);
                            if (reflected.Parameters.Any(p => p.Name == baseName))
                                continue;
                            if (seenParamNames.Add(baseName))
                                allParameters.Add(param with { Name = baseName });
                            continue;
                        }
                        if (seenParamNames.Add(param.Name))
                            allParameters.Add(param);
                    }

                    // Capture this shader's resource bindings for its .mgfx record.
                    shaderTextures[blobIndex]     = reflected.Textures;
                    shaderSamplers[blobIndex]     = reflected.Samplers;
                    shaderCbufferNames[blobIndex] = reflected.ConstantBuffers.Select(c => c.Name).ToList();
                    shaderSpirv[blobIndex]        = spirvBlob;
                    shaderNumericParams[blobIndex] = reflected.Parameters
                        .Where(p => p.Class is EffectParameterClass.Scalar
                                             or EffectParameterClass.Vector
                                             or EffectParameterClass.Matrix)
                        .ToList();
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

        // Every DXC compile accepted the source, so a reservation view that could not be built
        // is ShadowDusk's own preprocessor at fault: fail loudly rather than ship output whose
        // GL sampler registers were allocated on a guess (issue #283).
        if (reservationError is not null)
            return Fail(reservationError, runWarnings);

        // fxc (so mgfxc, and FNA's reference) refuses two legacy samplers that one entry point
        // reads through one explicit register: `sampler2D A : register(s0); sampler2D B :
        // register(s0);` is X4500 "overlapping register semantics not yet implemented 's0'" on
        // OpenGL and DirectX_11 (measured, mgfxc 3.8.4.1). The SM4 rewrite drops the clause, so
        // without this the compile succeeded and quietly gave B the next unit. Only samplers the
        // compiled stage actually reflects count, as in fxc: a second declaration nothing reads
        // compiles in mgfxc too.
        if (OverlappingLegacySamplerRegister(shaderSamplers, legacySamplerRegisters, hlslSource, sourceFileName)
            is { } overlapError)
        {
            return Fail(overlapError, runWarnings);
        }

        // A legacy sampler that binds no texture (`sampler2D A;`) is ONE object to mgfxc, and
        // the effect parameter it emits for it is named `A` on every profile. The SM4 rewrite
        // had to split it into `Texture2D A_SDTexture; SamplerState A;`, so reflection names
        // the texture after the synthesized declaration. Give it the sampler's name back, and
        // drop the standalone sampler parameter of that same name, which on OpenGL and Vulkan
        // would otherwise sit beside it bound to nothing (setting it did not reach the
        // texture). Done before ANY index into the table is taken, so the cbuffer and sampler
        // records below all see the final list; texture lookups go through
        // TextureParameterName. Rendering is unchanged: the records point at the same texture.
        //
        // KNIFX is the exception, because KNI's own content pipeline is its reference: there such a
        // sampler is NO effect parameter and its sampler record binds -1 (measured 2026-10-28, KNI
        // 4.2.9001 and 4.3.9001, DesktopGL and Windows: `sampler s0;`, `sampler2D S : register(s0);`
        // and a `sampler_state` without `Texture =` alike; a textured sampler beside it is unchanged).
        // It matters at run time: KNI's EffectPass.Apply writes every bound texture parameter into
        // GraphicsDevice.Textures, so a parameter the game never set replaced the texture it bound
        // directly with null (KNI EffectTest OldSyntax_x / OldSyntax_s0s1). MGFX keeps mgfxc's shape.
        bool kniTexturelessSamplers = effectiveContainer == EffectContainer.Knifx;
        HashSet<string> unboundSamplerTextures = kniTexturelessSamplers
            ? DropSynthesizedSamplerParameters(allParameters, fxParsed.SynthesizedSamplerTextures)
            : new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> textureParameterNames = kniTexturelessSamplers
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ApplySynthesizedSamplerParameterNames(allParameters, fxParsed.SynthesizedSamplerTextures);
        string TextureParameterName(string textureName)
        {
            // Only Vulkan declares aliases; elsewhere a name ending in the suffix is the author's own.
            if (options.Target == PlatformTarget.Vulkan)
                textureName = VulkanTextureAliasBase(textureName);
            return textureParameterNames.TryGetValue(textureName, out string? parameterName) ? parameterName : textureName;
        }
        // The effect parameter a sampler record binds for a reflected texture: -1 for a KNIFX
        // texture-less legacy sampler (deliberate, as KNI writes it), else the join by name. A
        // miss of that join (-1) fails loudly: in the MGFX writer (SD0022), and for KNIFX, where
        // -1 is a legal "no parameter" the writer cannot tell apart, through SD0018 below.
        var unjoinedSamplerTextures = new List<string>();
        int TextureParameterIndex(string textureName)
        {
            if (unboundSamplerTextures.Contains(textureName))
                return -1;
            int index = IndexOfParam(allParameters, TextureParameterName(textureName));
            if (index < 0 && kniTexturelessSamplers)
                unjoinedSamplerTextures.Add(textureName);
            return index;
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
                if (!shaderUniformLayouts.TryGetValue(i, out var layout))
                    layout = Array.Empty<MonoGameGlslUniform>();

                string cbName = compiledShaderBlobs[i].Stage == ShaderStage.Vertex
                    ? "vs_uniforms_vec4"
                    : "ps_uniforms_vec4";

                var paramIndices = new List<int>(layout.Count);
                var paramOffsets = new List<ushort>(layout.Count);
                var backedParamIndices = new HashSet<int>();
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
                        // legal+required GLSL; also underscore runs and collisions), so the
                        // GLSL layout's name no longer
                        // matches the reflected parameter (still `noise`) and the name
                        // join misses. Recover the parameter by DECLARATION ORDER: the
                        // reflected cbuffer variable at this member's position carries the
                        // ORIGINAL name, so the parameter is found without trusting the
                        // SPIRV-Cross-emitted spelling. Never by byte offset: packing makes
                        // offsets differ between the DXIL and SPIR-V reflections (issue
                        // #425). The parameter stays exposed under its original name in the
                        // .mgfx; only the INDEX is needed here.
                        paramIndex = IndexOfParamByDeclarationOrder(
                            allConstantBuffers, allParameters, layout, u);
                    }
                    if (paramIndex < 0)
                        return Fail(new ShaderError(
                            File: sourceFileName,
                            Line: 0,
                            Column: 0,
                            Code: "SD0012",
                            Message: $"GL uniform '{u.Name}' (shader #{i}) has no matching " +
                                     "effect parameter: the GLSL uniform layout and the reflected " +
                                     "parameter list diverged. SPIRV-Cross renames some uniforms " +
                                     "(a GLSL reserved word, noise -> _noise; a run of underscores, " +
                                     "my__val -> my_val; a name collision, _noise_1); a renamed " +
                                     "uniform can only be mapped back when the stage's uniforms are " +
                                     "all loose globals or in one cbuffer, so rename it otherwise"), runWarnings);
                    paramIndices.Add(paramIndex);
                    paramOffsets.Add((ushort)(u.BaseRegister * 16));
                    backedParamIndices.Add(paramIndex);
                }

                // Issue #187: the join above is GLSL-layout -> parameter only, so a
                // parameter REFLECTION reports for this stage that the shipped GLSL never
                // declares (DXC's -spirv backend folded its only reads and dropped the
                // cbuffer; fxc — and the reflection source — keep it) would end up with
                // no cbuffer membership at all: present in Parameters[], SetValue writes
                // nowhere. Complete the join in the other direction: append a register
                // slot for each such parameter so the .mgfx carries the same
                // parameter -> cbuffer -> declared-array chain the mgfxc golden has.
                // The data uploads (or is link-stripped to a silent, spec-sanctioned
                // skip) and nothing reads it — structurally identical to what real
                // mgfxc's DirectX profile ships for any declared-but-unused uniform.
                // Scalar/Vector/Matrix only: an Object parameter is never cbuffer-backed,
                // and a Struct's footprint is not derivable from rows*elements, so it is
                // left alone (conservative; no corpus fixture hits either case).
                int synthesizedRegisters = 0;
                if (shaderNumericParams.TryGetValue(i, out var reflectedNumeric))
                {
                    foreach (ParameterReflection p in reflectedNumeric)
                    {
                        int paramIndex = IndexOfParam(allParameters, p.Name);
                        if (paramIndex < 0)
                            // Impossible by construction (shaderNumericParams entries come
                            // from the same reflected.Parameters lists that populate
                            // allParameters, deduped under the same Ordinal comparer) —
                            // fail loudly like the mirrored layout->parameter join above
                            // rather than silently shipping an unbacked parameter.
                            return Fail(new ShaderError(
                                File: sourceFileName,
                                Line: 0,
                                Column: 0,
                                Code: "SD0012",
                                Message: $"internal: reflected parameter '{p.Name}' (shader #{i}) is " +
                                         "missing from the effect parameter table — the per-shader " +
                                         "reflection capture and the accumulated parameter list diverged"), runWarnings);
                        if (backedParamIndices.Contains(paramIndex))
                            continue;
                        // Footprint follows the RUNTIME'S write model, not the HLSL shape:
                        // MonoGame/KNI upload a Matrix parameter TRANSPOSED —
                        // ConstantBuffer.SetParameter writes ColumnCount 16-byte rows —
                        // so a Matrix must be sized by Columns (float2x4 ⇒ 4 registers;
                        // Rows would under-allocate and Buffer.BlockCopy throws on the
                        // FIRST EffectPass.Apply). float4x4 is unchanged (4 either way).
                        // Scalars/vectors are one register per row (= 1), arrays once per
                        // element — matching the rewriter's allocator for the live shapes
                        // it models (docs/glsl-uniform-naming.md).
                        int perElement = p.Class == EffectParameterClass.Matrix
                            ? Math.Max(1, p.Columns)
                            : Math.Max(1, p.Rows);
                        int registers = perElement * Math.Max(1, p.Elements);
                        paramIndices.Add(paramIndex);
                        paramOffsets.Add((ushort)(sizeRegisters * 16));
                        backedParamIndices.Add(paramIndex);
                        sizeRegisters += registers;
                        synthesizedRegisters += registers;
                    }
                }

                if (paramIndices.Count == 0)
                    continue;

                if (synthesizedRegisters > 0)
                {
                    // The GLSL must declare the array the cbuffer record names, sized to
                    // cover the synthesized slots, or MonoGame's location lookup and the
                    // declared-vs-recorded sizes fall out of sync structurally. (The GL
                    // driver may still link-strip the unread tail or the whole array;
                    // both are defined-ignored at glUniform4fv time.)
                    compiledShaderBlobs[i] = PatchGlUniformArrayDeclaration(
                        compiledShaderBlobs[i], cbName, sizeRegisters);
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

        // Global initializer defaults (`float R = 1;`): DXC and vkd3d drop the initializer, so it is
        // read off the preprocessed source and carried to the writers (deliberate deviation from
        // mgfxc, project_decisions.md). Every MGFX and KNIFX output gets them.
        // A row_major matrix's default blob is written in register order (issue #423 follow-up). Not on
        // OpenGL: every GL compile is row-major (-Zpr) and ShadowDusk's own rewriter rebuilds the
        // matrix from the column-by-column blob, which is render-proven there.
        if (options.Target == PlatformTarget.OpenGL)
        {
            for (int i = 0; i < allParameters.Count; i++)
                if (allParameters[i].IsRowMajor)
                    allParameters[i] = allParameters[i] with { IsRowMajor = false, IsHeaderTransposed = false };
        }

        IReadOnlyDictionary<string, GlobalInitializerDefault> parameterDefaults =
            ResolveParameterDefaults(preprocessed.Text, sourceFileName, allParameters, runWarnings);
        IReadOnlyList<EffectParameterInfo> effectParameterInfoList =
            BuildEffectParameterInfoList(allParameters, parameterDefaults, runWarnings);

        // Issue #413: the reflection result carries the very default blobs the writers are about to
        // write, so the two cannot disagree.
        IReadOnlyList<ParameterReflection> reflectedParameters =
            ParameterDefaultValues.Attach(allParameters, effectParameterInfoList, parameterDefaults);

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
                        // A LEGACY sampler's texture pairs with that sampler by name (DirectX 11:
                        // mgfxc's record carries the sampler's own DXBC binding, which is not the
                        // texture's when a register or a modern SamplerState moved it). Otherwise
                        // slot first (the 1:1 modern shape), then the sole shared sampler.
                        SamplerReflection? legacySamp = directX
                            ? samplerRefs.FirstOrDefault(s =>
                                legacySamplerTextureView.TryGetValue(s.Name, out string? t)
                                && string.Equals(t, tex.Name, StringComparison.Ordinal))
                            : null;
                        SamplerReflection? matchedSamp =
                            legacySamp
                            ?? samplerRefs.FirstOrDefault(s => s.BindSlot == slot)
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
                            SamplerSlot: (byte)(legacySamp?.BindSlot ?? slot),
                            // DX11 binds samplers via the DXBC resource table, not by name, so
                            // the name is empty — matching the mgfxc DirectX_11 goldens. DX12
                            // keeps the positional form it has been rung-4 proven with (Phase
                            // 54); real mgfxc writes the HLSL sampler name there instead, a
                            // separate divergence tracked in Phase 51, not changed here because
                            // it needs its own DX12 render re-proof.
                            Name:        directX ? string.Empty : $"ps_s{slot}",
                            Parameter:   TextureParameterIndex(tex.Name),
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
                    IReadOnlyList<int> glSamplerSlots =
                        SpirvCombinedSamplerPairs.ResolveSlots(
                            pairs, explicitGlSamplerSlots, reservedGlSamplerSlots, options.GlTextureDeclarationOrder, legacyGlTextures);

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
                            // The GL texture unit: MonoGame's GL runtime does
                            // glUniform1i(location("ps_s{slot}"), TextureSlot) and then binds
                            // Parameters[Parameter]'s texture to that unit. NOT the record
                            // index — SPIRV-Cross declares combined samplers in FIRST-USE
                            // order, while fxc (and therefore mgfxc) allocates GL sampler slots
                            // in DECLARATION order. Numbering by the record index put a
                            // `register(s0)` sampler on unit 1 whenever it was not also
                            // sampled first, and under SpriteBatch — which forces the sprite
                            // onto unit 0 right after EffectPass.Apply — that silently swapped
                            // textures (GitHub issue #189, render-proved by
                            // validation/SamplerRegisterOrderGl). ResolveSlots is the single
                            // definition of the rule, shared with the GLSL rewriter so the
                            // records and the uniforms they name cannot drift apart.
                            TextureSlot: (byte)glSamplerSlots[k],
                            SamplerSlot: (byte)glSamplerSlots[k],
                            Name:        $"ps_s{glSamplerSlots[k]}",
                            Parameter:   TextureParameterIndex(pair.TextureName),
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
                    // Emit the records in TEXTURE UNIT order, which is how mgfxc's own OpenGL
                    // goldens lay the table out (record[0] names ps_s0, record[1] ps_s1, …).
                    // The pairs arrive in SPIRV-Cross's first-use order, so without this the
                    // table would carry the right slots in a gratuitously different order.
                    // MonoGame itself just iterates the records and reads each one's fields, so
                    // this is a faithfulness/diffability change, not a binding fix.
                    samplers.Sort((a, b) => a.TextureSlot.CompareTo(b.TextureSlot));

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
                    //
                    // The slot is the binding minus 32 (VulkanResourceSlots, applied right after
                    // reflection), mgfxc's own rule and the one MonoGame's descriptor writer
                    // reads back, so an explicit register(t1) is slot 1 as on DirectX.
                    foreach (SamplerReflection samp in samplerRefs)
                    {
                        int slot = samp.BindSlot;
                        // Pair the sampler with the texture on its descriptor (the same raw
                        // binding, which is what makes the two one combined descriptor), then
                        // first, so the sampler-type byte can carry the texture's DIMENSION and
                        // the record names the texture's parameter.
                        TextureReflection? matchedTex =
                            textureRefs.FirstOrDefault(t => t.RawBinding == samp.RawBinding)
                            ?? textureRefs.FirstOrDefault();
                        // No Math.Max(0, …) clamp — see the DirectX branch (bug-hunt N11).
                        int paramIndex = matchedTex is null
                            ? 0
                            : TextureParameterIndex(matchedTex.Name);
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

                    // A texture with NO sampler on its binding (read with Load / operator[]) is a
                    // SAMPLED_IMAGE descriptor, which DesktopVK binds, but with no record above its
                    // parameter was never bound to a device slot and the shader read (0,0,0,0).
                    // mgfxc 3.8.5 writes such a texture one record of its own: texture slot =
                    // binding - 32, sampler slot 255 (none), an empty name, no state (measured; its
                    // build draws the texture in real DesktopVK). Written the same way, in slot order.
                    foreach (TextureReflection tex in textureRefs
                                 .Where(t => samplerRefs.All(s => s.RawBinding != t.RawBinding))
                                 .OrderBy(t => t.BindSlot))
                    {
                        samplers.Add(new MgfxSamplerInfo(
                            Type:        SamplerTypeByte(tex.Dimension),
                            TextureSlot: (byte)tex.BindSlot,
                            SamplerSlot: byte.MaxValue,
                            Name:        string.Empty,
                            Parameter:   IndexOfParam(allParameters, TextureParameterName(tex.Name)),
                            State:       null));
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

        if (UnjoinedSamplerTextureError(unjoinedSamplerTextures, options.SourceFileName) is { } unjoined)
            return Fail(unjoined, runWarnings);

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
            var knifxResult = new KnifxWriter().Write(ir, new KnifxWriterOptions(
                knifxBackend, options.Profile?.KnifxVersion ?? KnifxWriter.KnifxVersion));
            if (knifxResult.IsFailure)
                return Fail(knifxResult.Error, runWarnings);
            return Result<CompiledShader, ShaderError[]>.Ok(
                new CompiledShader(options.Target, knifxResult.Value) { Warnings = WithViewWarning(runWarnings, techniqueViewWarning), Parameters = reflectedParameters });
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
            new CompiledShader(options.Target, mgfxBytes) { Warnings = WithViewWarning(runWarnings, techniqueViewWarning), Parameters = reflectedParameters });
        }
        finally
        {
            if (dxcCompiler.IsValueCreated)
                (dxcCompiler.Value as IDisposable)?.Dispose();
        }
    }

    // The source-file string an MGFX v11 container stores per shader (issue #274). It is the
    // ONLY place a caller's SourceFileName reaches the bytes of a non-debug compile, so it has
    // its own option: a content build compiles from an absolute path (the diagnostics and
    // #include resolution need one) but must not record it. Unset, it is SourceFileName as
    // passed, which is what the mgfxc CLI writes for its own source argument; "<unknown>" is
    // mgfxc's null-fallback. Diagnostics never read this: they use SourceFileName.
    private static string EmbeddedSourceFile(CompilerOptions options) =>
        options.EmbeddedSourceFileName ?? options.SourceFileName ?? "<unknown>";

    // The per-run D3D-bytecode memo (issue #255) for the DirectX and FNA backends. The internal
    // CompilerOptions.BypassDxbcMemo seam (issue #358) skips it so a test can prove memo and
    // no-memo compiles give the same bytes; every real compile takes the memo.
    private static IDxbcShaderCompiler WithDxbcMemo(IDxbcShaderCompiler backend, CompilerOptions options) =>
        options.BypassDxbcMemo ? backend : new MemoizingDxbcCompiler(backend);

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
        string? entryPoint = null,
        bool kniDirectXFloor = false)
    {
        // A missing profile cannot be validated (and never reaches a compile with a real
        // target) — leave the existing SM3 fallback behavior untouched.
        if (string.IsNullOrEmpty(profile))
            return null;

        // Cheap path: an already-known literal profile is accepted with no expansion.
        // (IsKnownProfile is case-insensitive, so the lowercased form is fine here.)
        if (FxPreParser.IsKnownProfile(profile))
            return ResolvedProfileChecks(
                profile, stage, span, sourceFileName, enforceStagePrefix, enforceDirectX11Floor, entryPoint, kniDirectXFloor);

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
                expanded, stage, span, sourceFileName, enforceStagePrefix, enforceDirectX11Floor, entryPoint, kniDirectXFloor);

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
        string? entryPoint,
        bool kniDirectXFloor)
    {
        ShaderError? stagePrefix = StagePrefixCheck(knownProfile, stage, span, sourceFileName, enforceStagePrefix);
        if (stagePrefix is not null)
            return stagePrefix;

        if (!enforceDirectX11Floor)
            return null;
        return kniDirectXFloor
            ? KniDirectXFloorCheck(knownProfile, stage, span, sourceFileName, entryPoint)
            : DirectX11FloorCheck(knownProfile, stage, span, sourceFileName, entryPoint);
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
    ///
    /// <para>This is the MGFX container's floor. A KNIFX container on DirectX is checked
    /// against KNI's own pipeline instead (<see cref="KniDirectXFloorCheck"/>), which also
    /// accepts <c>*_2_0</c> and <c>*_3_0</c>.</para>
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
    /// The DirectX floor for a KNIFX container: the compile targets KNI's own DirectX pipeline
    /// accepts (<see cref="KniDirectXProfile.Accepts"/>), which are mgfxc's DirectX_11 set plus
    /// <c>*_2_0</c> and <c>*_3_0</c> (compiled as <c>*_4_0_level_9_1</c> / <c>*_4_0_level_9_3</c>).
    /// Everything else KNI's MGCB refuses ("Invalid profile 'ps_2_a'. Pixel shader 'P'.", or for SM1
    /// "... must be at least SM 2.0.", measured on 4.2.9001 and 4.3.9001), and so does this check,
    /// with the same <c>SD0015</c> the MGFX floor reports.
    /// </summary>
    private static ShaderError? KniDirectXFloorCheck(
        string knownProfile, ShaderStage stage, SourceSpan? span, string sourceFileName, string? entryPoint)
    {
        if (KniDirectXProfile.Accepts(knownProfile))
            return null;

        string stageWord = stage == ShaderStage.Vertex ? "vertex" : "pixel";
        string prefix    = stage == ShaderStage.Vertex ? "vs" : "ps";
        string named     = entryPoint is null ? string.Empty : $" (the {stageWord} shader '{entryPoint}')";
        return new ShaderError(
            File: sourceFileName,
            Line: span?.StartLine ?? 0,
            Column: span?.StartColumn ?? 0,
            Code: "SD0015",
            Message: $"compile target '{knownProfile}'{named} is not one KNI's DirectX pipeline accepts — " +
                     "KNI's own effect processor refuses it as an invalid profile. For the KNIFX container on " +
                     $"DirectX, KNI accepts only {prefix}_2_0 and {prefix}_3_0 (which it compiles as " +
                     $"{prefix}_4_0_level_9_1 and {prefix}_4_0_level_9_3), {prefix}_4_0_level_9_1, " +
                     $"{prefix}_4_0_level_9_3, {prefix}_4_0, {prefix}_4_1, and {prefix}_5_0");
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

        // Issue #407: techniques from the preprocessed translation unit, as on the other targets
        // (and as fxc's own effect compiler reads them for fx_2_0).
        Result<EffectTechniques.Resolution, ShaderError> fnaTechniques = EffectTechniques.Resolve(
            hlslSource, sourceFileName, fnaPlatformMacros, includeResolver, options.AdditionalIncludePaths, fxParsed);
        if (fnaTechniques.IsFailure)
            return Fail(fnaTechniques.Error);
        fxParsed = fxParsed with { Techniques = fnaTechniques.Value.Techniques };
        ShaderError? fnaTechniqueViewWarning = fnaTechniques.Value.ViewWarning;

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

            // An #include'd technique block is still in vkd3d's input; its technique comes from
            // the preprocessed view now (issue #407), so blank it, as on the other targets.
            if (fnaTechniqueViewWarning is null)
                preprocessed = preprocessed with { Text = FxPreParser.StripTechniqueBlocks(preprocessed.Text, sourceFileName) };
        }

        // Per-stage source: vkd3d 2.1 rejects D3D9 stage-scoped register reservations
        // (register(vs, c0)) — rewrite them per compiling stage. Lazy: most effects
        // have none and many are PS-only.
        string? vsSource = null;
        string? psSource = null;

        // Stage 3: compile each pass's entry points to SM1–3 D3D bytecode and reflect
        // each blob's CTAB (the constant table MojoShader itself binds against).
        // Always vkd3d (never the d3dcompiler oracle); an injected host backend (the
        // WASM vkd3d backend) is the same vkd3d behind a different call mechanism.
        // Memoized per run, like the DirectX path (issue #255).
        IDxbcShaderCompiler fnaCompiler = WithDxbcMemo(
            _dxbcCompilerFactory?.Invoke() ?? new Vkd3dShaderCompiler(), options);
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
                        cancellationToken, pass.VertexProfileFromMacro);
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
                        cancellationToken, pass.PixelProfileFromMacro);
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
            techniqueSources, shaders, ctabs, fxParsed.Samplers, sourceFileName, hlslSource);
        if (buildResult.IsFailure)
            return Fail(buildResult.Error, fnaWarnings);

        var writeResult = new Fx2EffectWriter().Write(buildResult.Value);
        if (writeResult.IsFailure)
            return Fail(writeResult.Error, fnaWarnings);

        return Result<CompiledShader, ShaderError[]>.Ok(
            new CompiledShader(PlatformTarget.Fna, writeResult.Value)
            {
                Warnings   = WithViewWarning(fnaWarnings, fnaTechniqueViewWarning),
                Parameters = Fx2ParameterReflection.From(buildResult.Value.Parameters, preprocessed.Text, sourceFileName),
            });
    }

    private static Result<(Fx2Shader Shader, CtabTable Ctab, IReadOnlyList<ShaderError> Warnings), ShaderError>
        CompileFnaStage(
            IDxbcShaderCompiler compiler,
            string source,
            string sourceFileName,
            string entryPoint,
            string? declaredProfile,
            ShaderStage stage,
            CancellationToken ct,
            bool profileFromMacro = false)
    {
        Result<string, ShaderError> profileResult =
            ResolveFnaProfile(declaredProfile, stage, sourceFileName, profileFromMacro);
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
            return Result<(Fx2Shader, CtabTable, IReadOnlyList<ShaderError>), ShaderError>.Fail(
                WaveQuadIntrinsics.Relabel(compileResult.Error, PlatformTarget.Fna, "FNA compiler") ?? compileResult.Error);

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
    /// vs_3_0/ps_3_0); a literal SM1 profile fails loudly too (vkd3d 2.1's ps_1_x
    /// backend has known instruction gaps and MojoShader's ps_1_x rules differ wholesale
    /// from SM2+ — never validated here, so refuse rather than risk silently-wrong
    /// output); anything else (no profile, or a token that is not a profile) defaults to
    /// the SM3 ceiling for the stage.
    ///
    /// <para>A profile a macro expands to (<paramref name="fromMacro"/>: the techniques come
    /// from the preprocessed source since issue #407, so <c>compile PS_SHADERMODEL P()</c>
    /// arrives expanded) is honored like a literal one when it is an SM 2–3 profile of the
    /// right stage. Any other expansion keeps the SM3 ceiling, as it did when the macro was
    /// never expanded: MonoGame's standard header defines <c>vs_4_0_level_9_1</c> in the arm
    /// every non-OpenGL target takes, FNA included, and refusing it would break that header
    /// on FNA for no output the author asked for.</para>
    /// </summary>
    internal static Result<string, ShaderError> ResolveFnaProfile(
        string? declaredProfile, ShaderStage stage, string sourceFileName, bool fromMacro = false)
    {
        string fallback = stage == ShaderStage.Vertex ? "vs_3_0" : "ps_3_0";

        if (fromMacro && declaredProfile is { Length: >= 4 } expanded &&
            !(expanded.StartsWith(stage == ShaderStage.Vertex ? "vs_" : "ps_", StringComparison.Ordinal) &&
              expanded[3] is '2' or '3'))
        {
            return Result<string, ShaderError>.Ok(fallback);
        }

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
                         "supports Shader Model 2–3 here: vkd3d 2.1's SM1 backend has known " +
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

    /// <summary>
    /// What the OpenGL rewriter must know about ONE entry point's matrix interpolants, from the
    /// declarations its own signature carries in the preprocessed source (DXC's GLSL is identical
    /// for row_major and column_major, and for <c>float1xN</c> and a vector, while fxc lays each out
    /// differently). The vertex stage's interpolants are what it writes, the pixel stage's what it
    /// reads; each stage is laid out as fxc lays out its own declaration, so two techniques, or a
    /// VS and a PS, may declare one semantic differently. Every matrix semantic the signature
    /// declares is listed, so the rewrite can refuse a matrix interpolant it has no declaration
    /// for instead of guessing column packing. A degenerate <c>float1xN</c> / <c>row_major
    /// floatNx1</c> interpolant is split over N one-component registers, as fxc lays it out; as a
    /// vertex INPUT (fxc: N attributes) it is refused, as is one semantic declared twice in one
    /// signature.
    /// </summary>
    private static MonoGameGlslInterpolantPacking GlInterpolantPacking(
        IReadOnlyDictionary<string, EntryPointMatrixInterpolants>? entryPoints, string? entryPoint, ShaderStage stage)
    {
        var rowMajor = new HashSet<string>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        var refusedInputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmodelled = new List<string>();
        if (entryPoints is null || entryPoint is null || !entryPoints.TryGetValue(entryPoint, out EntryPointMatrixInterpolants? found))
            return new MonoGameGlslInterpolantPacking(rowMajor, refused) { Declared = declared, RefusedVertexInputs = refusedInputs };

        static string Degenerate(MatrixInterpolantDeclaration d) =>
            $"'{d.Name}' is a {(d.RowMajor ? "row_major " : "")}{d.Rows}x{d.Columns} matrix, which fxc spreads over " +
            $"{Math.Max(d.Rows, d.Columns)} registers of one component each, but which reaches the OpenGL rewrite as " +
            "a single vector attribute; declare it as a vector instead.";

        bool vertex = stage == ShaderStage.Vertex;
        IReadOnlyList<MatrixInterpolantDeclaration> interpolants = vertex ? found.Outputs : found.Inputs;
        var split = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (IGrouping<string, MatrixInterpolantDeclaration> group in interpolants.GroupBy(d => d.Semantic))
        {
            if (group.Count() > 1)
            {
                refused[group.Key] = $"'{entryPoint}' declares it more than once.";
                continue;
            }
            MatrixInterpolantDeclaration only = group.Single();
            if (only.IsDegenerate)
            {
                // fxc gives a float1xN (column_major) / row_major floatNx1 N registers of one
                // component; DXC hands the rewrite one vecN, so the rewrite splits it the same way.
                split[group.Key] = Math.Max(only.Rows, only.Columns);
                continue;
            }
            declared.Add(group.Key);
            if (only.RowMajor)
                rowMajor.Add(group.Key);
        }
        if (vertex)
        {
            foreach (MatrixInterpolantDeclaration d in found.Inputs.Where(d => d.IsDegenerate))
                refusedInputs[d.Semantic] = Degenerate(d);
        }
        unmodelled.AddRange(found.Unmodelled);
        return new MonoGameGlslInterpolantPacking(rowMajor, refused)
        {
            Declared = declared,
            Split = split,
            RefusedVertexInputs = refusedInputs,
            Unmodelled = unmodelled,
        };
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
            IReadOnlyDictionary<string, int> explicitGlSamplerSlots,
            IReadOnlySet<int> reservedGlSamplerSlots,
            IReadOnlyDictionary<string, int> glTextureDeclarationOrder,
            IReadOnlySet<string> legacyGlTextures,
            MonoGameGlslInterpolantPacking? glInterpolants,
            DxTextureSlotInputs? dxTextureSlots,
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
                return (Result<byte[], ShaderError>.Fail(
                    WaveQuadIntrinsics.Relabel(dxbcResult.Error, platform, "DXBC compiler") ?? dxbcResult.Error), default, default, noAttributes, noUniforms, noWarnings);

            ReadOnlyMemory<byte> dxbc = dxbcResult.Value.Bytes;
            IReadOnlyList<ShaderError> dxbcWarnings = dxbcResult.Value.Warnings;
            if (dxTextureSlots is not null
                && ReorderDirectX11TextureSlots(dxbcCompiler, dxbcRequest, dxbc, dxTextureSlots, ct) is { } reordered)
            {
                if (reordered.Bytes is { } pinned)
                    dxbc = pinned;
                else if (reordered.Warning is { } slotWarning)
                    dxbcWarnings = [.. dxbcWarnings, slotWarning];
            }
            return (Result<byte[], ShaderError>.Ok(dxbc.ToArray()), dxbc, default, noAttributes, noUniforms, dxbcWarnings);
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
                    // The GL texture unit for each combined sampler, in SPIRV-Cross's own
                    // declaration order — so the uniforms the rewriter emits (ps_s{slot}) carry
                    // the same numbering the .mgfx sampler table will record for them. Both
                    // sides call ResolveSlots on the same SPIR-V, which is what keeps the table
                    // and the GLSL it describes in agreement (issue #189). A failure to model
                    // the shape is NOT fatal here: the sampler-table step re-runs the same
                    // extraction and fails loudly with SD0217 there, so this stays a
                    // null (positional-fallback) rather than duplicating the diagnostic.
                    IReadOnlyList<int>? samplerSlots = null;
                    Result<IReadOnlyList<CombinedSamplerPair>, ShaderError> pairsForSlots =
                        SpirvCombinedSamplerPairs.Extract(spirvResult.Value.Bytes);
                    if (pairsForSlots.IsSuccess)
                    {
                        samplerSlots = SpirvCombinedSamplerPairs.ResolveSlots(
                            pairsForSlots.Value, explicitGlSamplerSlots, reservedGlSamplerSlots, glTextureDeclarationOrder, legacyGlTextures);
                    }

                    MonoGameGlslResult rewritten =
                        MonoGameGlslRewriter.Rewrite(transpileResult.Value.Text, stage, samplerSlots, glInterpolants);
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
            {
                // SD0236 names the entry point DXC was asked for; on Vulkan that is the "main" of the
                // rename above, so put back the name the author wrote.
                ShaderError error = result.Error;
                if (string.Equals(error.Code, DxcSpirvEntryRedeclarations.ErrorCode, StringComparison.Ordinal)
                    && !string.Equals(effectiveEntryPoint, entryPoint, StringComparison.Ordinal))
                {
                    error = error with
                    {
                        Message = error.Message.Replace($"'{effectiveEntryPoint}'", $"'{entryPoint}'", StringComparison.Ordinal),
                    };
                }
                return (Result<byte[], ShaderError>.Fail(error), default, default, noAttributes, noUniforms, noWarnings);
            }

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
    private static string RenameEntryPointToMain(string hlsl, string entryPoint)
    {
        if (string.Equals(entryPoint, "main", StringComparison.Ordinal))
            return hlsl;

        string renamed = Regex.Replace(hlsl, $@"(?<=\s){Regex.Escape(entryPoint)}(?=\s*\()", "main");
        if (!string.Equals(renamed, hlsl, StringComparison.Ordinal))
            return renamed;

        // No definition spelled with this name: the compile target is a macro's expansion
        // (`#define PF Pmain` / `float4 PF(...)` / `compile ps_4_0 PF()`), and the technique
        // list (issue #407) names the expanded function. Let the preprocessor do the rename: a
        // `#define Pmain main` ahead of the macro's own definition. It goes before the
        // flattener's `#line 1` directive, which restarts the numbering, so no line moves.
        int firstLine = hlsl.IndexOf("#line 1", StringComparison.Ordinal);
        string define = $"#define {entryPoint} main\n";
        return firstLine >= 0 ? hlsl.Insert(firstLine, define) : define + hlsl;
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

    /// <summary>
    /// Renames each texture the SM4 rewrite synthesized for a texture-less legacy sampler
    /// (<c>A_SDTexture</c>) to the sampler's own name (<c>A</c>), mgfxc's name for that
    /// parameter on every profile, and removes the standalone sampler parameter of that name
    /// (OpenGL/Vulkan emit one per reflected sampler; it would duplicate the name and bind
    /// nothing). Only a texture that actually reached the table is renamed, and only then is
    /// its sampler's parameter removed. Returns reflected texture name -> parameter name for
    /// the renamed textures, which the sampler records join through.
    /// </summary>
    private static Dictionary<string, string> ApplySynthesizedSamplerParameterNames(
        List<ParameterReflection> parameters,
        IReadOnlyDictionary<string, string> synthesizedSamplerTextures)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (synthesizedSamplerTextures.Count == 0)
            return renamed;

        var samplerOfTexture = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string samplerName, string textureName) in synthesizedSamplerTextures)
            samplerOfTexture[textureName] = samplerName;

        foreach (ParameterReflection p in parameters)
        {
            if (p.Class == EffectParameterClass.Object
                && samplerOfTexture.TryGetValue(p.Name, out string? samplerName))
            {
                renamed[p.Name] = samplerName;
            }
        }
        if (renamed.Count == 0)
            return renamed;

        var renamedSamplers = new HashSet<string>(renamed.Values, StringComparer.Ordinal);
        var samplerAnnotations = new Dictionary<string, IReadOnlyList<AnnotationReflection>?>(StringComparer.Ordinal);
        foreach (ParameterReflection p in parameters)
        {
            // The parameter of the SAMPLER half: the only global left with the sampler's
            // name after the rewrite is the SamplerState itself, and it is always Object-class.
            if (p.Class == EffectParameterClass.Object && renamedSamplers.Contains(p.Name))
                samplerAnnotations[p.Name] = p.Annotations;
        }
        parameters.RemoveAll(p => p.Class == EffectParameterClass.Object && renamedSamplers.Contains(p.Name));

        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Class == EffectParameterClass.Object
                && renamed.TryGetValue(parameters[i].Name, out string? parameterName))
            {
                // An annotation written on the legacy sampler (`sampler2D A < ... >;`) is keyed
                // by the sampler's name, which this parameter now carries.
                IReadOnlyList<AnnotationReflection>? annotations = parameters[i].Annotations
                    ?? samplerAnnotations.GetValueOrDefault(parameterName);
                parameters[i] = parameters[i] with { Name = parameterName, Annotations = annotations };
            }
        }

        return renamed;
    }

    private static readonly System.Text.RegularExpressions.Regex VulkanTextureAlias = new(
        "^(?<base>.+)" + System.Text.RegularExpressions.Regex.Escape(VulkanTextureSamplerBindingRewriter.AliasSuffix) + @"\d+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsVulkanTextureAlias(string name) => VulkanTextureAlias.IsMatch(name);

    // The texture a Vulkan alias stands for, or the name itself.
    private static string VulkanTextureAliasBase(string name)
    {
        var m = VulkanTextureAlias.Match(name);
        return m.Success ? m.Groups["base"].Value : name;
    }

    /// <summary>
    /// <c>SD0234</c>: on Vulkan, an identifier of the source uses the suffix the binding rewrite
    /// reserves for its texture aliases (<see cref="VulkanTextureSamplerBindingRewriter.AliasSuffix"/>),
    /// so an alias could collide with it (a redefinition DXC would report against text the author
    /// never wrote), and its record would be bound to the wrong parameter. Located at the first
    /// such identifier.
    /// </summary>
    private static ShaderError? VulkanReservedName(string source, string sourceFileName)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            ResourceArrayDiagnostics.MaskCommentsAndStrings(source),
            @"\b\w*" + System.Text.RegularExpressions.Regex.Escape(VulkanTextureSamplerBindingRewriter.AliasSuffix) + @"\w*\b");
        if (!m.Success)
            return null;
        var (file, line, column) = VulkanTextureSamplerBindingRewriter.Locate(source, m.Index);
        return new ShaderError(
            File:    string.IsNullOrEmpty(file) ? sourceFileName : file,
            Line:    line,
            Column:  column,
            Code:    "SD0234",
            Message: $"Vulkan target: the identifier '{m.Value}' uses the suffix '{VulkanTextureSamplerBindingRewriter.AliasSuffix}', " +
                     "which ShadowDusk reserves on Vulkan for the second descriptor it declares when one texture is sampled " +
                     "through two samplers. Rename it.");
    }

    /// <summary>
    /// The KNIFX counterpart of <see cref="ApplySynthesizedSamplerParameterNames"/>: for each
    /// texture the SM4 rewrite synthesized for a texture-less legacy sampler that reached the
    /// table, removes that texture's parameter and the standalone sampler parameter of the
    /// sampler's name (OpenGL reflects one), as KNI's pipeline emits no parameter for such a
    /// sampler. Returns the reflected names of the removed textures, whose sampler records bind -1.
    /// </summary>
    internal static ShaderError? UnjoinedSamplerTextureError(IReadOnlyList<string> unjoinedTextures, string? sourceFileName)
    {
        if (unjoinedTextures.Count == 0)
            return null;

        return new ShaderError(
            File: sourceFileName ?? "<source>",
            Line: 0,
            Column: 0,
            Code: "SD0018",
            Message: "Internal: KNIFX sampler record(s) for texture(s) " +
                     string.Join(", ", unjoinedTextures.Distinct(StringComparer.Ordinal).Select(t => $"'{t}'")) +
                     " name no effect parameter, so the texture could not be bound. Only a texture-less " +
                     "legacy sampler binds no parameter (-1); this one is a reflection/parameter-table " +
                     "mismatch, a ShadowDusk bug.");
    }

    private static HashSet<string> DropSynthesizedSamplerParameters(
        List<ParameterReflection> parameters,
        IReadOnlyDictionary<string, string> synthesizedSamplerTextures)
    {
        var unbound = new HashSet<string>(StringComparer.Ordinal);
        var samplers = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string samplerName, string textureName) in synthesizedSamplerTextures)
        {
            if (parameters.Exists(p => p.Class == EffectParameterClass.Object && p.Name == textureName))
            {
                unbound.Add(textureName);
                samplers.Add(samplerName);
            }
        }

        parameters.RemoveAll(p => p.Class == EffectParameterClass.Object
            && (unbound.Contains(p.Name) || samplers.Contains(p.Name)));
        return unbound;
    }

    /// <summary>
    /// <c>SD0227</c>: two legacy samplers read by ONE compiled stage share an explicit
    /// <c>register(sN)</c>, which fxc refuses (<c>X4500</c>). Located at the second declaration in
    /// the source when it can be found there.
    /// </summary>
    private static ShaderError? OverlappingLegacySamplerRegister(
        IReadOnlyDictionary<int, IReadOnlyList<SamplerReflection>> shaderSamplers,
        IReadOnlyDictionary<string, int> legacySamplerRegisters,
        string source,
        string sourceFileName)
    {
        if (legacySamplerRegisters.Count < 2)
            return null;

        foreach (int shader in shaderSamplers.Keys.Order())
        {
            IGrouping<int, string>? clash = shaderSamplers[shader]
                .Select(s => s.Name)
                .Distinct(StringComparer.Ordinal)
                .Where(legacySamplerRegisters.ContainsKey)
                .GroupBy(name => legacySamplerRegisters[name])
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key)
                .FirstOrDefault();
            if (clash is null)
                continue;

            // Declaration order, so the message names them as the author wrote them.
            var names = clash
                .Select(n => (Name: n, Location: FindSamplerDeclaration(source, n)))
                .OrderBy(x => x.Location.Offset < 0 ? int.MaxValue : x.Location.Offset)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
            (int line, int column, _) = names[1].Location;
            return new ShaderError(
                File: sourceFileName,
                Line: line,
                Column: column,
                Code: "SD0227",
                Message: $"legacy samplers {string.Join(" and ", names.Select(x => $"'{x.Name}'"))} are both bound to " +
                         $"register s{clash.Key} and both read by the same shader. fxc (and so mgfxc) refuses this " +
                         $"(X4500: overlapping register semantics not yet implemented 's{clash.Key}'); give each " +
                         "sampler its own register.");
        }
        return null;
    }

    /// <summary>The 1-based line/column (and offset) of <c>sampler... name</c> in <paramref name="source"/>, or (0, 0, -1).</summary>
    private static (int Line, int Column, int Offset) FindSamplerDeclaration(string source, string samplerName)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
            source,
            $@"(?<![A-Za-z0-9_])[Ss][Aa][Mm][Pp][Ll][Ee][Rr]\w*\s+({System.Text.RegularExpressions.Regex.Escape(samplerName)})(?![A-Za-z0-9_])");
        if (!m.Success)
            return (0, 0, -1);
        int offset = m.Groups[1].Index;
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, offset - lineStart + 1, offset);
    }

    /// <summary>
    /// What the DirectX 11 texture slot re-assignment (<see cref="DxTextureSlotOrder"/>) needs from
    /// the preprocessed view: the textures legacy samplers sample through, each legacy sampler's
    /// texture (to join a natively compiled sampler's RDEF name), every explicit <c>t</c>
    /// register, and the legacy textures the source also calls a method on (refused, see
    /// <c>GlSamplerSlots.ModernMethodTextures</c>). The compiled source's own preprocessed text is cached per text, since every entry
    /// point of an effect compiles the same text.
    /// </summary>
    private sealed record DxTextureSlotInputs(
        IReadOnlySet<string> LegacyTextures,
        IReadOnlyDictionary<string, string> LegacySamplerTextures,
        IReadOnlyList<ExplicitTextureRegister> ExplicitRegisters,
        IReadOnlySet<string> ModernMethodTextures)
    {
        private string? _cachedText;
        private Result<string, ShaderError>? _cachedView;

        public Result<string, ShaderError> Preprocessed(string text, string sourceFile)
        {
            if (_cachedView is null || !ReferenceEquals(_cachedText, text))
            {
                _cachedText = text;
                _cachedView = ShadowDusk.HLSL.Preprocessing.FxMacroPreprocessor.Process(text, sourceFile);
            }
            return _cachedView.Value;
        }

        private string? _nativeText;
        private IReadOnlySet<string>? _native;

        /// <summary>
        /// Samplers still read with a legacy intrinsic (<c>tex2D(S, …)</c>) in the preprocessed
        /// compiled text: legacy samplers the rewrite did not reach, which the backend compiles
        /// natively under the sampler's own name.
        /// </summary>
        public IReadOnlySet<string> NativeLegacySamplers(string preprocessed)
        {
            if (_native is null || !ReferenceEquals(_nativeText, preprocessed))
            {
                _nativeText = preprocessed;
                _native = System.Text.RegularExpressions.Regex
                    .Matches(preprocessed, @"(?<![A-Za-z0-9_.])tex(?:1D|2D|3D|CUBE)(?:bias|grad|lod|proj)?\s*\(\s*([A-Za-z_]\w*)")
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal);
            }
            return _native;
        }
    }

    /// <summary>
    /// DirectX 11: when the shipped compile's <c>t</c> slots differ from fxc's
    /// (<see cref="DxTextureSlotOrder"/>), recompiles with each resource pinned to its fxc slot.
    /// <c>null</c> when nothing needs to move (the first compile's bytes stand, unchanged). Every
    /// case where the slots cannot be checked or applied keeps the first compile and reports
    /// <c>SD0230</c>, never silently.
    /// </summary>
    private static (ReadOnlyMemory<byte>? Bytes, ShaderError? Warning)? ReorderDirectX11TextureSlots(
        IDxbcShaderCompiler dxbcCompiler,
        D3DCompileRequest request,
        ReadOnlyMemory<byte> dxbc,
        DxTextureSlotInputs inputs,
        CancellationToken ct)
    {
        if (inputs.LegacyTextures.Count == 0)
            return null;

        string described = "";
        ShaderError Unplaced(string why) => new(
            File: request.SourceFileName,
            Line: 0,
            Column: 0,
            Code: "SD0230",
            Message: $"DirectX 11: the textures of '{request.EntryPoint}' could not be put on the slots fxc (and so mgfxc) " +
                     $"gives them{described}: {why}. They keep the backend's declaration-order slots, so " +
                     "GraphicsDevice.Textures[N] and SpriteBatch's texture (slot 0) can reach a different texture than in " +
                     "mgfxc's build; setting each texture through its effect parameter is unaffected.",
            Severity: ShaderErrorSeverity.Warning);

        IReadOnlyList<DxbcTRegisterBindings.Binding>? bindings = DxbcTRegisterBindings.Read(dxbc.Span);
        if (bindings is null)
            return (null, Unplaced("the compiled shader's resource table could not be read"));

        // The evaluation order comes from the source (HlslReadOrder): no compiled instruction stream
        // carries it (d3dcompiler_47 schedules its reads; vkd3d swaps the operands of '>' and '<=').
        Result<string, ShaderError> view = inputs.Preprocessed(request.HlslSource, request.SourceFileName);
        if (view.IsFailure)
            return (null, Unplaced($"the compiled source could not be preprocessed ({view.Error.Code}: {view.Error.Message})"));

        // A legacy texture's RDEF name is the rewritten texture's, or, for a legacy sampler the
        // rewrite did not reach (declared in an #include, compiled natively by the backend), the
        // sampler's own: such a sampler is still read with a legacy intrinsic in the compiled text.
        IReadOnlySet<string> nativeSamplers = inputs.NativeLegacySamplers(view.Value);
        string? LegacyKey(string rdefName) =>
            inputs.LegacyTextures.Contains(rdefName) ? rdefName
            : inputs.LegacySamplerTextures.TryGetValue(rdefName, out string? texture) ? texture
            : nativeSamplers.Contains(rdefName) ? rdefName
            : null;
        var legacyNames = new HashSet<string>(
            bindings.Where(b => LegacyKey(b.Name) is not null).Select(b => b.Name), StringComparer.Ordinal);
        if (legacyNames.Count == 0)
            return null;
        if (bindings.Any(b => b.Count != 1 || b.Name.Contains('[', StringComparison.Ordinal)))
            return (null, Unplaced("the shader also declares an array of resources, whose slots this check does not model"));

        var present = new HashSet<string>(
            bindings.Select(b => LegacyKey(b.Name)).OfType<string>(), StringComparer.Ordinal);

        // A legacy sampler's texture the source also uses through a method of its own
        // (`Tb.GetDimensions`, `Tb.Load`, even `Tb.Sample`): fxc gives that use a texture record of
        // its own, numbered apart (mgfxc A@t0 Tb@t1 Tb@t2, measured), and in the compiled text the
        // rewrite's `Tb.Sample(SB, uv)` reads look the same as the author's.
        string? modern = present.Where(inputs.ModernMethodTextures.Contains).Order(StringComparer.Ordinal).FirstOrDefault();
        if (modern is not null)
        {
            return (null, Unplaced($"the legacy sampler's texture '{modern}' is also used through a method call of its own " +
                                   "(GetDimensions, Load, Sample, ...), which fxc gives a texture record of its own"));
        }

        IReadOnlyList<string>? reads = HlslReadOrder.FirstReads(
            view.Value, request.EntryPoint, inputs.LegacyTextures, inputs.LegacySamplerTextures, present, out string? refusal);
        if (reads is null)
            return (null, Unplaced($"the evaluation order of the reads is not certain ({refusal})"));
        var readOrder = new List<string>();
        foreach (string texture in reads)
        {
            foreach (var b in bindings)
            {
                if (string.Equals(LegacyKey(b.Name), texture, StringComparison.Ordinal) && !readOrder.Contains(b.Name))
                    readOrder.Add(b.Name);
            }
        }

        string stage = request.Stage == ShaderStage.Vertex ? "vs" : "ps";
        var registers = inputs.ExplicitRegisters
            .Where(r => r.Stage is null || string.Equals(r.Stage, stage, StringComparison.Ordinal))
            .Select(r => (r.Name, r.Slot))
            .ToList();
        var resources = bindings.Select(b => (b.Name, b.Slot)).ToList();
        IReadOnlyDictionary<string, int>? plan = DxTextureSlotOrder.Plan(resources, readOrder, legacyNames, registers, out string? planRefusal);
        if (planRefusal is not null)
            return (null, Unplaced(planRefusal));
        if (plan is null)
            return null;

        described = " (" + string.Join(", ", plan.OrderBy(p => p.Value).Select(p => $"{p.Key} t{p.Value}")) + ")";
        string? pinnedText = DxTextureSlotOrder.Pin(request.HlslSource, plan);
        if (pinnedText is null)
            return (null, Unplaced("a declaration could not be given an explicit register (declared through a macro or in an #include, in a declarator list, as an array, or with an annotation)"));

        var pinnedRequest = new D3DCompileRequest
        {
            HlslSource      = pinnedText,
            SourceFileName  = request.SourceFileName,
            EntryPoint      = request.EntryPoint,
            Stage           = request.Stage,
            EmbedDebugInfo  = request.EmbedDebugInfo,
            AllowWarnings   = request.AllowWarnings,
            ProfileOverride = request.ProfileOverride,
        };
        var pinned = dxbcCompiler.Compile(pinnedRequest, ct);
        if (pinned.IsFailure)
            return (null, Unplaced($"the pinned compile failed ({pinned.Error.Code}: {pinned.Error.Message})"));
        var pinnedBindings = DxbcTRegisterBindings.Read(pinned.Value.Bytes.Span);
        if (pinnedBindings is null || !DxTextureSlotOrder.Matches(pinnedBindings.Select(b => (b.Name, b.Slot)).ToList(), plan))
            return (null, Unplaced("the pinned compile did not land on those slots"));
        return (pinned.Value.Bytes, null);
    }

    /// <summary>
    /// Adds <c>: register(sN)</c> to every rewritten <c>SamplerState A;</c> whose legacy sampler
    /// carries an explicit register in <paramref name="registers"/> (DirectX 11 only).
    /// </summary>
    private static string PinLegacySamplerRegisters(string text, IReadOnlyDictionary<string, int> registers)
    {
        // Two legacy samplers on one register are fxc's X4500 when both are read, which SD0227
        // reports after the compile; pinning both would make the backend refuse first, with its
        // own message. Pin only the first declared of each register (mgfxc compiles the shape
        // when the second one is never read, and binds the first there).
        var pinned = registers
            .GroupBy(r => r.Value)
            .Select(g => g.OrderBy(r =>
            {
                int offset = FindSamplerDeclaration(text, r.Key).Offset;
                return offset < 0 ? int.MaxValue : offset;
            }).ThenBy(r => r.Key, StringComparer.Ordinal).First());
        foreach ((string sampler, int slot) in pinned)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                $@"(?<![A-Za-z0-9_])SamplerState(\s+){System.Text.RegularExpressions.Regex.Escape(sampler)}\s*;",
                m => $"SamplerState{m.Groups[1].Value}{sampler} : register(s{slot});");
        }
        return text;
    }

    private static int IndexOfParam(IReadOnlyList<ParameterReflection> parameters, string name)
    {
        for (int i = 0; i < parameters.Count; i++)
            if (parameters[i].Name == name)
                return i;
        return -1;
    }

    /// <summary>
    /// The DECLARATION-ORDER BRIDGE for the GL cbuffer/parameter join (B10), used ONLY
    /// when the primary name match fails, which happens exactly when SPIRV-Cross renamed
    /// a uniform: one named after a GLSL reserved word (<c>noise</c> → <c>_noise</c>; also
    /// <c>filter</c>, <c>output</c>, …), one with a run of underscores (<c>my__val</c> →
    /// <c>my_val</c>), or one whose rename collides with another name (<c>_noise_1</c>).
    /// The rewriter's layout lists the block's members in declaration order, and the
    /// reflected cbuffer lists its variables in that same order (every layout keeps it), so
    /// the renamed <paramref name="uniform"/> is the reflected variable at its position
    /// in the layout, whose ORIGINAL name recovers the effect-parameter index
    /// without trusting the SPIRV-Cross spelling. Returns the parameter index, or -1 when
    /// the mapping is not provably unambiguous (the caller then fails with
    /// <c>SD0012</c>).
    ///
    /// <para><b>Never by byte offset</b> (issue #425 review). The previous bridge matched
    /// <c>StartOffset == baseRegister * 16</c>, which holds only when no member is packed
    /// mid-register: after <c>float g;</c> a renamed <c>float3 noise</c> sits at byte 4 in
    /// the DXIL reflection (HLSL packing) and byte 16 in the SPIR-V one (std140), so the
    /// desktop mapped it onto the next variable and the browser did not; a packed scalar
    /// (<c>float a; float b; float noise;</c>) mis-mapped on both. Position is the same on
    /// every reflection source.</para>
    ///
    /// <para><b>Proven, not assumed:</b> the bridge requires a single reflected cbuffer
    /// (the reserved-word case is always a free global in <c>$Globals</c>; with several
    /// cbuffers the rewriter's merge order is not reconstructed), the same member count on
    /// both sides, and every position to agree by name: exactly, or, at a renamed
    /// position, a GLSL spelling that is not itself a parameter and an original name that
    /// appears nowhere else in the layout. SPIRV-Cross's rename rules are deliberately not
    /// modelled (an earlier prefix-only check refused valid underscore and collision
    /// renames). Anything else declines.</para>
    /// </summary>
    private static int IndexOfParamByDeclarationOrder(
        IReadOnlyList<ConstantBufferReflection> constantBuffers,
        IReadOnlyList<ParameterReflection> parameters,
        IReadOnlyList<MonoGameGlslUniform> layout,
        MonoGameGlslUniform uniform)
    {
        if (constantBuffers.Count != 1)
            return -1;

        int layoutIndex = -1;
        for (int j = 0; j < layout.Count; j++)
            if (ReferenceEquals(layout[j], uniform))
                layoutIndex = j;

        // Declaration order; OrderBy is stable, so equal offsets keep the reflected order.
        var variables = constantBuffers[0].Variables.OrderBy(v => v.StartOffset).ToList();
        if (variables.Count != layout.Count || layoutIndex < 0 || layoutIndex >= layout.Count)
            return -1;

        for (int j = 0; j < layout.Count; j++)
        {
            string glslName = layout[j].Name;
            string reflectedName = variables[j].Name;
            if (glslName == reflectedName)
                continue;
            // A renamed position: SPIRV-Cross's spelling must not be some other parameter's
            // name, and the original name must not appear elsewhere in the layout. The rename
            // rule itself is not modelled (it prefixes reserved words, collapses runs of
            // underscores and suffixes collisions): position is the proof.
            bool renamed = IndexOfParam(parameters, glslName) < 0
                && !LayoutHasName(layout, reflectedName);
            if (!renamed)
                return -1;
        }

        return IndexOfParam(parameters, variables[layoutIndex].Name);
    }

    private static bool LayoutHasName(IReadOnlyList<MonoGameGlslUniform> layout, string name)
    {
        foreach (MonoGameGlslUniform u in layout)
            if (u.Name == name)
                return true;
        return false;
    }

    private static int IndexOfCbuffer(IReadOnlyList<ConstantBufferReflection> cbuffers, string name)
    {
        for (int i = 0; i < cbuffers.Count; i++)
            if (cbuffers[i].Name == name)
                return i;
        return -1;
    }

    /// <summary>
    /// Issue #187 (synthesized GL backing): resize — or insert, when the rewriter emitted
    /// none — the <c>uniform vec4 {vs,ps}_uniforms_vec4[N];</c> declaration in a GL shader
    /// blob so the declared array covers register slots synthesized for parameters that
    /// reflection reports but the transpiled GLSL never mentioned (DXC folded their only
    /// reads). Insertion must clear the rewriter's ENTIRE leading preprocessor prologue —
    /// any <c>#extension</c> lines (emitted FIRST, before the precision block, for
    /// derivative-using shaders) and the <c>#ifdef GL_ES</c> precision block — because
    /// GLSL ES requires <c>#extension</c> before any non-preprocessor token, and a
    /// fragment-stage <c>float</c> global before the ES default-precision statement is an
    /// error on strict GLSL ES compilers (ANGLE/WebGL, Android GLES).
    /// </summary>
    private static CompiledShaderBlob PatchGlUniformArrayDeclaration(
        CompiledShaderBlob blob, string cbName, int totalRegisters)
    {
        string glsl = Encoding.UTF8.GetString(blob.Bytes);
        string declaration = $"uniform vec4 {cbName}[{totalRegisters}];";

        var existing = new Regex($@"^uniform vec4 {Regex.Escape(cbName)}\[\d+\];",
                                 RegexOptions.Multiline);
        string patched = existing.IsMatch(glsl)
            ? existing.Replace(glsl, declaration, count: 1)
            : glsl.Insert(GlPrologueEnd(glsl), declaration + "\n");

        return blob with { Bytes = Encoding.UTF8.GetBytes(patched) };
    }

    /// <summary>
    /// The character index just past the leading GL prologue of a rewritten GLSL blob:
    /// consecutive <c>#extension …</c> lines and/or balanced preprocessor conditional
    /// blocks (<c>#if</c>/<c>#ifdef</c>/<c>#ifndef</c> … <c>#endif</c>, depth-tracked),
    /// plus blank lines between them, in any order. The rewriter emits up to THREE such
    /// headers before the body — the derivatives <c>#extension</c> line, the
    /// <c>#ifdef GL_ES</c> precision block, and the TexLod
    /// <c>#if __VERSION__ &gt;= 300 … #elif … #extension … #endif</c> block (whose
    /// <c>#extension</c> directives live INSIDE branches, which is why the block must be
    /// consumed as a balanced whole). Global declarations are legal from this point on
    /// for both desktop GLSL and GLSL ES; stopping earlier can put an <c>#extension</c>
    /// after a non-preprocessor token (a hard error on Mesa and strict ESSL front ends).
    /// </summary>
    private static int GlPrologueEnd(string glsl)
    {
        int pos = 0;
        while (pos < glsl.Length)
        {
            if (glsl[pos] == '\r' || glsl[pos] == '\n')
            {
                pos++;
                continue;
            }

            int lineEnd = glsl.IndexOf('\n', pos);
            int nextLine = lineEnd < 0 ? glsl.Length : lineEnd + 1;
            ReadOnlySpan<char> line = glsl.AsSpan(pos);

            if (line.StartsWith("#extension"))
            {
                pos = nextLine;
                continue;
            }

            if (line.StartsWith("#if"))   // #if, #ifdef, #ifndef
            {
                int depth = 1;
                int scan = nextLine;
                while (depth > 0 && scan < glsl.Length)
                {
                    int scanLineEnd = glsl.IndexOf('\n', scan);
                    int scanNext = scanLineEnd < 0 ? glsl.Length : scanLineEnd + 1;
                    ReadOnlySpan<char> scanLine = glsl.AsSpan(scan);
                    if (scanLine.StartsWith("#if"))
                        depth++;
                    else if (scanLine.StartsWith("#endif"))
                        depth--;
                    scan = scanNext;
                }
                if (depth > 0)
                    break;   // unbalanced — do not treat as prologue
                pos = scan;
                continue;
            }

            break;
        }
        return pos;
    }

    private static IReadOnlyList<EffectParameterInfo> BuildEffectParameterInfoList(
        IReadOnlyList<ParameterReflection> parameters,
        IReadOnlyDictionary<string, GlobalInitializerDefault> defaults,
        List<ShaderError> warnings)
    {
        var result = new List<EffectParameterInfo>(parameters.Count);

        foreach (ParameterReflection param in parameters)
        {
            var annotations = param.Annotations?
                .Select(MapAnnotation)
                .ToList() ?? new List<AnnotationInfo>();

            GlobalInitializerDefault? def = null;
            if (defaults.TryGetValue(param.Name, out GlobalInitializerDefault? candidate))
            {
                (GlobalInitializerDefault stored, RowMajorRefusal? refusal) = InStorageOrder(param, candidate);
                string? mismatch = refusal?.Reason ?? DefaultShapeMismatch(param, stored);
                if (mismatch is null)
                    def = stored;
                else if (refusal is not null)
                    warnings.Add(DefaultNotPreserved(candidate.File, candidate.Line, candidate.Column, param.Name, mismatch,
                        refusal.Advice));
                else
                    warnings.Add(DefaultNotPreserved(candidate.File, candidate.Line, candidate.Column, param.Name, mismatch));
            }

            IReadOnlyList<EffectParameterInfo> elements = BuildElementParameters(param, def);
            result.Add(new EffectParameterInfo(
                Class: (byte)param.Class,
                Type: (byte)param.Type,
                Name: param.Name,
                Semantic: param.Semantic,
                Annotations: annotations,
                RowCount: (byte)param.Rows,
                ColumnCount: (byte)param.Columns,
                Members: Array.Empty<EffectParameterInfo>(),
                Elements: elements,
                // An array parent carries no blob (each element leaf does); a plain leaf carries its own.
                DefaultValue: elements.Count == 0 && def is not null ? def.ElementBlobs[0] : null));
        }

        return result;
    }

    /// <summary>
    /// The default as the writers must store it for <paramref name="param"/>'s buffer layout, or why
    /// a <c>row_major</c> one cannot be. The reader's blob is column by column
    /// (<c>Data[c * R + r] = M[r][c]</c>). MonoGame uploads a parameter with header Rh x Ch as Ch
    /// registers of Rh floats, register j taking <c>Data[j * Rh .. j * Rh + Rh - 1]</c>: right as it
    /// is for HLSL's default column-major packing (one register per column, header R x C). A
    /// <c>row_major</c> buffer holds one register per HLSL row (register j is <c>M[j][0..C-1]</c>), so
    /// its blob is <c>Data[j * Rh + k] = M[j][k]</c> for every row j the upload reaches (j &lt; Ch)
    /// and every column k inside a register (k &lt; Rh), zeros elsewhere. On Vulkan the header is the
    /// C x R transpose and every element is reached (the rows in order); on DirectX it is R x C, so a
    /// square matrix is the rows in order, a TALL one (R &gt; C) reaches rows 0..C-1 only (an element
    /// in a row j &gt;= C is refused, an all-zero tail is kept: an affine world or bone matrix's
    /// fourth column), and a WIDE one (C &gt; R) uploads C registers over a matrix of R, so it is
    /// refused (the upload runs into the next member, or past the end of the buffer). An ARRAY is
    /// uploaded element after element, each <c>Ch</c> registers on from the last
    /// (<c>ConstantBuffer.SetParameter</c>: <c>offset += ColumnCount * 16</c>), while a row_major
    /// array's buffer holds R registers per element, so unless <c>Ch == R</c> (Vulkan, or a square
    /// matrix) every element after the first lands in the wrong registers: a DirectX tall row_major
    /// array is refused too. Before this, a row_major default was the column-major blob and arrived
    /// transposed, with no warning.
    /// </summary>
    private static (GlobalInitializerDefault Stored, RowMajorRefusal? Refusal) InStorageOrder(
        ParameterReflection param, GlobalInitializerDefault def)
    {
        if (!param.IsRowMajor || def.Class != (byte)EffectParameterClass.Matrix || param.Class != EffectParameterClass.Matrix)
            return (def, null);

        int rows = def.Rows, cols = def.Columns;              // the declaration, R x C
        int rh = param.Rows, ch = param.Columns;              // the header MonoGame uploads by
        if (rh * ch != rows * cols)
            return (def, null);                               // DefaultShapeMismatch reports it

        if (ch > rows)
            return (def, new RowMajorRefusal(
                $"it is a row_major {rows}x{cols} matrix, whose buffer holds {rows} registers, one per row, " +
                $"and MonoGame uploads its {rh}x{ch} parameter as {ch} registers of {rh} floats, past the matrix",
                "A Set cannot fill those registers either (and last in the globals the first Apply throws). " +
                ColumnMajorAdvice));

        if (Math.Max(1, param.Elements) > 1 && ch != rows)
            return (def, new RowMajorRefusal(
                $"it is an array of row_major {rows}x{cols} matrices, whose buffer holds {rows} registers per element, " +
                $"and MonoGame uploads each element {ch} registers after the last, so every element after the first " +
                "lands in the wrong registers on this target",
                "A Set of the array lands those elements in the wrong registers too. " + ColumnMajorAdvice +
                " Separate (non-array) parameters also work."));

        var blobs = new List<byte[]>(def.ElementBlobs.Count);
        foreach (byte[] blob in def.ElementBlobs)
        {
            if (blob.Length != rows * cols * 4)
                return (def, null);                           // DefaultShapeMismatch reports it
            var stored = new byte[blob.Length];
            for (int j = 0; j < rows; j++)
            {
                for (int k = 0; k < cols; k++)
                {
                    int source = (k * rows + j) * 4;
                    if (j < ch && k < rh)
                    {
                        Buffer.BlockCopy(blob, source, stored, (j * rh + k) * 4, 4);
                    }
                    else if (def.Type == (byte)EffectParameterType.Single
                                 ? BitConverter.ToSingle(blob, source) != 0f   // numerically: -0.0 is zero
                                 : BitConverter.ToInt32(blob, source) != 0)
                    {
                        return (def, new RowMajorRefusal(
                            $"it is a row_major {rows}x{cols} matrix, whose buffer holds one register per row, " +
                            $"and MonoGame uploads its {rh}x{ch} parameter as {ch} registers of {rh} floats, so " +
                            $"row {j} (non-zero in the initializer) is never written on this target",
                            $"A Set cannot reach rows {ch} and beyond either. " + ColumnMajorAdvice +
                            $" A row_major {rows}x{cols} default is kept on this target only when those rows are zero."));
                    }
                }
            }
            blobs.Add(stored);
        }
        return (def with { Rows = rh, Columns = ch, ElementBlobs = blobs }, null);
    }

    /// <summary>Why a row_major default cannot be stored, and the advice for it (a runtime Set does not help).</summary>
    private sealed record RowMajorRefusal(string Reason, string Advice);

    private const string ColumnMajorAdvice =
        "Declare the matrix column-major instead: remove its row_major modifier, or, if a " +
        "#pragma pack_matrix(row_major) applies, declare it column_major or after #pragma pack_matrix(column_major).";

    /// <summary>
    /// Why the default read off the source cannot be attached to the reflected parameter, or null
    /// when the shapes agree (the declared type, and the array length, are what reflection reports).
    /// </summary>
    internal static string? DefaultShapeMismatch(ParameterReflection param, GlobalInitializerDefault def)
    {
        int declaredElements = Math.Max(1, def.ArrayLength);

        // SPIR-V has no bool in a uniform block (Vulkan reflection reports a `bool` as Int32); the
        // blob is the same 32-bit word either way.
        bool typeAgrees = (byte)param.Type == def.Type
            || (def.Type == 1 && param.Type == EffectParameterType.Int32);

        if ((byte)param.Class != def.Class || !typeAgrees ||
            param.Rows != def.Rows || param.Columns != def.Columns || Math.Max(1, param.Elements) != declaredElements)
        {
            return $"the declaration reads as class {def.Class} type {def.Type} {def.Rows}x{def.Columns} " +
                   $"x{declaredElements}, but the compiled parameter is class {(int)param.Class} type " +
                   $"{(int)param.Type} {param.Rows}x{param.Columns} x{param.Elements}";
        }
        return null;
    }

    /// <summary>
    /// The warnings of a SUCCESSFUL compile, led by the technique-view warning (issue #407) when the
    /// preprocessed view could not be built and the raw technique list was used. A failed compile
    /// does not carry it: its own error is the news.
    /// </summary>
    private static IReadOnlyList<ShaderError> WithViewWarning(List<ShaderError> warnings, ShaderError? viewWarning) =>
        viewWarning is null ? warnings : [viewWarning, .. warnings];

    internal static ShaderError DefaultNotPreserved(string file, int line, int column, string name, string reason,
        string advice = "Set the parameter by name at runtime, or simplify the initializer.") =>
        new(File: file,
            Line: line,
            Column: column,
            Code: "SD0017",
            Message: $"the initializer of '{name}' was not preserved as its default value, so the effect ships " +
                     $"a default of zero: {reason}. " + advice,
            Severity: ShaderErrorSeverity.Warning);

    /// <summary>
    /// Reads the global initializers of the compiled source (<c>float R = 1;</c>) and returns the
    /// defaults for the parameters that exist in <paramref name="parameters"/>. An initializer that
    /// cannot be evaluated is a registered <c>SD0017</c> warning on the compile, never a silent zero.
    /// DXC's own "initializer ignored" warning for a declaration whose default IS preserved is
    /// dropped, since it would now be untrue.
    /// </summary>
    internal static IReadOnlyDictionary<string, GlobalInitializerDefault> ResolveParameterDefaults(
        string preprocessedText,
        string sourceFileName,
        IReadOnlyList<ParameterReflection> parameters,
        List<ShaderError> warnings)
    {
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParameterReflection p in parameters)
            parameterNames.Add(p.Name);

        Result<GlobalInitializers, ShaderError> read;
        try
        {
            read = GlobalInitializerReader.Read(preprocessedText, sourceFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException
                                       or FormatException or OverflowException or KeyNotFoundException)
        {
            // The reader is bounded and returns failures, so this is a bug in it; the compile
            // itself succeeded, so say so loudly and ship zeros rather than fail a good shader.
            warnings.Add(DefaultNotPreserved(sourceFileName, 0, 0, "<any global>",
                $"the initializer reader hit an internal error ({ex.GetType().Name}: {ex.Message})"));
            return new Dictionary<string, GlobalInitializerDefault>();
        }

        if (read.IsFailure)
        {
            // The compile itself already succeeded, so the view is ours to build and we could not:
            // say so rather than ship unexplained zeros.
            warnings.Add(DefaultNotPreserved(read.Error.File, read.Error.Line, 0, "<any global>",
                "the preprocessed view needed to read initializers could not be built: " + read.Error.Message));
            return new Dictionary<string, GlobalInitializerDefault>();
        }

        // Only worth saying when there is an initializer whose branch could be the wrong one: generated
        // HLSL (the Slang route) tests __DXC_VERSION_MAJOR in every file.
        if (read.Value.CompilerPredefinedMacro is { } predefined
            && (read.Value.Defaults.Count > 0 || read.Value.Failures.Count > 0))
            warnings.Add(DefaultNotPreserved(sourceFileName, 0, 0, "<any global>",
                $"a conditional tests {predefined}, a macro only the compiler defines, so the branch the " +
                "initializers were read from may not be the one that was compiled"));

        var kept = new Dictionary<string, GlobalInitializerDefault>(StringComparer.Ordinal);
        foreach ((string name, GlobalInitializerDefault def) in read.Value.Defaults)
            if (parameterNames.Contains(name))
                kept[name] = def;

        foreach (GlobalInitializerFailure f in read.Value.Failures)
            if (parameterNames.Contains(f.Name))
                warnings.Add(DefaultNotPreserved(f.File, f.Line, f.Column, f.Name, f.Reason));

        if (kept.Count > 0)
        {
            // DXC's wording differs by path: the SPIR-V compiles say "variable 'X' will be placed in
            // $Globals so initializer ignored", the DXIL (DirectX 12) compile "Initializer of external
            // global will be ignored". Both are untrue once the default is stored.
            //
            // The warning is matched to a kept default by POSITION, inside the span from the declared
            // name to the end of its initializer: the SPIR-V path reports the initializer's first
            // token, the DXIL path the name (measured, DXC 1.7.2212.40), so a brace or '=' on the next
            // line (`float4 C =` / `{ 1, 0, 0, 1 };`) used to leave a stale warning behind when only
            // the name's line was compared. The variable name in the message is no key either: that
            // DXC names the FIRST global with an initializer in every one of these warnings.
            // A clang note that follows a removed warning ("expanded from macro 'RED'", when the
            // initializer is a macro) belongs to it and goes with it.
            //
            // The span starts at column 1 of the name's line, not at the name: a declaration written
            // through a macro (`#define DECL(n, v) float n = v;` + `DECL(Good, 0.5)`) is reported by
            // the DXIL path at the macro call's argument, left of where the reader places the name.
            // It starts at the name only when another declaration precedes it on that line, so
            // `float Unmodelled = Twice(0.25); float Good = 0.5;` keeps Unmodelled's true warning.
            var spanStarts = new Dictionary<GlobalInitializerDefault, int>(ReferenceEqualityComparer.Instance);
            foreach (GlobalInitializerDefault d in kept.Values)
            {
                string file = SourceBaseName(d.File);
                bool preceded =
                    read.Value.Defaults.Values.Any(o => !ReferenceEquals(o, d) && o.Line == d.Line && o.Column < d.Column
                        && string.Equals(SourceBaseName(o.File), file, StringComparison.Ordinal)) ||
                    read.Value.Failures.Any(o => o.Line == d.Line && o.Column < d.Column
                        && string.Equals(SourceBaseName(o.File), file, StringComparison.Ordinal));
                spanStarts[d] = preceded ? d.Column : 1;
            }

            for (int i = 0; i < warnings.Count; i++)
            {
                ShaderError w = warnings[i];
                if (w.Code != "X0000" || w.Severity == ShaderErrorSeverity.Note ||
                    !(w.Message.Contains("so initializer ignored", StringComparison.Ordinal) ||
                      w.Message.Contains("Initializer of external global will be ignored", StringComparison.Ordinal)) ||
                    !kept.Values.Any(d => InitializerSpanContains(d, w, spanStarts[d])))
                {
                    continue;
                }

                int notes = 0;
                while (i + 1 + notes < warnings.Count &&
                       warnings[i + 1 + notes].Code == "X0000" &&
                       warnings[i + 1 + notes].Severity == ShaderErrorSeverity.Note)
                {
                    notes++;
                }
                warnings.RemoveRange(i, 1 + notes);
                i--;
            }
        }

        return kept;
    }

    /// <summary>
    /// Whether <paramref name="warning"/>'s location lies in <paramref name="def"/>'s declaration,
    /// from <paramref name="startColumn"/> of the declared name's line (1 unless another declaration
    /// precedes it there) to the end of its initializer, in the same file (by base name, the form
    /// both the compilers and the reader report).
    /// </summary>
    internal static bool InitializerSpanContains(GlobalInitializerDefault def, ShaderError warning, int startColumn = 1)
    {
        if (!string.Equals(SourceBaseName(def.File), SourceBaseName(warning.File), StringComparison.Ordinal))
            return false;

        (int Line, int Column) start = (def.Line, Math.Max(1, startColumn));
        (int Line, int Column) end = def.EndLine > 0 ? (def.EndLine, def.EndColumn) : (def.Line, def.Column);
        (int Line, int Column) at = (warning.Line, warning.Column);

        // A diagnostic without a column (0) is placed by its line alone.
        if (at.Column <= 0)
            return at.Line >= start.Line && at.Line <= end.Line;

        return Compare(at, start) >= 0 && Compare(at, end) <= 0;

        static int Compare((int Line, int Column) a, (int Line, int Column) b) =>
            a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);
    }

    private static string SourceBaseName(string path)
    {
        int cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// Phase 43 F6: the element sub-parameter records for an ARRAY parameter, on
    /// EVERY target. MonoGame's <c>Effect.ReadParameters</c> reads array elements as
    /// a recursive parameter collection, and <c>EffectParameter.SetValue</c> for an
    /// array (or <c>Elements[i]</c> indexing) requires them — with <c>Elements</c>
    /// empty, an array parameter was un-settable beyond element 0 even on DirectX.
    /// Shape mirrors mgfxc (<c>ConstantBufferData.GetParameterFromSymbol</c>): each
    /// element carries an EMPTY name/semantic, the parent's class/type/rows/columns,
    /// no annotations, and its own default-value blob (the initializer's element, or zeros).
    /// </summary>
    private static IReadOnlyList<EffectParameterInfo> BuildElementParameters(
        ParameterReflection param, GlobalInitializerDefault? def)
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
                Elements: Array.Empty<EffectParameterInfo>(),
                DefaultValue: def is not null ? def.ElementBlobs[i] : null));
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
