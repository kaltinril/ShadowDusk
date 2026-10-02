#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ShadowDusk.Core;
using ShadowDusk.Wasm;

namespace ShadowDusk.Slang.Wasm;

/// <summary>
/// Full Slang input in the browser (issue #257): the browser counterpart of
/// <see cref="SlangCompiler"/>. A browser cannot spawn a process, so slangc runs inside the
/// page as WebAssembly: the SAME pinned slangc (v2026.14.1), linked from upstream's own
/// prebuilt wasm libraries, driven through slang's own command-line parser with the exact
/// argument list the desktop route passes. Its HLSL then goes through the unchanged
/// in-browser pipeline (<see cref="WasmShaderCompiler"/>: DXC, SPIRV-Cross, vkd3d), so a
/// <c>.slang</c> file compiles to the same bytes in the browser as on the desktop.
///
/// <para>Everything around the slangc call (entry discovery, the host-independent
/// rejections, the register strip, the per-entry merge, the <c>.fx</c> assembly) is
/// <see cref="SlangCompiler"/>'s own code, reached through its internal in-process seam, not a
/// copy. Slang is an input language only: this class never compiles HLSL itself and never
/// stands in for DXC.</para>
///
/// <para>Like <see cref="WasmShaderCompiler"/>, the one asynchronous step is the one-time
/// module load. <see cref="CompileAsync"/> does it on first use; a synchronous caller awaits
/// <see cref="InitializeAsync"/> once and may then call <see cref="Compile"/>.</para>
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WasmSlangCompiler
{
    private readonly WasmShaderCompiler _downstream;
    private readonly SlangCompiler _compiler;

    /// <summary>Creates a compiler over a new <see cref="WasmShaderCompiler"/>.</summary>
    public WasmSlangCompiler()
        : this(new WasmShaderCompiler())
    {
    }

    /// <summary>
    /// Creates a compiler that hands slangc's HLSL to <paramref name="downstream"/>, so a page
    /// that already owns a <see cref="WasmShaderCompiler"/> shares its loaded modules.
    /// </summary>
    public WasmSlangCompiler(WasmShaderCompiler downstream)
    {
        _downstream = downstream ?? throw new ArgumentNullException(nameof(downstream));
        _compiler = new SlangCompiler(downstream, RunSlangc);
    }

    /// <summary>
    /// Loads the slangc module and warms the downstream pipeline (DXC, SPIRV-Cross, vkd3d).
    /// Idempotent; required once before <see cref="Compile"/>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SlangcModule.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        await _downstream.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles Slang source for <paramref name="options"/>' target, loading the modules first
    /// if needed. Diagnostics carry slangc's or DXC's own file, line and column.
    /// </summary>
    public async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string slangSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            return Result<CompiledShader, ShaderError[]>.Fail(
            [
                SlangcModule.LoadFailed(options.SourceFileName ?? "<memory>.slang", ex.Message),
            ]);
        }
        return Compile(slangSource, options, cancellationToken);
    }

    /// <summary>
    /// Synchronous compile; <see cref="InitializeAsync"/> must have completed. Before that,
    /// returns <c>SD0629</c> rather than aborting the .NET WebAssembly runtime.
    /// </summary>
    public Result<CompiledShader, ShaderError[]> Compile(
        string slangSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!SlangcModule.IsReady)
        {
            return Result<CompiledShader, ShaderError[]>.Fail(
            [
                new ShaderError(
                    File: options.SourceFileName ?? "<memory>.slang", Line: 0, Column: 0, Code: "SD0629",
                    Message: "The in-browser slangc module is not loaded. Await WasmSlangCompiler.InitializeAsync() " +
                             "once before calling Compile, or call CompileAsync, which loads it on first use."),
            ]);
        }
        return _compiler.Compile(slangSource, options, cancellationToken);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunSlangc(
        string slangSource, IReadOnlyList<string> arguments)
    {
        try
        {
            string[] r = SlangcModule.RunSlangc(slangSource, arguments.ToArray());
            return (int.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture), r[1], r[2]);
        }
        catch (JSException ex)
        {
            // A trap inside the module (out of memory, an internal abort) is reported like a
            // slangc that died without diagnostics: non-zero exit, the module's own words.
            return (1, "", "shadowdusk-slangc module failed: " + ex.Message);
        }
    }
}
