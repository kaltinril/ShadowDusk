#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Fna;

/// <summary>How a corpus shader is rendered at rung 4.</summary>
public enum FnaScene
{
    /// <summary>PS-only effect: rides SpriteBatch's own vertex shader (the Phase 17/18 scene).</summary>
    Sprite,

    /// <summary>
    /// VS-driven effect: the effect ships its OWN vertex shader, which SpriteBatch can
    /// never exercise — drawn as a custom-geometry clip-space quad instead (the Phase-28
    /// analog, mirroring <c>validation/SharedDx/VsDxEffectImageRenderer.cs</c>).
    /// </summary>
    VsQuad,
}

/// <summary>One shader to validate: both arms' fx_2_0 bytes (or their compile errors).</summary>
public sealed record ShaderCase(
    string Name, bool Gate,
    byte[]? ReferenceBytes, string? ReferenceCompileError,
    byte[]? CandidateBytes, string? CandidateCompileError,
    string? ParityNote,
    FnaScene Scene = FnaScene.Sprite,
    string? Technique = null);

/// <summary>What happened to one arm of one shader inside real FNA.
/// <see cref="ParamsSet"/> = the parameter names SetParams actually hit on this arm —
/// a name hit on one arm but absent on the other is a fidelity signal the harness
/// reports (it can be the documented optimized-out-globals case, or a lost binding).</summary>
public sealed record ArmOutcome(
    bool Loaded, bool Rendered, string? Error, Color[]? Pixels, string? PngPath,
    IReadOnlyList<string>? ParamsSet = null);

/// <summary>Both arms' outcomes for one shader.</summary>
public sealed record CaseOutcome(string Name, bool Gate, ArmOutcome Reference, ArmOutcome Candidate);

/// <summary>
/// The FNA analogue of <c>validation/SharedDx/DxEffectImageRenderer.cs</c> — ONE process,
/// ONE GraphicsDevice, so the reference-vs-candidate comparison is same-backend by
/// construction (FNA3D_FORCE_DRIVER=D3D11 pins the backend for determinism).
///
/// For each case it loads each arm's fx_2_0 bytes into a REAL FNA
/// <see cref="Effect"/> (rung 3: FNA3D hands the bytes to MojoShader — a parse/translate
/// failure surfaces here), renders through the effect (rung 4), reads the pixels back,
/// and saves a PNG. Two scenes, selected per case by <see cref="FnaScene"/>:
/// <list type="bullet">
/// <item><see cref="FnaScene.Sprite"/> mirrors DxEffectImageRenderer exactly: prime
/// SpriteBatch's own sprite vertex shader, then Immediate-mode draw the cat with the
/// effect (PS-only passes ride SpriteBatch's VS — ShadowDusk omits the VertexShader
/// state for absent stages, so MojoShader keeps SpriteBatch's VS bound; that is by
/// design and matches fxc output).</item>
/// <item><see cref="FnaScene.VsQuad"/> mirrors VsDxEffectImageRenderer (Phase 28): a
/// custom-geometry clip-space quad via <c>DrawUserIndexedPrimitives</c>, so the
/// effect's OWN vertex shader runs.</item>
/// </list>
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
    private readonly Func<Effect, Texture2D, Texture2D, IReadOnlyList<string>> _setParams;
    private readonly List<string> _fna3dErrors;

    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private Texture2D _mask = null!;
    private bool _done;

    // Vertex with POSITION0 / COLOR0 / TEXCOORD0 — the SpriteBatch-compatible set, a
    // SUPERSET of every VS-driven corpus shader's input semantics (FNA enforces strict
    // VS-input ⊆ vertex-declaration matching; extra declared elements are fine).
    // Mirrors validation/SharedDx/VsDxEffectImageRenderer.VsVertex byte for byte.
    private readonly struct VsVertex : IVertexType
    {
        public readonly Vector3 Position;
        public readonly Color Color;
        public readonly Vector2 TexCoord;

        public VsVertex(Vector3 position, Color color, Vector2 texCoord)
        {
            Position = position; Color = color; TexCoord = texCoord;
        }

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Color, VertexElementUsage.Color, 0),
            new VertexElement(16, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    public List<CaseOutcome> Outcomes { get; } = new();

    public FnaEffectImageRenderer(
        string catPath, string refOutDir, string candOutDir,
        IReadOnlyList<ShaderCase> cases,
        Func<Effect, Texture2D, Texture2D, IReadOnlyList<string>> setParams,
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
        _mask = FnaShaderInputs.CreateMaskTexture(GraphicsDevice);
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
            ArmOutcome reference = RunArm(c.Name, c.Scene, c.Technique, c.ReferenceBytes, c.ReferenceCompileError, _refOutDir);
            ArmOutcome candidate = RunArm(c.Name, c.Scene, c.Technique, c.CandidateBytes, c.CandidateCompileError, _candOutDir);
            Outcomes.Add(new CaseOutcome(c.Name, c.Gate, reference, candidate));
        }

        _done = true;
        Exit();
    }

    private ArmOutcome RunArm(string name, FnaScene scene, string? technique, byte[]? bytes, string? compileError, string outDir)
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

        // Technique-selector rows exercise FNA's technique-by-name lookup (otherwise
        // only CurrentTechnique — the first technique — is ever rendered).
        if (technique is not null)
        {
            EffectTechnique? selected = effect.Techniques[technique];
            if (selected is null)
            {
                effect.Dispose();
                return new ArmOutcome(false, false,
                    $"technique '{technique}' not found by name in the loaded effect", null, null);
            }
            effect.CurrentTechnique = selected;
        }

        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            // A parameter-set failure must be VISIBLE, never swallowed: skipping the
            // remaining SetValue calls silently renders the arm with default (zero)
            // values, which fabricates a divergence (or hides a real one).
            string? paramError = null;
            IReadOnlyList<string>? paramsSet = null;

            if (scene == FnaScene.Sprite)
            {
                // Clear stale texture bindings from the previous arm/row (slot 0 is
                // rebound by SpriteBatch.Draw below; slot 1 — second textures — would
                // otherwise leak across arms and could mask a lost candidate binding).
                GraphicsDevice.Textures[0] = null;
                GraphicsDevice.Textures[1] = null;

                var dest = new Rectangle(0, 0, w, h);

                // Prime SpriteBatch's sprite vertex shader (pixel-only effects need a VS).
                _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp,
                    DepthStencilState.None, RasterizerState.CullNone);
                _sb.Draw(_cat, dest, Color.White);
                _sb.End();

                try { paramsSet = _setParams(effect, _cat, _mask); }
                catch (Exception ex) { paramError = $"SetParams threw: {ex.GetType().Name}: {ex.Message}"; }

                _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp,
                    DepthStencilState.None, RasterizerState.CullNone, effect);
                _sb.Draw(_cat, dest, Color.White);
                _sb.End();
            }
            else
            {
                // VS-driven effect: SpriteBatch supplies its own VS, so it can never
                // exercise an effect that ships one — draw a full-screen clip-space quad
                // through a custom vertex declaration instead, so the effect's OWN
                // vertex shader runs (the Phase-28 analog). Device state is set
                // explicitly (no SpriteBatch.Begin to do it); in-pass render states the
                // effect carries are applied ON TOP by pass.Apply(), identically for
                // both arms. No priming draw: a VS-driven effect needs no donor VS, and
                // the transparent clear keeps discarded/uncovered regions distinct from
                // any survivor color (the Appendix-G non-vacuousness lesson).
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.DepthStencilState = DepthStencilState.None;
                GraphicsDevice.RasterizerState = RasterizerState.CullNone;
                GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
                GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp;
                // Clear texture slots too: in this scene the ONLY path that binds a
                // texture is the effect's own sampler→texture map (no SpriteBatch.Draw
                // to rebind it), and the reference arm runs first on the same device —
                // a candidate .fxb whose map regressed away would otherwise silently
                // inherit the oracle's stale binding and render pixel-identical,
                // masking exactly the defect class this harness exists to catch.
                GraphicsDevice.Textures[0] = null;
                GraphicsDevice.Textures[1] = null;

                try { paramsSet = _setParams(effect, _cat, _mask); }
                catch (Exception ex) { paramError = $"SetParams threw: {ex.GetType().Name}: {ex.Message}"; }

                // TexCoord (0,0) top-left .. (1,1) bottom-right; indices wound so the
                // two triangles cover the quad under CullNone. Identical vertices to
                // validation/Shared{,Dx}/Vs*EffectImageRenderer.
                var verts = new[]
                {
                    new VsVertex(new Vector3(-1f,  1f, 0f), Color.White, new Vector2(0f, 0f)), // TL
                    new VsVertex(new Vector3( 1f,  1f, 0f), Color.White, new Vector2(1f, 0f)), // TR
                    new VsVertex(new Vector3(-1f, -1f, 0f), Color.White, new Vector2(0f, 1f)), // BL
                    new VsVertex(new Vector3( 1f, -1f, 0f), Color.White, new Vector2(1f, 1f)), // BR
                };
                var indices = new short[] { 0, 1, 2, 2, 1, 3 };

                // Multi-pass technique = sequential full-quad draws, XNA semantics:
                // a pass with no VertexShader state keeps the previous pass's VS bound,
                // and in-pass render states persist until something overwrites them.
                foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    GraphicsDevice.DrawUserIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        verts, 0, verts.Length,
                        indices, 0, 2,
                        VsVertex.Declaration);
                }
            }

            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[w * h];
            rt.GetData(pixels);

            string png = Path.Combine(outDir, name + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            // Surface BOTH error kinds when both occurred — never drop one.
            string? renderErrors = (_fna3dErrors.Count > 0, paramError) switch
            {
                (true, not null) => $"FNA3D errors during render: {string.Join(" | ", _fna3dErrors)} | {paramError}",
                (true, null) => $"FNA3D errors during render: {string.Join(" | ", _fna3dErrors)}",
                (false, _) => paramError,
            };
            return new ArmOutcome(true, renderErrors is null, renderErrors, pixels, png, paramsSet);
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
