// Issue #282 end-to-end consumer (tools/verify-vortice-dxc-conflict.sh): compiles one real
// shader for a DXC-backed target (OpenGL) and two that do not use DXC (DirectX 11, FNA), and
// prints one "Target=OK|CODE" line per target plus "message.Target=..." for a failure. The
// script decides what each line must be for the Vortice.Dxc version it forced.
using ShadowDusk.Compiler;
using ShadowDusk.Core;

string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Grayscale.fx"));
var compiler = new EffectCompiler();
foreach (PlatformTarget target in new[] { PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.Fna })
{
    try
    {
        var result = await compiler.CompileAsync(source, new CompilerOptions { Target = target, SourceFileName = "Grayscale.fx" });
        if (result.IsSuccess)
        {
            Console.WriteLine($"{target}={(result.Value.Data.Length > 4 ? "OK" : "EMPTY")}");
        }
        else
        {
            Console.WriteLine($"{target}={result.Error[0].Code}");
            Console.WriteLine($"message.{target}={result.Error[0].Message.ReplaceLineEndings(" ")}");
        }
    }
    catch (Exception ex)
    {
        // A raw exception is exactly what this check exists to rule out; report it as a value.
        Console.WriteLine($"{target}=THROW");
        Console.WriteLine($"message.{target}={ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
    }
}
