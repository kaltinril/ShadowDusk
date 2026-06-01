using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Xna.Framework;
using ShadowDusk.Core;
using ShadowDusk.Wasm;

namespace ShadowDusk.ShaderFiddle.Web.Pages;

public partial class Index
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

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
    private bool _jsBackendsRegistered;   // shadowdusk-dxc / -spirv-cross [JSImport] modules registered
    private string? _jsRegisterError;     // why registration failed, if it did
    private readonly List<string> _errors = new();

    protected override async Task OnInitializedAsync()
    {
        // Fetch the fixed inputs the page needs before the game can boot.
        _catBytes = await Http.GetByteArrayAsync("cat.jpg");
        _defaultMgfx = await TryGetBytesAsync($"shaders/OpenGL/{WebShaderInputs.DefaultShader}.mgfx");
        _source = await TryGetStringAsync($"shaders/src/{WebShaderInputs.DefaultShader}.fx")
                  ?? "// could not load default shader source";

        // Register the JS modules that satisfy ShadowDusk.Wasm's [JSImport]
        // contracts (shadowdusk-dxc / shadowdusk-spirv-cross). This MUST happen
        // before any CompileAsync: invoking a [JSImport] whose module is not
        // registered aborts the .NET WASM runtime (crashing the page). With the
        // modules registered, the (stubbed) JS functions throw a catchable error
        // that surfaces as a clean diagnostic instead. They stay stubs until
        // Phase 100 delivers the emscripten DXC/SPIRV-Cross WASM builds.
        try
        {
            // JSHost.ImportAsync resolves a relative URL against _framework/, so
            // use an app-root-absolute URL (BaseUri) to reach the wwwroot files.
            await JSHost.ImportAsync("shadowdusk-dxc", $"{Nav.BaseUri}shadowdusk-dxc.js");
            await JSHost.ImportAsync("shadowdusk-spirv-cross", $"{Nav.BaseUri}shadowdusk-spirv-cross.js");
            _jsBackendsRegistered = true;
        }
        catch (Exception ex)
        {
            _jsBackendsRegistered = false;
            _jsRegisterError = ex.Message;
        }
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
            StateHasChanged();
        }

        _game.Tick();
    }

    private async Task OnCorpusChanged(ChangeEventArgs e)
    {
        _selected = e.Value?.ToString() ?? WebShaderInputs.DefaultShader;
        await LoadCorpusAsync(_selected);
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
        }
        else
        {
            // KNI's forked MGFXReader10 may diverge from MonoGame's MGFX v10.
            SetError($"KNI could not load {name}.mgfx: {err}");
        }
    }

    /// <summary>
    /// Mode 2: compile the textarea source entirely in-browser via
    /// <see cref="WasmShaderCompiler"/> and apply the result. Until Phase 100
    /// wires the WASM DXC/SPIRV-Cross modules, this surfaces the real stub error.
    /// </summary>
    private async Task CompileAndApplyAsync()
    {
        _compiling = true;
        _errors.Clear();
        _status = "Compiling in-browser…";
        _statusIsError = false;
        StateHasChanged();

        // Never invoke the [JSImport] path if module registration failed —
        // calling an unregistered module aborts the WASM runtime.
        if (!_jsBackendsRegistered)
        {
            _errors.Add($"shadowdusk-dxc / shadowdusk-spirv-cross JS modules failed to register: {_jsRegisterError}");
            SetError("In-browser compile backend unavailable. Last good render kept.");
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
            // The expected path today: JsDxcShaderCompiler's [JSImport] throws
            // because the WASM DXC module isn't wired (Phase 100). Surface it
            // verbatim instead of faking success.
            _errors.Add(ex.Message);
            SetError("In-browser compile unavailable (Phase 19 mode 2 → Phase 100). Last good render kept.");
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
