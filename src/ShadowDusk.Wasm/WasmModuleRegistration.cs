#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace ShadowDusk.Wasm;

/// <summary>
/// Self-registers the three faithful <c>[JSImport]</c> modules ShadowDusk.Wasm ships as
/// Blazor static web assets — <c>shadowdusk-dxc</c> (faithful DXC→WASM),
/// <c>shadowdusk-spirv-cross</c> (SPIRV-Cross→WASM), and <c>shadowdusk-vkd3d</c>
/// (faithful vkd3d-shader→WASM, Phase 4.1) — so a consumer that adds ONLY a
/// <c>PackageReference</c> wires NOTHING. This is the zero-consumer-wiring core of
/// Phase 23 M1.
///
/// <para><b>Where the modules live:</b> the package's <c>wwwroot/</c> is published as
/// Blazor static web assets, served at <c>_content/ShadowDusk.Wasm/…</c>. We must
/// resolve that URL without the consumer telling us the app base.
/// <see cref="JSHost.ImportAsync(string, string, CancellationToken)"/> resolves a
/// <em>relative</em> module URL against the WASM runtime's <c>_framework/</c> folder,
/// which is always a direct child of the app base. So the relative URL
/// <c>../_content/ShadowDusk.Wasm/&lt;file&gt;</c> resolves to
/// <c>&lt;appBase&gt;/_content/ShadowDusk.Wasm/&lt;file&gt;</c> — correct whether the
/// app is hosted at the site root or under a sub-path, with no <c>document.baseURI</c>
/// read or consumer-supplied <c>NavigationManager</c> needed.</para>
///
/// <para><b>Registration is PER COMPILE PATH (Phase 27 — SD1902 attribution):</b> the
/// OpenGL/Vulkan path registers <c>shadowdusk-dxc</c> + <c>shadowdusk-spirv-cross</c>
/// (the only modules whose synchronous <c>[JSImport]</c>s that path makes), and the
/// DirectX/FNA path registers <c>shadowdusk-vkd3d</c> alone. Registering all three from
/// every path meant a SPIRV-Cross load failure surfaced under the vkd3d SD1902 headline
/// (and vice versa) — misleading attribution. Each import failure is also re-wrapped to
/// NAME the failing module, so the underlying error text can never be ambiguous.
/// <see cref="WasmShaderCompiler.InitializeAsync"/> still warms everything (it awaits
/// both groups via the two <c>Ensure*ReadyAsync</c> gates) — Phase 42 semantics are
/// unchanged.</para>
///
/// <para><b>Ordering within the DXC group:</b> the <c>shadowdusk-spirv-cross</c> shim
/// uses a top-level <c>await</c> (it instantiates its WASM eagerly during module
/// evaluation) so that its exported <c>transpileToGlsl</c> can be synchronous.
/// <see cref="JSHost.ImportAsync"/> only resolves after the module has finished
/// evaluating, so awaiting the import here guarantees the SPIRV-Cross WASM is fully
/// initialized before the synchronous <c>[JSImport]</c> call in
/// <see cref="JsSpirvToGlslTranspiler"/>. The DXC path always registers both up front,
/// so neither synchronous <c>[JSImport]</c> can ever hit an unregistered module (which
/// would abort the .NET WASM runtime). The vkd3d path makes no DXC/SPIRV-Cross
/// <c>[JSImport]</c> calls (vkd3d → managed RDEF/CTAB reflection → managed writers), so
/// it needs only its own module.</para>
/// </summary>
[SupportedOSPlatform("browser")]
internal static class WasmModuleRegistration
{
    // Relative-to-_framework/ URLs of the package's static web assets. ".." climbs out
    // of _framework/ to the app base; _content/<PackageId>/ is where the consuming app
    // serves this package's wwwroot.
    private const string DxcModuleUrl = "../_content/ShadowDusk.Wasm/shadowdusk-dxc.js";
    private const string SpirvCrossModuleUrl = "../_content/ShadowDusk.Wasm/shadowdusk-spirv-cross.js";
    private const string Vkd3dModuleUrl = "../_content/ShadowDusk.Wasm/shadowdusk-vkd3d.js";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Task? _dxcChainRegistration;   // shadowdusk-dxc + shadowdusk-spirv-cross
    private static Task? _vkd3dRegistration;      // shadowdusk-vkd3d

    /// <summary>
    /// Idempotently imports (registers) the two <c>[JSImport]</c> modules the
    /// OpenGL/Vulkan compile path needs (<c>shadowdusk-dxc</c> then
    /// <c>shadowdusk-spirv-cross</c>) from the package's own
    /// <c>_content/ShadowDusk.Wasm/</c> base. Safe to call repeatedly and concurrently;
    /// the import happens exactly once. The heavy DXC WASM binary is NOT downloaded
    /// here — the shim lazy-loads it on the first compile via
    /// <c>DxcInterop.EnsureReadyAsync</c>; SPIRV-Cross instantiates eagerly during its
    /// module evaluation (top-level await), by design.
    /// </summary>
    public static Task EnsureDxcChainRegisteredAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: already registered (or in flight).
        var existing = Volatile.Read(ref _dxcChainRegistration);
        if (existing is not null)
            return existing;

        return RegisterOnceAsync(vkd3d: false, cancellationToken);
    }

    /// <summary>
    /// Idempotently imports (registers) the single <c>[JSImport]</c> module the
    /// DirectX/FNA compile path needs (<c>shadowdusk-vkd3d</c>). Evaluating the shim is
    /// cheap and does NOT require <c>vkd3d/vkd3d-shader.{js,wasm}</c> to be present;
    /// that WASM lazy-loads on the first DirectX/FNA compile via
    /// <c>Vkd3dInterop.EnsureReadyAsync</c>. Deliberately does NOT touch the
    /// DXC/SPIRV-Cross modules: this path never calls into them, and pulling them in
    /// here would mis-attribute their load failures to vkd3d (SD1902).
    /// </summary>
    public static Task EnsureVkd3dRegisteredAsync(CancellationToken cancellationToken = default)
    {
        var existing = Volatile.Read(ref _vkd3dRegistration);
        if (existing is not null)
            return existing;

        return RegisterOnceAsync(vkd3d: true, cancellationToken);
    }

    private static async Task RegisterOnceAsync(bool vkd3d, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (vkd3d)
            {
                _vkd3dRegistration ??= ImportModuleAsync("shadowdusk-vkd3d", Vkd3dModuleUrl, cancellationToken);
                try
                {
                    await _vkd3dRegistration.ConfigureAwait(false);
                }
                catch
                {
                    // A failed import must NOT be cached as "done" — reset so a later
                    // call can retry (e.g. transient asset-fetch failure). Re-throw so
                    // the caller sees it.
                    _vkd3dRegistration = null;
                    throw;
                }
            }
            else
            {
                _dxcChainRegistration ??= ImportDxcChainAsync(cancellationToken);
                try
                {
                    await _dxcChainRegistration.ConfigureAwait(false);
                }
                catch
                {
                    _dxcChainRegistration = null;
                    throw;
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ImportDxcChainAsync(CancellationToken cancellationToken)
    {
        // Register DXC first, then SPIRV-Cross. Importing shadowdusk-spirv-cross drives
        // its top-level await (WASM instantiation) to completion, so the subsequent
        // synchronous transpileToGlsl [JSImport] is safe.
        await ImportModuleAsync("shadowdusk-dxc", DxcModuleUrl, cancellationToken).ConfigureAwait(false);
        await ImportModuleAsync("shadowdusk-spirv-cross", SpirvCrossModuleUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports one JS module, re-wrapping a load failure so the message NAMES the
    /// failing module and its asset URL (Phase 27 — SD1902/SD1900 attribution: the
    /// underlying error a consumer sees must never leave WHICH module failed ambiguous).
    /// </summary>
    private static async Task ImportModuleAsync(string moduleName, string moduleUrl, CancellationToken cancellationToken)
    {
        try
        {
            await JSHost.ImportAsync(moduleName, moduleUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (JSException ex)
        {
            throw new JSException(
                $"The '{moduleName}' JS module failed to load from its static web asset " +
                $"('{moduleUrl}', resolved relative to _framework/): {ex.Message}");
        }
    }
}
