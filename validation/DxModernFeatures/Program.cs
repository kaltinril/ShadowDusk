// Phase 44 — DirectX modern-features RENDER validation.
//
// Proves that the SM4/5 features the OpenGL target rejects with SD0210 (vertex texture fetch)
// actually RENDER correctly through ShadowDusk's DirectX path in a REAL MonoGame WindowsDX
// runtime. The reference is Microsoft's own compiler: ShadowDusk compiles the same .fx with
// BOTH DXBC backends — the shipping cross-platform `vkd3d-shader` and the Windows-only
// `d3dcompiler_47` (real `fxc`) oracle — renders each, and asserts they draw the same picture.
// If the shipping vkd3d output matches Microsoft's fxc output pixel-for-pixel, the feature is
// proven correct (not merely "it compiled").
//
// This is the DirectX analog of validation/VsDriven: arm-vs-arm, same scene, only the compiler
// differs. Run on Windows: `dotnet run --project validation/DxModernFeatures`.
//
// ARM "legacy-register" (tests/fixtures/golden/legacy-sampler-units/DxLegacyRegisterRender.fx):
// a legacy `sampler2D A : register(s1);` must bind its SAMPLER at s1, as fxc does (mgfxc
// 3.8.4.1 records `t0 s1`). The SM4 rewrite used to drop the clause, so the sampler fell to s0
// and the state the game set on GraphicsDevice.SamplerStates[1] never reached it. The scene
// draws a 2-texel (red|green) sprite through SpriteBatch (LinearClamp on s0) with PointWrap on
// s1 and samples at uv*3: the s1 wrap repeats the stripes, the s0 clamp smears the edge
// texel. ShadowDusk's vkd3d build is pixel-diffed against the COMMITTED mgfxc golden, and the
// golden is rendered once more with PointClamp on s1 to prove the scene can tell the two apart.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Validation.Dx;

DxHeadlessRasterizer.PinIfRequested();

// Vertex texture fetch: the VS samples a height texture (SampleLevel) and displaces each
// vertex. A non-uniform height map deforms the quad; the PS paints UV so the deformation is
// visible. If VTF silently failed (height read as 0) the quad would be undeformed — a visibly
// different image, so the test is non-vacuous.
const string VtfShader = """
Texture2D HeightMap;
SamplerState HeightSampler;
struct VIn  { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };
VOut MainVS(VIn input)
{
    VOut o = (VOut)0;
    float h = HeightMap.SampleLevel(HeightSampler, input.UV, 0).r;   // <-- vertex texture fetch
    o.Pos = float4(input.Pos.x + (h - 0.5) * 0.6, input.Pos.y, 0, 1);
    o.UV = input.UV;
    return o;
}
float4 MainPS(VOut input) : SV_Target0 { return float4(input.UV, 0, 1); }
technique T { pass P { VertexShader = compile vs_4_0 MainVS(); PixelShader = compile ps_4_0 MainPS(); } }
""";

async Task<(byte[]? Bytes, string? Err)> CompileDx(string src, DxbcBackend backend)
{
    var r = await new EffectCompiler().CompileAsync(src, new CompilerOptions
    {
        Target = PlatformTarget.DirectX,
        DxbcBackend = backend,
        SourceFileName = "vtf.fx",
    });
    return r.IsFailure
        ? (null, string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")))
        : (r.Value.Data, null);
}

Console.WriteLine("[dx-modern] Vertex texture fetch — vkd3d (shipping) vs d3dcompiler/fxc (oracle), real MonoGame WindowsDX\n");

var (oracle, oracleErr) = await CompileDx(VtfShader, DxbcBackend.D3DCompiler);
var (vkd3d,  vkd3dErr)  = await CompileDx(VtfShader, DxbcBackend.Vkd3d);
Console.WriteLine($"[dx-modern] oracle(fxc): {(oracle is null ? "COMPILE FAIL: " + oracleErr : oracle.Length + " bytes")}");
Console.WriteLine($"[dx-modern] vkd3d:       {(vkd3d  is null ? "COMPILE FAIL: " + vkd3dErr  : vkd3d.Length  + " bytes")}\n");

if (oracle is null || vkd3d is null)
{
    Console.WriteLine("[dx-modern] verdict: FAIL (a backend did not compile the VTF shader)");
    return 1;
}

using var game = new RenderHarness(oracle, vkd3d);
game.Run();

int maxd = game.MaxDelta;
bool ok = game.OracleRendered && game.Vkd3dRendered && maxd == 0 && game.NonTrivial;
Console.WriteLine($"\n[dx-modern] oracle rendered: {game.OracleRendered}, vkd3d rendered: {game.Vkd3dRendered}");
Console.WriteLine($"[dx-modern] vkd3d-vs-fxc max per-channel delta: {(maxd < 0 ? "n/a" : maxd.ToString())}");
Console.WriteLine($"[dx-modern] scene non-trivial (VTF actually deformed the quad): {game.NonTrivial}");
Console.WriteLine($"[dx-modern] verdict: {(ok ? "PASS" : "FAIL")}");

// ---- Arm "legacy-register" ----------------------------------------------------------------
string repoRoot = FindRepoRoot();
string unitsDir = System.IO.Path.Combine(repoRoot, "tests", "fixtures", "golden", "legacy-sampler-units");
string legacySrc = await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(unitsDir, "DxLegacyRegisterRender.fx"));
byte[] legacyGolden = await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(unitsDir, "DxLegacyRegisterRender.DirectX_11.mgfx"));
var legacyResult = await new EffectCompiler().CompileAsync(legacySrc, new CompilerOptions
{
    Target = PlatformTarget.DirectX,
    SourceFileName = "DxLegacyRegisterRender.fx",
});
if (legacyResult.IsFailure)
{
    Console.WriteLine("[dx-modern] legacy-register: COMPILE FAIL: " +
                      string.Join(" | ", legacyResult.Error.Select(e => $"{e.Code}: {e.Message}")));
    return 1;
}
using var legacyGame = new LegacyRegisterHarness(legacyResult.Value.Data, legacyGolden);
legacyGame.Run();
bool legacyOk = legacyGame.Rendered && legacyGame.MaxDelta == 0 && legacyGame.NonTrivial;
Console.WriteLine($"\n[dx-modern] legacy-register: candidate vs mgfxc golden max delta: {legacyGame.MaxDelta}");
Console.WriteLine($"[dx-modern] legacy-register: scene tells s0 from s1 (golden with PointClamp on s1 differs): {legacyGame.NonTrivial}");
Console.WriteLine($"[dx-modern] legacy-register verdict: {(legacyOk ? "PASS" : "FAIL")}");

return ok && legacyOk ? 0 : 1;

static string FindRepoRoot()
{
    var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "ShadowDusk.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
}

// ---- Arm "legacy-register": SpriteBatch scene, s0 LinearClamp, s1 PointWrap ----------------

sealed class LegacyRegisterHarness : Game
{
    private const int W = 96, H = 32;
    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _candidate, _golden;
    private bool _done;

    public bool Rendered { get; private set; }
    public bool NonTrivial { get; private set; }
    public int MaxDelta { get; private set; } = -1;

    public LegacyRegisterHarness(byte[] candidate, byte[] golden)
    {
        _candidate = candidate; _golden = golden;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = W,
            PreferredBackBufferHeight = H,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        var gd = GraphicsDevice;
        using var sprite = new Texture2D(gd, 2, 1, false, SurfaceFormat.Color);
        sprite.SetData(new[] { Color.Red, Color.Lime });

        Color[]? Render(byte[] effectBytes, SamplerState s1)
        {
            try
            {
                using var effect = new Effect(gd, effectBytes);
                using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
                using var sb = new SpriteBatch(gd);
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Black);
                sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
                gd.SamplerStates[1] = s1;
                sb.Draw(sprite, new Rectangle(0, 0, W, H), Color.White);
                sb.End();
                gd.SetRenderTarget(null);
                var px = new Color[W * H];
                rt.GetData(px);
                return px;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  legacy-register render threw: {ex.GetType().Name}: {ex.Message}");
                try { gd.SetRenderTarget(null); } catch { }
                return null;
            }
        }

        static int MaxD(Color[] a, Color[] b)
        {
            int m = 0;
            for (int i = 0; i < a.Length; i++)
                m = Math.Max(m, Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)),
                                         Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A))));
            return m;
        }

        Color[]? candidate = Render(_candidate, SamplerState.PointWrap);
        Color[]? golden = Render(_golden, SamplerState.PointWrap);
        Color[]? goldenClamp = Render(_golden, SamplerState.PointClamp);
        if (candidate is not null && golden is not null && goldenClamp is not null)
        {
            Rendered = true;
            MaxDelta = MaxD(candidate, golden);
            NonTrivial = MaxD(golden, goldenClamp) > 16;
            Console.WriteLine($"  legacy-register row (candidate): {string.Join(" ", Enumerable.Range(0, 6).Select(i => candidate[(H / 2) * W + i * W / 6]).Select(c => $"({c.R},{c.G},{c.B})"))}");
            Console.WriteLine($"  legacy-register row (mgfxc):     {string.Join(" ", Enumerable.Range(0, 6).Select(i => golden[(H / 2) * W + i * W / 6]).Select(c => $"({c.R},{c.G},{c.B})"))}");
        }
        _done = true;
        Exit();
    }
}

// ---- Real MonoGame WindowsDX render harness (offscreen render target + readback) ----------

sealed class RenderHarness : Game
{
    private const int W = 128, H = 128;
    private readonly GraphicsDeviceManager _gdm;
    private readonly byte[] _oracleBytes, _vkd3dBytes;
    private bool _done;

    public bool OracleRendered { get; private set; }
    public bool Vkd3dRendered { get; private set; }
    public bool NonTrivial { get; private set; }
    public int MaxDelta { get; private set; } = -1;

    public RenderHarness(byte[] oracle, byte[] vkd3d)
    {
        _oracleBytes = oracle; _vkd3dBytes = vkd3d;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = W,
            PreferredBackBufferHeight = H,
            GraphicsProfile = GraphicsProfile.HiDef,   // HiDef = SM4/5 features available
        };
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        var gd = GraphicsDevice;

        // A horizontal-gradient height texture (height = u, each vertex displaces by its UV.x)
        // and a FLAT one (all 0.5 -> zero displacement) used to prove the VTF actually changes
        // the picture (non-vacuousness).
        var height = new Texture2D(gd, 8, 1, false, SurfaceFormat.Single);
        height.SetData(Enumerable.Range(0, 8).Select(x => x / 7f).ToArray());
        var flat = new Texture2D(gd, 8, 1, false, SurfaceFormat.Single);
        flat.SetData(Enumerable.Repeat(0.5f, 8).ToArray());

        // A small grid (5x5 verts) so the per-vertex VTF displacement is visible across the quad.
        const int N = 5;
        var verts = new VertexPositionTexture[N * N];
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float u = x / (float)(N - 1), v = y / (float)(N - 1);
            verts[y * N + x] = new VertexPositionTexture(
                new Vector3(u * 2 - 1, 1 - v * 2, 0), new Vector2(u, v));
        }
        var indices = new System.Collections.Generic.List<short>();
        for (int y = 0; y < N - 1; y++)
        for (int x = 0; x < N - 1; x++)
        {
            short tl = (short)(y * N + x), tr = (short)(tl + 1), bl = (short)(tl + N), br = (short)(bl + 1);
            indices.AddRange(new short[] { tl, tr, bl, tr, br, bl });
        }
        var idx = indices.ToArray();

        byte[] Render(byte[] effectBytes, Texture2D heightTex, out bool rendered)
        {
            rendered = false;
            var pixels = new Color[W * H];
            using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.None);
            try
            {
                Effect effect = new Effect(gd, effectBytes);
                gd.SetRenderTarget(rt);
                gd.Clear(Color.Black);
                gd.BlendState = BlendState.Opaque;
                gd.DepthStencilState = DepthStencilState.None;
                gd.RasterizerState = RasterizerState.CullNone;
                gd.SamplerStates[0] = SamplerState.PointClamp;
                gd.VertexSamplerStates[0] = SamplerState.PointClamp;
                effect.Parameters["HeightMap"]?.SetValue(heightTex);
                foreach (var pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList,
                        verts, 0, verts.Length, idx, 0, idx.Length / 3);
                }
                gd.SetRenderTarget(null);
                rt.GetData(pixels);
                rendered = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  render threw: {ex.GetType().Name}: {ex.Message}");
                try { gd.SetRenderTarget(null); } catch { }
            }
            return pixels.SelectMany(p => new[] { p.R, p.G, p.B, p.A }).ToArray();
        }

        static int MaxD(byte[] a, byte[] b)
        {
            int m = 0;
            for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        byte[] oracleGrad = Render(_oracleBytes, height, out bool oRend); OracleRendered = oRend;
        byte[] vkd3dGrad  = Render(_vkd3dBytes,  height, out bool vRend); Vkd3dRendered  = vRend;
        byte[] oracleFlat = Render(_oracleBytes, flat,   out bool fRend);

        if (oRend && vRend)
        {
            // Main result: ShadowDusk's shipping vkd3d output renders identical to Microsoft's
            // fxc (the d3dcompiler oracle) for the same VTF scene.
            MaxDelta = MaxD(oracleGrad, vkd3dGrad);
            // Non-vacuous: the vertex texture fetch MUST change the picture — the gradient-height
            // render differs from the flat-height (zero-displacement) render. If VTF were a no-op,
            // these would be identical and the pixel-match above would be meaningless.
            NonTrivial = fRend && MaxD(oracleGrad, oracleFlat) > 16;
        }

        height.Dispose(); flat.Dispose();
        _done = true;
        Exit();
    }
}
