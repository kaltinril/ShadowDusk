using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
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

    // ---- Export station (owner-directed, 2026-06-09) -------------------------
    // The browser is an export station: compile the editor source for ANY
    // supported target and download the artifact, byte-identical to desktop
    // output. OpenGL stays the host-appropriate default (it renders live in KNI
    // WebGL); DirectX (DX11 SM5 DXBC .mgfx) and FNA (fx_2_0 .fxb) are export-only
    // — a browser cannot execute DXBC/D3D9 bytecode, so the UI says so honestly.

    /// <summary>One row of the export panel: a compile target plus its artifact shape.</summary>
    private sealed record ExportTarget(
        PlatformTarget Target, string Title, string Extension, string Description);

    private static readonly ExportTarget[] ExportTargets =
    {
        new(PlatformTarget.OpenGL, "OpenGL", ".mgfx",
            "MonoGame DesktopGL / KNI — the same target the canvas renders live."),
        new(PlatformTarget.DirectX, "DirectX (DX11 SM5)", ".mgfx",
            "Export-only: compiles + downloads here, renders in your MonoGame WindowsDX game " +
            "(a browser cannot render DXBC)."),
        new(PlatformTarget.Fna, "FNA (fx_2_0)", ".fxb",
            "Export-only: compiles + downloads here, renders in your FNA game " +
            "(a browser cannot render D3D9 bytecode)."),
    };

    /// <summary>Base name for downloaded artifacts (no extension). Follows the
    /// selected sample / uploaded file; user-editable.</summary>
    private string _exportName = WebShaderInputs.DefaultShader;

    /// <summary>The target whose export compile is in flight, or null when idle.</summary>
    private PlatformTarget? _exporting;

    /// <summary>Per-target outcome line shown under each export row.</summary>
    private readonly Dictionary<PlatformTarget, (string Message, bool IsError)> _exportStatus = new();

    private string FileNameFor(ExportTarget t) => SafeExportName() + t.Extension;

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
    /// Phase 4.1 G2 headless-test entry point (export-target byte-identity gate).
    /// Compiles <paramref name="source"/> through the REAL product path
    /// (<see cref="WasmShaderCompiler.CompileAsync"/>) for an EXPORT target the browser
    /// cannot render — <see cref="PlatformTarget.DirectX"/> (SM4/5 DXBC) or
    /// <see cref="PlatformTarget.Fna"/> (fx_2_0 .fxb) — and returns the compiled
    /// artifact bytes for the Playwright harness
    /// (<c>tests/ShadowDusk.BrowserTests/browser-vkd3d-gate.mjs</c>) to SHA-256 against
    /// the committed cross-host byte-identity manifest. Rendering is deliberately out of
    /// scope: a browser has no Direct3D, so the honest browser-side bar for these
    /// targets is byte-identity to the desktop-render-proven bytes (see
    /// <c>plan/PHASE-4.1-SPIKE-wasm-directx-dxbc.md</c>, the G2 rung).
    /// Protocol: <c>"OK:&lt;base64 artifact&gt;"</c> on success, <c>"ERR:&lt;verbatim
    /// diagnostics&gt;"</c> on failure. Test-only and UI-invisible (the
    /// <see cref="TestLoadCorpus"/> pattern): only callable explicitly via JS interop;
    /// no UI element reaches it, and it does not touch the game, canvas, editor, or
    /// status line. Unlike the render hooks it does not require the game to have
    /// booted — it is a pure compile.
    /// </summary>
    [JSInvokable]
    public async Task<string> TestCompileExport(string source, string targetName, string sourceFileName)
    {
        try
        {
            if (!Enum.TryParse<PlatformTarget>(targetName, ignoreCase: false, out var target) ||
                (target != PlatformTarget.DirectX && target != PlatformTarget.Fna))
            {
                return $"ERR:unsupported export target '{targetName}' (expected 'DirectX' or 'Fna')";
            }

            var result = await _compiler.CompileAsync(source, new CompilerOptions
            {
                Target = target,
                // The fixture-relative name the byte-identity manifest was generated
                // with (diagnostics only — proven not to leak into output bytes by
                // CrossHostByteIdentityTests.SourceFileName_DoesNotAffect_OutputBytes*).
                SourceFileName = sourceFileName,
            });

            if (result.IsFailure)
            {
                return "ERR:" + string.Join(" | ",
                    System.Linq.Enumerable.Select(result.Error, d => d.FxcFormattedMessage));
            }

            return "OK:" + Convert.ToBase64String(result.Value.Data);
        }
        catch (Exception ex)
        {
            // Surface the failure verbatim — the harness records it; never fake a pass.
            return "ERR:" + ex.Message;
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
        _exportName = name;   // future exports default to the sample's name

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

    /// <summary>
    /// Export station: compile the editor source for <paramref name="target"/> entirely
    /// in-browser via the SAME <see cref="WasmShaderCompiler"/> the live path uses (never a
    /// substitute pipeline), then hand the artifact to a JS blob download. The live render
    /// is left untouched — exporting is a side-channel, "Compile &amp; Apply" stays the
    /// only thing that changes the canvas.
    /// </summary>
    private async Task ExportAsync(ExportTarget target)
    {
        if (!_ready || _compiling || _exporting is not null)
            return;

        _exporting = target.Target;
        ClearDiagnostics();
        _exportStatus[target.Target] = ("Compiling in-browser…", false);
        StateHasChanged();

        var fileName = FileNameFor(target);
        try
        {
            var options = new CompilerOptions
            {
                Target = target.Target,
                SourceFileName = SafeExportName() + ".fx",
            };

            var result = await _compiler.CompileAsync(_source, options);

            if (result.IsSuccess)
            {
                await JsRuntime.InvokeVoidAsync("sdDownloadBytes", fileName, result.Value.Data);
                _exportStatus[target.Target] =
                    ($"Downloaded {fileName} ({result.Value.Data.Length:N0} bytes).", false);
            }
            else
            {
                // Same verbatim file:line:col fidelity as the live path: the structured
                // diagnostics drive the shared error panel + editor squiggles.
                SetDiagnostics(result.Error);
                // SD1902 = the vkd3d WASM module is genuinely absent (not restored /
                // not hosted). Surface that clearly instead of a generic failure; the
                // verbatim diagnostic with the restore pointer is in the panel below.
                _exportStatus[target.Target] = result.Error.Any(e => e.Code == "SD1902")
                    ? ("The vkd3d-shader WASM module could not be loaded (SD1902), so DirectX/FNA " +
                       "export is unavailable in this session — details in the diagnostics below.", true)
                    : ($"{result.Error.Length} compile error(s) — see diagnostics below.", true);
            }
        }
        catch (Exception ex)
        {
            // Surface any in-browser backend failure verbatim instead of faking success.
            AddGenericDiagnostic(ex.Message);
            _exportStatus[target.Target] = ($"{target.Title} export failed — see diagnostics below.", true);
        }
        finally
        {
            _exporting = null;
            StateHasChanged();
        }
    }

    /// <summary>Keep the user-edited artifact name; the safe form is applied on use.</summary>
    private void OnExportNameChanged(ChangeEventArgs e)
        => _exportName = e.Value?.ToString() ?? string.Empty;

    /// <summary>
    /// The artifact base name with anything filename-hostile stripped; falls back to
    /// "fiddle" when nothing usable remains.
    /// </summary>
    private string SafeExportName()
    {
        var cleaned = new string(_exportName.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')
            .ToArray()).Trim();
        return cleaned.Length == 0 ? "fiddle" : cleaned;
    }

    /// <summary>
    /// Upload a local <c>.fx</c> into the editor (the export-station entry point for
    /// users with a file rather than pasted source). Loads the text, names future
    /// downloads after the file, and leaves compiling/exporting to the buttons.
    /// </summary>
    private async Task OnFxFileSelectedAsync(InputFileChangeEventArgs e)
    {
        const long maxBytes = 2 * 1024 * 1024;   // .fx sources are tiny; 2 MB is generous.
        try
        {
            var file = e.File;
            await using var stream = file.OpenReadStream(maxAllowedSize: maxBytes);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();

            _source = NormalizeNewlines(text);
            ClearDiagnostics();
            _exportStatus.Clear();
            var baseName = Path.GetFileNameWithoutExtension(file.Name);
            if (!string.IsNullOrWhiteSpace(baseName))
                _exportName = baseName;
            _status = $"Loaded {file.Name} into the editor — Compile & Apply to render, or export below.";
            _statusIsError = false;
        }
        catch (Exception ex)
        {
            SetError($"Could not read the selected file: {ex.Message}");
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
