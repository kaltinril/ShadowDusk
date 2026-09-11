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

    private readonly IShaderCompiler _downstreamCompiler;

    /// <summary>
    /// Creates a <see cref="SlangCompiler"/>. The optional <paramref name="downstreamCompiler"/>
    /// exists for tests that want to isolate the slangc-invocation/merge/assembly logic from
    /// the (heavy, native-backed) downstream pipeline; production code should pass nothing
    /// and get the real <see cref="EffectCompiler"/>.
    /// </summary>
    public SlangCompiler(IShaderCompiler? downstreamCompiler = null)
    {
        _downstreamCompiler = downstreamCompiler ?? new EffectCompiler();
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

        if (!SlangToolPath.IsSupportedOnThisPlatform)
        {
            return Fail(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0620",
                Message: "ShadowDusk.Slang's real-slangc compile route packages slangc for " +
                         "win-x64 only today (Phase 66 A2/A3); this platform/architecture is " +
                         "not yet supported. ShadowDusk.Compiler's built-in .slang frontend " +
                         "(the HLSL-compatible subset) works everywhere if the source does " +
                         "not need genuine Slang-only features (import/generics/interfaces)."));
        }

        string? slangcPath = SlangToolPath.Resolve();
        if (slangcPath is null)
        {
            return Fail(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0621",
                Message: "slangc.exe was not found. A framework-dependent build that never " +
                         "copies runtimes/win-x64/native locally (or a repo/dev build that " +
                         "never ran tools/restore.ps1 / restore.sh) hits this — see " +
                         "SlangToolPath.ResolveOrThrow's remarks."));
        }

        Result<IReadOnlyList<SlangEntryPoint>, ShaderError[]> entriesResult =
            SlangEntryScanner.Scan(slangSource, sourceName);
        if (entriesResult.IsFailure)
            return Result<CompiledShader, ShaderError[]>.Fail(entriesResult.Error);
        IReadOnlyList<SlangEntryPoint> entries = entriesResult.Value;

        string toolDirectory;
        try
        {
            toolDirectory = SlangNativeCache.EnsureWritableToolDirectory(slangcPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return Fail(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0623",
                Message: "Could not prepare a writable directory to run slangc from (it writes " +
                         $"a runtime cache file, slang-glsl-module.bin, into its own directory " +
                         $"on first compile): {ex.Message}"));
        }
        string runnableSlangc = Path.Combine(toolDirectory, "slangc.exe");

        // Sequential, not parallel: every invocation shares the SAME writable directory (and
        // therefore the same first-compile cache write into it), so running entries one at a
        // time sidesteps any question of concurrent-write safety in slangc itself, which
        // this project makes no claim about.
        var perEntryHlsl = new List<string>(entries.Count);
        foreach (SlangEntryPoint entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string stage = entry.Stage == SlangStage.Vertex ? "vertex" : "fragment";
            (int exitCode, string stdout, string stderr) = RunSlangc(
                runnableSlangc, toolDirectory, slangSource, entry.Name, stage, options.Defines);

            if (exitCode != 0)
            {
                return Fail(SlangDiagnosticReformatter.SelectPrimary(stderr, sourceName, entry.Name, stage));
            }

            perEntryHlsl.Add(stdout);
        }

        string mergedHlsl = SlangHlslMerger.Merge(perEntryHlsl);
        string fxText = AssembleFx(mergedHlsl, entries, sourceName);

        return _downstreamCompiler.Compile(fxText, options, cancellationToken);
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

        var sb = new StringBuilder();
        sb.AppendLine($"// Generated from '{sourceName}' by ShadowDusk's real-slangc Slang route (Phase 66 A3).");
        sb.AppendLine("// The body below is slangc's own -target hlsl emission for the discovered entry");
        sb.AppendLine("// point(s); the technique block is synthesized the same way the HLSL-compatible-");
        sb.AppendLine("// subset frontend (SlangFrontend.ConvertToFx) does for its own .slang input.");
        sb.AppendLine();

        // Same measured convention SlangFrontend/the ShaderToy frontend use: gate on SM4
        // (exactly what the DirectX profiles define), not on OPENGL.
        sb.AppendLine("#if SM4");
        sb.AppendLine("    #define VS_SHADERMODEL vs_4_0_level_9_1");
        sb.AppendLine("    #define PS_SHADERMODEL ps_4_0_level_9_1");
        sb.AppendLine("#else");
        sb.AppendLine("    #define VS_SHADERMODEL vs_3_0");
        sb.AppendLine("    #define PS_SHADERMODEL ps_3_0");
        sb.AppendLine("#endif");
        sb.AppendLine();
        string cleanedHlsl = StripMatrixPackingPragma(StripUnresolvableConditionalIncludes(mergedHlsl));
        sb.AppendLine(cleanedHlsl.Trim());
        sb.AppendLine();
        sb.AppendLine($"technique {TechniqueName}");
        sb.AppendLine("{");
        sb.AppendLine("    pass P0");
        sb.AppendLine("    {");
        if (vs is not null)
            sb.AppendLine($"        VertexShader = compile VS_SHADERMODEL {vs.Name}();");
        if (ps is not null)
            sb.AppendLine($"        PixelShader = compile PS_SHADERMODEL {ps.Name}();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
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
    private static (int ExitCode, string Stdout, string Stderr) RunSlangc(
        string slangcPath,
        string workingDirectory,
        string slangSource,
        string entryName,
        string stage,
        IReadOnlyList<UserDefine> defines)
    {
        var psi = new ProcessStartInfo(slangcPath)
        {
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        psi.ArgumentList.Add("-lang");
        psi.ArgumentList.Add("slang");
        foreach (UserDefine define in defines)
            psi.ArgumentList.Add($"-D{define.Name}={define.Value}");
        psi.ArgumentList.Add("-target");
        psi.ArgumentList.Add("hlsl");
        // Without this, slangc wraps every cbuffer's members in a generated
        // 'SLANG_ParameterGroup_*' struct and gives the cbuffer itself a single member of
        // that struct type (Phase 65 §2's residue finding). That is legal HLSL — DXC
        // compiles it fine — but ShadowDusk's own OpenGL uniform-block lowering
        // (the MojoShader-dialect GLSL rewrite) only models FLAT float/vec2/vec3/vec4/mat4
        // members and arrays of those directly inside a cbuffer, so a nested-struct member
        // fails loudly with SD0210 (measured, Phase 66 A3: every corpus shader with a
        // cbuffer failed OpenGL specifically until this flag was added). This flag makes
        // slangc emit flat members directly in the cbuffer instead — DirectX_11 was
        // unaffected either way. It does NOT touch the separate, still-unfixed '_N' name
        // mangling (A4's job): 'float Desaturation_0' still carries slangc's suffix, only
        // the cbuffer's SHAPE changes.
        psi.ArgumentList.Add("-no-hlsl-pack-constant-buffer-elements");
        // Phase 66 A4: without this, slangc renames every symbol with an '_N' suffix
        // ('float BlurAmount' -> 'float BlurAmount_0'), which would surface in a
        // consumer's compiled effect's reflected parameter table and break
        // effect.Parameters["BlurAmount"] lookups. '-no-mangle' is documented by slangc
        // itself as experimental ("do as little mangling of names as possible"), but
        // measured (Phase 66 A4) against the full 21-shader corpus on both DirectX_11 and
        // OpenGL: every top-level declaration that matters for the reflected parameter
        // table — cbuffer names, cbuffer members, Texture2D/SamplerState declarations —
        // comes back with the author's exact original name, with no collisions anywhere
        // in the corpus (including GenericsProbe.slang's real generic-over-interface
        // function). Local variables and struct field names (VSOutput/PsInput members,
        // the loop-body temporaries) still carry an '_N' suffix, but those are never part
        // of an Effect's reflected parameter table, so they don't matter for the
        // consumer-visible surface this flag exists to fix. No corpus shader failed to
        // compile with the flag added, so the simpler fix (this flag) was taken over
        // building a separate demangling/renaming shim.
        psi.ArgumentList.Add("-no-mangle");
        psi.ArgumentList.Add("-entry");
        psi.ArgumentList.Add(entryName);
        psi.ArgumentList.Add("-stage");
        psi.ArgumentList.Add(stage);
        // '--' then '-': read the single input file from stdin (slangc -h: "Use '-' once to
        // read from standard input; -lang is required, stdin is limited to 256 MiB").
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("-");

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
