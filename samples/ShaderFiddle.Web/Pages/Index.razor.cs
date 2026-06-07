using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Core;
using ShadowDusk.Wasm;

namespace ShadowDusk.ShaderFiddle.Web.Pages;

public partial class Index
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private ShaderFiddleGame? _game;
    private readonly WasmShaderCompiler _compiler = new();

    // Phase 33 (issue #7): a `?profile=hidef` query param boots the KNI game in
    // the HiDef profile (WebGL2 / GLSL ES 3.00) instead of the default Reach
    // (WebGL1 / GLSL ES 1.00). Mirrors the `?test=<size>` headless-capture hook.
    // The interactive app, with no query param, stays on Reach (the broad-compat
    // default a consumer expects — Phase 33 Task 7).
    private GraphicsProfile ResolveProfile()
    {
        // Manual query-string parse (no Microsoft.AspNetCore.WebUtilities dep):
        // look for a `profile=hidef` pair in the current URL's query.
        var query = Nav.ToAbsoluteUri(Nav.Uri).Query;        // e.g. "?test=512&profile=hidef"
        if (query.Length > 1)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair[..eq] : pair;
                var val = eq >= 0 ? pair[(eq + 1)..] : "";
                if (string.Equals(key, "profile", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(val, "hidef", StringComparison.OrdinalIgnoreCase))
                {
                    return GraphicsProfile.HiDef;
                }
            }
        }
        return GraphicsProfile.Reach;
    }

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

    // Structured compile diagnostics (file/line/col/message), kept verbatim from
    // the compiler so the editor can squiggle the offending lines and show the
    // reason. Line 0 means "no source location" (e.g. a load/runtime failure).
    private IReadOnlyList<ShaderError> _diagnostics = Array.Empty<ShaderError>();
    // Per-line lookups derived from _diagnostics, used by the editor backdrop
    // (squiggles) and the gutter (hover tooltips).
    private Dictionary<int, List<string>> _lineMessages = new();
    private Dictionary<int, ShaderErrorSeverity> _lineSeverity = new();

    /// <summary>The editor source split into lines (newlines normalized to LF).</summary>
    private string[] SourceLines => _source.Split('\n');

    // Live-editable float params of the currently applied effect (refreshed on
    // every apply). Lets users drive tunables — e.g. a custom global like
    // FishEyeAmount, whose `= 0.35` initializer is not baked into the bytes.
    private IReadOnlyList<ShaderParam> _params = Array.Empty<ShaderParam>();

    protected override async Task OnInitializedAsync()
    {
        // Fetch the fixed inputs the page needs before the game can boot.
        _catBytes = await Http.GetByteArrayAsync("cat.jpg");
        _defaultMgfx = await TryGetBytesAsync($"shaders/OpenGL/{WebShaderInputs.DefaultShader}.mgfx");
        var defaultSrc = await TryGetStringAsync($"shaders/src/{WebShaderInputs.DefaultShader}.fx");
        _source = defaultSrc is null
            ? "// could not load default shader source"
            : NormalizeNewlines(defaultSrc);

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

        // Wire (once) and re-apply the backdrop/gutter scroll sync after every
        // render so the squiggles and line numbers track the textarea's scroll.
        _ = JsRuntime.InvokeVoidAsync("sdEditorSync");
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
            var profile = ResolveProfile();
            _game = new ShaderFiddleGame(_catBytes, _defaultMgfx, profile);
            _game.Run();          // KNI: initializes the device + LoadContent
            _ready = true;
            _status = $"Loaded sample: {WebShaderInputs.DefaultShader} (mode 1, {profile})";
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
        ClearDiagnostics();
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
        ClearDiagnostics();
        _status = null;

        var corpusSrc = await TryGetStringAsync($"shaders/src/{name}.fx");
        if (corpusSrc is not null)
            _source = NormalizeNewlines(corpusSrc);
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
        ClearDiagnostics();
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
                    AddGenericDiagnostic(err);
                    SetError(err);
                }
            }
            else
            {
                // Keep the structured diagnostics so the editor can squiggle the
                // offending lines and the gutter can show the reason on hover.
                SetDiagnostics(result.Error);
                SetError($"{result.Error.Length} compile error(s) — last good render kept.");
            }
        }
        catch (Exception ex)
        {
            // Surface any in-browser compile/backend failure verbatim instead of
            // faking success. With the package self-registering its faithful WASM
            // modules, this path now only fires on a genuine error.
            AddGenericDiagnostic(ex.Message);
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

    /// <summary>Replace the diagnostics and rebuild the per-line squiggle/tooltip maps.</summary>
    private void SetDiagnostics(IReadOnlyList<ShaderError> diags)
    {
        _diagnostics = diags;
        _lineMessages = new();
        _lineSeverity = new();
        foreach (var d in diags)
        {
            if (d.Line <= 0)
                continue;   // no source location -> shown in the list, not squiggled
            if (!_lineMessages.TryGetValue(d.Line, out var list))
                _lineMessages[d.Line] = list = new();
            list.Add(d.Column > 0 ? $"col {d.Column}: {d.Message}" : d.Message);
            // Error (1) outranks Warning (0) for the squiggle colour on a shared line.
            if (!_lineSeverity.TryGetValue(d.Line, out var s) || d.Severity > s)
                _lineSeverity[d.Line] = d.Severity;
        }
    }

    private void ClearDiagnostics()
    {
        _diagnostics = Array.Empty<ShaderError>();
        _lineMessages = new();
        _lineSeverity = new();
    }

    /// <summary>Append a diagnostic with no source location (load/runtime failures).</summary>
    private void AddGenericDiagnostic(string message)
    {
        var list = new List<ShaderError>(_diagnostics)
        {
            new ShaderError("fiddle.fx", 0, 0, "SD", message),
        };
        SetDiagnostics(list);
    }

    /// <summary>Update the source on each keystroke and drop now-stale squiggles.</summary>
    private void OnSourceInput(ChangeEventArgs e)
    {
        _source = e.Value?.ToString() ?? string.Empty;
        if (_diagnostics.Count > 0)
            ClearDiagnostics();
    }

    /// <summary>Scroll the editor to a diagnostic's line and focus it.</summary>
    private async Task GotoLineAsync(int line)
    {
        if (line > 0)
            await JsRuntime.InvokeVoidAsync("sdEditorGotoLine", line);
    }

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n");

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
