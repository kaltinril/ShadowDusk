// Phase 44 D - KNI v4.02 DIRECTX (WinForms.DX11) load + render validation.
//
// Compiles the SM5 PS-only corpus with ShadowDusk's UNCHANGED EffectCompiler
// (DirectX target -> DXBC SM5 in an MGFX v10 container), then loads BOTH those bytes
// (candidate) AND the committed mgfxc DirectX goldens (tests/fixtures/golden/
// DirectX_11/*.mgfx) into a REAL KNI (nkast) DX11 Effect on the WinForms.DX11 desktop
// backend, renders the standard cat-corpus scene for each through the IDENTICAL
// SpriteBatch path, and pixel-compares the two arms in process.
//
// This is the DirectX analogue of validation/KniDesktopGL (which proved the GL cell):
// it closes the matrix's KNI DirectX cell by showing ShadowDusk's shipping DX output
// loads + renders pixel-equivalent to mgfxc's DX output on a CURRENT KNI runtime
// (v4.2.9001), same backend (DX11), same scene.
//
//   dotnet run -c Release --project validation/KniWinFormsDX   (needs a real DX11 desktop)
//
// Exit 0 iff every row passes: candidate loads, golden loads (the control), and the
// two renders differ by at most --tolerance (default 4, the Phase 18 DX bar) per channel.
//
// Row "HalfGlobals" (driver-local, not in the shared DX corpus list): XNA-era `half` uniforms
// (tests/fixtures/shaders/HalfGlobals.fx), which mgfxc's DirectX_11 build compiles as float and
// ShadowDusk's DirectX target refused with vkd3d's E5002 until it passed ALLOW_HALF_GLOBALS.
// MGFX container on purpose: KNIFX-on-DirectX at HiDef is a separate open issue. Its
// ColorMatrix/Weights default blobs are the documented #422 deviation from mgfxc, so both
// arms get the same explicit values through the typed SetValue overloads (outside the
// swallowed shared SetParams call, so a mistyped parameter fails the row).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Validation.Dx;
// UseWindowsForms pulls in System.Drawing via implicit usings; pin Color to XNA's.
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int tolerance = 4;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--tolerance" && int.TryParse(args[i + 1], out int t))
                tolerance = t;

        // ---- Runtime-integrity guard ------------------------------------------------
        // Prove we are actually running on KNI, not MonoGame. KNI ships its Game type in
        // an assembly named "Xna.Framework.Game"; MonoGame's is "MonoGame.Framework". If a
        // stray MonoGame assembly ever shadowed KNI's, this harness would silently validate
        // the wrong runtime; fail loudly instead. KNI v4.02 stamps version 4.2.9001.x.
        AssemblyName xna = typeof(Game).Assembly.GetName();
        Console.WriteLine($"[kni-dx] XNA implementation: {xna.Name} {xna.Version}");
        bool isKni = xna.Name is not null
            && xna.Name.StartsWith("Xna.Framework", StringComparison.OrdinalIgnoreCase)
            && !xna.Name.StartsWith("MonoGame", StringComparison.OrdinalIgnoreCase);
        if (!isKni)
        {
            Console.WriteLine($"[kni-dx] verdict: FAIL - expected the KNI (nkast 'Xna.Framework.*') runtime, " +
                              $"got '{xna.Name}'. This harness must render on KNI DX11; aborting so we cannot " +
                              "mislabel a MonoGame render as KNI.");
            return 2;
        }

        string repoRoot  = DxShaderInputs.FindRepoRoot();
        string shaderDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        string goldenDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11");
        string catPath   = DxShaderInputs.CatPath(repoRoot);
        string outDir    = Path.Combine(repoRoot, "validation", "output-dx", "kni");

        Console.WriteLine($"[kni-dx] cat: {catPath}");
        Console.WriteLine($"[kni-dx] shaders: {shaderDir}");
        Console.WriteLine($"[kni-dx] goldens: {goldenDir}");
        Console.WriteLine($"[kni-dx] out: {outDir}  tolerance: {tolerance}\n");

        // ---- Compile every candidate with ShadowDusk (DirectX, synchronously). ----
        // Phase 42 sync Compile(): on desktop no InitializeAsync warm-up is needed, so the
        // STA Main stays synchronous (no async-over-[STAThread] dance).
        var compiler = new EffectCompiler();
        var rows = new List<DxRow>();
        foreach (string name in DxShaderInputs.ShaderNames.Append("HalfGlobals"))
        {
            string fxPath = Path.Combine(shaderDir, name + ".fx");
            string? compileError = null;
            byte[]? candidate = null;
            if (!File.Exists(fxPath))
            {
                compileError = $".fx not found: {fxPath}";
            }
            else
            {
                string src = File.ReadAllText(fxPath);
                var result = compiler.Compile(src, new CompilerOptions
                {
                    Target = PlatformTarget.DirectX,
                    IncludeResolver = new FileSystemIncludeResolver(),
                    SourceFileName = fxPath,
                });
                if (result.IsSuccess) candidate = result.Value.Data;
                else compileError = string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"));
            }

            string goldenPath = Path.Combine(goldenDir, name + ".mgfx");
            byte[]? golden = File.Exists(goldenPath) ? File.ReadAllBytes(goldenPath) : null;
            if (golden is null)
                compileError = (compileError is null ? "" : compileError + " | ") +
                               $"golden missing: {goldenPath}";

            rows.Add(new DxRow(name, candidate, golden, compileError));
        }

        Console.WriteLine("[kni-dx] compile results:");
        foreach (var r in rows)
            Console.WriteLine($"  [{(r.CandidateBytes is null ? "FAIL" : "OK  ")}] {r.Name,-12} " +
                              $"{(r.CompileError ?? $"{r.CandidateBytes!.Length} bytes")}");
        Console.WriteLine();

        // ---- Texture-slot arm: legacy sampler textures on fxc's t slots ----------------
        // tests/fixtures/golden/dx11-texture-slots/: fxc gives a legacy sampler's texture the
        // slot of its first read (ahead of unregistered modern textures, around explicit texture
        // registers); ShadowDusk used to number the SM4 rewrite in declaration order, and in the
        // BloomCombine shape SpriteBatch's scene (t0) reached the wrong sampler (maxd 188 here).
        // Each shape is compiled with BOTH DXBC backends (vkd3d and the d3dcompiler_47 option) into
        // BOTH containers KNI loads (MGFX v10 and KNIFX), drawn through
        // SpriteBatch with the sprite on t0 and the other textures (all distinct) set through their
        // parameters, and must match the mgfxc 3.8.4.1 golden exactly; the positive control redraws
        // the golden with the sprite's texture and one parameter's exchanged. These rows run on a
        // second device at GraphicsProfile.FL10_0: KNI 4.2.9001 refuses a KNIFX DirectX effect
        // recording shader model 4.0 on HiDef ("Shader model 4.0 is not supported by the current
        // graphics profile 'HiDef'"; PR #430 records `_level_9_x` as 2.0/3.0, as KNI does).
        string slotsDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "dx11-texture-slots");
        var slotRows = new List<SlotRow>();
        foreach ((string shape, Dictionary<string, string> parameters, string? control) in SlotRow.Cases)
        {
            string src = File.ReadAllText(Path.Combine(slotsDir, shape + ".fx"));
            byte[] golden = File.ReadAllBytes(Path.Combine(slotsDir, shape + ".DirectX_11.mgfx"));
            foreach (EffectContainer container in new[] { EffectContainer.Mgfx, EffectContainer.Knifx })
            foreach (DxbcBackend backend in new[] { DxbcBackend.Vkd3d, DxbcBackend.D3DCompiler })
            {
                var result = compiler.Compile(src, new CompilerOptions
                {
                    Target = PlatformTarget.DirectX,
                    Container = container,
                    DxbcBackend = backend,
                    SourceFileName = shape + ".fx",
                });
                slotRows.Add(new SlotRow($"{shape} ({container}, {backend})", result.IsSuccess ? result.Value.Data : null,
                    result.IsSuccess ? null : string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")),
                    golden, parameters, control, container, src));
            }
        }

        var outcomes = new List<DxOutcome>();
        using (var game = new KniDxCompareGame(catPath, outDir, rows, tolerance, [], GraphicsProfile.HiDef))
        {
            game.Run();
            outcomes.AddRange(game.Outcomes);
        }
        using (var slotGame = new KniDxCompareGame(catPath, outDir, [], tolerance, slotRows, GraphicsProfile.FL10_0))
        {
            slotGame.Run();
            outcomes.AddRange(slotGame.Outcomes);
        }

        int ok = 0;
        Console.WriteLine("\n[kni-dx] load + render results (real KNI WinForms.DX11):");
        foreach (var o in outcomes)
        {
            if (o.Pass) ok++;
            Console.WriteLine($"  [{(o.Pass ? "PASS" : "FAIL")}] {o.Name,-12} {o.Detail}");
        }
        Console.WriteLine($"\n[kni-dx] {ok}/{outcomes.Count} loaded + rendered + matched mgfxc in real KNI v{xna.Version}.");
        return ok == outcomes.Count && outcomes.Count == rows.Count + slotRows.Count ? 0 : 1;
    }
}

internal sealed record DxRow(string Name, byte[]? CandidateBytes, byte[]? GoldenBytes, string? CompileError);

internal sealed record DxOutcome(string Name, bool Pass, string Detail);

/// <summary>One texture-slot row: a shape compiled into one container, its golden, the parameter textures it sets.</summary>
internal sealed record SlotRow(
    string Name, byte[]? CandidateBytes, string? CompileError, byte[] GoldenBytes,
    Dictionary<string, string> Parameters, string? ControlParameter, EffectContainer Container, string Source)
{
    public const string Red = "red", Green = "green", Blue = "blue", White = "white";

    /// <summary>
    /// The texture slot of each texture-less legacy sampler (<c>sampler2D A;</c>), read off the
    /// mgfxc golden: mgfxc gives such a sampler a texture parameter named after the SAMPLER (a
    /// name the source declares as a sampler, not as a texture; mgfxc's DirectX_11 records carry
    /// no name), KNIFX gives it none and a -1 record, as KNI's own pipeline does, so a KNIFX row
    /// binds that texture through GraphicsDevice.Textures[slot] instead, exactly where mgfxc's
    /// build reads it.
    /// </summary>
    public IReadOnlyDictionary<string, int> TexturelessSamplerSlots { get; } = TexturelessSlots(GoldenBytes, Source);

    /// <summary>The golden's sampler records as `'record-name' -> parameter @tN`, for a failure message.</summary>
    public string GoldenRecords
    {
        get
        {
            var reader = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(GoldenBytes);
            return string.Join(", ", reader.Samplers.Select(r =>
                $"'{r.Name}' -> {(r.Parameter < reader.Parameters.Count ? reader.Parameters[r.Parameter].Name : "?")} @t{r.TextureSlot}"));
        }
    }

    private static Dictionary<string, int> TexturelessSlots(byte[] golden, string source)
    {
        var reader = ShadowDusk.Integration.Tests.MgfxBlobReader.Parse(golden);
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in reader.Samplers)
        {
            if (record.Parameter >= reader.Parameters.Count)
                continue;
            string parameter = reader.Parameters[record.Parameter].Name;
            bool declaredAsSampler = System.Text.RegularExpressions.Regex.IsMatch(source,
                $@"\bsampler(?:1D|2D|3D|CUBE)?\s+{System.Text.RegularExpressions.Regex.Escape(parameter)}\b");
            if (declaredAsSampler)
                slots[parameter] = record.TextureSlot;
        }
        return slots;
    }

    /// <summary>The same shapes as validation/DxModernFeatures' "texture-slots" arm.</summary>
    public static readonly (string Shape, Dictionary<string, string> Parameters, string? Control)[] Cases =
    {
        ("BloomCombineUndeclaredTexture", new() { ["Tex2"] = Green }, "Tex2"),
        ("DeclaredTextureAboveSpriteSampler", new() { ["Tex2"] = Green }, "Tex2"),
        ("ModernReadFirstStillAfterLegacy", new() { ["A"] = Green, ["T"] = Blue }, "A"),
        ("ModernExplicitT1BetweenLegacy", new() { ["A"] = Green, ["T"] = Blue }, "A"),
        // Explicit registers on sampler_state textures only reserve (mgfxc: t2/t3).
        ("ExplicitTextureRegistersReversed", new() { ["Diffuse"] = Green, ["Spec"] = Blue }, "Spec"),
        ("ExplicitStateTextureT0BesideSpriteSampler", new() { ["Tex2"] = Green }, "Tex2"),
        // Evaluation-order shapes d3dcompiler_47 would schedule differently.
        ("S10_ClipFirst", new() { ["B"] = Green }, "B"),
        ("D14_PrecedenceMulLeft", new() { ["B"] = White, ["C"] = Blue }, "C"),
        // vkd3d swaps the operands of '>' and '<=' in its instruction stream.
        ("ZZ_GreaterThanReview", new() { ["B"] = Green }, "B"),
        ("G04_Le", new() { ["SB"] = Blue }, "SB"),
    };
}

/// <summary>
/// One real KNI WinForms.DX11 device: loads each row's candidate (ShadowDusk) and golden
/// (mgfxc) DX bytes into real <see cref="Effect"/>s, renders both through the identical
/// SpriteBatch path, and pixel-compares the two arms in memory. Mirrors Phase 43's
/// StateFidelityGame, on the DX backend under KNI. Headless note: WinForms.DX11 hosts the
/// swap chain in a Win32/WinForms window; we render a single frame to an offscreen
/// RenderTarget2D then Exit, so the window is created but never meaningfully shown.
/// </summary>
internal sealed class KniDxCompareGame : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _catPath;
    private readonly string _outDir;
    private readonly IReadOnlyList<DxRow> _rows;
    private readonly int _tolerance;
    private readonly IReadOnlyList<SlotRow> _slotRows;
    private SpriteBatch _sb = null!;
    private Texture2D _cat = null!;
    private bool _done;

    public List<DxOutcome> Outcomes { get; } = new();

    public KniDxCompareGame(string catPath, string outDir, IReadOnlyList<DxRow> rows, int tolerance, IReadOnlyList<SlotRow> slotRows, GraphicsProfile profile)
    {
        _catPath = catPath;
        _outDir = outDir;
        _rows = rows;
        _tolerance = tolerance;
        _slotRows = slotRows;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 64,
            PreferredBackBufferHeight = 64,
            GraphicsProfile = profile,
        };
        // Issue #204: KNI carries the WARP knob on PresentationParameters (not a static
        // like MonoGame's GraphicsAdapter.UseDriverType, see SharedDx/DxHeadlessRasterizer.cs),
        // so it needs the standard XNA PreparingDeviceSettings hook instead. Same env var
        // name as that helper, so `run-windows-render-gates.ps1` can pin every DX gate with
        // one setting; opt-in so a local `dotnet run` still renders on the real GPU.
        if (Environment.GetEnvironmentVariable("SHADOWDUSK_DX_WARP") == "1")
        {
            _gdm.PreparingDeviceSettings += (_, e) =>
                e.GraphicsDeviceInformation.PresentationParameters.UseDriverType =
                    PresentationParameters.DriverType.FastSoftware;
        }
        Window.Title = "ShadowDusk Phase 44 KNI DX11 validation (headless)";
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        using var fs = File.OpenRead(_catPath);
        _cat = Texture2D.FromStream(GraphicsDevice, fs);
        Directory.CreateDirectory(_outDir);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_done) { Exit(); return; }
        GraphicsDevice.Clear(Color.Black);

        foreach (DxRow row in _rows)
            Outcomes.Add(RunRow(row));

        if (_slotRows.Count > 0)
        {
            Texture2D Solid(Color c)
            {
                var t = new Texture2D(GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
                t.SetData(new[] { c });
                return t;
            }
            using var red = Solid(new Color(200, 0, 0));
            using var green = Solid(new Color(0, 200, 0));
            using var blue = Solid(new Color(0, 0, 200));
            using var white = Solid(new Color(255, 255, 255));
            foreach (SlotRow row in _slotRows)
                Outcomes.Add(RunSlotRow(row, red, green, blue, white));
        }

        _done = true;
        Exit();
    }

    private DxOutcome RunSlotRow(SlotRow row, Texture2D red, Texture2D green, Texture2D blue, Texture2D white)
    {
        if (row.CandidateBytes is null)
            return new DxOutcome(row.Name, false, $"compile failure: {row.CompileError}");
        Texture2D Named(string n) => n switch { SlotRow.Green => green, SlotRow.Blue => blue, SlotRow.White => white, _ => red };
        var parameters = row.Parameters.ToDictionary(p => p.Key, p => Named(p.Value));

        // The KNIFX candidate has no parameter for a texture-less legacy sampler (as in KNI's own
        // build): such a texture goes straight to the device slot mgfxc's build reads it from.
        Color[]? Render(byte[] bytes, bool knifx, Texture2D sprite, IReadOnlyDictionary<string, Texture2D> values, out string? error)
        {
            error = null;
            const int w = 32, h = 32;
            try
            {
                using var effect = new Effect(GraphicsDevice, bytes);
                var deviceSlots = new List<(int Slot, Texture2D Texture)>();
                foreach ((string name, Texture2D tex) in values)
                {
                    EffectParameter? p = effect.Parameters[name];
                    if (p is not null)
                        p.SetValue(tex);
                    else if (knifx && row.TexturelessSamplerSlots.TryGetValue(name, out int slot))
                        deviceSlots.Add((slot, tex));
                    else
                        throw new InvalidOperationException($"no parameter '{name}' (golden records: {row.GoldenRecords})");
                }
                using var rt = new RenderTarget2D(GraphicsDevice, w, h, false, SurfaceFormat.Color, DepthFormat.None);
                GraphicsDevice.SetRenderTarget(rt);
                GraphicsDevice.Clear(Color.Black);
                foreach ((int slot, Texture2D tex) in deviceSlots)
                    GraphicsDevice.Textures[slot] = tex;
                _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp, null, null, effect);
                _sb.Draw(sprite, new Rectangle(0, 0, w, h), Color.White);
                _sb.End();
                GraphicsDevice.SetRenderTarget(null);
                var px = new Color[w * h];
                rt.GetData(px);
                return px;
            }
            catch (Exception ex)
            {
                try { _sb.End(); } catch { /* may not be in a batch */ }
                try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
                error = $"{ex.GetType().Name}: {ex.Message}";
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

        bool knifxCandidate = row.Container == EffectContainer.Knifx;
        Color[]? candidate = Render(row.CandidateBytes, knifxCandidate, red, parameters, out string? candErr);
        if (candidate is null)
            return new DxOutcome(row.Name, false, $"candidate failed: {candErr}");
        Color[]? golden = Render(row.GoldenBytes, knifx: false, red, parameters, out string? goldErr);
        if (golden is null)
            return new DxOutcome(row.Name, false, $"GOLDEN failed (control failure): {goldErr}");
        int maxDelta = MaxD(candidate, golden);

        string controlText = "n/a";
        bool controlOk = true;
        if (row.ControlParameter is { } swap)
        {
            var swapped = new Dictionary<string, Texture2D>(parameters) { [swap] = red };
            Color[]? goldenSwapped = Render(row.GoldenBytes, knifx: false, parameters[swap], swapped, out string? swapErr);
            if (goldenSwapped is null)
                return new DxOutcome(row.Name, false, $"positive control failed: {swapErr}");
            int controlDelta = MaxD(golden, goldenSwapped);
            controlOk = controlDelta > 16;
            controlText = controlDelta.ToString();
        }
        Color k = candidate[16 * 32 + 16], g = golden[16 * 32 + 16];
        return new DxOutcome(row.Name, maxDelta == 0 && controlOk,
            $"texture slots: maxDelta={maxDelta} (tolerance 0) candidate ({k.R},{k.G},{k.B}) mgfxc ({g.R},{g.G},{g.B}); " +
            $"positive control (sprite and '{row.ControlParameter ?? "-"}' swapped) delta {controlText}");
    }

    private DxOutcome RunRow(DxRow row)
    {
        if (row.CandidateBytes is null || row.GoldenBytes is null)
            return new DxOutcome(row.Name, false, $"compile/golden failure: {row.CompileError}");

        // THE load bar: a real KNI DX11 Effect must accept ShadowDusk's bytes.
        Effect candidate;
        try { candidate = new Effect(GraphicsDevice, row.CandidateBytes); }
        catch (Exception ex)
        {
            return new DxOutcome(row.Name, false, $"candidate new Effect() threw: {ex.GetType().Name}: {ex.Message}");
        }

        Effect golden;
        try { golden = new Effect(GraphicsDevice, row.GoldenBytes); }
        catch (Exception ex)
        {
            candidate.Dispose();
            return new DxOutcome(row.Name, false, $"GOLDEN new Effect() threw (control failure): {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            Color[]? candPixels = RenderArm(candidate, row.Name + ".candidate", out string? candErr);
            if (candPixels is null)
                return new DxOutcome(row.Name, false, $"candidate render failed: {candErr}");
            Color[]? goldPixels = RenderArm(golden, row.Name + ".golden", out string? goldErr);
            if (goldPixels is null)
                return new DxOutcome(row.Name, false, $"golden render failed: {goldErr}");

            int maxDelta = 0, diffCount = 0;
            for (int i = 0; i < candPixels.Length; i++)
            {
                int d = Math.Max(
                    Math.Max(Math.Abs(candPixels[i].R - goldPixels[i].R),
                             Math.Abs(candPixels[i].G - goldPixels[i].G)),
                    Math.Max(Math.Abs(candPixels[i].B - goldPixels[i].B),
                             Math.Abs(candPixels[i].A - goldPixels[i].A)));
                if (d > 0) diffCount++;
                if (d > maxDelta) maxDelta = d;
            }

            bool pass = maxDelta <= _tolerance;
            return new DxOutcome(row.Name, pass,
                $"loaded both arms; diffPixels={diffCount} maxDelta={maxDelta} (tolerance {_tolerance})");
        }
        finally
        {
            candidate.Dispose();
            golden.Dispose();
        }
    }

    private Color[]? RenderArm(Effect effect, string pngStem, out string? error)
    {
        error = null;
        int w = _cat.Width, h = _cat.Height;
        using var rt = new RenderTarget2D(GraphicsDevice, w, h, false,
            SurfaceFormat.Color, DepthFormat.None);
        try
        {
            var dest = new Rectangle(0, 0, w, h);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Color.Transparent);

            // Prime SpriteBatch's sprite vertex shader (PS-only effects need a VS).
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            try { DxShaderInputs.SetParams(effect, _cat); }
            catch { /* a missing/typed param on one side must not abort the row */ }

            // HalfGlobals row: the #422 default deviation, neutralized identically on both arms.
            // NOT swallowed: a half global that is not a Single Matrix / array parameter throws here.
            effect.Parameters["ColorMatrix"]?.SetValue(new Matrix(
                0.75f, 0.25f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0.5f, 0f, 0.5f, 0f,
                0f, 0f, 0f, 1f));
            effect.Parameters["Weights"]?.SetValue(new[] { 0.25f, 0.5f });

            _sb.Begin(SpriteSortMode.Immediate, BlendState.Opaque,
                SamplerState.LinearClamp, null, null, effect);
            _sb.Draw(_cat, dest, Color.White);
            _sb.End();

            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[w * h];
            rt.GetData(pixels);

            string png = Path.Combine(_outDir, pngStem + ".png");
            using (var outFs = File.Create(png))
                rt.SaveAsPng(outFs, w, h);

            return pixels;
        }
        catch (Exception ex)
        {
            try { _sb.End(); } catch { /* may not be in a batch */ }
            try { GraphicsDevice.SetRenderTarget(null); } catch { /* ignore */ }
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
