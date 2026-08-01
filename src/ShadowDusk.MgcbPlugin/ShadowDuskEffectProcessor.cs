#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using ShadowDusk.Cli;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.MgcbPlugin;

/// <summary>
/// MGCB content processor that compiles an HLSL effect with ShadowDusk, in MGCB's own process:
/// no <c>mgfxc</c> child process, no <c>fxc.exe</c>, no Wine, no <c>PATH</c> plumbing.
/// <para>
/// <b>This is a delivery shape of the ShadowDusk library, not a second compiler.</b> It builds a
/// <see cref="CompilerOptions"/> from MGCB's build context, calls the one
/// <see cref="EffectCompiler"/> the CLI and the runtime API call, and wraps the resulting
/// <c>.mgfx</c> bytes in <see cref="CompiledEffectContent"/> for MonoGame's own effect
/// <c>ContentTypeWriter</c> to serialize into the <c>.xnb</c>. The bytes are byte-for-byte what
/// the ShadowDusk CLI emits for the same source and target, because they come from the same call.
/// </para>
/// <para>
/// Use it from a <c>.mgcb</c> like this:
/// <code>
/// /reference:&lt;path-to&gt;/ShadowDusk.MgcbPlugin.dll
///
/// #begin MyEffect.fx
/// /importer:ShadowDuskEffectImporter
/// /processor:ShadowDuskEffectProcessor
/// /build:MyEffect.fx
/// </code>
/// Every parameter below is optional: with none set, the target is derived from the
/// <c>.mgcb</c>'s own <c>/platform:</c> line and the output is the correct, backwards-compatible
/// MGFX v10 artifact.
/// </para>
/// </summary>
[ContentProcessor(DisplayName = "ShadowDusk Effect - ShadowDusk")]
public sealed class ShadowDuskEffectProcessor : ContentProcessor<EffectContent, CompiledEffectContent>
{
    // Installs the plugin-directory native fallback before anything can P/Invoke. See
    // PluginNativeLibraryResolver for why an MGCB host cannot find our natives otherwise.
    static ShadowDuskEffectProcessor() => PluginNativeLibraryResolver.Register();

    /// <summary>
    /// Whether to compile with debug information. <see cref="EffectProcessorDebugMode.Auto"/>
    /// (the default, and what MGCB writes into a <c>.mgcb</c> by default) means "debug when the
    /// content build configuration is Debug", matching MonoGame's stock <c>EffectProcessor</c>.
    /// </summary>
    public EffectProcessorDebugMode DebugMode { get; set; } = EffectProcessorDebugMode.Auto;

    /// <summary>
    /// Preprocessor macros, in the same spelling <c>mgfxc</c>'s <c>/Defines:</c> takes:
    /// <c>NAME=VALUE</c> entries separated by <c>;</c> or <c>,</c>; a bare <c>NAME</c> defines it
    /// as <c>1</c>. Empty by default. Same property name and format as the stock
    /// <c>EffectProcessor</c>, so an existing <c>/processorParam:Defines=...</c> carries over.
    /// </summary>
    public string Defines { get; set; } = string.Empty;

    /// <summary>
    /// <b>Escape hatch, never required.</b> Overrides the target that would be derived from the
    /// <c>.mgcb</c>'s <c>/platform:</c>. Accepts the ShadowDusk CLI's profile names
    /// (<c>DirectX_11</c>, <c>DirectX_12</c>, <c>OpenGL</c>, <c>Vulkan</c>). It exists because
    /// MGCB's <c>TargetPlatform</c> enum has no member for MonoGame's <c>WindowsDX12</c> or
    /// <c>DesktopVK</c> runtimes, so a consumer shipping those has no other way to say so.
    /// Empty (the default) derives the target from the platform, which is correct for every
    /// platform MGCB can name.
    /// </summary>
    public string ShaderProfile { get; set; } = string.Empty;

    /// <summary>
    /// Additional <c>#include</c> search directories, separated by <c>;</c>. The including
    /// file's own directory is always searched first and needs no entry here, so this is empty
    /// by default. Relative entries resolve against the effect's directory. Equivalent to the
    /// CLI's <c>/I</c> flag.
    /// </summary>
    public string IncludeDirs { get; set; } = string.Empty;

    /// <summary>
    /// <b>Escape hatch, never required.</b> The MGFX container version to emit. Defaults to
    /// <c>10</c>, the version every supported MonoGame and KNI runtime loads; <c>11</c> is the
    /// additive newer container. Ignored for <see cref="PlatformTarget.Fna"/> (which MGCB
    /// cannot target anyway).
    /// </summary>
    public int MgfxVersion { get; set; } = 10;

    /// <summary>
    /// <b>Escape hatch, never required.</b> Which backend compiles HLSL to SM5 DXBC for the
    /// DirectX target: <c>vkd3d</c> (the default, cross-platform, host-independent output) or
    /// <c>d3dcompiler</c> (the Windows-only correctness oracle). Ignored for non-DirectX targets.
    /// </summary>
    public string DxbcBackend { get; set; } = string.Empty;

    /// <summary>
    /// Compiles <paramref name="input"/> to a <c>.mgfx</c> effect for the platform
    /// <paramref name="context"/> names.
    /// </summary>
    /// <param name="input">The imported effect source.</param>
    /// <param name="context">MGCB's processor context: target platform, build config, logger.</param>
    /// <returns>The compiled effect, for MonoGame's effect writer to serialize into the <c>.xnb</c>.</returns>
    /// <exception cref="InvalidContentException">
    /// The shader failed to compile. The message carries every diagnostic verbatim, in the
    /// <c>file(line,col-col): severity CODE: message</c> form <c>fxc</c>/<c>mgfxc</c> use and MGCB,
    /// MSBuild, and IDEs parse.
    /// </exception>
    public override CompiledEffectContent Process(EffectContent input, ContentProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        string sourceFile = input.Identity?.SourceFilename ?? string.Empty;

        // Records every #include actually resolved so MGCB rebuilds when an .fxh changes.
        var includeRecorder = new RecordingIncludeResolver(new FileSystemIncludeResolver());

        var options = new CompilerOptions
        {
            Target                 = ResolveTarget(input, context),
            IncludeResolver        = includeRecorder,
            AdditionalIncludePaths = ParseIncludeDirs(sourceFile),
            SourceFileName         = sourceFile,
            Debug                  = ResolveDebug(context),
            MgfxVersion            = MgfxVersion,
            DxbcBackend            = ResolveDxbcBackend(input),
            Defines                = ParseDefines(Defines),
        };

        // SYNCHRONOUS on purpose. IContentProcessor.Process is a synchronous contract, and
        // EffectCompiler.Compile IS the pipeline core (CompileAsync is a thin Task.Run shell
        // over this same method - see EffectCompiler). So this is the sanctioned sync call, NOT
        // sync-over-async: there is no .Result and no .Wait() anywhere on this path, and the
        // bytes are identical to the async route by construction.
        Result<CompiledShader, ShaderError[]> result =
            new EffectCompiler().Compile(input.EffectCode, options);

        // Register include dependencies whether or not the compile succeeded: an edit that
        // FIXES a broken include must trigger a rebuild too.
        foreach (string include in includeRecorder.ResolvedPaths)
            context.AddDependency(include);

        if (result.IsFailure)
            throw BuildInvalidContentException(result.Error);

        // Non-fatal diagnostics: the underlying compiler's verbatim warnings plus ShadowDusk's
        // GL portability findings (SD0400-SD0499). Same text the CLI writes to stderr.
        LogWarnings(result.Value.Warnings, input, context);

        return new CompiledEffectContent(result.Value.Data);
    }

    /// <summary>
    /// The target to compile for: the <c>ShaderProfile</c> escape hatch when set, otherwise
    /// derived from MGCB's <c>/platform:</c>.
    /// </summary>
    private PlatformTarget ResolveTarget(EffectContent input, ContentProcessorContext context)
    {
        if (!string.IsNullOrWhiteSpace(ShaderProfile))
        {
            if (MgcbPlatformMap.TryParseShaderProfile(ShaderProfile, out PlatformTarget explicitTarget))
                return explicitTarget;

            throw Fail(new ShaderError(
                File: SourceFileOf(input),
                Line: 0,
                Column: 0,
                Code: "SD0500",
                Message:
                    $"Unknown ShaderProfile '{ShaderProfile}'. Valid values: {MgcbPlatformMap.ShaderProfileNames}. " +
                    "Leave the parameter empty to derive the target from the content project's /platform."));
        }

        PlatformTarget? mapped = MgcbPlatformMap.FromTargetPlatform(context.TargetPlatform);
        if (mapped is null)
        {
            throw Fail(new ShaderError(
                File: SourceFileOf(input),
                Line: 0,
                Column: 0,
                Code: "SD0501",
                Message:
                    $"platform '{context.TargetPlatform}' is not supported by ShadowDusk. " +
                    "Supported MGCB platforms: Windows, DesktopGL, MacOSX, iOS, Android, RaspberryPi, Web, NativeClient."));
        }

        return mapped.Value;
    }

    /// <summary>
    /// Resolves <see cref="DebugMode"/> the way MonoGame's stock <c>EffectProcessor</c> does:
    /// <c>Auto</c> follows the content build configuration, and anything else is explicit.
    /// </summary>
    private bool ResolveDebug(ContentProcessorContext context) => DebugMode switch
    {
        EffectProcessorDebugMode.Debug    => true,
        EffectProcessorDebugMode.Optimize => false,
        // Auto: a Debug content build gets debug info; anything else (including the common
        // empty /config:) optimizes, which is what the CLI does with no /Debug flag.
        _ => context.BuildConfiguration?.StartsWith("debug", StringComparison.OrdinalIgnoreCase) == true,
    };

    /// <summary>Resolves the DXBC backend escape hatch; empty means the cross-platform vkd3d default.</summary>
    private DxbcBackend ResolveDxbcBackend(EffectContent input)
    {
        if (string.IsNullOrWhiteSpace(DxbcBackend))
            return Core.DxbcBackend.Vkd3d;

        return DxbcBackend.Trim().ToLowerInvariant() switch
        {
            "vkd3d"        => Core.DxbcBackend.Vkd3d,
            "d3dcompiler"  => Core.DxbcBackend.D3DCompiler,
            _ => throw Fail(new ShaderError(
                File: SourceFileOf(input),
                Line: 0,
                Column: 0,
                Code: "SD0502",
                Message: $"Invalid DxbcBackend value '{DxbcBackend}'. Valid backends: vkd3d, d3dcompiler.")),
        };
    }

    /// <summary>
    /// Splits the <c>IncludeDirs</c> parameter, resolving relative entries against the effect's
    /// own directory (a <c>.mgcb</c> is built from the content project's directory, which is not
    /// necessarily where the effect lives).
    /// </summary>
    private IReadOnlyList<string> ParseIncludeDirs(string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(IncludeDirs))
            return [];

        string baseDir = sourceFile.Length > 0
            ? Path.GetDirectoryName(Path.GetFullPath(sourceFile)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        var dirs = new List<string>();
        foreach (string entry in IncludeDirs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
                continue;
            dirs.Add(Path.IsPathRooted(trimmed) ? trimmed : Path.GetFullPath(Path.Combine(baseDir, trimmed)));
        }

        return dirs;
    }

    /// <summary>
    /// Parses the <c>Defines</c> parameter with exactly the CLI's <c>/Defines:</c> grammar
    /// (<c>;</c> or <c>,</c> separated, bare name defines <c>1</c>), so the same string means the
    /// same thing in both delivery shapes.
    /// </summary>
    internal static IReadOnlyList<UserDefine> ParseDefines(string? defines)
    {
        if (string.IsNullOrWhiteSpace(defines))
            return [];

        var parsed = new List<UserDefine>();
        foreach (string entry in defines.Split(';', ','))
        {
            string trimmed = entry.Trim();
            if (trimmed.Length == 0)
                continue;
            int eq = trimmed.IndexOf('=');
            parsed.Add(eq < 0
                ? new UserDefine(trimmed)
                : new UserDefine(trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
        }

        return parsed;
    }

    /// <summary>
    /// Translates ShadowDusk's <c>Result</c> failure into the exception MGCB's contract requires.
    /// <b>This is the only place exceptions are used for control flow</b>, and it is at the edge:
    /// <c>IContentProcessor.Process</c> has no way to return a failure.
    /// </summary>
    private static InvalidContentException BuildInvalidContentException(IReadOnlyList<ShaderError> errors)
    {
        // Every diagnostic, in the CLI's own formatter, verbatim - file, line, column, code and
        // the underlying compiler's exact words, plus its raw output when it said more. The
        // first line is the canonical `file(line,col-col): error CODE: text` MSBuild and MGCB parse.
        string message = string.Join(Environment.NewLine, MgcbErrorFormatter.FormatAll(errors));

        // NO ContentIdentity, deliberately. MGCB prefixes `sourceFilename(fragmentIdentifier): `
        // to the message whenever the exception carries an identity with a source filename, which
        // would print the location TWICE - once in MGCB's prefix and once in our own already
        // correctly-formatted text. Nothing is lost: the file, line and column are in `message`,
        // in the exact form fxc/mgfxc/MSBuild use.
        return new InvalidContentException(message);
    }

    /// <summary>
    /// Renders a plugin-level failure through the same formatter shader errors use, so a
    /// configuration mistake and a compile error read identically in MGCB's output.
    /// The <c>SD05xx</c> codes are literals at each call site, which is what lets
    /// <c>DiagnosticCodeRegistryTests</c> see them and require a <c>docs/error-codes.md</c> row.
    /// </summary>
    private static InvalidContentException Fail(ShaderError error)
        => new(MgcbErrorFormatter.Format(error));

    /// <summary>The effect's path for diagnostics, or empty when MGCB supplied no identity.</summary>
    private static string SourceFileOf(EffectContent input)
        => input.Identity?.SourceFilename ?? string.Empty;

    /// <summary>
    /// Routes non-fatal diagnostics through MGCB's logger, one line each, in the CLI's format.
    /// The text is passed as a format ARGUMENT, never as the format string: a compiler message
    /// containing <c>{</c> (HLSL is full of braces) would otherwise blow up
    /// <c>string.Format</c> inside the logger.
    /// </summary>
    private static void LogWarnings(
        IReadOnlyList<ShaderError> warnings, EffectContent input, ContentProcessorContext context)
    {
        foreach (string line in MgcbErrorFormatter.FormatAll(warnings))
            context.Logger.LogWarning(null, input.Identity, "{0}", line);
    }
}
