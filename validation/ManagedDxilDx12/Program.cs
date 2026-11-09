// Issue #478: does real D3D12 accept DXIL signed ONLY by ShadowDusk's managed container hash?
//
// A Linux/macOS host has no dxil.dll, so its DirectX12 .mgfx carries DXIL whose container hash is
// signed by DxilContainerHash alone. Here (Windows) we reproduce exactly that artifact: compile
// normally (dxil.dll signs), zero every DXBC container hash (what an unsigned off-Windows compile
// emits), then sign with DxilContainerHash and nothing else.
//
// default : the PS corpus + the VS rig (VsTransformColorTexture) - reference (dxil.dll-signed),
//           managed-signed, and the real mgfxc DirectX_12 golden each load in a real MonoGame
//           WindowsDX12 Effect with the D3D12 debug layer on; managed bytes must equal the reference
//           bytes and the renders must match (maxd 0). Then re-runs itself as `negative`.
// negative: the same container left at a zero hash must be REJECTED by D3D12 (a signed reference is
//           rendered first in the same process, so the rejection is attributable to the hash).

using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.Validation;
using ShadowDusk.Validation.Dx;

DxHeadlessRasterizer.PinIfRequested();
// The strict mode the Apos.Shapes failure (Apostolique/Apos.Shapes#43) was reported under. Only
// requested when Graphics Tools is installed (D3D12SDKLayers.dll), else device creation could fail.
bool debugLayer = File.Exists(Path.Combine(Environment.SystemDirectory, "D3D12SDKLayers.dll"));
GraphicsAdapter.UseDebugLayers = debugLayer;
// Windows Developer Mode makes D3D12 accept ANY DXIL container hash (measured: zero and one-bit-wrong
// both render), so on such a box a successful load proves nothing about the signer. Set by CI, where
// the box is not in Developer Mode, to turn "inconclusive" into a failure.
bool developerMode = (Microsoft.Win32.Registry.GetValue(
    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock",
    "AllowDevelopmentWithoutDevLicense", 0) as int? ?? 0) != 0;
bool requireStrict = Environment.GetEnvironmentVariable("SHADOWDUSK_REQUIRE_STRICT_D3D12") == "1";
const int InconclusiveExit = 3;
Console.WriteLine($"[managed-dxil] D3D12 debug layer: {(debugLayer ? "on" : "off (Graphics Tools not installed)")}; Windows Developer Mode: {(developerMode ? "ON" : "off")}");

string mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "default";
string repoRoot = ShaderInputs.FindRepoRoot();
string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_12");
string catPath = ShaderInputs.CatPath(repoRoot);
string outBase = Path.Combine(repoRoot, "validation", "output", "managed-dxil-dx12");
const string VsFixture = "VsTransformColorTexture";

var compiler = new EffectCompiler();
async Task<byte[]> CompileRef(string name)
{
    string fx = Path.Combine(shaderDir, name + ".fx");
    var r = await compiler.CompileAsync(await File.ReadAllTextAsync(fx), new CompilerOptions
    {
        Target = PlatformTarget.DirectX12,
        IncludeResolver = new FileSystemIncludeResolver(),
        SourceFileName = fx,
    });
    if (r.IsFailure)
        throw new InvalidOperationException($"{name}: " + string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")));
    return r.Value.Data;
}

if (mode == "negative")
    return await RunNegative();
if (mode != "default")
{
    Console.Error.WriteLine($"unknown mode '{mode}' - expected no argument or 'negative'");
    return 2;
}
return await RunPositive();

byte[]? Golden(string n)
{
    string path = Path.Combine(goldenDir, n + ".mgfx");
    return File.Exists(path) ? File.ReadAllBytes(path) : null;
}

async Task<int> RunPositive()
{
    var names = ShaderInputs.ShaderNames.Append(VsFixture).ToList();
    var reference = new Dictionary<string, byte[]?>();
    var managed = new Dictionary<string, byte[]?>();
    var golden = new Dictionary<string, byte[]?>();
    bool bytesOk = true;
    foreach (var name in names)
    {
        byte[] r = await CompileRef(name);
        byte[] viaManaged = (byte[])r.Clone();
        int zeroed = DxilContainers.ZeroHashes(viaManaged);   // what a Linux host's compile leaves
        int signed = DxilContainers.SignAll(viaManaged);      // ...and ShadowDusk's own signer
        bool eq = viaManaged.AsSpan().SequenceEqual(r);
        bytesOk &= eq && zeroed > 0 && signed == zeroed;
        Console.WriteLine($"[managed-dxil] {name,-24} containers={zeroed} zeroed then managed-signed; bytes == dxil.dll-signed: {(eq ? "yes" : "NO")}");
        reference[name] = r;
        managed[name] = viaManaged;
        golden[name] = Golden(name);
    }

    var results = new List<(string Name, string Arm, ShaderOutcome O, Color[]? Px)>();
    foreach (var (arm, set) in new[] { ("reference", reference), ("managed", managed), ("golden", golden) })
    {
        var psJobs = ShaderInputs.ShaderNames
            .Select(n => new ShaderJob(n, set[n], set[n] is null ? "missing" : null)).ToList();
        using (var g = new EffectImageRenderer(catPath, Path.Combine(outBase, arm), psJobs, ShaderInputs.SetParams))
        {
            g.Run();
            foreach (var o in g.Outcomes)
                results.Add((o.Name, arm, o, g.Captures.Where(c => c.Name == o.Name).Select(c => c.Pixels).FirstOrDefault()));
        }
        var vsJobs = new List<ShaderJob> { new(VsFixture, set[VsFixture], set[VsFixture] is null ? "missing" : null) };
        using (var g = new VsDxEffectImageRenderer(catPath, Path.Combine(outBase, arm + "-vs"), vsJobs))
        {
            g.Run();
            foreach (var o in g.Outcomes)
                results.Add((o.Name, arm, o, g.RenderedPixels.TryGetValue(o.Name, out var px) ? px : null));
        }
    }

    bool allRendered = true, allMatch = true;
    foreach (var name in names)
    {
        var row = results.Where(x => x.Name == name).ToDictionary(x => x.Arm);
        foreach (var arm in new[] { "reference", "managed", "golden" })
        {
            var o = row[arm].O;
            if (!(o.Loaded && o.Rendered)) { allRendered = false; Console.WriteLine($"  [FAIL] {name} {arm}: {o.Error}"); }
        }
        int dRef = MaxDelta(row["managed"].Px, row["reference"].Px);
        int dGold = MaxDelta(row["managed"].Px, row["golden"].Px);
        bool ok = dRef == 0 && dGold == 0;
        allMatch &= ok;
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name,-24} managed vs dxil.dll-signed maxd={dRef}, managed vs mgfxc golden maxd={dGold}");
    }

    Console.WriteLine($"\n[managed-dxil] bytes equal: {bytesOk}, loaded+rendered everywhere: {allRendered}, pixels identical: {allMatch}");
    if (!(bytesOk && allRendered && allMatch))
    {
        Console.WriteLine("[managed-dxil] FAIL");
        return 1;
    }

    // Negative control, in a fresh process: a D3D12 rejection may take the device down.
    Console.WriteLine("\n[managed-dxil] negative control: zero-hash container must be rejected by D3D12 ...");
    var psi = new ProcessStartInfo("dotnet", $"\"{Environment.GetCommandLineArgs()[0]}\" negative")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var child = Process.Start(psi)!;
    var stdout = child.StandardOutput.ReadToEndAsync();
    var stderr = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    string childOut = await stdout + await stderr;
    Console.WriteLine(childOut);
    if (child.ExitCode == InconclusiveExit)
    {
        Console.WriteLine("[managed-dxil] negative control INCONCLUSIVE: Windows Developer Mode is on, so D3D12 accepts any container hash. "
            + "The acceptance above does NOT prove the managed signer here; run on a box with Developer Mode off (CI does).");
        Console.WriteLine($"\n[managed-dxil] {(requireStrict ? "FAIL (SHADOWDUSK_REQUIRE_STRICT_D3D12=1)" : "PASS (bytes + renders only; strictness unproven on this box)")}");
        return requireStrict ? 1 : 0;
    }
    // Exit 0 = rejected cleanly. Exit 1 = it rendered (or the signed reference did not): the control is dead.
    // Anything else after the signed reference rendered = the bad container took the process down.
    bool refRendered = childOut.Contains("[negative] reference rendered", StringComparison.Ordinal);
    bool rejected = refRendered && child.ExitCode != 1
        && !childOut.Contains("wrong-hash container RENDERED", StringComparison.Ordinal);
    Console.WriteLine($"[managed-dxil] negative control exit code {child.ExitCode}: {(rejected ? "rejected as required" : "NOT REJECTED, the gate cannot tell a signed container from an unsigned one")}");
    Console.WriteLine($"\n[managed-dxil] {(rejected ? "PASS" : "FAIL")}");
    return rejected ? 0 : 1;
}

async Task<int> RunNegative()
{
    byte[] r = await CompileRef("Grayscale");
    byte[] zero = (byte[])r.Clone();
    int n = DxilContainers.ZeroHashes(zero);
    // A hash that is present but wrong: one bit flipped in the correct one.
    byte[] wrong = (byte[])r.Clone();
    DxilContainers.FlipHashBit(wrong);
    Console.WriteLine($"[negative] {n} DXBC container(s); variants: zero hash, one-bit-wrong hash");

    var jobs = new List<ShaderJob> { new("Grayscale", r, null), new("GrayscaleZero", zero, null), new("GrayscaleWrong", wrong, null) };
    var outcomes = new Dictionary<string, ShaderOutcome>();
    using (var g = new EffectImageRenderer(catPath, Path.Combine(outBase, "negative"), jobs, ShaderInputs.SetParams))
    {
        g.Run();
        foreach (var o in g.Outcomes) outcomes[o.Name] = o;
    }

    if (outcomes.TryGetValue("Grayscale", out var refO) && refO is { Loaded: true, Rendered: true })
        Console.WriteLine("[negative] reference rendered (dxil.dll-signed container accepted)");
    else
    {
        Console.WriteLine($"[negative] reference did NOT render: {(outcomes.TryGetValue("Grayscale", out var x) ? x.Error : "no outcome")}");
        return 1;
    }

    bool zeroRendered = outcomes.TryGetValue("GrayscaleZero", out var z) && z is { Loaded: true, Rendered: true };
    bool wrongRendered = outcomes.TryGetValue("GrayscaleWrong", out var w) && w is { Loaded: true, Rendered: true };
    Console.WriteLine($"[negative] zero-hash container: {(zeroRendered ? "RENDERED" : "rejected: " + (z?.Error ?? "no outcome"))}");
    Console.WriteLine($"[negative] one-bit-wrong-hash container: {(wrongRendered ? "RENDERED" : "rejected: " + (w?.Error ?? "no outcome"))}");
    if (zeroRendered || wrongRendered)
    {
        if (developerMode)
            return InconclusiveExit;
        Console.WriteLine("[negative] wrong-hash container RENDERED");
        return 1;
    }
    return 0;
}

static int MaxDelta(Color[]? a, Color[]? b)
{
    if (a is null || b is null || a.Length != b.Length) return int.MaxValue;
    int m = 0;
    for (int i = 0; i < a.Length; i++)
        m = Math.Max(m, Math.Max(Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].G - b[i].G)),
                                 Math.Max(Math.Abs(a[i].B - b[i].B), Math.Abs(a[i].A - b[i].A))));
    return m;
}

/// <summary>Finds DXBC containers inside an .mgfx and zeroes or signs their container hash in place.</summary>
static class DxilContainers
{
    public static int ZeroHashes(byte[] mgfx) => Walk(mgfx, off => { Array.Clear(mgfx, off + 4, 16); return true; });

    public static void FlipHashBit(byte[] mgfx) => Walk(mgfx, off => { mgfx[off + 4] ^= 1; return true; });

    public static int SignAll(byte[] mgfx) => Walk(mgfx, off =>
    {
        // SignInPlace takes the container alone: sign a copy of exactly its extent, write the hash back.
        int size = (int)BitConverter.ToUInt32(mgfx, off + 24);
        byte[] c = mgfx.AsSpan(off, size).ToArray();
        if (!DxilContainerHash.SignInPlace(c)) return false;
        Array.Copy(c, 4, mgfx, off + 4, 16);
        return true;
    });

    private static int Walk(byte[] b, Func<int, bool> act)
    {
        int count = 0;
        for (int i = 0; i + 32 <= b.Length; i++)
        {
            if (b[i] != 'D' || b[i + 1] != 'X' || b[i + 2] != 'B' || b[i + 3] != 'C') continue;
            uint size = BitConverter.ToUInt32(b, i + 24);
            uint parts = BitConverter.ToUInt32(b, i + 28);
            if (size < 32 || i + size > b.Length || parts == 0 || parts > 64) continue;
            if (act(i)) count++;
            i += (int)size - 1;
        }
        return count;
    }
}
