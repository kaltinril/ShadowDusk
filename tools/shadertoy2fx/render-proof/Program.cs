#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ShadowDusk.ShaderToy;
using ShadowDusk.ShaderToy.RenderProof;

// =============================================================================
// ShaderToy -> .fx -> .mgfx -> REAL MonoGame GL Effect render proof (Phase 46).
//
// For each deterministic corpus shader this driver:
//   1. converts the .glsl -> .fx by calling the ShadowDusk.ShaderToy library directly,
//   2. compiles the .fx -> .mgfx for OpenGL by shelling the BUILT ShadowDusk CLI,
//   3. loads the .mgfx into a real MonoGame DesktopGL Effect,
//   4. drives the ShaderToy uniforms (fixed iResolution, iTime=0) via ShaderToyEffect,
//   5. renders a fullscreen pass to an offscreen RenderTarget,
//   6. reads back the pixels and ASSERTS analytic expected values,
//   7. saves the rendered PNG for human eyeball.
//
// HONESTY: if the MonoGame GL context cannot initialize, this REPORTS THE FAILURE
// (non-zero exit) instead of soft-skipping green. A faked pass is worse than a blocker.
// =============================================================================

string driverDir = AppContext.BaseDirectory;
string shadersDir = Path.Combine(driverDir, "shaders");
string repoRoot = FindRepoRoot(driverDir);
string outDir = Path.Combine(repoRoot, "tools", "shadertoy2fx", "render-proof", "output");
Directory.CreateDirectory(outDir);

string cliDll = LocateCliDll(driverDir);

// ---- GALLERY MODE (Phase 46 render-fidelity broadening): render EVERY authored corpus shader. ----
// `--gallery` iterates corpus/authored/*.glsl, converts + compiles (OpenGL), renders each at a fixed
// resolution/time, asserts each frame is NON-TRIVIAL, and writes a single committed montage PNG.
if (args.Length > 0 && args[0] == "--gallery")
{
    Console.WriteLine($"[gallery] CLI:    {cliDll}");
    Console.WriteLine($"[gallery] output: {outDir}\n");
    return ShadowDusk.ShaderToy.RenderProof.GalleryRunner.Run(cliDll, repoRoot, outDir);
}

Console.WriteLine($"[render-proof] CLI:     {cliDll}");
Console.WriteLine($"[render-proof] shaders: {shadersDir}");
Console.WriteLine($"[render-proof] output:  {outDir}\n");

// ---- Build the jobs: convert + compile each shader on the CPU (no GL yet). ----
var jobs = new List<RenderJob>();
foreach ((string name, Func<int, int, RgbAssertion[]> asserter, Action<ShadowDusk.ShaderToy.Runtime.ShaderToyEffect>? customSetup) in RenderProofShaders.Catalog)
{
    string glslPath = Path.Combine(shadersDir, name + ".glsl");
    if (!File.Exists(glslPath))
    {
        Console.Error.WriteLine($"[render-proof] MISSING shader source: {glslPath}");
        return 2;
    }

    string glsl = File.ReadAllText(glslPath);
    ConvertResult conv = ShaderToyConverter.Convert(glsl, new ConvertOptions { EffectName = name });
    if (!conv.Success || conv.Fx is null)
    {
        Console.Error.WriteLine($"[render-proof] CONVERT FAILED for {name}:");
        foreach (ConvertDiagnostic d in conv.Diagnostics)
            Console.Error.WriteLine($"    {d.Severity} ({d.Line},{d.Column}): {d.Message}");
        return 2;
    }

    string fxPath = Path.Combine(outDir, name + ".fx");
    string mgfxPath = Path.Combine(outDir, name + ".mgfx");
    File.WriteAllText(fxPath, conv.Fx);

    string compileError = CompileFxToMgfx(cliDll, fxPath, mgfxPath);
    if (compileError.Length > 0)
    {
        Console.Error.WriteLine($"[render-proof] COMPILE FAILED for {name}:\n{compileError}");
        return 2;
    }

    jobs.Add(new RenderJob(name, File.ReadAllBytes(mgfxPath), asserter, customSetup));
    Console.WriteLine($"[render-proof] prepared {name}: uniforms=[{string.Join(", ", conv.UsedUniforms)}]");
}

Console.WriteLine();

// ---- Render + assert inside a real MonoGame GL context. ----
int exitCode;
try
{
    using var game = new RenderProofGame(jobs, outDir, width: 256, height: 256);
    game.Run();
    exitCode = game.Report();
}
catch (Exception ex)
{
    // HONEST FAILURE: GL context could not init, or the game loop threw. Do NOT pass.
    Console.Error.WriteLine(
        "[render-proof] FATAL: the MonoGame GL render harness threw before producing results.");
    Console.Error.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 3;
}

// ---- MULTIPASS render proof: the hand-wired chain2 two-pass example. ----
Console.WriteLine();
string chain2Json = Path.Combine(driverDir, "multipass", "chain2.json");
int multipassExit = ShadowDusk.ShaderToy.RenderProof.MultipassChain2Proof.Run(cliDll, chain2Json, outDir);

return exitCode != 0 ? exitCode : multipassExit;

// ----------------------------------------------------------------------------

static string CompileFxToMgfx(string cliDll, string fxPath, string mgfxPath)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(cliDll);
    psi.ArgumentList.Add(fxPath);
    psi.ArgumentList.Add(mgfxPath);
    psi.ArgumentList.Add("/Profile:OpenGL");

    using var proc = Process.Start(psi)
        ?? throw new InvalidOperationException("Failed to start the ShadowDusk CLI process.");
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();

    if (proc.ExitCode != 0)
        return $"exit={proc.ExitCode}\n{stderr}\n{stdout}".Trim();
    if (!File.Exists(mgfxPath))
        return $"CLI exited 0 but produced no .mgfx at {mgfxPath}\n{stderr}\n{stdout}".Trim();
    return string.Empty;
}

static string LocateCliDll(string driverDir)
{
    string repoRoot = FindRepoRoot(driverDir);
    foreach (string config in new[] { "Debug", "Release" })
    {
        string candidate = Path.Combine(
            repoRoot, "src", "ShadowDusk.Cli", "bin", config, "net8.0", "ShadowDuskCLI.dll");
        if (File.Exists(candidate))
            return candidate;
    }

    throw new FileNotFoundException(
        "Built ShadowDuskCLI.dll not found. Build it first: " +
        "dotnet build src/ShadowDusk.Cli/ShadowDusk.Cli.csproj");
}

static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException(
        $"Could not locate the ShadowDusk repo root (ShadowDusk.slnx) from {start}.");
}
