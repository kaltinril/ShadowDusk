#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ShadowDusk.Core;

namespace ShadowDusk.Wasm;

/// <summary>
/// Tracks which faithful WASM compiler modules have finished their one-time async load,
/// so the SYNCHRONOUS compile path (issue #28: <see cref="WasmShaderCompiler.Compile"/>)
/// can know — without awaiting anything — whether its synchronous <c>[JSImport]</c>
/// calls are safe to make. The flags are the C#-side source of truth: a synchronous
/// <c>[JSImport]</c> into an unregistered module would abort the .NET WASM runtime, and
/// the JS shims' own not-ready errors only exist <em>after</em> registration — so the
/// managed gate here is what turns "opaque runtime abort" into the clear, diagnosable
/// <c>SD1903</c> error.
/// </summary>
/// <remarks>
/// A flag is set only after BOTH the path's module registration
/// (<see cref="WasmModuleRegistration.EnsureDxcChainRegisteredAsync"/> — which also
/// drives SPIRV-Cross's eager WASM instantiation — or
/// <see cref="WasmModuleRegistration.EnsureVkd3dRegisteredAsync"/>) and the module's
/// own lazy WASM load (<c>ensureReady()</c>) have completed. Loading is single-threaded
/// (browser WASM), so plain <c>volatile</c> writes-after-await are sufficient.
/// </remarks>
[SupportedOSPlatform("browser")]
internal static class WasmCompilerInitialization
{
    private static volatile bool _dxcReady;
    private static volatile bool _vkd3dReady;

    /// <summary>Whether the DXC (HLSL → SPIR-V) module is loaded — the OpenGL/Vulkan targets' frontend.</summary>
    internal static bool DxcReady => _dxcReady;

    /// <summary>Whether the vkd3d-shader module is loaded — the DirectX (DXBC) and FNA (fx_2_0) targets' backend.</summary>
    internal static bool Vkd3dReady => _vkd3dReady;

    /// <summary>
    /// One-time load of everything an OpenGL/Vulkan compile needs: registration of the
    /// DXC + SPIRV-Cross modules (which eagerly instantiates SPIRV-Cross) + the
    /// ~17.4 MB DXC WASM. Idempotent; resolves immediately once loaded. A load failure
    /// throws (JSException) — the caller maps it to a loud ShaderError or rethrows with
    /// context. Registers ONLY this path's modules (Phase 27 — never pull vkd3d in
    /// here, so a vkd3d failure can never be mis-headlined as a DXC one).
    /// </summary>
    internal static async Task EnsureDxcReadyAsync(CancellationToken cancellationToken = default)
    {
        await WasmModuleRegistration.EnsureDxcChainRegisteredAsync(cancellationToken).ConfigureAwait(false);
        await DxcInterop.EnsureReadyAsync().ConfigureAwait(false);
        _dxcReady = true;
    }

    /// <summary>
    /// One-time load of everything a DirectX/FNA compile needs: registration of the
    /// vkd3d module alone + the vkd3d-shader WASM (432 KB). Idempotent; resolves
    /// immediately once loaded. A load failure throws (JSException) — the caller maps
    /// it to SD1902 or rethrows with context. Registers ONLY the vkd3d module
    /// (Phase 27 — a DXC/SPIRV-Cross load failure must never surface under the vkd3d
    /// SD1902 headline; this path makes no [JSImport] into those modules).
    /// </summary>
    internal static async Task EnsureVkd3dReadyAsync(CancellationToken cancellationToken = default)
    {
        await WasmModuleRegistration.EnsureVkd3dRegisteredAsync(cancellationToken).ConfigureAwait(false);
        await Vkd3dInterop.EnsureReadyAsync().ConfigureAwait(false);
        _vkd3dReady = true;
    }

    /// <summary>
    /// The clear, diagnosable error a synchronous compile returns when its WASM module
    /// has not been loaded yet (the issue #28 acceptance criterion: never an opaque
    /// runtime abort). Code <c>SD1903</c>; the message tells the consumer exactly what
    /// to do — await <c>InitializeAsync()</c> once, or use <c>CompileAsync</c>.
    /// </summary>
    /// <param name="moduleDescription">Human-readable name of the missing module.</param>
    /// <param name="sourceFileName">The compile's source name, for the diagnostic.</param>
    internal static ShaderError NotInitializedError(string moduleDescription, string? sourceFileName) =>
        new(
            File:    sourceFileName ?? "<source>",
            Line:    0,
            Column:  0,
            Code:    "SD1903",
            Message: "Synchronous Compile() was called before the browser/WASM compiler was " +
                     $"initialized: the {moduleDescription} WASM module loads asynchronously and " +
                     "has not been loaded in this session. Await IShaderCompiler.InitializeAsync() " +
                     "once from an async context (e.g. during Blazor startup) before compiling " +
                     "synchronously — or use CompileAsync(), which performs the load itself. " +
                     "Never block on the task (.Result/.Wait()): on single-threaded browser WASM " +
                     "that deadlocks.");

    /// <summary>
    /// Wraps a module-load failure thrown out of <see cref="WasmShaderCompiler.InitializeAsync"/>
    /// with enough context to diagnose which module failed and how to fix it.
    /// </summary>
    internal static InvalidOperationException InitializationFailed(string moduleDescription, JSException inner) =>
        new(
            $"ShadowDusk WASM initialization failed while loading the {moduleDescription} WASM " +
            "module. The module ships inside the ShadowDusk.Wasm package as a Blazor static web " +
            "asset (served under _content/ShadowDusk.Wasm/); in a source checkout it must first " +
            "be restored via tools/restore.ps1 / tools/restore.sh. Underlying error: " + inner.Message,
            inner);
}
