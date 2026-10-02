#nullable enable

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ShadowDusk.Core;

namespace ShadowDusk.Slang.Wasm;

/// <summary>
/// The <c>shadowdusk-slangc</c> <c>[JSImport]</c> module: registered from this package's own
/// static web assets (<c>_content/ShadowDusk.Slang.Wasm/shadowdusk-slangc.js</c>, resolved
/// relative to <c>_framework/</c> exactly like <c>ShadowDusk.Wasm</c>'s modules), then the
/// ~23 MB slangc WebAssembly is fetched once by <c>ensureReady</c>. The consumer wires nothing.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class SlangcModule
{
    private const string ModuleName = "shadowdusk-slangc";
    private const string ModuleUrl = "../_content/ShadowDusk.Slang.Wasm/shadowdusk-slangc.js";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _registered;
    private static volatile bool _ready;

    public static bool IsReady => _ready;

    public static async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
            return;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
                return;
            if (!_registered)
            {
                try
                {
                    await JSHost.ImportAsync(ModuleName, ModuleUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (JSException ex)
                {
                    throw new JSException(
                        $"The '{ModuleName}' JS module failed to load from its static web asset " +
                        $"('{ModuleUrl}', resolved relative to _framework/): {ex.Message}");
                }
                _registered = true;
            }
            // A failed download is not cached: the next call retries.
            await EnsureReadyJs().ConfigureAwait(false);
            _ready = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static ShaderError LoadFailed(string sourceName, string message) =>
        new(File: sourceName, Line: 0, Column: 0, Code: "SD0628",
            Message: "The in-browser slangc module (shadowdusk-slangc) could not be loaded: " + message);

    [JSImport("ensureReady", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task EnsureReadyJs();

    /// <summary>
    /// One slangc run: <c>[exitCode, stdout, stderr]</c>. Synchronous; valid only after
    /// <see cref="EnsureReadyAsync"/>.
    /// </summary>
    [JSImport("runSlangc", ModuleName)]
    public static partial string[] RunSlangc(string slangSource, string[] arguments);
}
