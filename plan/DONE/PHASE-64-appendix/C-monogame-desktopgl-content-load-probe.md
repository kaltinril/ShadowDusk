# C. MonoGame DesktopGL `Content.Load<Effect>` manifest probe (3.8.1.263, 3.8.2.1105, 3.8.5)

The throwaway probe behind Phase 64 section 2.5. Built three times with `dotnet build -c Release -p:MgVersion=<v> -o out-<v>`; the assembly name carries the version so the three builds coexist. It is the DesktopGL measurement `validation/XnbContentLoad` (WindowsDX only) never made.

Run from the probe directory: `dotnet build -c Release`, then `bin/Release/net8.0/<Probe>.exe <scratchpad>/issue199 <worktree>` (argument 1 = the directory holding `cli/` and `mgcb/`, argument 2 = the repo root). The probe writes one `Grayscale.xnb` per arm into its own temp directory, loads each through a real `ContentManager(Services, dir).Load<Effect>("Grayscale")`, renders the cat through the same SpriteBatch scene `validation/XnbContentLoad` uses, saves PNGs, and pixel-compares in process.

## Measured output (2026-09-09)

### MonoGame 3.8.1.263

```text
[probe] runtime: MonoGame.Framework, Version=3.8.1.263, Culture=neutral, PublicKeyToken=null
[probe] MonoGame 3.8.1.263 results (reference = A, stock mgcb DesktopGL xnb via Content.Load):
  [A-mgcb-DesktopGL-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [B-sd-OpenGL-v10-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [C-sd-xna-manifest-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [D-mgcbpayload-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [E-sd-kni-manifest-d] FAILED: ContentLoadException: Could not find ContentTypeReader Type. Please ensure the name of the Assembly that contains the Type matches the assembly in the full type name: Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null (Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics)
  [F-neg-bare-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [G-ctl-glpayload-w] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [H-neg-dx-payload-w] FAILED: Exception: This MGFX effect was built for a different platform!
```

### MonoGame 3.8.2.1105

```text
[probe] runtime: MonoGame.Framework, Version=3.8.2.1105, Culture=neutral, PublicKeyToken=null
[probe] MonoGame 3.8.2.1105 results (reference = A, stock mgcb DesktopGL xnb via Content.Load):
  [A-mgcb-DesktopGL-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [B-sd-OpenGL-v10-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [C-sd-xna-manifest-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [D-mgcbpayload-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [E-sd-kni-manifest-d] FAILED: ContentLoadException: Could not find ContentTypeReader Type. Please ensure the name of the Assembly that contains the Type matches the assembly in the full type name: Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null (Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics)
  [F-neg-bare-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [G-ctl-glpayload-w] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [H-neg-dx-payload-w] FAILED: Exception: This MGFX effect was built for a different platform!
```

### MonoGame 3.8.5

```text
[probe] runtime: MonoGame.Framework, Version=3.8.5.0, Culture=neutral, PublicKeyToken=null
[probe] MonoGame 3.8.5.0 results (reference = A, stock mgcb DesktopGL xnb via Content.Load):
  [A-mgcb-DesktopGL-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [B-sd-OpenGL-v10-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [C-sd-xna-manifest-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [D-mgcbpayload-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [E-sd-kni-manifest-d] FAILED: ContentLoadException: Could not find ContentTypeReader Type. Please ensure the name of the Assembly that contains the Type matches the assembly in the full type name: Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null (Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics).  If you are using trimming, PublishAOT, or targeting mobile platforms, you should call ContentTypeReaderManager.AddTypeCreator() on that reader type somewhere in your code.
  [F-neg-bare-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [G-ctl-glpayload-w] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [H-neg-dx-payload-w] FAILED: Exception: This MGFX effect was built for a different platform!
```

## The project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <MgVersion Condition="'$(MgVersion)' == ''">3.8.5</MgVersion>
    <AssemblyName>MgXnbProbe_$(MgVersion)</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="$(MgVersion)" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\src\ShadowDusk.Core\ShadowDusk.Core.csproj" />
  </ItemGroup>
</Project>
```

## Program.cs

```csharp
// MonoGame DesktopGL Content.Load<Effect> manifest probe for Phase 64 (issue #199). Scratch.
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Core;

string S = args[0];
string W = args[1];
AssemblyName mg = typeof(Game).Assembly.GetName();
Console.WriteLine($"[probe] runtime: {mg.FullName}");
string outDir = Path.Combine(S, "mg-out-" + mg.Version);
Directory.CreateDirectory(outDir);

const string XnaManifest = "Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553";
const string KniManifest = "Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null";

byte[] mgcbGl = File.ReadAllBytes(Path.Combine(S, "mgcb", "DesktopGL", "bin", "Grayscale.xnb"));
byte[] sdGl   = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_OpenGL.xnb"));
byte[] glPayload = Payload(sdGl);
byte[] mgcbPayload = Payload(mgcbGl);

var arms = new List<(string Name, byte[] Xnb)>
{
    ("A-mgcb-DesktopGL-d",        mgcbGl),
    ("B-sd-OpenGL-v10-d",         sdGl),
    ("C-sd-xna-manifest-d",       WrapWithReaderName(glPayload, 'd', XnaManifest)),
    ("D-mgcbpayload-xna-manifest",WrapWithReaderName(mgcbPayload, 'd', XnaManifest)),
    ("E-sd-kni-manifest-d",       WrapWithReaderName(glPayload, 'd', KniManifest)),
    ("F-neg-bare-manifest",       WrapWithReaderName(glPayload, 'd', "Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework")),
    ("G-ctl-glpayload-w",         XnbWriter.Wrap(glPayload, 'w')),
    ("H-neg-dx-payload-w",        File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_DirectX_11.xnb"))),
};

string work = Path.Combine(Path.GetTempPath(), "sd_mg_xnb_probe_" + Guid.NewGuid().ToString("N"));
var dirs = new List<(string Name, string Dir)>();
foreach (var (name, xnb) in arms)
{
    string d = Path.Combine(work, name);
    Directory.CreateDirectory(d);
    File.WriteAllBytes(Path.Combine(d, "Grayscale.xnb"), xnb);
    dirs.Add((name, d));
}

string cat = Path.Combine(W, "samples", "ShaderViewer", "Content", "cat.jpg");
using var game = new ProbeGame(cat, outDir, dirs);
game.Run();

Color[]? reference = game.Pixels.GetValueOrDefault("A-mgcb-DesktopGL-d");
Console.WriteLine();
Console.WriteLine($"[probe] MonoGame {mg.Version} results (reference = A, stock mgcb DesktopGL xnb via Content.Load):");
foreach (var (name, _) in dirs)
{
    if (game.Errors.TryGetValue(name, out string? err)) { Console.WriteLine($"  [{name}] FAILED: {err}"); continue; }
    Color[] px = game.Pixels[name];
    if (reference is null) { Console.WriteLine($"  [{name}] loaded+rendered, no reference"); continue; }
    int diff = 0, maxd = 0;
    for (int i = 0; i < px.Length; i++)
    {
        int m = Math.Max(Math.Max(Math.Abs(px[i].R - reference[i].R), Math.Abs(px[i].G - reference[i].G)), Math.Max(Math.Abs(px[i].B - reference[i].B), Math.Abs(px[i].A - reference[i].A)));
        if (m > 0) diff++;
        maxd = Math.Max(maxd, m);
    }
    Console.WriteLine($"  [{name}] Content.Load OK; {px.Length} px, {diff} differ from A (maxd {maxd})");
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
    private readonly IReadOnlyList<(string Name, string Dir)> _dirs;
    private SpriteBatch _sb = null!;
    private Texture2D _tex = null!;
    private bool _done;
    public Dictionary<string, Color[]> Pixels { get; } = new();
    public Dictionary<string, string> Errors { get; } = new();

    public ProbeGame(string cat, string outDir, IReadOnlyList<(string, string)> dirs)
    {
        _cat = cat; _out = outDir; _dirs = dirs;
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
        foreach (var (name, dir) in _dirs)
        {
            Effect effect;
            try
            {
                var cm = new ContentManager(Services, dir);
                effect = cm.Load<Effect>("Grayscale");
            }
            catch (Exception ex)
            {
                Errors[name] = $"{ex.GetType().Name}: {ex.Message}" + (ex.InnerException is null ? "" : $" | inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                continue;
            }
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
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
        _sb.Draw(_tex, dest, Color.White);
        _sb.End();
        _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, effect);
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
