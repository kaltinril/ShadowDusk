# A. KNI `Content.Load<Effect>` probe (real nkast KNI 4.2.9001 and 4.3.9001, SDL2.GL)

The throwaway probe behind Phase 64 section 2.4. `KniXnbProbe43` is byte-identical except the package versions are pinned to `4.3.9001`. The KNI-version dependence of the reader-name resolver (section 2.3) is visible directly in the two outputs below: arms A, B, C, E, F, H, I, K fail on 4.2.9001 with `FileLoadException` and load on 4.3.9001.

Run from the probe directory: `dotnet build -c Release`, then `bin/Release/net8.0/<Probe>.exe <scratchpad>/issue199 <worktree>` (argument 1 = the directory holding `cli/` and `mgcb/`, argument 2 = the repo root). The probe writes one `Grayscale.xnb` per arm into its own temp directory, loads each through a real `ContentManager(Services, dir).Load<Effect>("Grayscale")`, renders the cat through the same SpriteBatch scene `validation/XnbContentLoad` uses, saves PNGs, and pixel-compares in process.

## Measured output (2026-09-09)

### KNI 4.2.9001

```text
[probe] runtime: Xna.Framework.Game 4.2.9001.0
[probe] content assembly: Xna.Framework.Content, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null
[probe] graphics assembly: Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null
[probe] results (reference = L, stock mgcb DesktopGL PAYLOAD in an XNA-manifest container via KNI Content.Load):
  [A-mgcb-DesktopGL-d] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [B-sd-OpenGL-v10-d] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [C-sd-knifx-d] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [D-sd-mgfx-v11-d] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [E-ctl-glpayload-w] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [F-ctl-glpayload-b] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [G-neg-glpayload-V] LOAD/RENDER FAILED: ContentLoadException: Asset does not appear to target a known platform. Platform Identifier: 'V'.
  [H-ctl-glpayload-G] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [I-neg-bare-manifest] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [J-ctl-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [K-neg-dx-payload-w] LOAD/RENDER FAILED: FileLoadException: The given assembly name was invalid.
  [L-ref-mgcbpayload-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [M-sd-knifx-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [N-sd-v11-xna-manifest] LOAD/RENDER FAILED: Exception: This effect seems to be for a newer version of KNI.
  [O-sd-kni-native-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [P-sd-xna-manifest-w] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
```

### KNI 4.3.9001

```text
[probe] runtime: Xna.Framework.Game 4.3.9001.0
[probe] content assembly: Xna.Framework.Content, Version=4.3.9001.0, Culture=neutral, PublicKeyToken=null
[probe] graphics assembly: Xna.Framework.Graphics, Version=4.3.9001.0, Culture=neutral, PublicKeyToken=null
[probe] results (reference = L, stock mgcb DesktopGL PAYLOAD in an XNA-manifest container via KNI Content.Load):
  [A-mgcb-DesktopGL-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [B-sd-OpenGL-v10-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [C-sd-knifx-d] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [D-sd-mgfx-v11-d] LOAD/RENDER FAILED: Exception: This effect is an unsupported effect format. Please rebuild the effect using the KNI content pipeline.
  [E-ctl-glpayload-w] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [F-ctl-glpayload-b] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [G-neg-glpayload-V] LOAD/RENDER FAILED: ContentLoadException: Asset does not appear to target a known platform. Platform Identifier: 'V'.
  [H-ctl-glpayload-G] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [I-neg-bare-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [J-ctl-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [K-neg-dx-payload-w] LOAD/RENDER FAILED: Exception: Effect profile 'DirectX_11' is not compatible with the graphics backend 'OpenGL'.
  [L-ref-mgcbpayload-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [M-sd-knifx-xna-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [N-sd-v11-xna-manifest] LOAD/RENDER FAILED: Exception: This effect is an unsupported effect format. Please rebuild the effect using the KNI content pipeline.
  [O-sd-kni-native-manifest] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
  [P-sd-xna-manifest-w] Content.Load OK; 1230720 px, 0 differ from A (maxd 0)
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
    <DefineConstants>$(DefineConstants);DESKTOPGL</DefineConstants>
    <KniPlatform>DesktopGL</KniPlatform>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="nkast.Xna.Framework" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Xna.Framework.Content" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Xna.Framework.Graphics" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Xna.Framework.Audio" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Xna.Framework.Media" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Xna.Framework.Input" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Xna.Framework.Game" Version="4.2.9001.*" />
    <PackageReference Include="nkast.Kni.Platform.SDL2.GL" Version="4.2.9001.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="C:\git\ShadowDusk\.claude\worktrees\issue-199-xnb\src\ShadowDusk.Compiler\ShadowDusk.Compiler.csproj" />
  </ItemGroup>
</Project>
```

## Program.cs

```csharp
// KNI Content.Load<Effect> probe for Phase 64 (issue #199). Scratch, not product code.
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Core;

string S = args[0];   // scratchpad/issue199
string W = args[1];   // worktree
string outDir = Path.Combine(S, "kni-out");
Directory.CreateDirectory(outDir);

AssemblyName xna = typeof(Game).Assembly.GetName();
Console.WriteLine($"[probe] runtime: {xna.Name} {xna.Version}");
AssemblyName content = typeof(ContentManager).Assembly.GetName();
Console.WriteLine($"[probe] content assembly: {content.FullName}");
Console.WriteLine($"[probe] graphics assembly: {typeof(Effect).Assembly.GetName().FullName}");

byte[] mgcbGl   = File.ReadAllBytes(Path.Combine(S, "mgcb", "DesktopGL", "bin", "Grayscale.xnb"));
byte[] sdGl     = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_OpenGL.xnb"));
byte[] sdKnifx  = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_tr_kni-knifx.xnb"));
byte[] sdV11    = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_tr_monogame-gl-v11.xnb"));
byte[] sdDx     = File.ReadAllBytes(Path.Combine(S, "cli", "Grayscale_DirectX_11.xnb"));
byte[] glPayload = Payload(sdGl);
const string XnaManifest = "Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553";

var arms = new List<(string Name, byte[] Xnb)>
{
    ("A-mgcb-DesktopGL-d",      mgcbGl),
    ("B-sd-OpenGL-v10-d",       sdGl),
    ("C-sd-knifx-d",            sdKnifx),
    ("D-sd-mgfx-v11-d",         sdV11),
    ("E-ctl-glpayload-w",       XnbWriter.Wrap(glPayload, 'w')),
    ("F-ctl-glpayload-b",       XnbWriter.Wrap(glPayload, 'b')),
    ("G-neg-glpayload-V",       XnbWriter.Wrap(glPayload, 'V')),
    ("H-ctl-glpayload-G",       XnbWriter.Wrap(glPayload, 'G')),
    ("I-neg-bare-manifest",     WrapWithReaderName(glPayload, 'd', "Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework")),
    ("J-ctl-xna-manifest",      WrapWithReaderName(glPayload, 'd', "Microsoft.Xna.Framework.Content.EffectReader, Microsoft.Xna.Framework.Graphics, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553")),
    ("K-neg-dx-payload-w",      sdDx),
    ("L-ref-mgcbpayload-xna-manifest", WrapWithReaderName(Payload(mgcbGl), 'd', XnaManifest)),
    ("M-sd-knifx-xna-manifest",        WrapWithReaderName(Payload(sdKnifx), 'd', XnaManifest)),
    ("N-sd-v11-xna-manifest",          WrapWithReaderName(Payload(sdV11), 'd', XnaManifest)),
    ("O-sd-kni-native-manifest",       WrapWithReaderName(glPayload, 'd', "Microsoft.Xna.Framework.Content.EffectReader, Xna.Framework.Graphics, Version=4.2.9001.0, Culture=neutral, PublicKeyToken=null")),
    ("P-sd-xna-manifest-w",            WrapWithReaderName(glPayload, 'w', XnaManifest)),
};

string work = Path.Combine(Path.GetTempPath(), "sd_kni_xnb_probe_" + Guid.NewGuid().ToString("N"));
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

Color[]? reference = game.Pixels.GetValueOrDefault("L-ref-mgcbpayload-xna-manifest");
Console.WriteLine();
Console.WriteLine("[probe] results (reference = L, stock mgcb DesktopGL PAYLOAD in an XNA-manifest container via KNI Content.Load):");
foreach (var (name, _) in dirs)
{
    if (game.Errors.TryGetValue(name, out string? err))
    {
        Console.WriteLine($"  [{name}] LOAD/RENDER FAILED: {err}");
        continue;
    }
    Color[] px = game.Pixels[name];
    if (reference is null) { Console.WriteLine($"  [{name}] loaded+rendered ({px.Length} px), no reference"); continue; }
    int diff = 0, maxd = 0;
    for (int i = 0; i < px.Length; i++)
    {
        if (px[i] == reference[i]) continue;
        diff++;
        maxd = Math.Max(maxd, Math.Max(Math.Max(Math.Abs(px[i].R - reference[i].R), Math.Abs(px[i].G - reference[i].G)), Math.Max(Math.Abs(px[i].B - reference[i].B), Math.Abs(px[i].A - reference[i].A))));
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
    w.Write(0); // patched below
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
