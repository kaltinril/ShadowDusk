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
/// <see cref="InitializeAsync"/> once and may then call <see cref="Compile"/>. The compile
/// itself (slangc, then DXC/SPIRV-Cross or vkd3d) is synchronous and runs on the calling
/// thread, which in a browser is the page's main thread.</para>
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WasmSlangCompiler
{
    private readonly WasmShaderCompiler _downstream;
    private readonly RecordingDownstream _recording;
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
        _recording = new RecordingDownstream(downstream);
        _compiler = new SlangCompiler(_recording, RunSlangc);
    }

    /// <summary>
    /// Loads the slangc module and warms the downstream pipeline (DXC, SPIRV-Cross, vkd3d).
    /// Idempotent; required once before <see cref="Compile"/>. Throws
    /// <see cref="InvalidOperationException"/> naming the module that failed to load.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SlangcModule.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw new InvalidOperationException(
                "ShadowDusk WASM initialization failed while loading the slangc (Slang -> HLSL) WASM module " +
                "(shadowdusk-slangc, served under _content/ShadowDusk.Slang.Wasm/). Underlying error: " + ex.Message, ex);
        }
        // The downstream reports its own module failures (DXC / vkd3d) in its own words.
        await _downstream.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles Slang source for <paramref name="options"/>' target, loading what it needs first:
    /// the slangc module, then (only if the compile reaches it) the downstream module for this
    /// target. Diagnostics carry slangc's or DXC's own file, line and column; a module that fails
    /// to load is reported under its own code (<c>SD1904</c> for slangc, the downstream's
    /// <c>SD1900</c>/<c>SD1902</c> for DXC/vkd3d).
    /// </summary>
    public async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string slangSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        string sourceName = options.SourceFileName ?? "<memory>.slang";
        try
        {
            await SlangcModule.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            return Fail(SlangcModule.LoadFailed(sourceName, ex.Message));
        }

        Result<CompiledShader, ShaderError[]> result = Compile(slangSource, options, cancellationToken);

        // Another compile trapped and discarded the slangc instance between our EnsureReadyAsync
        // and this Compile (its continuation was queued behind the trap). SD1903's "await
        // InitializeAsync" advice is wrong for a CompileAsync caller, so reload once and retry.
        if (!result.IsSuccess && !SlangcModule.IsReady && result.Error.Any(static e => e.Code == "SD1903"))
        {
            try
            {
                await SlangcModule.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (JSException ex)
            {
                return Fail(SlangcModule.LoadFailed(sourceName, ex.Message));
            }
            result = Compile(slangSource, options, cancellationToken);
        }

        if (result.IsSuccess || !result.Error.Any(static e => e.Code == "SD1903") || _recording.LastFx is null)
            return result;

        // The downstream module for this target is not loaded yet (SD1903 from
        // WasmShaderCompiler's synchronous core). Hand the SAME assembled .fx to its async
        // entry, which loads that one module and maps a load failure to its own code.
        return await _downstream.CompileAsync(_recording.LastFx, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous compile; <see cref="InitializeAsync"/> must have completed. Before that,
    /// returns <c>SD1903</c> rather than aborting the .NET WebAssembly runtime. A trap inside the
    /// slangc module (for example a stack overflow on pathologically deep source) returns
    /// <c>SD1905</c>, and the module is discarded so the next load starts clean.
    /// </summary>
    public Result<CompiledShader, ShaderError[]> Compile(
        string slangSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        string sourceName = options.SourceFileName ?? "<memory>.slang";
        if (!SlangcModule.IsReady)
        {
            return Fail(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD1903",
                Message: "Synchronous Compile() was called before the browser/WASM compiler was initialized: the " +
                         "slangc (Slang -> HLSL) WASM module loads asynchronously and has not been loaded in this " +
                         "session. Await WasmSlangCompiler.InitializeAsync() once before compiling synchronously, " +
                         "or use CompileAsync(), which performs the load itself."));
        }

        _recording.LastFx = null;
        try
        {
            return _compiler.Compile(slangSource, options, cancellationToken);
        }
        catch (SlangcTrapException ex)
        {
            return Fail(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD1905",
                Message: "The in-browser slangc module trapped while compiling this source (" + ex.Message + "). " +
                         "This is a WebAssembly runtime failure, not a slangc diagnostic: native slangc may accept the " +
                         "same source. The module instance was discarded; the next CompileAsync (or InitializeAsync) " +
                         "loads a fresh one."));
        }
    }

    private static Result<CompiledShader, ShaderError[]> Fail(ShaderError error) =>
        Result<CompiledShader, ShaderError[]>.Fail([error]);

    private static (int ExitCode, string Stdout, string Stderr) RunSlangc(
        string slangSource, IReadOnlyList<string> arguments)
    {
        string[] r;
        try
        {
            r = SlangcModule.RunSlangc(slangSource, arguments.ToArray());
        }
        catch (JSException ex)
        {
            // The shim has already dropped the trapped instance; mark the module not ready so
            // nothing calls into it again before a fresh load.
            SlangcModule.Invalidate();
            throw new SlangcTrapException(ex.Message, ex);
        }
        return (int.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture), r[1], r[2]);
    }

    private sealed class SlangcTrapException(string message, Exception inner) : Exception(message, inner);

    /// <summary>
    /// Passes every call to the real downstream, remembering the last assembled <c>.fx</c> so
    /// <see cref="CompileAsync"/> can retry it through the downstream's own async (loading) path.
    /// </summary>
    private sealed class RecordingDownstream(WasmShaderCompiler inner) : IShaderCompiler
    {
        public string? LastFx { get; set; }

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            LastFx = hlslSource;
            return inner.CompileAsync(hlslSource, options, cancellationToken);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            LastFx = hlslSource;
            return inner.Compile(hlslSource, options, cancellationToken);
        }
    }
}
