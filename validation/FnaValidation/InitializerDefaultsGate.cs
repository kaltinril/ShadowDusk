#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Validation.Fna;

/// <summary>
/// Owner directive 2026-10-28: every runtime must preserve a shader's initializer defaults.
/// This is the FNA proof (<c>dotnet run --project validation/FnaValidation -c Release -- defaults
/// [D3D11|OpenGL]</c>), run once per FNA3D driver because FNA allows one <see cref="Game"/> per
/// process and the driver is fixed at startup.
///
/// <para>For each fixture in <c>tests/fixtures/golden/FNA/defaults</c> it loads BOTH arms into a real
/// FNA <see cref="Effect"/>, then, WITHOUT ever calling a <c>SetValue</c>:</para>
/// <list type="number">
/// <item>reads every numeric parameter back through FNA's own getters (the values MojoShader parsed
/// out of the <c>.fxb</c>): the candidate's must equal the reference's exactly;</item>
/// <item>renders a 64x64 target through SpriteBatch with the effect: every pixel must be identical
/// across the arms, and the reference's centre pixel must match the value pinned from the reference
/// compiler within +-1 per channel (so two arms that are BOTH zero cannot pass; the +-1 absorbs
/// the device-dependent rounding of 0.5 to 127 or 128).</item>
/// </list>
/// The reference is Microsoft's fx_2_0 compiler (<see cref="ReferenceFx2Compiler"/>, byte-identical
/// to <c>fxc /T fx_2_0</c>), the oracle for what the FNA runtime should see.
///
/// <para><b>Positive controls</b> (must diverge, the gate fails if they do not): for the float2x2
/// matrix and the float4 array, the candidate is compiled from the same source with the initializer
/// removed, which is exactly the old bug's observable behaviour (zero defaults).
/// <see cref="ControlEnvVar"/>=1 additionally plants those stripped builds into the gated rows, so
/// the whole run must exit non-zero.</para>
/// </summary>
public static class InitializerDefaultsGate
{
    public const string ControlEnvVar = "SHADOWDUSK_FNA_DEFAULTS_CONTROL";

    private sealed record Fixture(string Name, int[] ExpectedCenter, string? StripInitializerOf = null);

    // Centre pixel (RGBA) of the unset render as fxc's build produces it in real FNA, pinned so a
    // gate whose arms both read zero cannot pass. DefaultsVec2Mat2 and DefaultsArray are the two
    // shapes the original bug lost (float2x2 and an array).
    private static readonly Fixture[] Fixtures =
    {
        new("DefaultsTint", new[] { 255, 0, 0, 255 }),
        new("DefaultsScale", new[] { 128, 128, 128, 255 }),
        new("DefaultsVec2Mat2", new[] { 32, 96, 0, 255 }, StripInitializerOf: "M"),
        new("DefaultsIntBool", new[] { 128, 255, 0, 255 }),
        new("DefaultsArray", new[] { 128, 128, 0, 255 }, StripInitializerOf: "Colors"),
        new("DefaultsMatrices", new[] { 61, 166, 186, 255 }, StripInitializerOf: "M3"),
        new("DefaultsArrays", new[] { 77, 107, 103, 255 }, StripInitializerOf: "MA2"),
        new("DefaultsNoInit", new[] { 0, 0, 0, 0 }),
    };

    private sealed record Row(
        string Name, bool Gate, byte[]? ReferenceBytes, string? ReferenceError,
        byte[]? CandidateBytes, string? CandidateError, int[] ExpectedCenter);

    private sealed record Arm(
        bool Loaded, string? Error, SortedDictionary<string, float[]> Values, Color[]? Pixels, string? Png);

    public static async Task<int> RunAsync(string driver, List<string> fna3dErrors, List<string> fna3dInfo)
    {
        string repoRoot = FnaShaderInputs.FindRepoRoot();
        string fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "FNA", "defaults");
        string outDir = Path.Combine(repoRoot, "validation", "output-fna-defaults", driver);
        Directory.CreateDirectory(outDir);
        bool plantControls = Environment.GetEnvironmentVariable(ControlEnvVar) == "1";

        Console.WriteLine($"[fna-defaults] driver   : {driver} (FNA3D_FORCE_DRIVER)");
        Console.WriteLine($"[fna-defaults] fixtures : {fixtureDir}");
        Console.WriteLine("[fna-defaults] reference: d3dcompiler_47 fx_2_0 (== fxc /T fx_2_0)");
        Console.WriteLine("[fna-defaults] candidate: ShadowDusk PlatformTarget.Fna");
        Console.WriteLine($"[fna-defaults] out      : {outDir}");
        if (plantControls)
            Console.WriteLine($"[fna-defaults] POSITIVE CONTROL ACTIVE ({ControlEnvVar}=1): this run is expected to FAIL");
        Console.WriteLine();

        var compiler = new EffectCompiler();
        var rows = new List<Row>();
        var goldenDrift = new Dictionary<string, string>();

        foreach (Fixture fixture in Fixtures)
        {
            string path = Path.Combine(fixtureDir, fixture.Name + ".fx");
            string source = await File.ReadAllTextAsync(path);

            string? parity = ReferenceFx2Compiler.CheckOpenGlParity(source);
            if (parity is not null)
                throw new InvalidOperationException($"{fixture.Name}: macro-parity-unsafe: {parity}");

            ReferenceFx2Compiler.ReferenceResult reference = ReferenceFx2Compiler.Compile(path, source);
            (byte[]? candBytes, string? candError) = await CompileCandidate(compiler, path, source);

            string? stripped = fixture.StripInitializerOf is null
                ? null
                : StripInitializer(source, fixture.StripInitializerOf);

            if (plantControls && stripped is not null)
                (candBytes, candError) = await CompileCandidate(compiler, path, stripped);

            // The committed golden (what the unit/integration tests compare against on every
            // OS) must be the live oracle's output for this exact source, or those tests
            // would be proving equality with a stale file.
            string goldenPath = Path.Combine(fixtureDir, fixture.Name + ".fxb");
            if (reference.Bytes is not null)
            {
                if (!File.Exists(goldenPath))
                    goldenDrift[fixture.Name] = "committed golden .fxb is missing";
                else if (!(await File.ReadAllBytesAsync(goldenPath)).AsSpan().SequenceEqual(reference.Bytes))
                    goldenDrift[fixture.Name] = "committed golden .fxb differs from the live fx_2_0 oracle output (regenerate it from this run's *.reference.fxb)";
            }

            rows.Add(new Row(fixture.Name, true, reference.Bytes, reference.Error, candBytes, candError, fixture.ExpectedCenter));

            if (stripped is not null)
            {
                (byte[]? ctlBytes, string? ctlError) = await CompileCandidate(compiler, path, stripped);
                rows.Add(new Row(fixture.Name + "~stripped", false, reference.Bytes, reference.Error, ctlBytes, ctlError, fixture.ExpectedCenter));
            }

            if (reference.Bytes is not null)
                await File.WriteAllBytesAsync(Path.Combine(outDir, fixture.Name + ".reference.fxb"), reference.Bytes);
            if (candBytes is not null)
                await File.WriteAllBytesAsync(Path.Combine(outDir, fixture.Name + ".candidate.fxb"), candBytes);
        }

        Dictionary<string, (Arm Reference, Arm Candidate)> arms;
        using (var game = new DefaultsGame(rows, outDir, fna3dErrors))
        {
            game.Run();
            arms = game.Results;
        }

        Console.WriteLine($"{"fixture",-26} {"gate",-5} {"params=",-8} {"pixels=",-8} {"centre (cand)",-18} {"expected",-18} verdict");
        Console.WriteLine(new string('-', 118));

        int gatePass = 0, gateTotal = 0, caught = 0, controls = 0;
        foreach (Row row in rows)
        {
            (Arm reference, Arm candidate) = arms[row.Name];
            var problems = new List<string>();

            if (goldenDrift.TryGetValue(row.Name, out string? drift)) problems.Add(drift);
            if (!reference.Loaded) problems.Add("reference did not load/render: " + reference.Error);
            if (!candidate.Loaded) problems.Add("candidate did not load/render: " + candidate.Error);

            bool paramsEqual = false, pixelsEqual = false;
            int[]? centre = null;
            if (reference.Loaded && candidate.Loaded)
            {
                paramsEqual = ParamsEqual(reference.Values, candidate.Values, problems);
                if (reference.Values.Count == 0)
                {
                    // Two empty read-backs are "equal"; a fixture whose parameters were never
                    // read proves nothing.
                    problems.Add("no numeric parameter could be read back from the reference effect");
                    paramsEqual = false;
                }
                pixelsEqual = reference.Pixels!.SequenceEqual(candidate.Pixels!);
                if (!pixelsEqual) problems.Add("rendered pixels differ");
                centre = Centre(candidate.Pixels!);
                // Sanity check of the oracle only (the strict reference == candidate comparison is
                // pixelsEqual above, exact). Several fixtures put 0.5 in a channel, i.e. 127.5/255,
                // which the GPU/driver rounds to 127 or 128 depending on the device, so the pin is
                // matched within +-1 per channel, as the InitializerDefaults* drivers do
                // (validation/SharedInitializerDefaults/InitializerDefaultsProbe.cs). Anything
                // further off means the oracle really moved.
                int[] refCentre = Centre(reference.Pixels!);
                if (!WithinPinTolerance(refCentre, row.ExpectedCenter))
                    problems.Add($"REFERENCE centre {Fmt(refCentre)} != pinned {Fmt(row.ExpectedCenter)} +-{PinTolerance} (the oracle moved: re-pin deliberately)");
            }

            if (Environment.GetEnvironmentVariable("SHADOWDUSK_FNA_DEFAULTS_VERBOSE") == "1")
            {
                foreach ((string pn, float[] pv) in reference.Values)
                    Console.WriteLine($"    ref  {row.Name}.{pn} = [{string.Join(",", pv.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}]");
                foreach ((string pn, float[] pv) in candidate.Values)
                    Console.WriteLine($"    cand {row.Name}.{pn} = [{string.Join(",", pv.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}]");
            }

            string verdict;
            if (row.Gate)
            {
                gateTotal++;
                verdict = problems.Count == 0 ? "PASS" : "FAIL";
                if (verdict == "PASS") gatePass++;
            }
            else
            {
                controls++;
                bool diverged = reference.Loaded && candidate.Loaded && !paramsEqual && !pixelsEqual;
                verdict = diverged ? "CAUGHT (control diverged, as required)" : "FAIL (control NOT detected)";
                if (diverged) caught++;
            }

            Console.WriteLine(
                $"{row.Name,-26} {(row.Gate ? "GATE" : "ctl"),-5} {(paramsEqual ? "yes" : "NO"),-8} {(pixelsEqual ? "yes" : "NO"),-8} " +
                $"{(centre is null ? "-" : Fmt(centre)),-18} {Fmt(row.ExpectedCenter),-18} {verdict}");
            if (row.Gate)
                foreach (string p in problems)
                    Console.WriteLine($"{"",-26}  - {p}");
            else if (verdict.StartsWith("FAIL", StringComparison.Ordinal))
                Console.WriteLine($"{"",-26}  - the stripped-initializer control matched the reference: this gate cannot see lost defaults");
        }

        Console.WriteLine(new string('-', 118));

        int expectedControls = Fixtures.Count(f => f.StripInitializerOf is not null);
        bool driverConfirmed = fna3dInfo.Any(l => l.Trim().Equals("FNA3D Driver: " + driver, StringComparison.OrdinalIgnoreCase));
        if (!driverConfirmed)
            Console.WriteLine($"[fna-defaults] DRIVER NOT CONFIRMED: FNA3D never logged 'FNA3D Driver: {driver}' (saw: {string.Join(" | ", fna3dInfo.Where(l => l.Contains("Driver", StringComparison.Ordinal)))}); this run did not exercise the requested driver.");

        bool ok = driverConfirmed && gatePass == gateTotal && gateTotal == Fixtures.Length && controls == expectedControls && caught == controls;
        Console.WriteLine($"\n[fna-defaults] GATE ({driver}): {gatePass}/{gateTotal} fixtures PASS vs fxc /T fx_2_0 in real FNA " +
                          "(every numeric parameter read back before any Set, and the unset render, identical to the reference; centre pinned).");
        Console.WriteLine($"[fna-defaults] positive controls: {caught}/{controls} diverged from the reference as required.");
        Console.WriteLine(ok ? "[fna-defaults] PASSED" : "[fna-defaults] FAILED");
        Console.WriteLine($"[fna-defaults] PNGs: {outDir}");
        return ok ? 0 : 1;
    }

    /// <summary>Per-channel tolerance of the reference-centre vs pin sanity check only (0.5 rounds
    /// to 127 or 128 by device). Never applied to the reference vs candidate comparison.</summary>
    private const int PinTolerance = 1;

    private static bool WithinPinTolerance(int[] actual, int[] pinned) =>
        actual.Length == pinned.Length
        && actual.Zip(pinned).All(p => Math.Abs(p.First - p.Second) <= PinTolerance);

    private static async Task<(byte[]? Bytes, string? Error)> CompileCandidate(EffectCompiler compiler, string path, string source)
    {
        var result = await compiler.CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.Fna,
            SourceFileName = path,
            IncludeResolver = new FileSystemIncludeResolver(),
        });
        return result.IsFailure
            ? (null, string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")))
            : (result.Value.Data, null);
    }

    /// <summary>Removes the initializer of the global <paramref name="name"/> (the old bug's
    /// observable effect: the parameter exists but its default is zero).</summary>
    internal static string StripInitializer(string source, string name)
    {
        var declaration = new Regex(@"^(\s*(?:row_major\s+)?\w+\s+" + Regex.Escape(name) + @"(?:\[\d+\])?)\s*=.*;\s*$", RegexOptions.Multiline);
        string stripped = declaration.Replace(source, "$1;");
        if (stripped == source)
            throw new InvalidOperationException($"no initializer for '{name}' found to strip");
        return stripped;
    }

    private static bool ParamsEqual(
        SortedDictionary<string, float[]> reference, SortedDictionary<string, float[]> candidate, List<string> problems)
    {
        bool equal = true;
        foreach (string name in reference.Keys.Union(candidate.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!reference.TryGetValue(name, out float[]? r) || !candidate.TryGetValue(name, out float[]? c))
            {
                problems.Add($"parameter '{name}' exists on only one arm (reference {reference.ContainsKey(name)}, candidate {candidate.ContainsKey(name)})");
                equal = false;
                continue;
            }

            if (!r.SequenceEqual(c))
            {
                problems.Add($"parameter '{name}' reads [{string.Join(",", c)}] but the reference reads [{string.Join(",", r)}]");
                equal = false;
            }
        }
        return equal;
    }

    private static int[] Centre(Color[] pixels)
    {
        Color c = pixels[32 * 64 + 32];
        return new[] { (int)c.R, c.G, c.B, c.A };
    }

    private static string Fmt(int[] c) => $"{c[0]},{c[1]},{c[2]},{c[3]}";

    private sealed class DefaultsGame : Game
    {
        private readonly IReadOnlyList<Row> _rows;
        private readonly string _outDir;
        private readonly List<string> _fna3dErrors;
        private bool _done;

        public Dictionary<string, (Arm Reference, Arm Candidate)> Results { get; } = new();

        public DefaultsGame(IReadOnlyList<Row> rows, string outDir, List<string> fna3dErrors)
        {
            _rows = rows;
            _outDir = outDir;
            _fna3dErrors = fna3dErrors;
            _ = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = 64,
                PreferredBackBufferHeight = 64,
                GraphicsProfile = GraphicsProfile.HiDef,
            };
        }

        protected override void Draw(GameTime gameTime)
        {
            if (_done)
            {
                Exit();
                return;
            }

            _done = true;
            try
            {
                RunAll();
            }
            finally
            {
                Exit();
            }
        }

        private void RunAll()
        {
            var spriteBatch = new SpriteBatch(GraphicsDevice);
            var white = new Texture2D(GraphicsDevice, 1, 1);
            white.SetData(new[] { Color.White });
            using var target = new RenderTarget2D(GraphicsDevice, 64, 64, false, SurfaceFormat.Color, DepthFormat.None);

            foreach (Row row in _rows)
            {
                Arm reference = RunArm(row.Name, "reference", row.ReferenceBytes, row.ReferenceError, spriteBatch, white, target);
                Arm candidate = RunArm(row.Name, "candidate", row.CandidateBytes, row.CandidateError, spriteBatch, white, target);
                Results[row.Name] = (reference, candidate);
            }
        }

        private Arm RunArm(string name, string arm, byte[]? bytes, string? compileError, SpriteBatch spriteBatch, Texture2D white, RenderTarget2D target)
        {
            var values = new SortedDictionary<string, float[]>(StringComparer.Ordinal);
            if (bytes is null)
                return new Arm(false, "compile failed: " + compileError, values, null, null);

            _fna3dErrors.Clear();
            Effect effect;
            try
            {
                effect = new Effect(GraphicsDevice, bytes);
            }
            catch (Exception ex)
            {
                string detail = _fna3dErrors.Count > 0 ? " | FNA3D: " + string.Join(" | ", _fna3dErrors) : "";
                return new Arm(false, $"{ex.GetType().Name}: {ex.Message}{detail}", values, null, null);
            }

            try
            {
                // Read every numeric parameter BEFORE anything is set: these are the bytes
                // MojoShader parsed out of the .fxb, i.e. the defaults the runtime will apply.
                foreach (EffectParameter p in effect.Parameters)
                {
                    float[]? v = ReadValues(p);
                    if (v is not null)
                        values[p.Name] = v;
                }

                Color[] pixels = Render(spriteBatch, white, target, effect);
                string png = Path.Combine(_outDir, $"{name}.{arm}.png");
                using (FileStream fs = File.Create(png))
                    target.SaveAsPng(fs, 64, 64);

                string? errors = _fna3dErrors.Count > 0 ? "FNA3D: " + string.Join(" | ", _fna3dErrors) : null;
                return new Arm(errors is null, errors, values, pixels, png);
            }
            catch (Exception ex)
            {
                return new Arm(false, $"{ex.GetType().Name}: {ex.Message}", values, null, null);
            }
            finally
            {
                effect.Dispose();
            }
        }

        private static float[]? ReadValues(EffectParameter p)
        {
            if (p.ParameterClass is not (EffectParameterClass.Scalar or EffectParameterClass.Vector or EffectParameterClass.Matrix))
                return null;

            int count = Math.Max(1, p.Elements.Count) * p.RowCount * p.ColumnCount;
            return p.ParameterType switch
            {
                EffectParameterType.Single => p.GetValueSingleArray(count),
                EffectParameterType.Int32 => p.GetValueInt32Array(count).Select(i => (float)i).ToArray(),
                EffectParameterType.Bool => p.GetValueBooleanArray(count).Select(b => b ? 1f : 0f).ToArray(),
                _ => null,
            };
        }

        private Color[] Render(SpriteBatch spriteBatch, Texture2D white, RenderTarget2D target, Effect effect)
        {
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(new Color(255, 0, 255, 255));
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
                DepthStencilState.None, RasterizerState.CullNone, effect);
            spriteBatch.Draw(white, new Rectangle(0, 0, 64, 64), Color.White);
            spriteBatch.End();
            GraphicsDevice.SetRenderTarget(null);

            var pixels = new Color[64 * 64];
            target.GetData(pixels);
            return pixels;
        }
    }
}
