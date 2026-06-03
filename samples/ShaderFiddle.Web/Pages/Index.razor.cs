using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Xna.Framework;
using ShadowDusk.Core;
using ShadowDusk.Wasm;

namespace ShadowDusk.ShaderFiddle.Web.Pages;

public partial class Index
{
    private ShaderFiddleGame? _game;
    private readonly WasmShaderCompiler _compiler = new();

    private byte[]? _catBytes;
    private byte[]? _defaultMgfx;

    private string _source = "// loading…";
    private string _selected = WebShaderInputs.DefaultShader;
    private bool _ready;            // assets fetched + game booted
    private bool _compiling;
    private string? _status;
    private bool _statusIsError;
    // The ShadowDusk.Wasm package self-registers its faithful DXC / SPIRV-Cross
    // [JSImport] modules lazily on first compile (no page-level wiring). This flag is
    // kept only so the headless mode-2 entry point can short-circuit before the game
    // boots; it is set true once init completes.
    private bool _jsBackendsRegistered;
    private readonly List<string> _errors = new();

    // Live-editable float params of the currently applied effect (refreshed on
    // every apply). Lets users drive tunables — e.g. a custom global like
    // FishEyeAmount, whose `= 0.35` initializer is not baked into the bytes.
    private IReadOnlyList<ShaderParam> _params = Array.Empty<ShaderParam>();

    protected override async Task OnInitializedAsync()
    {
        // Fetch the fixed inputs the page needs before the game can boot.
        _catBytes = await Http.GetByteArrayAsync("cat.jpg");
        _defaultMgfx = await TryGetBytesAsync($"shaders/OpenGL/{WebShaderInputs.DefaultShader}.mgfx");
        _source = await TryGetStringAsync($"shaders/src/{WebShaderInputs.DefaultShader}.fx")
                  ?? "// could not load default shader source";

        // Phase 23 M1 — ZERO consumer wiring. The ShadowDusk.Wasm PACKAGE now
        // self-registers BOTH [JSImport] modules (shadowdusk-dxc /
        // shadowdusk-spirv-cross) from its own _content/ShadowDusk.Wasm/ static web
        // assets, lazily on the first CompileAsync (see WasmModuleRegistration). This
        // sample no longer hand-wires JSHost.ImportAsync — it just calls the API,
        // exactly like a third-party consumer. The faithful DXC->WASM module is the
        // product frontend served from the package; the Slang shim files remain in
        // this sample's wwwroot/ for reference only and are never registered.
        // (Headless faithful proof: publish-sample-faithful.mjs overlays the served
        // package asset with the faithful binaries before the run.)
        _jsBackendsRegistered = true;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        base.OnAfterRender(firstRender);
        if (firstRender)
        {
            // Start the JS requestAnimationFrame loop, which calls back into TickDotNet.
            JsRuntime.InvokeAsync<object>("initRenderJS", DotNetObjectReference.Create(this));
        }
    }

    /// <summary>Driven once per animation frame by the JS render loop.</summary>
    [JSInvokable]
    public void TickDotNet()
    {
        // Wait until the cat image is fetched before booting the game.
        if (_catBytes is null)
            return;

        if (_game is null)
        {
            _game = new ShaderFiddleGame(_catBytes, _defaultMgfx);
            _game.Run();          // KNI: initializes the device + LoadContent
            _ready = true;
            _status = $"Loaded sample: {WebShaderInputs.DefaultShader} (mode 1)";
            RefreshParams();
            StateHasChanged();
        }

        _game.Tick();
    }

    private async Task OnCorpusChanged(ChangeEventArgs e)
    {
        _selected = e.Value?.ToString() ?? WebShaderInputs.DefaultShader;
        await LoadCorpusAsync(_selected);
    }

    /// <summary>
    /// Phase 24 headless-test entry point. Loads a named corpus shader (mode 1)
    /// deterministically and returns <c>null</c> on success or the human error
    /// string from KNI's <c>new Effect(gd, bytes)</c> path. The Playwright
    /// harness calls this via <c>DotNet.invokeMethodAsync</c>, then waits a few
    /// frames and reads the canvas. Only callable explicitly; the normal UI path
    /// is untouched.
    /// </summary>
    [JSInvokable]
    public async Task<string?> TestLoadCorpus(string name)
    {
        if (!_ready || _game is null)
            return "game not ready";

        var bytes = await TryGetBytesAsync($"shaders/OpenGL/{name}.mgfx");
        if (bytes is null)
            return $"could not fetch shaders/OpenGL/{name}.mgfx";

        var err = _game.ApplyEffect(bytes);
        _selected = name;
        if (err is null)
        {
            _status = $"Loaded sample: {name} (mode 1)";
            _statusIsError = false;
        }
        else
        {
            SetError($"KNI could not load {name}.mgfx: {err}");
        }
        StateHasChanged();
        return err;
    }

    /// <summary>
    /// Phase 24 headless-test entry point for mode 2 (in-browser compile). Uses the
    /// package-self-registered faithful DXC + SPIRV-Cross WASM modules.
    /// </summary>
    [JSInvokable]
    public async Task<string?> TestCompileAndApply(string source)
    {
        if (!_ready || _game is null)
            return "game not ready";
        if (!_jsBackendsRegistered)
            return "backends not registered (init not complete)";

        try
        {
            var options = new CompilerOptions
            {
                Target = PlatformTarget.OpenGL,
                SourceFileName = "fiddle.fx",
            };
            var result = await _compiler.CompileAsync(source, options);
            if (result.IsFailure)
                return string.Join(" | ",
                    System.Linq.Enumerable.Select(result.Error, d => d.FxcFormattedMessage));

            var err = _game.ApplyEffect(result.Value.Data);
            if (err is null)
            {
                _status = "Compiled in-browser and applied (mode 2).";
                _statusIsError = false;
                StateHasChanged();
            }
            return err;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Reset the canvas to the original cat with no shader applied. Drops the
    /// current effect (the plain-cat draw pass renders on its own) without
    /// touching the editor source or the selected sample.
    /// </summary>
    private void ResetEffect()
    {
        _game?.ClearEffect();
        _errors.Clear();
        _params = Array.Empty<ShaderParam>();
        _status = "Reset — showing the original cat (no shader applied).";
        _statusIsError = false;
    }

    /// <summary>Pull the current effect's editable float params for the UI.</summary>
    private void RefreshParams()
        => _params = _game?.GetEditableParameters() ?? Array.Empty<ShaderParam>();

    /// <summary>
    /// Apply a live edit of one component of a float scalar/vector parameter.
    /// Parses the input invariantly, writes it into the effect, and the next
    /// rendered frame reflects it (the game loop redraws every frame).
    /// </summary>
    private void OnParamChanged(ShaderParam p, int index, ChangeEventArgs e)
    {
        if (float.TryParse(e.Value?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f))
        {
            p.Values[index] = f;
            _game?.SetParameter(p.Name, p.Values);
        }
    }

    /// <summary>Mode 1: load a precompiled <c>.mgfx</c> and show its source.</summary>
    private async Task LoadCorpusAsync(string name)
    {
        _errors.Clear();
        _status = null;

        _source = await TryGetStringAsync($"shaders/src/{name}.fx") ?? _source;
        var bytes = await TryGetBytesAsync($"shaders/OpenGL/{name}.mgfx");
        if (bytes is null)
        {
            SetError($"Could not fetch precompiled bytes for {name}.");
            return;
        }

        var err = _game?.ApplyEffect(bytes);
        if (err is null)
        {
            _status = $"Loaded sample: {name} (mode 1)";
            _statusIsError = false;
            RefreshParams();
        }
        else
        {
            // KNI's forked MGFXReader10 may diverge from MonoGame's MGFX v10.
            SetError($"KNI could not load {name}.mgfx: {err}");
        }
    }

    /// <summary>
    /// Mode 2: compile the textarea source entirely in-browser via
    /// <see cref="WasmShaderCompiler"/> and apply the result. The package
    /// self-registers its faithful DXC + SPIRV-Cross WASM modules on first compile,
    /// so any failure here is a real compile/load error surfaced verbatim.
    /// </summary>
    private async Task CompileAndApplyAsync()
    {
        _compiling = true;
        _errors.Clear();
        _status = "Compiling in-browser…";
        _statusIsError = false;
        StateHasChanged();

        // Don't attempt a compile before init has completed (the package
        // self-registers its WASM modules lazily on the first CompileAsync).
        if (!_jsBackendsRegistered)
        {
            SetError("In-browser compile backend not ready yet. Last good render kept.");
            _compiling = false;
            StateHasChanged();
            return;
        }

        try
        {
            var options = new CompilerOptions
            {
                Target = PlatformTarget.OpenGL,
                SourceFileName = "fiddle.fx",
            };

            var result = await _compiler.CompileAsync(_source, options);

            if (result.IsSuccess)
            {
                var err = _game?.ApplyEffect(result.Value.Data);
                if (err is null)
                {
                    _status = "Compiled in-browser and applied (mode 2).";
                    _statusIsError = false;
                    RefreshParams();
                }
                else
                {
                    SetError(err);
                }
            }
            else
            {
                foreach (var d in result.Error)
                    _errors.Add(d.FxcFormattedMessage);
                SetError($"{result.Error.Length} compile error(s) — last good render kept.");
            }
        }
        catch (Exception ex)
        {
            // Surface any in-browser compile/backend failure verbatim instead of
            // faking success. With the package self-registering its faithful WASM
            // modules, this path now only fires on a genuine error.
            _errors.Add(ex.Message);
            SetError("In-browser compile failed. Last good render kept.");
        }
        finally
        {
            _compiling = false;
            StateHasChanged();
        }
    }

    private void SetError(string message)
    {
        _status = message;
        _statusIsError = true;
    }

    private async Task<byte[]?> TryGetBytesAsync(string url)
    {
        try { return await Http.GetByteArrayAsync(url); }
        catch { return null; }
    }

    private async Task<string?> TryGetStringAsync(string url)
    {
        try { return await Http.GetStringAsync(url); }
        catch { return null; }
    }
}
