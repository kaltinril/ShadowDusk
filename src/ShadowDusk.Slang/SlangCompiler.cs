#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Compiler;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Slang;

/// <summary>
/// The <b>real-slangc compile route</b> (Phase 66 A3): <c>.slang</c> source containing
/// genuine Slang — <c>import</c>, generics, <c>interface</c> conformances, everything real
/// slangc accepts, none of which the HLSL-compatible-subset frontend
/// (<c>ShadowDusk.Compiler.Slang.SlangFrontend</c>) can compile — in, a compiled effect out.
///
/// <para><b>The route</b> (Phase 66 §1's non-negotiable, unchanged): <c>.slang</c> →
/// <c>[real slangc, -target hlsl]</c> → HLSL text → <c>[the SAME unchanged faithful
/// pipeline every .fx uses, via EffectCompiler]</c>. Slang never substitutes for DXC
/// anywhere; it only ever produces HLSL that DXC then compiles exactly like it would
/// compile any other <c>.fx</c> body.</para>
///
/// <para><b>Entry discovery and technique synthesis are reused, not reinvented</b>: this
/// class calls the shipped <c>SlangEntryScanner.Scan</c> — the same <c>[shader("vertex")]</c>
/// / <c>[shader("fragment")]</c> attribute convention <c>SlangFrontend</c> already uses —
/// to learn which entry points exist. The <c>.fx</c> wrapper this class assembles around
/// slangc's HLSL emission (the <c>#if SM4</c> shader-model header, the synthesized
/// <c>technique</c>/<c>pass</c> block) mirrors <c>SlangFrontend.ConvertToFx</c>'s shape
/// exactly, but is separate code: <c>SlangFrontend</c>'s own SD0600 Slang-only-construct
/// rejection and attribute-stripping steps operate on RAW Slang text and do not apply here
/// (real slangc already consumed the attributes and accepts the constructs SD0600 rejects),
/// so this class cannot simply call into <c>SlangFrontend</c> — and per this phase's scope,
/// <c>SlangFrontend.cs</c> stays byte-for-byte untouched.</para>
///
/// <para><b>Name mangling and the OpenGL row_major gap, both closed (Phase 66 A4):</b> the
/// <c>-no-mangle</c> flag passed to every slangc invocation keeps every symbol that matters
/// for a consumer's reflected parameter table (cbuffer names/members, texture/sampler
/// declarations) at the author's original spelling — <c>float BlurAmount</c> stays
/// <c>BlurAmount</c>, not <c>BlurAmount_0</c> (measured against the full corpus; only
/// local variables and struct field names still carry slangc's <c>_N</c> suffix, and
/// those are never part of a reflected parameter table). <see cref="StripMatrixPackingPragma"/>
/// removes slangc's unconditional <c>#pragma pack_matrix(column_major)</c>, which otherwise
/// silently overrides <c>DxcFlagBuilder</c>'s OpenGL row-major convention and was the root
/// cause of a <c>layout(row_major)</c> qualifier <c>MonoGameGlslRewriter</c> doesn't model
/// (see that method's own comment for the full mechanism).</para>
///
/// <para><b>The three-band accept/reject rule (Phase 66 A5, Phase 61 §6 A6):</b> slangc's own
/// syntax errors surface verbatim via <see cref="SlangDiagnosticReformatter"/> (band 1);
/// constructs that compile but have nowhere to land in an <c>Effect</c> are rejected loudly by a
/// registered diagnostic naming the construct and the target — a non-vertex/fragment entry stage
/// (<c>SD0602</c>, <see cref="SlangEntryScanner"/>) or an SM6-only Wave/Quad intrinsic on a
/// target whose backend cannot represent SM6 HLSL (<c>SD0624</c>,
/// <see cref="SlangSm6ConstructGuard"/>), or a Wave/Quad intrinsic on Vulkan, whose MonoGame
/// runtime has no subgroup support (<c>SD0218</c>, shared with the <c>.fx</c> route) (band 2);
/// everything else compiles, with no curated
/// allow-list (band 3).</para>
///
/// <para><b>Per-target platform macros, forwarded to slangc (Phase 66 A6):</b> every
/// slangc invocation now also receives <c>-D</c> flags for
/// <c>PlatformMacros.For(options.Target, options.Container)</c> — the same
/// <c>OPENGL</c>/<c>SM4</c>/<c>VULKAN</c>/<c>SM6</c>/<c>HLSL</c>/<c>GLSL</c>/<c>MGFX</c>/
/// <c>FNA</c>/<c>SM3</c>/<c>__KNIFX__</c> macros the ordinary <c>.fx</c> route already
/// defines for every hand-written shader. Found via the A6 residue sweep: without this,
/// a Slang author's own <c>#if OPENGL</c> / <c>#if VULKAN</c> branch (the exact idiom a
/// huge share of the real, non-Slang-authored fixture corpus relies on for per-target
/// correctness) resolved to the SAME branch on every target, since slangc's own
/// preprocessor pass never saw them — a silent, target-blind divergence.</para>
///
/// <para><b>Two transports, one route (issue #257):</b> on desktop slangc runs as a child
/// process; where no process can be spawned (the browser), <c>ShadowDusk.Slang.Wasm</c> runs
/// the same pinned slangc compiled to WebAssembly inside the page. Both receive the identical
/// argument list (<see cref="SlangcArguments"/>), and everything before and after the slangc
/// call is this one class, so the two hosts hand DXC byte-identical HLSL (measured over the
/// whole corpus, every target's macros, success and failure output alike).</para>
/// </summary>
/// <remarks>
/// Deliberately does NOT implement <c>IShaderCompiler</c>: that interface's contract is
/// specifically HLSL <c>.fx</c> source in (see its own doc comment), and this class's input
/// is Slang, a different (superset-ish) language — implementing the interface would either
/// misdocument the parameter or require a second, misleading doc comment on the same
/// member. The method shapes below intentionally mirror it anyway (matching
/// <c>Compile</c>/<c>CompileAsync</c> signatures) for ergonomic parity with every other
/// ShadowDusk compiler entry point.
/// </remarks>
public sealed class SlangCompiler
{
    private const string TechniqueName = "SlangEffect";

    /// <summary><c>SD0629</c>: slangc's preprocess-only pass exited 0 with output that cannot
    /// be the source (empty, or missing an entry point the compile found).</summary>
    internal const string PreprocessOutputUnusableCode = "SD0629";

    /// <summary><c>SD0628</c>: slangc's emission carries a texture/sampler register on a
    /// declaration from another file (an <c>import</c>ed module or an <c>__include</c>d file)
    /// and no preprocess pass can prove whether the author wrote it (issue #292).</summary>
    internal const string RegisterAuthorshipUnprovenCode = "SD0628";

    private readonly IShaderCompiler _downstreamCompiler;
    private readonly Func<SlangcLocation> _locateSlangc;
    private readonly Func<string, string> _prepareSlangc;
    private readonly SlangcInvoker _runSlangc;
    private readonly InProcessSlangc? _inProcessSlangc;

    /// <summary>
    /// Where (and whether) this host's slangc is: <see cref="UnsupportedReason"/> is
    /// non-null on a host the bundled natives cannot run on (<c>SD0620</c>);
    /// <see cref="SlangcPath"/> is null when the native was not found (<c>SD0621</c>).
    /// </summary>
    internal readonly record struct SlangcLocation(string? UnsupportedReason, string? SlangcPath);

    private static SlangcLocation LocateBundledSlangc()
    {
        string? reason = SlangToolPath.GetUnsupportedReason();
        return reason is not null
            ? new SlangcLocation(reason, null)
            : new SlangcLocation(null, SlangToolPath.Resolve());
    }

    /// <summary>
    /// Creates a <see cref="SlangCompiler"/>. The optional <paramref name="downstreamCompiler"/>
    /// exists for tests that want to isolate the slangc-invocation/merge/assembly logic from
    /// the (heavy, native-backed) downstream pipeline; production code should pass nothing
    /// and get the real <see cref="EffectCompiler"/>.
    /// </summary>
    public SlangCompiler(IShaderCompiler? downstreamCompiler = null)
        : this(downstreamCompiler, LocateBundledSlangc)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="locateSlangc"/> replaces the host/native lookup, so the
    /// checks that must run BEFORE it (entry-stage policy, the SM6 intrinsic guard) are
    /// provably independent of whether this host has a slangc at all.
    /// </summary>
    internal SlangCompiler(IShaderCompiler? downstreamCompiler, Func<SlangcLocation> locateSlangc)
        : this(downstreamCompiler, locateSlangc, SlangNativeCache.EnsureRunnableSlangc, RunSlangc)
    {
    }

    /// <summary>
    /// One slangc process run: <paramref name="arguments"/> is a <see cref="SlangcArguments"/>
    /// list (a per-entry compile or the preprocess-only pass) and the source goes to stdin.
    /// Shaped exactly like <see cref="RunSlangc(string, string, string, IReadOnlyList{string})"/>.
    /// </summary>
    internal delegate (int ExitCode, string Stdout, string Stderr) SlangcInvoker(
        string slangcPath,
        string workingDirectory,
        string slangSource,
        IReadOnlyList<string> arguments);

    /// <summary>
    /// Test seam (issue #258): <paramref name="prepareSlangc"/> replaces the native-cache
    /// preparation and <paramref name="runSlangc"/> replaces the process spawn, so the
    /// post-slangc logic (the per-entry merge and its <c>SD0625</c> rejection, the
    /// <c>.fx</c> assembly) can be driven end to end with canned slangc output. No valid
    /// Slang source reproduces <c>SD0625</c> through real slangc, which is why this exists.
    /// </summary>
    internal SlangCompiler(
        IShaderCompiler? downstreamCompiler,
        Func<SlangcLocation> locateSlangc,
        Func<string, string> prepareSlangc,
        SlangcInvoker runSlangc)
    {
        _downstreamCompiler = downstreamCompiler ?? new EffectCompiler();
        _locateSlangc = locateSlangc;
        _prepareSlangc = prepareSlangc;
        _runSlangc = runSlangc;
    }

    /// <summary>
    /// One slangc run hosted INSIDE this process (issue #257): <paramref name="arguments"/>
    /// is a <see cref="SlangcArguments"/> list (<see cref="SlangcArguments.Build"/> for an entry
    /// point, <see cref="SlangcArguments.BuildPreprocess"/> for the preprocess-only pass), to be
    /// handed to slang's own command-line parser verbatim, and <paramref name="slangSource"/>
    /// is what the desktop route pipes to slangc's stdin. Returns slangc's exit status and its
    /// raw stdout/stderr text;
    /// <see cref="Compile"/> normalizes the text exactly as the process route does
    /// (<see cref="SlangcArguments.JoinOutputLines"/>).
    /// </summary>
    internal delegate (int ExitCode, string Stdout, string Stderr) InProcessSlangc(
        string slangSource,
        IReadOnlyList<string> arguments);

    /// <summary>
    /// The in-process route (issue #257): slangc runs inside this process instead of as a
    /// child process, for hosts that cannot spawn one (the browser). Everything around the
    /// slangc call (the host-independent rejections, the argument list, the register strip,
    /// the per-entry merge, the <c>.fx</c> assembly, and the downstream pipeline) is the same
    /// code the desktop route runs; only the transport differs. Internal, and reached only by
    /// <c>ShadowDusk.Slang.Wasm</c>, so no public API plugs a different compiler in here (a
    /// convention rather than a security boundary: the assemblies are not strong-named).
    /// </summary>
    internal SlangCompiler(IShaderCompiler downstreamCompiler, InProcessSlangc inProcessSlangc)
        : this(downstreamCompiler, LocateBundledSlangc)
    {
        _inProcessSlangc = inProcessSlangc;
    }

    /// <summary>
    /// Compiles Slang source into a compiled effect for the target in <paramref name="options"/>.
    /// See the class doc comment for the route. Runs on the calling thread; intended for
    /// synchronous call sites (matching <c>IShaderCompiler.Compile</c>'s contract).
    /// </summary>
    public Result<CompiledShader, ShaderError[]> Compile(
        string slangSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceName = options.SourceFileName ?? "<memory>.slang";

        // Issue #231: an HLSL Effect (.fx) file is not Slang. Checked before the entry scan,
        // whose SD0603 ("add [shader] attributes") would send the author down a dead end.
        (string Construct, int Line)? effectHit = SlangEffectFrameworkGuard.FindConstruct(slangSource);
        if (effectHit is not null)
        {
            return Fail(new ShaderError(
                File: sourceName, Line: effectHit.Value.Line, Column: 1, Code: "SD0626",
                Message: "'" + effectHit.Value.Construct + "' is an HLSL Effect (.fx) construct, not Slang: real slangc has no " +
                         "technique/pass concept and cannot parse it (nor the legacy sampler declarations those effects use). " +
                         "ShadowDusk.Slang compiles Slang source whose entry points are marked [shader(''vertex'')] / " +
                         "[shader(''fragment'')]; it synthesizes the technique itself. Compile .fx files, including MonoGame's own " +
                         "Macros.fxh-based effects (BasicEffect, SpriteEffect, SkinnedEffect, ...), through the .fx route: " +
                         "EffectCompiler, the CLI, or the MGCB plugin."));
        }

        // Host-independent rejections run FIRST (entry-stage policy, then the SM6 intrinsic
        // guard): they depend only on the source and the target, so a host without a usable
        // slangc must report them exactly like one with it, and their tests run on every OS.
        Result<IReadOnlyList<SlangEntryPoint>, ShaderError[]> entriesResult =
            SlangEntryScanner.Scan(slangSource, sourceName);
        if (entriesResult.IsFailure)
            return Result<CompiledShader, ShaderError[]>.Fail(entriesResult.Error);
        IReadOnlyList<SlangEntryPoint> entries = entriesResult.Value;

        // Phase 66 A5, Band 2 part B (OQ2): reject an SM6-only Wave/Quad intrinsic up front,
        // before spawning slangc at all, when the target can never represent SM6 HLSL through
        // ShadowDusk's real backend for it — see SlangSm6ConstructGuard's own doc comment for
        // why this is a static source scan rather than a downstream catch-and-wrap.
        if (SlangSm6ConstructGuard.IsArchitecturallyBelowSm6(options.Target))
        {
            (string Construct, int Line)? sm6Hit = SlangSm6ConstructGuard.FindConstruct(slangSource);
            if (sm6Hit is not null)
            {
                return Fail(new ShaderError(
                    File: sourceName, Line: sm6Hit.Value.Line, Column: 1,
                    Code: WaveQuadIntrinsics.BelowSm6Code,
                    Message: WaveQuadIntrinsics.BelowSm6Message(sm6Hit.Value.Construct, options.Target)));
            }
        }
        else if (options.Target == PlatformTarget.Vulkan)
        {
            // Issue #229: Vulkan CAN represent SM6, but MonoGame's DesktopVK runtime cannot run
            // wave/quad ops (Vulkan 1.0 instance, no subgroup support). Same code and message as
            // the .fx route, which reaches the same verdict from DXC's own rejection.
            (string Construct, int Line)? waveHit = SlangSm6ConstructGuard.FindConstruct(slangSource);
            if (waveHit is not null)
            {
                return Fail(new ShaderError(
                    File: sourceName, Line: waveHit.Value.Line, Column: 1,
                    Code: WaveQuadIntrinsics.VulkanUnsupportedCode,
                    Message: WaveQuadIntrinsics.VulkanUnsupportedMessage(waveHit.Value.Construct)));
            }
        }

        // Issue #257: the in-process route has no executable to find or prepare; everything
        // from the argument list onward is shared with the process route below.
        string? runnableSlangc = null;
        string? toolDirectory = null;
        if (_inProcessSlangc is null)
        {
            ShaderError? hostError = PrepareProcessSlangc(sourceName, out runnableSlangc, out toolDirectory);
            if (hostError is not null)
                return Fail(hostError);
        }

        // Phase 66 A6: forward the SAME per-target platform macros (OPENGL/SM4/VULKAN/SM6/
        // HLSL/GLSL/MGFX/FNA/SM3, plus __KNIFX__ when options.Container is Knifx) the
        // ordinary .fx pipeline defines for every hand-written shader (PlatformMacros.For,
        // CompilationPipeline). Found via the residue sweep: a huge share of the real
        // (non-Slang-authored) fixture corpus branches on '#if OPENGL' / '#if VULKAN' /
        // '#if SM4' / '#ifdef __KNIFX__' for per-target correctness (different SV_POSITION
        // spellings, the Vulkan/FXC matrix-transpose difference in Instancing.fx, profile
        // selection, ...). Before this fix, RunSlangc only ever forwarded the user's OWN
        // CompilerOptions.Defines to slangc's preprocessor — never the target macros — so a
        // Slang author writing the exact same '#if OPENGL'/'#if VULKAN' idiom the rest of
        // this project's shaders already rely on got the SAME resolved branch on every
        // target (whichever one is true with none of these macros defined, whether that is
        // a real branch or the least-surprising "just fails to compile" case for an
        // '#ifdef'-only guard), independent of options.Target — a silent, target-blind
        // divergence for any Slang author who reasonably expects the convention every other
        // ShadowDusk shader already gets. Not a Slang-only concern to skip: Metal is the
        // one PlatformMacros.For target ShadowDusk doesn't implement yet, so it is left
        // out entirely (no macros) rather than throwing — nothing routes a real compile at
        // Metal through this class today, and no diagnostic is owed for a target that
        // cannot reach here.
        IReadOnlyList<MacroDefinition> platformMacros = PlatformMacros.IsSupported(options.Target)
            ? PlatformMacros.For(options.Target, options.Container).Macros
            : [];

        // Sequential, not parallel: every invocation shares the SAME writable directory (and
        // therefore the same first-compile cache write into it), so running entries one at a
        // time sidesteps any question of concurrent-write safety in slangc itself, which
        // this project makes no claim about.
        var perEntryHlsl = new List<string>(entries.Count);
        foreach (SlangEntryPoint entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string stage = entry.Stage == SlangStage.Vertex ? "vertex" : "fragment";
            ShaderError? startError = InvokeSlangc(
                SlangcArguments.Build(platformMacros, options.Defines, entry.Name, stage),
                slangSource, sourceName, runnableSlangc, toolDirectory,
                out int exitCode, out string stdout, out string stderr);
            if (startError is not null)
                return Fail(startError);

            if (exitCode != 0)
            {
                return Fail(SlangDiagnosticReformatter.SelectPrimary(stderr, sourceName, entry.Name, stage));
            }

            perEntryHlsl.Add(stdout);
        }

        // Issue #252: slangc registers every texture/sampler itself; only the author's own
        // register(...) annotations survive (SlangcRegisterStripper). Which ones the author
        // wrote is read from slangc's OWN preprocess-only output, produced with the macros the
        // compiles above saw: a register in an inactive #if branch is not this target's intent,
        // and one written through a macro is. Issue #292 extends it to declarations from an
        // imported module or an __include'd file, and to a combined Sampler2D's split halves.
        Result<IReadOnlySet<string>, ShaderError> kept = DecideAuthorRegisters(
            perEntryHlsl, entries, slangSource, sourceName, options.Defines, platformMacros,
            runnableSlangc, toolDirectory, cancellationToken);
        if (kept.IsFailure)
            return Fail(kept.Error);

        // Stripped per entry, before the merge, so both entries' copies of a shared
        // declaration stay identical.
        for (int i = 0; i < perEntryHlsl.Count; i++)
            perEntryHlsl[i] = SlangcRegisterStripper.Strip(perEntryHlsl[i], kept.Value);

        string mergedHlsl = SlangHlslMerger.TryMerge(
            perEntryHlsl, entries.Select(e => e.Name).ToArray(), out var mergeConflicts);
        if (mergeConflicts.Count > 0)
        {
            // Entries compiled by separate slangc processes number their generated symbols
            // independently; the merger renames colliding structs/functions, but a colliding
            // cbuffer/resource name cannot be renamed (it IS a reflected parameter name).
            // Reject by name instead of letting DXC report a redefinition with no location.
            var c = mergeConflicts[0];
            return Fail(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0625",
                Message: "Entry points compile to different declarations of the same global " +
                         "name '" + c.Name + "' and slangc output for each entry is merged into one effect, " +
                         "so the name cannot be kept unique. Rename the shader parameter or resource, " +
                         "or give each entry point its own source file."));
        }
        // Issue #230: FNA's fx_2_0 needs DX9 effect texture syntax; slangc only emits texture
        // objects, which compiled but crashed real FNA on the first draw. See the respeller.
        if (options.Target == PlatformTarget.Fna)
        {
            SlangFx2TextureRespeller.Result respelled = SlangFx2TextureRespeller.Respell(mergedHlsl);
            if (respelled.Text is null)
            {
                return Fail(new ShaderError(
                    File: sourceName, Line: respelled.SourceLine, Column: respelled.SourceLine > 0 ? 1 : 0,
                    Code: SlangFx2TextureRespeller.UnsupportedCode,
                    Message: "The FNA target (fx_2_0, Shader Model 2-3) binds textures through DX9 " +
                             "texture/sampler_state/tex2D, and ShadowDusk cannot respell slangc's emission " +
                             "into that form because it contains " + respelled.Unsupported + ". Supported on " +
                             "FNA: a global Texture2D sampled as T.Sample(S, uv) through a global " +
                             "SamplerState, one texture per sampler."));
            }
            mergedHlsl = respelled.Text;
        }

        string fxText = AssembleFx(mergedHlsl, entries, sourceName);

        return _downstreamCompiler.Compile(fxText, options, cancellationToken);
    }

    /// <summary>
    /// The emitted texture/sampler declarations whose register the author wrote (by emitted
    /// name), every other slangc-numbered one to be stripped. Fails rather than guess: a
    /// preprocess pass that cannot be read, or a declaration no pass can decide, is an error.
    /// </summary>
    /// <remarks>
    /// Runs after every compile (so each compile diagnostic is unchanged, and a source whose
    /// preprocessing reports an error never gets here: <c>-E</c> exits 0 even then). The
    /// entry source's <c>-E</c> pass runs only when a <c>register</c> token can exist in it,
    /// or when the emission holds a registered declaration from another file. A declaration
    /// from another file that the entry source's text does not mention (an <c>import</c>ed
    /// module or an <c>__include</c>d file, issue #292) is judged from that file's own
    /// <c>-E</c> pass, one per file, over the same transport: on the in-process (browser)
    /// route slangc opens the path in its own virtual filesystem exactly as its compile did.
    /// </remarks>
    private Result<IReadOnlySet<string>, ShaderError> DecideAuthorRegisters(
        List<string> perEntryHlsl,
        IReadOnlyList<SlangEntryPoint> entries,
        string slangSource,
        string sourceName,
        IReadOnlyList<UserDefine> defines,
        IReadOnlyList<MacroDefinition> platformMacros,
        string? runnableSlangc,
        string? toolDirectory,
        CancellationToken cancellationToken)
    {
        List<SlangcRegisterStripper.EmittedResource> emitted =
            perEntryHlsl.SelectMany(SlangcRegisterStripper.FindRegistered).ToList();
        bool fromOtherFiles = emitted.Any(r => r.File != SlangcRegisterStripper.EntrySourceFile);
        if (emitted.Count == 0)
            return Result<IReadOnlySet<string>, ShaderError>.Ok(new HashSet<string>(StringComparer.Ordinal));

        var entryBindings = SlangcRegisterStripper.AuthorBindings.None;
        string entryPreprocessed = "";
        if (fromOtherFiles || SlangcRegisterStripper.MayWriteRegister(slangSource, defines))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ShaderError? startError = InvokeSlangc(
                SlangcArguments.BuildPreprocess(platformMacros, defines),
                slangSource, sourceName, runnableSlangc, toolDirectory,
                out int exitCode, out string preprocessed, out string stderr);
            if (startError is not null)
                return Result<IReadOnlySet<string>, ShaderError>.Fail(startError);

            if (exitCode != 0)
            {
                // Never guess which registers are the author's: without the preprocessed
                // source the strip could silently move a texture to another slot.
                return Result<IReadOnlySet<string>, ShaderError>.Fail(SlangDiagnosticReformatter.SelectPrimary(
                    stderr, sourceName,
                    "slangc failed its preprocess-only pass (-E, which finds the registers the " +
                    "author wrote) with no diagnostic output, after every entry point compiled."));
            }

            // A successful pass that printed nothing (or dropped an entry point the compile
            // just found) is not "the author wrote no register": reading it that way would strip
            // every author register silently. Fail by name instead.
            string? missingEntry = string.IsNullOrWhiteSpace(preprocessed)
                ? null
                : entries.Select(e => e.Name).FirstOrDefault(
                    name => !Regex.IsMatch(preprocessed, $@"\b{Regex.Escape(name)}\b"));
            if (string.IsNullOrWhiteSpace(preprocessed) || missingEntry is not null)
            {
                return Result<IReadOnlySet<string>, ShaderError>.Fail(new ShaderError(
                    File: sourceName, Line: 0, Column: 0, Code: PreprocessOutputUnusableCode,
                    Message: "slangc's preprocess-only pass (-E, which finds the registers the author " +
                             "wrote) exited 0 but its output " +
                             (missingEntry is null
                                 ? "was empty"
                                 : $"does not contain the entry point '{missingEntry}' that slangc just compiled") +
                             ", so ShadowDusk cannot tell which texture/sampler registers are the author's " +
                             "and will not guess (guessing would silently move textures to other slots)."));
            }

            entryBindings = SlangcRegisterStripper.AuthorBindings.Parse(preprocessed);
            entryPreprocessed = preprocessed;
        }

        // Issue #292: declarations the entry source's text cannot speak for (an imported module's
        // or an __include'd file's). The files read, each through the same -E pass, are: the file
        // slangc's #line names for each such declaration, then every file the texts read so far
        // import or __include by quoted path, transitively. A resource slangc hoisted out of an
        // aggregate (a combined sampler's half, a struct's field) carries slangc's own core-module
        // location instead of the author's file (measured), so for those the imports are the way in.
        List<SlangcRegisterStripper.EmittedResource> unproven = emitted
            .Where(r => SlangcRegisterStripper.Judge(r, entryBindings, null) == SlangcRegisterStripper.RegisterVerdict.Unproven)
            .ToList();
        SlangcRegisterStripper.AuthorBindings? otherFiles = null;
        var readFiles = new HashSet<string>(StringComparer.Ordinal) { SlangcRegisterStripper.EntrySourceFile };
        if (unproven.Count > 0)
        {
            var queue = new Queue<(string File, SlangcRegisterStripper.EmittedResource? Owner)>();
            var queued = new HashSet<string>(StringComparer.Ordinal);
            foreach (SlangcRegisterStripper.EmittedResource r in unproven)
            {
                if (queued.Add(r.File))
                    queue.Enqueue((r.File, r));
            }
            foreach (string file in SlangcRegisterStripper.QuotedImports(entryPreprocessed, importingFile: null))
            {
                if (queued.Add(file))
                    queue.Enqueue((file, null));
            }

            var texts = new List<string>();
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string file, SlangcRegisterStripper.EmittedResource? owner) = queue.Dequeue();

                ShaderError? startError = InvokeSlangc(
                    SlangcArguments.BuildPreprocessFile(platformMacros, defines, file),
                    slangSource, sourceName, runnableSlangc, toolDirectory,
                    out int exitCode, out string preprocessed, out string stderr);
                if (startError is not null)
                    return Result<IReadOnlySet<string>, ShaderError>.Fail(startError);

                // '-E' exits 0 even when it cannot open the file (measured), so an error line on
                // stderr or an empty output counts as a failed read, with slangc's words verbatim.
                if (exitCode != 0 || string.IsNullOrWhiteSpace(preprocessed) || stderr.Contains("error[", StringComparison.Ordinal))
                {
                    // A file slangc itself named for an author's declaration must be readable. An
                    // import path read from the text is resolved here, not by slangc, and a hoisted
                    // resource's #line names slangc's core module, so either one that does not open
                    // only leaves its declarations unproven (reported by name below).
                    if (owner is not { } o || o.IsHoisted)
                        continue;
                    string why = string.IsNullOrWhiteSpace(stderr)
                        ? (exitCode != 0 ? $"slangc exited {exitCode} with no diagnostic output" : "its output was empty")
                        : "slangc reported: " + stderr.Trim();
                    return Result<IReadOnlySet<string>, ShaderError>.Fail(Unprovable(
                        o, sourceName, locatable: true,
                        $"the preprocess-only pass over '{file}' (slangc -E, which finds the registers the author " +
                        $"wrote there) could not be read: {why}"));
                }
                texts.Add(preprocessed);
                readFiles.Add(file);
                foreach (string imported in SlangcRegisterStripper.QuotedImports(preprocessed, file))
                {
                    if (queued.Add(imported))
                        queue.Enqueue((imported, null));
                }
            }
            otherFiles = SlangcRegisterStripper.AuthorBindings.Union(texts);
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (SlangcRegisterStripper.EmittedResource resource in emitted)
        {
            switch (SlangcRegisterStripper.Judge(resource, entryBindings, otherFiles))
            {
                case SlangcRegisterStripper.RegisterVerdict.Keep:
                    keep.Add(resource.Name);
                    break;
                case SlangcRegisterStripper.RegisterVerdict.Unproven:
                    bool locatable = readFiles.Contains(resource.File);
                    return Result<IReadOnlySet<string>, ShaderError>.Fail(Unprovable(
                        resource, sourceName, locatable,
                        locatable
                            ? $"'{resource.File}' preprocessed with this target's macros neither writes register(...) on " +
                              $"'{resource.AuthorName}' nor declares it plainly (a register spelled through a macro defined " +
                              "in another file, a declaration inside a namespace or block, or two files that disagree)"
                            : $"no file ShadowDusk could read declares '{resource.AuthorName}': it is not in the entry " +
                              $"source, slangc's #line names '{resource.File}' for it (its own core module, for a resource " +
                              "it hoisted out of a combined sampler or a struct), and an import by module name rather " +
                              "than by quoted path cannot be followed"));
            }
        }
        return Result<IReadOnlySet<string>, ShaderError>.Ok(keep);
    }

    /// <summary><c>SD0628</c>: a texture/sampler register whose authorship cannot be proven,
    /// located at the declaration slangc's <c>#line</c> names when that is a file ShadowDusk could
    /// read, else at the entry source (a hoisted resource's <c>#line</c> is slangc's core module).</summary>
    private static ShaderError Unprovable(
        SlangcRegisterStripper.EmittedResource resource, string sourceName, bool locatable, string reason)
    {
        string what = resource.AuthorName == resource.Name
            ? $"'{resource.Name}'"
            : $"'{resource.Name}' (which slangc may have hoisted out of '{resource.AuthorName}')";
        return new ShaderError(
            File: locatable ? resource.File : sourceName,
            Line: locatable ? resource.Line : 0,
            Column: locatable ? 1 : 0,
            Code: RegisterAuthorshipUnprovenCode,
            Message: $"slangc emitted register({resource.RegisterClass}...) on {what}, declared in '{resource.File}', and " +
                     $"ShadowDusk cannot prove whether the author wrote that register or slangc numbered it itself: {reason}. " +
                     "ShadowDusk will not guess (keeping an invented register or dropping an author's one would silently " +
                     "move a texture to another slot). Declare the resource in the entry source, or write its register(...) " +
                     "directly on its declaration in a file the entry source imports by quoted path.");
    }

    /// <summary>
    /// One slangc run over whichever transport this instance uses, with the output text in
    /// the same form on both (issue #257). Returns an error only when the process could not
    /// be started at all; a slangc failure is a non-zero <paramref name="exitCode"/>.
    /// </summary>
    private ShaderError? InvokeSlangc(
        IReadOnlyList<string> arguments,
        string slangSource,
        string sourceName,
        string? runnableSlangc,
        string? toolDirectory,
        out int exitCode,
        out string stdout,
        out string stderr)
    {
        if (_inProcessSlangc is { } inProcess)
        {
            (exitCode, stdout, stderr) = inProcess(slangSource, arguments);
            stdout = SlangcArguments.JoinOutputLines(stdout);
            stderr = SlangcArguments.JoinOutputLines(stderr);
            return null;
        }

        try
        {
            (exitCode, stdout, stderr) = _runSlangc(runnableSlangc!, toolDirectory!, slangSource, arguments);
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The OS refused to start the process at all (no execute permission, wrong
            // architecture, a loader rejection): surface the OS's own words, never a crash.
            exitCode = -1;
            stdout = stderr = "";
            return new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0622",
                Message: $"slangc could not be started ('{runnableSlangc}'): {ex.Message}");
        }
    }

    /// <summary>
    /// The process route's host checks: finds the bundled slangc (<c>SD0620</c> on a host the
    /// natives cannot run on, <c>SD0621</c> when it is missing) and prepares it to run
    /// (<c>SD0623</c>). Returns null when <paramref name="runnableSlangc"/> is ready.
    /// </summary>
    private ShaderError? PrepareProcessSlangc(string sourceName, out string? runnableSlangc, out string? toolDirectory)
    {
        runnableSlangc = null;
        toolDirectory = null;
        SlangcLocation location = _locateSlangc();
        if (location.UnsupportedReason is not null)
        {
            return new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0620",
                Message: location.UnsupportedReason + " ShadowDusk.Compiler's built-in .slang " +
                         "frontend (the HLSL-compatible subset) works everywhere if the source " +
                         "does not need genuine Slang-only features (import/generics/interfaces).");
        }

        if (location.SlangcPath is null)
        {
            return new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0621",
                Message: $"slangc was not found for {SlangToolPath.CurrentRid}. Probed the app " +
                         $"base directory, runtimes/{SlangToolPath.CurrentRid}/native/ under it, " +
                         "the host's native search directories, and a repository " +
                         $"tools/slang/{SlangToolPath.CurrentRid}/ restore (tools/restore.sh / " +
                         "restore.ps1). A package consumer should never hit this: the native " +
                         "rides inside the ShadowDusk.Slang package.");
        }

        string prepared;
        try
        {
            prepared = _prepareSlangc(location.SlangcPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0623",
                Message: "Could not prepare slangc to run (it needs its compiler library beside " +
                         "it, an execute permission, and a writable directory for the runtime " +
                         $"cache file it writes on first compile): {ex.Message}");
        }
        runnableSlangc = prepared;
        toolDirectory = Path.GetDirectoryName(prepared)!;
        return null;
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="Compile"/>: the whole slangc-invocation +
    /// downstream-pipeline work is offloaded to the thread pool, matching
    /// <c>DxcShaderCompiler.CompileAsync</c>'s shape (one implementation, never a fork).
    /// </summary>
    public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string slangSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Compile(slangSource, options, cancellationToken), cancellationToken);
    }

    private static Result<CompiledShader, ShaderError[]> Fail(ShaderError error) =>
        Result<CompiledShader, ShaderError[]>.Fail([error]);

    /// <summary>
    /// Assembles the same kind of <c>.fx</c> wrapper <c>SlangFrontend.ConvertToFx</c>
    /// synthesizes for the subset route — the SM4 shader-model header plus a one-pass
    /// <c>technique</c> referencing the discovered entry point name(s) — around slangc's
    /// (possibly merged) HLSL emission.
    /// </summary>
    private static string AssembleFx(
        string mergedHlsl, IReadOnlyList<SlangEntryPoint> entries, string sourceName)
    {
        SlangEntryPoint? vs = entries.FirstOrDefault(e => e.Stage == SlangStage.Vertex);
        SlangEntryPoint? ps = entries.FirstOrDefault(e => e.Stage == SlangStage.Fragment);

        // '\n' explicitly, never AppendLine: AppendLine writes the HOST newline ("\r\n" on
        // Windows), and slangc's own body is already '\n'-joined, so Windows got mixed line
        // endings and a different intermediate text than Linux/macOS (caught by
        // SlangCrossHostByteIdentityTests' AssembledFx keys; the compiled bytes matched).
        var sb = new StringBuilder();
        void Line(string text = "") => sb.Append(text).Append('\n');
        Line($"// Generated from '{sourceName}' by ShadowDusk's real-slangc Slang route (Phase 66 A3).");
        Line("// The body below is slangc's own -target hlsl emission for the discovered entry");
        Line("// point(s); the technique block is synthesized the same way the HLSL-compatible-");
        Line("// subset frontend (SlangFrontend.ConvertToFx) does for its own .slang input.");
        Line();

        // Same measured convention SlangFrontend/the ShaderToy frontend use: gate on SM4
        // (exactly what the DirectX profiles define), not on OPENGL.
        Line("#if SM4");
        Line("    #define VS_SHADERMODEL vs_4_0_level_9_1");
        Line("    #define PS_SHADERMODEL ps_4_0_level_9_1");
        Line("#else");
        Line("    #define VS_SHADERMODEL vs_3_0");
        Line("    #define PS_SHADERMODEL ps_3_0");
        Line("#endif");
        Line();
        string cleanedHlsl = StripMatrixPackingPragma(StripUnresolvableConditionalIncludes(mergedHlsl));
        Line(cleanedHlsl.Trim());
        Line();
        Line($"technique {TechniqueName}");
        Line("{");
        Line("    pass P0");
        Line("    {");
        if (vs is not null)
            Line($"        VertexShader = compile VS_SHADERMODEL {vs.Name}();");
        if (ps is not null)
            Line($"        PixelShader = compile PS_SHADERMODEL {ps.Name}();");
        Line("    }");
        Line("}");
        return sb.ToString();
    }

    // Every slangc -target hlsl emission opens with an '#ifdef SLANG_HLSL_ENABLE_NVAPI /
    // #include "nvHLSLExtns.h" / #endif' guard (NVIDIA's HLSL extension header — irrelevant
    // to every ShadowDusk target, and the macro is never defined here so real HLSL
    // preprocessing would always skip it). ShadowDusk.Core.Preprocessor.Preprocessor does a
    // textual '#include' scan ahead of DXC (matching mgfxc's own flatten-before-compile
    // shape) and does NOT track #ifdef/#endif nesting, so it tries to resolve the include
    // unconditionally and fails with SD0001 even though a real preprocessor would never
    // reach it. Stripped here rather than taught to the shared Preprocessor: this guard is
    // specific to slangc's HLSL emission, not a general HLSL shape ShadowDusk needs to
    // support for hand-written .fx input.
    private static readonly Regex ConditionalIncludeGuard = new(
        """#ifdef\s+\w+\r?\n\s*#include\s*["<][^">]+[">]\r?\n\s*#endif\r?\n?""",
        RegexOptions.Compiled);

    private static string StripUnresolvableConditionalIncludes(string hlsl) =>
        ConditionalIncludeGuard.Replace(hlsl, "");

    // Every slangc -target hlsl emission opens with an unconditional '#pragma
    // pack_matrix(column_major)' (confirmed, Phase 66 A4: present regardless of whether the
    // source declares a float4x4 at all). An HLSL '#pragma' always overrides a compiler
    // command-line packing flag, so left in place this silently defeats
    // DxcFlagBuilder's per-platform matrix-packing convention that every OTHER .fx shader
    // in the pipeline (hand-written ones never emit this pragma) already relies on:
    // OpenGL's '-Zpr' (row-major) flag exists specifically so DXC's SPIR-V decorates a
    // cbuffer matrix ColMajor (SPIR-V's inverted term for HLSL row-major — see
    // DxcFlagBuilder's own comment), matching GLSL's own column-major default so
    // SPIRV-Cross never needs an explicit layout qualifier. slangc's pragma forces
    // column-major regardless, which decorates the SPIR-V the opposite way (RowMajor),
    // and SPIRV-Cross then emits an explicit 'layout(row_major)' prefix on the GLSL mat4
    // member that MonoGameGlslRewriter.UniformMember's regex does not model (SD0210) —
    // this was the root cause of the 3 OpenGL corpus failures (Desaturate/ScrollUv/
    // WaveVertex, all with a float4x4 cbuffer member). Stripping the pragma is a measured
    // no-op for DirectX_11 (d3dcompiler_47's ShaderFlags.PackMatrixColumnMajor already
    // matches HLSL's own column-major default with or without a redundant pragma saying
    // so) and for Vulkan/DirectX12 (DXC's own default is column-major, and DxcFlagBuilder
    // deliberately omits -Zpr for both) — it restores OpenGL to the SAME convention every
    // other .fx shader already gets, rather than teaching MonoGameGlslRewriter a parallel
    // layout-qualifier special case for Slang's own output shape.
    private static readonly Regex MatrixPackingPragma = new(
        """^[ \t]*#pragma\s+pack_matrix\(column_major\)[ \t]*\r?\n""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string StripMatrixPackingPragma(string hlsl) =>
        MatrixPackingPragma.Replace(hlsl, "");

    /// <summary>
    /// Runs slangc once for a single entry point: <c>-target hlsl -entry &lt;name&gt;
    /// -stage &lt;stage&gt;</c>, source piped over stdin (<c>-- -</c>), HLSL text captured
    /// from stdout. One invocation per entry point, not one multi-<c>-entry</c> invocation:
    /// slangc v2026.14.1's own <c>-o</c>-to-entry-point association rejects every ordering
    /// this project tried for a multi-entry, multi-output single invocation (measured, Phase
    /// 66 A3), while running with NO <c>-o</c> at all and one entry per process is reliable
    /// and — confirmed by diffing the two per-entry HLSL bodies against a single invocation
    /// that named both entries and let it print both to stdout — produces byte-identical
    /// declarations (slangc's mangling is deterministic per source identifier, not per
    /// process), which is exactly what makes <see cref="SlangHlslMerger"/>'s dedup valid.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> (not <c>private</c>) so <c>validation/SlangFullCorpus</c> (Phase 66 A7)
    /// can invoke the SAME slangc flags this class uses internally to build its pixel-equivalence
    /// route B (slangc's raw HLSL emission, fed directly to DXC) — duplicating the flag list in
    /// the validation driver would silently drift from whatever this method actually passes and
    /// invalidate the comparison; internal + <c>InternalsVisibleTo</c> (see the .csproj) keeps
    /// them provably identical instead.
    /// </remarks>
    internal static (int ExitCode, string Stdout, string Stderr) RunSlangc(
        string slangcPath,
        string workingDirectory,
        string slangSource,
        string entryName,
        string stage,
        IReadOnlyList<MacroDefinition> platformMacros,
        IReadOnlyList<UserDefine> defines) =>
        // Issue #257: the argument list lives in SlangcArguments, shared with the in-process
        // (browser) route so both hosts hand slangc the identical command line.
        RunSlangc(slangcPath, workingDirectory, slangSource,
            SlangcArguments.Build(platformMacros, defines, entryName, stage));

    /// <summary>
    /// Runs slangc once with <paramref name="arguments"/> (a <see cref="SlangcArguments"/>
    /// list): <paramref name="slangSource"/> piped over stdin, stdout and stderr captured line
    /// by line and re-joined with <c>'\n'</c>.
    /// </summary>
    internal static (int ExitCode, string Stdout, string Stderr) RunSlangc(
        string slangcPath,
        string workingDirectory,
        string slangSource,
        IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(slangcPath)
        {
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            // slangc reads its stdin source as UTF-8. Without an explicit encoding, .NET
            // writes stdin in the console's input code page, which on Windows is the OEM code
            // page, so a non-ASCII byte in the source would reach slangc differently per host.
            StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };

        // Event-based async reads + a synchronous WaitForExit below: the standard
        // deadlock-safe pattern for a redirected child process (writing all of stdin before
        // stdout/stderr are being drained risks a full pipe buffer blocking the child while
        // this thread blocks writing — draining both streams concurrently avoids that
        // regardless of shader source/output size).
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.Append(e.Data).Append('\n'); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.StandardInput.Write(slangSource);
        process.StandardInput.Close();

        process.WaitForExit();

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
