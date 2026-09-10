# B. FNA `Content.Load<Effect>` probe (real FNA 26.06, FNA3D D3D11, fnalibs win-x64)

The throwaway probe behind Phase 64 section 2.6. It references `validation/FnaValidation/external/FNA/FNA.Core.csproj` (restored by `restore-fna.ps1`), copies the fnalibs natives beside the exe, and reuses the existing gate's `ReferenceFx2Compiler` (the `d3dcompiler_47` `fx_2_0` oracle) for the reference arm. `FNALoggerEXT.LogError` is hooked so MojoShader text is captured verbatim.

Run from the probe directory: `dotnet build -c Release`, then `bin/Release/net8.0/<Probe>.exe <scratchpad>/issue199 <worktree>` (argument 1 = the directory holding `cli/` and `mgcb/`, argument 2 = the repo root). The probe writes one `Grayscale.xnb` per arm into its own temp directory, loads each through a real `ContentManager(Services, dir).Load<Effect>("Grayscale")`, renders the cat through the same SpriteBatch scene `validation/XnbContentLoad` uses, saves PNGs, and pixel-compares in process.

## Measured output (2026-09-09)

### FNA 26.06

```text
[probe] runtime: FNA, Version=26.6.0.0, Culture=neutral, PublicKeyToken=null
[probe] results (reference = A, fxc fx_2_0 oracle via new Effect):
  [A-ref-fxc-newEffect] loaded+rendered; 1230720 px, 0 differ from A (maxd 0)
  [B-sd-Fna-xnb-w] loaded+rendered; 1230720 px, 0 differ from A (maxd 0)
  [C-ctl-fxc-in-xnb-w] loaded+rendered; 1230720 px, 0 differ from A (maxd 0)
  [D-ctl-fnapayload-d] loaded+rendered; 1230720 px, 0 differ from A (maxd 0)
  [E-neg-fnapayload-V] FAILED: InvalidOperationException: MOJOSHADER_compileEffect Error: Not an Effects Framework binary | FNA3D: MOJOSHADER_compileEffect Error: Not an Effects Framework binary
  [F-neg-fnapayload-G] FAILED: InvalidOperationException: MOJOSHADER_compileEffect Error: Not an Effects Framework binary | FNA3D: MOJOSHADER_compileEffect Error: Not an Effects Framework binary
  [G-neg-bare-manifest] FAILED: ContentLoadException: Could not find ContentTypeReader Type. Please ensure the name of the Assembly that contains the Type matches the assembly in the full type name: Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework (Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework)
  [H-ctl-xna-manifest] loaded+rendered; 1230720 px, 0 differ from A (maxd 0)
  [I-neg-mgfx-payload-d] FAILED: InvalidOperationException: MOJOSHADER_compileEffect Error: Not an Effects Framework binary | FNA3D: MOJOSHADER_compileEffect Error: Not an Effects Framework binary
```

## The project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\src\ShadowDusk.Compiler\ShadowDusk.Compiler.csproj" />
    <ProjectReference Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\validation\FnaValidation\external\FNA\FNA.Core.csproj" />
    <Compile Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\validation\FnaValidation\ReferenceFx2Compiler.cs" />
  </ItemGroup>
  <ItemGroup>
    <None Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\validation\FnaValidation\external\fnalibs\x64\*.dll" CopyToOutputDirectory="PreserveNewest" Link="%(Filename)%(Extension)" />
    <None Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\validation\FnaValidation\external\fnalibs\D3D12\*.dll" CopyToOutputDirectory="PreserveNewest" Link="D3D12\%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

## Program.cs

```csharp
// FNA Content.Load<Effect> probe for Phase 64 (issue #199). Scratch, not product code.
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Core;
using ShadowDusk.Validation.Fna;

Environment.SetEnvironmentVariable("FNA3D_FORCE_DRIVER", "D3D11");
var fna3dErrors = new List<string>();
FNALoggerEXT.LogError = m => fna3dErrors.Add(m);

string S = args[0];
string W = args[1];
string outDir = Path.Combine(S, "fna-out");
Directory.CreateDirectory(outDir);

AssemblyName fna = typeof(Game).Assembly.GetName();
Console.WriteLine($"[probe] runtime: {fna.FullName}");

string fx = Path.Combine(W, "tests", "fixtures", "shaders", "Grayscale.fx");
var reference = ReferenceFx2Compiler.Compile(fx, File.ReadAllText(fx));
if (reference.Bytes is null) { Console.WriteLine($"reference compile failed: {reference.Error}"); return 1; }
byte[] fxc = reference.Bytes;

byte[] sdFna = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_FNA.xnb"));
byte[] sdGl  = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_OpenGL.xnb"));
byte[] fnaPayload = Payload(sdFna);

var arms = new List<(string Name, byte[]? Xnb, byte[]? Raw)>
{
    ("A-ref-fxc-newEffect",     null,  fxc),
    ("B-sd-Fna-xnb-w",          sdFna, null),
    ("C-ctl-fxc-in-xnb-w",      XnbWriter.Wrap(fxc, 'w'), null),
    ("D-ctl-fnapayload-d",      XnbWriter.Wrap(fnaPayload, 'd'), null),
    ("E-neg-fnapayload-V",      XnbWriter.Wrap(fnaPayload, 'V'), null),
    ("F-neg-fnapayload-G",      XnbWriter.Wrap(fnaPayload, 'G'), null),
    ("G-neg-bare-manifest",     WrapWithReaderName(fnaPayload, 'w', "Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework"), null),
    ("H-ctl-xna-manifest",      WrapWithReaderName(fnaPayload, 'w', "Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553"), null),
    ("I-neg-mgfx-payload-d",    sdGl, null),
};

string work = Path.Combine(Path.GetTempPath(), "sd_fna_xnb_probe_" + Guid.NewGuid().ToString("N"));
var rows = new List<(string Name, string? Dir, byte[]? Raw)>();
foreach (var (name, xnb, raw) in arms)
{
    string? d = null;
    if (xnb is not null)
    {
        d = Path.Combine(work, name);
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "Grayscale.xnb"), xnb);
    }
    rows.Add((name, d, raw));
}

string cat = Path.Combine(W, "samples", "ShaderViewer", "Content", "cat.jpg");
using (var game = new ProbeGame(cat, outDir, rows, fna3dErrors))
{
    game.Run();
    Color[]? refPx = game.Pixels.GetValueOrDefault("A-ref-fxc-newEffect");
    Console.WriteLine();
    Console.WriteLine("[probe] results (reference = A, fxc fx_2_0 oracle via new Effect):");
    foreach (var (name, _, _) in rows)
    {
        if (game.Errors.TryGetValue(name, out string? err)) { Console.WriteLine($"  [{name}] FAILED: {err}"); continue; }
        Color[] px = game.Pixels[name];
        if (refPx is null) { Console.WriteLine($"  [{name}] ok, no reference"); continue; }
        int diff = 0, maxd = 0;
        for (int i = 0; i < px.Length; i++)
        {
            int m = Math.Max(Math.Max(Math.Abs(px[i].R - refPx[i].R), Math.Abs(px[i].G - refPx[i].G)), Math.Max(Math.Abs(px[i].B - refPx[i].B), Math.Abs(px[i].A - refPx[i].A)));
            if (m > 0) diff++;
            maxd = Math.Max(maxd, m);
        }
        Console.WriteLine($"  [{name}] loaded+rendered; {px.Length} px, {diff} differ from A (maxd {maxd})");
    }
}
try { Directory.Delete(work, true); } catch { }
return 0;

static byte[] Payload(byte[] xnb)
{
    int i = 10;
    int readers = Read7(xnb, ref i);
    for (int r = 0; r < readers; r++) { int n = Read7(xnb, ref i); i += n + 4; }
    Read7(xnb, ref i); Read7(xnb, ref i);
    int len = BitConverter.ToInt32(xnb, i); i += 4;
    return xnb.AsSpan(i, len).ToArray();
}
static int Read7(byte[] b, ref int i) { int r = 0, s = 0; while (true) { byte x = b[i++]; r |= (x & 0x7F) << s; if ((x & 0x80) == 0) return r; s += 7; } }
static byte[] WrapWithReaderName(byte[] payload, char platform, string readerName)
{
    byte[] name = Encoding.UTF8.GetBytes(readerName);
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((byte)'X'); w.Write((byte)'N'); w.Write((byte)'B'); w.Write((byte)platform); w.Write((byte)5); w.Write((byte)0);
    w.Write(0);
    w.Write((byte)1); Write7(w, name.Length); w.Write(name); w.Write(0);
    w.Write((byte)0); w.Write((byte)1);
    w.Write(payload.Length); w.Write(payload);
    w.Flush();
    byte[] all = ms.ToArray();
    BitConverter.GetBytes(all.Length).CopyTo(all, 6);
    return all;
}
static void Write7(BinaryWriter w, int v) { uint u = (uint)v; while (u >= 0x80) { w.Write((byte)(u | 0x80)); u >>= 7; } w.Write((byte)u); }

sealed class ProbeGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _cat, _out;
    private readonly IReadOnlyList<(string Name, string? Dir, byte[]? Raw)> _rows;
    private readonly List<string> _fna3d;
    private SpriteBatch _sb = null!;
    private Texture2D _tex = null!;
    private bool _done;
    public Dictionary<string, Color[]> Pixels { get; } = new();
    public Dictionary<string, string> Errors { get; } = new();

    public ProbeGame(string cat, string outDir, IReadOnlyList<(string, string?, byte[]?)> rows, List<string> fna3d)
    {
        _cat = cat; _out = outDir; _rows = rows; _fna3d = fna3d;
        _gdm = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64, GraphicsProfile = GraphicsProfile.HiDef };
    }
    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_cat);
        _tex = Texture2D.FromStream(GraphicsDevice, fs);
    }
    protected override void Draw(GameTime gt)
    {
        if (_done) { Exit(); return; }
        foreach (var (name, dir, raw) in _rows)
        {
            _fna3d.Clear();
            Effect effect;
            try
            {
                if (dir is not null)
                {
                    var cm = new ContentManager(Services, dir);
                    effect = cm.Load<Effect>("Grayscale");
                }
                else
                {
                    effect = new Effect(GraphicsDevice, raw!);
                }
            }
            catch (Exception ex)
            {
                string mojo = _fna3d.Count > 0 ? $" | FNA3D: {string.Join(" | ", _fna3d)}" : "";
                Errors[name] = $"{ex.GetType().Name}: {ex.Message}" + (ex.InnerException is null ? "" : $" | inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}") + mojo;
                continue;
            }
            if (_fna3d.Count > 0) { Errors[name] = "MojoShader errors on load: " + string.Join(" | ", _fna3d); continue; }
            try { Pixels[name] = Render(effect, name); }
            catch (Exception ex) { Errors[name] = $"render threw {ex.GetType().Name}: {ex.Message}"; }
        }
        _done = true; Exit();
    }
    private Color[] Render(Effect effect, string name)
    {
        int w = _tex.Width, h = _tex.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        var dest = new Rectangle(0, 0, w, h);
        GraphicsDevice.SetRenderTarget(rt);
        GraphicsDevice.Clear(Color.Transparent);
        GraphicsDevice.Textures[0] = null; GraphicsDevice.Textures[1] = null;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
        _sb.Draw(_tex, dest, Color.White);
        _sb.End();
        _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, effect);
        _sb.Draw(_tex, dest, Color.White);
        _sb.End();
        GraphicsDevice.SetRenderTarget(null);
        var px = new Color[w * h];
        rt.GetData(px);
        using var f = File.Create(Path.Combine(_out, name + ".png"));
        rt.SaveAsPng(f, w, h);
        return px;
    }
}
```
