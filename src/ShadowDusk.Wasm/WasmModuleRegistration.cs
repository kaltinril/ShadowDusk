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
/// <para><b>Ordering:</b> the <c>shadowdusk-spirv-cross</c> shim uses a top-level
/// <c>await</c> (it instantiates its WASM eagerly during module evaluation) so that its
/// exported <c>transpileToGlsl</c> can be synchronous. <see cref="JSHost.ImportAsync"/>
/// only resolves after the module has finished evaluating, so awaiting the import here
/// guarantees the SPIRV-Cross WASM is fully initialized before the synchronous
/// <c>[JSImport]</c> call in <see cref="JsSpirvToGlslTranspiler"/>. We register BOTH
/// modules up front in the async DXC path (which always runs before SPIRV-Cross in the
/// pipeline) so neither synchronous <c>[JSImport]</c> can ever hit an unregistered
/// module (which would abort the .NET WASM runtime).</para>
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
    private static Task? _registration;

    /// <summary>
    /// Idempotently imports (registers) all three <c>[JSImport]</c> modules from the
    /// package's own <c>_content/ShadowDusk.Wasm/</c> base. Safe to call repeatedly and
    /// concurrently; the import happens exactly once. Awaiting it before the first
    /// <c>[JSImport]</c> call in any backend is what makes the consumer's experience
    /// zero-wiring. The heavy WASM binaries are NOT downloaded here — registering the
    /// <c>shadowdusk-dxc</c> / <c>shadowdusk-vkd3d</c> shims only evaluates their tiny
    /// JS; each shim lazy-loads its WASM on the first compile via its
    /// <c>EnsureReadyAsync</c> (<c>DxcInterop</c> / <c>Vkd3dInterop</c>).
    /// </summary>
    public static Task EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: already registered (or in flight).
        var existing = Volatile.Read(ref _registration);
        if (existing is not null)
            return existing;

        return RegisterOnceAsync(cancellationToken);
    }

    private static async Task RegisterOnceAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _registration ??= ImportBothAsync(cancellationToken);
            await _registration.ConfigureAwait(false);
        }
        catch
        {
            // A failed import must NOT be cached as "done" — reset so a later call can
            // retry (e.g. transient asset-fetch failure). Re-throw so the caller sees it.
            _registration = null;
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ImportBothAsync(CancellationToken cancellationToken)
    {
        // Register DXC first, then SPIRV-Cross. Importing shadowdusk-spirv-cross drives
        // its top-level await (WASM instantiation) to completion, so the subsequent
        // synchronous transpileToGlsl [JSImport] is safe. The vkd3d shim (Phase 4.1) is
        // lazy like the DXC one (no top-level await — evaluating it is cheap and does
        // NOT require vkd3d-shader.{js,wasm} to be present); its WASM loads on the
        // first DirectX/FNA compile via Vkd3dInterop.EnsureReadyAsync.
        await JSHost.ImportAsync("shadowdusk-dxc", DxcModuleUrl, cancellationToken).ConfigureAwait(false);
        await JSHost.ImportAsync("shadowdusk-spirv-cross", SpirvCrossModuleUrl, cancellationToken).ConfigureAwait(false);
        await JSHost.ImportAsync("shadowdusk-vkd3d", Vkd3dModuleUrl, cancellationToken).ConfigureAwait(false);
    }
}
