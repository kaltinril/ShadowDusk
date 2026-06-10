#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Fna;

/// <summary>One shader to validate: both arms' fx_2_0 bytes (or their compile errors).</summary>
public sealed record ShaderCase(
    string Name, bool Gate,
    byte[]? ReferenceBytes, string? ReferenceCompileError,
    byte[]? CandidateBytes, string? CandidateCompileError,
    string? ParityNote);

/// <summary>What happened to one arm of one shader inside real FNA.</summary>
public sealed record ArmOutcome(
    bool Loaded, bool Rendered, string? Error, Color[]? Pixels, string? PngPath);

/// <summary>Both arms' outcomes for one shader.</summary>
public sealed record CaseOutcome(string Name, bool Gate, ArmOutcome Reference, ArmOutcome Candidate);

/// <summary>
/// The FNA analogue of <c>validation/SharedDx/DxEffectImageRenderer.cs</c> — ONE process,
/// ONE GraphicsDevice, so the reference-vs-candidate comparison is same-backend by
/// construction (FNA3D_FORCE_DRIVER=D3D11 pins the backend for determinism).
///
/// For each case it loads each arm's fx_2_0 bytes into a REAL FNA
/// <see cref="Effect"/> (rung 3: FNA3D hands the bytes to MojoShader — a parse/translate
/// failure surfaces here), renders the cat through the effect via the normal
/// <see cref="SpriteBatch"/> path (rung 4), reads the pixels back, and saves a PNG.
/// The scene mirrors DxEffectImageRenderer exactly: prime SpriteBatch's own sprite
/// vertex shader, then Immediate-mode draw with the effect (PS-only passes ride
/// SpriteBatch's VS — ShadowDusk omits the VertexShader state for absent stages, so
/// MojoShader keeps SpriteBatch's VS bound; that is by design and matches fxc output).
///
/// MojoShader parse errors do NOT throw in FNA3D — they are logged via FNA3D_LogError
/// and the broken effect then throws in FNA's managed Effect ctor — so the harness
/// hooks <see cref="FNALoggerEXT.LogError"/> (assigned in Program.cs BEFORE the Game is
/// constructed, which stops FNA's default Console hook from claiming it) and attaches
/// the exact MojoShader error text to the outcome.
/// </summary>
public sealed class FnaEffectImageRenderer : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _refOutDir;
    private readonly string _candOutDir;
    private readonly IReadOnlyList<ShaderCase> _cases;
    private readonly Action<Effect, Texture2D> _setParams;
    private readonly List<string> _fna3dErrors;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<CaseOutcome> Outcomes { get; } = new();

    public FnaEffectImageRenderer(
        string catPath, string refOutDir, string candOutDir,
        IReadOnlyList<ShaderCase> cases, Action<Effect, Texture2D> setParams,
        List<string> fna3dErrorSink)
    {
        _catPath = catPath;
        _refOutDir = refOutDir;
        _candOutDir = candOutDir;
        _cases = cases;
        _setParams = setParams;
        _fna3dErrors = fna3dErrorSink;

        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        Window.Title = "ShadowDusk FNA validation (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_refOutDir);
        Directory.CreateDirectory(_candOutDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done)
        {
            Exit();
            return;
        }

        GraphicsDevice.Clear(Color.Black);
        foreach (ShaderCase c in _cases)
        {
            ArmOutcome reference = RunArm(c.Name, c.ReferenceBytes, c.ReferenceCompileError, _refOutDir);
            ArmOutcome candidate = RunArm(c.Name, c.CandidateBytes, c.CandidateCompileError, _candOutDir);
            Outcomes.Add(new CaseOutcome(c.Name, c.Gate, reference, candidate));
        }

        _done = true;
        Exit();
    }

    private ArmOutcome RunArm(string name, byte[]? bytes, string? compileError, string outDir)
    {
        if (bytes is null)
            return new ArmOutcome(false, false, $"compile failed: {compileError}", null, null);

        _fna3dErrors.Clear();
        Effect effect;
        try
        {
            // THE rung-3 load test: FNA3D hands the bytes to MojoShader. A MojoShader
            // parse/translate failure logs via FNA3D_LogError and then throws here.
            effect = new Effect(GraphicsDevice, bytes);
        }
        catch (Exception ex)
        {
            string mojo = _fna3dErrors.Count > 0 ? $" | FNA3D: {string.Join(" | ", _fna3dErrors)}" : "";
            return new ArmOutcome(false, false,
                $"new Effect() threw: {ex.GetType().Name}: {ex.Message}{mojo}", null, null);
        }

        if (_fna3dErrors.Count > 0)
        {
            // MojoShader reported errors but the managed ctor survived — the effect is
            // not trustworthy; treat as a load failure with the exact MojoShader text.
            string mojo = string.Join(" | ", _fna3dErrors);
            effect.Dispose();
            return new ArmOutcome(false, false, $"MojoShader errors on load: {mojo}", null, null);
        }

        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            var dest = new Rectangle(0, 0, w, h);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            // Prime SpriteBatch's sprite vertex shader (pixel-only effects need a VS).
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp,
                DepthStencilState.None, RasterizerState.CullNone);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            // A parameter-set failure must be VISIBLE, never swallowed: skipping the
            // remaining SetValue calls silently renders the arm with default (zero)
            // values, which fabricates a divergence (or hides a real one).
            string? paramError = null;
            try { _setParams(effect, _cat); }
            catch (Exception ex) { paramError = $"SetParams threw: {ex.GetType().Name}: {ex.Message}"; }

            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp,
                DepthStencilState.None, RasterizerState.CullNone, effect);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[w * h];
            rt.GetData(pixels);

            string png = Path.Combine(outDir, name + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            string? renderErrors = _fna3dErrors.Count > 0
                ? $"FNA3D errors during render: {string.Join(" | ", _fna3dErrors)}"
                : paramError;
            return new ArmOutcome(true, renderErrors is null, renderErrors, pixels, png);
        }
        catch (Exception ex)
        {
            try { _sb.End(); } catch { /* may not be in a batch */ }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            string mojo = _fna3dErrors.Count > 0 ? $" | FNA3D: {string.Join(" | ", _fna3dErrors)}" : "";
            string frames = string.Join(" <- ",
                (ex.StackTrace ?? "").Split('\n').Take(4).Select(f => f.Trim().Replace("at ", "")));
            return new ArmOutcome(true, false,
                $"render threw: {ex.GetType().Name}: {ex.Message}{mojo} @ {frames}", null, null);
        }
        finally
        {
            effect.Dispose();
        }
    }
}
