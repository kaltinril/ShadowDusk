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
//   default : open an interactive window; SPACE/arrows cycle the bundled shaders,
//             the mouse drives iMouse, ESC quits. Each cycle recompiles at runtime.
//   --smoke : headless validation. Convert + compile + load + render ONE frame per
//             bundled shader to an offscreen RenderTarget, write a PNG each, assert
//             the frame is non-trivial, exit 0 (non-zero on any failure). No window.
// =============================================================================

bool smoke = false;
foreach (string arg in args)
{
    if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
        smoke = true;
}

if (smoke)
    return RunSmoke();

RunInteractive();
return 0;

static void RunInteractive()
{
    using var game = new SampleGame();
    game.Run();
}

static int RunSmoke()
{
    string outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
    outDir = Path.GetFullPath(outDir);
    Directory.CreateDirectory(outDir);

    Console.WriteLine("[smoke] ShadowDusk ShaderToy sample: convert -> in-memory compile -> load -> render.");
    Console.WriteLine($"[smoke] shaders: {SampleCompiler.ShadersDirectory}");
    Console.WriteLine($"[smoke] output:  {outDir}");

    try
    {
        using var game = new SmokeGame(outDir);
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
