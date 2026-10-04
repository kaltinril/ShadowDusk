using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.Content.Res;
using Android.Util;
using ShadowDusk.Probes;

namespace ShadowDusk.Validation.AndroidGl;

/// <summary>
/// Issue #304 follow-up: the OpenGL fixture corpus compiled ON the device, checked stage by stage
/// against the desktop. Every OpenGL entry of <c>manifest.json</c> is compiled through
/// <see cref="OpenGlCorpusProbe"/> (the same source file the desktop test runs) and its SPIR-V
/// (DXC), GLSL (SPIRV-Cross) and <c>.mgfx</c> hashes compared with the committed manifests, which
/// the APK carries as assets. Started with <c>am start ... --es mode corpus</c>; the verdict is the
/// logcat line <c>CORPUS RESULT: PASS|FAIL ...</c>.
/// </summary>
internal static class CorpusCheck
{
    private const string Tag = "SHADOWDUSK";

    public static async Task RunAsync(AssetManager assets)
    {
        try
        {
            string manifestJson = Read(assets, "golden/manifest.json");
            var mgfx = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson)!;
            var intermediates = OpenGlCorpusProbe.ReadIntermediates(Read(assets, "golden/intermediates-manifest.json"));
            IReadOnlyList<string> fixtures = OpenGlCorpusProbe.OpenGlFixtures(manifestJson);

            int ok = 0, spirvBad = 0, glslBad = 0, mgfxBad = 0, failed = 0;
            foreach (string fx in fixtures)
            {
                string key = OpenGlCorpusProbe.Prefix + fx;
                var result = await OpenGlCorpusProbe.CompileAsync(fx, Read(assets, "shaders/" + fx), CancellationToken.None);
                if (result.IsFailure)
                {
                    failed++;
                    Log.Error(Tag, $"CORPUS COMPILE FAILED {key}: {result.Error}");
                    continue;
                }

                var h = result.Value;
                var e = intermediates[key];
                bool good = true;
                if (h.Spirv != e.Spirv) { spirvBad++; good = false; Log.Error(Tag, $"CORPUS MISMATCH SPIRV {key}: desktop={e.Spirv} device={h.Spirv}"); }
                if (h.Glsl != e.Glsl) { glslBad++; good = false; Log.Error(Tag, $"CORPUS MISMATCH GLSL {key}: desktop={e.Glsl} device={h.Glsl}"); }
                if (h.Mgfx != mgfx[key]) { mgfxBad++; good = false; Log.Error(Tag, $"CORPUS MISMATCH MGFX {key}: desktop={mgfx[key]} device={h.Mgfx}"); }
                if (good) ok++;
            }

            string verdict = ok == fixtures.Count && fixtures.Count > 0 ? "PASS" : "FAIL";
            Log.Info(Tag, $"CORPUS RESULT: {verdict} {ok}/{fixtures.Count} identical to the desktop " +
                          $"(spirv mismatches {spirvBad}, glsl mismatches {glslBad}, mgfx mismatches {mgfxBad}, compile failures {failed})");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "CORPUS RESULT: FAIL " + ex.GetType().Name + ": " + ex.Message, ex);
        }
    }

    private static string Read(AssetManager assets, string path)
    {
        using var reader = new StreamReader(assets.Open(path));
        return reader.ReadToEnd();
    }
}
