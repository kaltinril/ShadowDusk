// Issue #350 review: a host that loads ShadowDusk straight from the NuGet global packages folder
// (dotnet fsi `#r "nuget: ..."`, .NET Interactive / Polyglot notebooks). Its AppContext.BaseDirectory
// is the F# SDK and its NATIVE_DLL_SEARCH_DIRECTORIES know nothing of the packages, so the natives
// are reachable only through the package folders themselves: SPIRV-Cross in
// silk.net.spirv.cross.native/<ver>, beside shadowdusk.glsl/<ver>. tools/verify-fsi-consumer.sh
// writes the two `#i` / `#r` lines above this file's body and runs it; it prints one
// "Target=OK|CODE" line per target and the assembly location, and the script asserts them.
open ShadowDusk.Compiler
open ShadowDusk.Core

let source = System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "Grayscale.fx"))
let compiler = EffectCompiler()
for target in [ PlatformTarget.OpenGL; PlatformTarget.DirectX; PlatformTarget.Fna ] do
    let result =
        compiler.CompileAsync(source, CompilerOptions(Target = target, SourceFileName = "Grayscale.fx"))
            .GetAwaiter().GetResult()
    if result.IsSuccess then
        printfn "%O=OK" target
    else
        printfn "%O=%s" target result.Error.[0].Code
        printfn "message.%O=%s" target (result.Error.[0].Message.ReplaceLineEndings(" "))

printfn "glsl=%s" typeof<ShadowDusk.GLSL.SpirvCrossGlslTranspiler>.Assembly.Location
