#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Wasm;

/// <summary>
/// Browser/WASM <see cref="IShaderCompiler"/>. Composes the real managed compilation
/// pipeline (<see cref="EffectCompiler"/>) with browser-backed native stages:
/// <list type="bullet">
///   <item>HLSL → SPIR-V via <c>JsDxcShaderCompiler</c> (host JS, WASM DXC).</item>
///   <item>SPIR-V → GLSL via <c>JsSpirvToGlslTranspiler</c> (host JS, WASM SPIRV-Cross).</item>
///   <item>SPIR-V reflection via <see cref="SpirvReflector"/> (pure managed — runs in-browser).</item>
///   <item>HLSL → DXBC (DirectX) / D3D9 bytecode (FNA) via
///   <c>WasmVkd3dShaderCompiler</c> (host JS, WASM vkd3d-shader — Phase 4.1).</item>
/// </list>
/// Injecting the reflector makes the OpenGL path reflect SPIR-V directly and skip the
/// native DXIL reflection oracle, so no Windows-only reflection is ever required.
/// Injecting the vkd3d backend routes <see cref="PlatformTarget.DirectX"/> and
/// <see cref="PlatformTarget.Fna"/> through the SAME pinned vkd3d 1.17 the desktop
/// uses, compiled to WASM (never a substitute compiler) — the rest of both pipelines
/// (RdefReader, Fx2EffectWriter, D3d9BytecodePatcher, CTAB reflection, MGFX writer) is
/// pure managed C# that already runs in-browser. The host-appropriate backend is
/// injected automatically — the consumer never sets a flag to get correct output.
/// (This replaced the Phase 39 SD0304 "FNA unavailable on WASM" guard; if the vkd3d
/// WASM module itself cannot load, the compile fails loudly with SD1902 instead.)
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WasmShaderCompiler : IShaderCompiler
{
    /// <inheritdoc/>
    /// <remarks>
    /// A thin asynchronous shell over the same synchronous pipeline core
    /// <see cref="Compile"/> runs (issue #28 — one implementation, byte-identical
    /// output): it first performs the one-time lazy load of the WASM module(s) the
    /// target needs (DXC for OpenGL/Vulkan, vkd3d-shader for DirectX/FNA — deferred to
    /// first compile, exactly as before, so the heavy ~17.4 MB DXC download never
    /// burdens page init), then runs the compile synchronously on the browser thread.
    /// No prior <see cref="InitializeAsync"/> is required on this path.
    /// </remarks>
    public async Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // First attempt. When the target's module is already loaded (or the failure is
        // anything other than "module not loaded yet" — e.g. a parse error, which must
        // surface WITHOUT forcing the heavy module download, the pre-existing lazy
        // behavior) this single synchronous run is the whole compile.
        Result<CompiledShader, ShaderError[]> result = Compile(hlslSource, options, cancellationToken);

        if (result.IsSuccess || !result.Error.Any(static e => e.Code == "SD1903"))
            return result;

        // Cold path: the synchronous core reported "module not loaded" (SD1903). Do the
        // one-time async load for this target, then run the SAME synchronous core again.
        string sourceFileName = options.SourceFileName ?? "<source>";
        try
        {
            switch (options.Target)
            {
                case PlatformTarget.DirectX:
                case PlatformTarget.Fna:
                    await WasmCompilerInitialization.EnsureVkd3dReadyAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await WasmCompilerInitialization.EnsureDxcReadyAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (JSException ex)
        {
            // The module genuinely is not loadable — map with the SAME per-backend
            // failure shape the pre-#28 lazy path produced (SD1900-family for DXC,
            // SD1902 for vkd3d), loudly and unswallowed.
            ShaderError loadError = options.Target is PlatformTarget.DirectX or PlatformTarget.Fna
                ? WasmVkd3dShaderCompiler.MapLoadFailure(ex, sourceFileName)
                : JsDxcShaderCompiler.MapJsException(ex, sourceFileName);
            return Result<CompiledShader, ShaderError[]>.Fail([loadError]);
        }

        return Compile(hlslSource, options, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>Runs entirely on the calling (browser) thread with no task to await or
    /// block on — the compile work is synchronous once the WASM modules are loaded —
    /// so it is safe from a synchronous call site such as MonoGame/KNI's
    /// <c>Content.Load&lt;Effect&gt;</c> (the issue #28 scenario).</para>
    /// <para><b>Precondition:</b> <see cref="InitializeAsync"/> has completed (the WASM
    /// compiler modules load asynchronously; that one-time load cannot happen inside a
    /// synchronous call on the single browser thread). Called before initialization,
    /// this returns a clear <c>SD1903</c> <see cref="ShaderError"/> saying exactly
    /// that — never an opaque runtime abort.</para>
    /// </remarks>
    public Result<CompiledShader, ShaderError[]> Compile(
        string hlslSource,
        CompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        // The same pipeline composition CompileAsync has always used — the synchronous
        // entry differs ONLY in skipping the module warm-up (the backends' readiness
        // gates turn a cold call into the clear SD1903 error).
        var compiler = new EffectCompiler(
            dxcCompilerFactory: () => new JsDxcShaderCompiler(),
            glslTranspilerFactory: () => new JsSpirvToGlslTranspiler(),
            reflectorFactory: () => new SpirvReflector(),
            dxbcCompilerFactory: () => new WasmVkd3dShaderCompiler());

        return compiler.Compile(hlslSource, options, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>Warms EVERYTHING a subsequent synchronous <see cref="Compile"/> of any
    /// supported target needs: registers the three <c>[JSImport]</c> modules (which
    /// eagerly instantiates SPIRV-Cross) and loads + instantiates both the DXC
    /// (~17.4 MB, the OpenGL/Vulkan frontend) and vkd3d-shader (432 KB, the DirectX/FNA
    /// backend) WASM modules. Eager-everything is deliberate (the seamless rule): after
    /// one <c>await compiler.InitializeAsync()</c>, <c>Compile</c> of ANY target just
    /// works — the consumer never has to know which target needs which module. The
    /// vkd3d module is negligible next to DXC, so per-target laziness would save
    /// nothing meaningful.</para>
    /// <para>Idempotent and safe to await repeatedly — every load happens exactly once
    /// per browser session. <see cref="CompileAsync"/> does NOT require this (it warms
    /// the needed module itself, lazily); only the synchronous <see cref="Compile"/>
    /// does. A module that cannot be loaded throws an
    /// <see cref="InvalidOperationException"/> naming the module and the fix.</para>
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await WasmCompilerInitialization.EnsureDxcReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw WasmCompilerInitialization.InitializationFailed("DXC (HLSL → SPIR-V frontend)", ex);
        }

        try
        {
            await WasmCompilerInitialization.EnsureVkd3dReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw WasmCompilerInitialization.InitializationFailed(
                "vkd3d-shader (DirectX DXBC / FNA fx_2_0 backend)", ex);
        }
    }
}
