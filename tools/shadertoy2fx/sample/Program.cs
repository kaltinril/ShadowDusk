#nullable enable

using System;
using System.IO;
using ShadowDusk.ShaderToy.Sample;

// =============================================================================
// ShadowDusk ShaderToy sample (Phase 46 capstone).
//
// Demonstrates, end to end and AT RUNTIME, the converter + ShadowDusk's in-memory
// compile: ShaderToy GLSL -> .fx -> .mgfx (compiled in memory, no mgfxc, no build
// step) -> a live MonoGame Effect rendered over a fullscreen quad.
//
// USAGE:
//   (no args)            open an interactive window over the BUNDLED catalog;
//                        SPACE/arrows cycle, the mouse drives iMouse, ESC quits.
//   <path-to-shader>     open the window with that ShaderToy/GLSL file added as the
//                        first, HOT-RELOADABLE entry (edit on disk -> live reload),
//                        with the bundled catalog still cyclable behind it. Accepts
//                        .glsl / .frag / .fs / .txt.
//   --smoke              headless: convert+compile+render ONE frame per BUNDLED shader
//                        to an offscreen PNG, assert non-trivial, exit 0/non-zero.
//   --smoke <path>       same, but for the GIVEN file only (testable without a window).
// =============================================================================

bool smoke = false;
string? path = null;

foreach (string arg in args)
{
    if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
        smoke = true;
    else if (arg.StartsWith("--", StringComparison.Ordinal))
        Console.Error.WriteLine($"[sample] ignoring unknown option: {arg}");
    else if (path is null)
        path = arg; // first positional arg is the shader file to load
    else
        Console.Error.WriteLine($"[sample] ignoring extra argument: {arg}");
}

// Resolve a user-supplied path (for either mode) before opening any GL context, so a bad path / bad
// extension reports cleanly with a non-zero exit instead of crashing inside the game loop.
ShaderSource? external = null;
if (path is not null)
{
    if (!ShaderSource.TryFromPath(path, out external, out string error))
    {
        Console.Error.WriteLine($"[sample] cannot load shader:\n{error}");
        return 2;
    }
}

if (smoke)
    return RunSmoke(external);

RunInteractive(external);
return 0;

static void RunInteractive(ShaderSource? external)
{
    using var game = new SampleGame(external);
    game.Run();
}

static int RunSmoke(ShaderSource? external)
{
    string outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
    outDir = Path.GetFullPath(outDir);
    Directory.CreateDirectory(outDir);

    Console.WriteLine("[smoke] ShadowDusk ShaderToy sample: convert -> in-memory compile -> load -> render.");
    Console.WriteLine(external is null
        ? $"[smoke] shaders: {SampleCompiler.ShadersDirectory} (bundled catalog)"
        : $"[smoke] shader:  {external.Path} (external file)");
    Console.WriteLine($"[smoke] output:  {outDir}");

    try
    {
        using var game = new SmokeGame(outDir, external);
        game.Run();
        return game.Report();
    }
    catch (Exception ex)
    {
        // HONEST FAILURE: if the MonoGame GL context cannot init in this environment, REPORT it
        // (non-zero) rather than soft-passing. A faked pass is worse than an honest blocker.
        Console.Error.WriteLine(
            "[smoke] FATAL: the MonoGame GL harness threw before producing results.");
        Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        return 3;
    }
}
