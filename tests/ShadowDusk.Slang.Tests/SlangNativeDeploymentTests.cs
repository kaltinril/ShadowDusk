#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// The real, restored slangc driven through the deployment shapes a NuGet consumer's
/// machine can produce (issues #225/#227): a copy without its execute bit, a read-only
/// package directory, non-ASCII source over stdin, and a binary the OS refuses to start.
/// Each test copies the restored native into a throwaway directory and points
/// <see cref="SlangCompiler"/> at it through its locator seam, so the repo's own
/// <c>tools/slang/&lt;rid&gt;/</c> copy is never modified.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangNativeDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-deploy-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        if (!OperatingSystem.IsWindows())
        {
            foreach (string dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
    }

    private const string PixelShader = """
        [shader("fragment")]
        float4 MainPS(float2 uv : TEXCOORD0) : SV_Target
        {
            return float4(uv, 0.5, 1.0);
        }
        """;

    private static string Rid => SlangToolPath.CurrentRid
        ?? throw new InvalidOperationException("test host is not a bundled slangc RID");

    /// <summary>Copies the restored slangc + its library into a fresh directory.</summary>
    private string CopyRestoredNative()
    {
        string restored = SlangToolPath.ResolveOrThrow();
        string sourceDir = Path.GetDirectoryName(restored)!;
        string dir = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, SlangToolPath.ExecutableFileName(Rid));
        File.Copy(restored, exe);
        string lib = SlangToolPath.CompilerLibraryFileName(Rid);
        File.Copy(Path.Combine(sourceDir, lib), Path.Combine(dir, lib));
        return exe;
    }

    private static Result<CompiledShader, ShaderError[]> CompileWith(string slangcPath, string source = PixelShader) =>
        new SlangCompiler(null, () => new(null, slangcPath)).Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "deploy.slang" });

    private static string Errors(Result<CompiledShader, ShaderError[]> r) =>
        r.IsFailure ? string.Join("; ", r.Error.Select(e => $"{e.Code}: {e.Message}")) : "";

    [Fact]
    public void NonExecutableCopy_StillCompiles()
    {
        string exe = CopyRestoredNative();
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var result = CompileWith(exe);

        result.IsSuccess.ShouldBeTrue(Errors(result));
        System.Text.Encoding.ASCII.GetString(result.Value.Data, 0, 4).ShouldBe("MGFX");
        if (!OperatingSystem.IsWindows())
            (File.GetUnixFileMode(exe) & UnixFileMode.UserExecute).ShouldBe(UnixFileMode.UserExecute);
    }

    [Fact]
    public void ReadOnlyPackageDirectory_RunsFromAWritableCachedCopy()
    {
        string exe = CopyRestoredNative();
        string dir = Path.GetDirectoryName(exe)!;

        if (OperatingSystem.IsWindows())
        {
            // Windows' directory ReadOnly attribute does not block file creation (Phase 66
            // A3), so there is no real read-only directory to build here; the unwritable path
            // is covered by SlangNativeCacheTests' seam on Windows and for real off Windows.
            CompileWith(exe).IsSuccess.ShouldBeTrue();
            return;
        }

        // Strip the execute bit too: the cached copy must be made executable, not just copied.
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        string runnable = SlangNativeCache.EnsureRunnableSlangc(exe);
        var result = CompileWith(exe);

        Path.GetDirectoryName(runnable).ShouldNotBe(dir);
        result.IsSuccess.ShouldBeTrue(Errors(result));
        Directory.GetFiles(dir).Length.ShouldBe(2, "slangc's runtime cache must not have been written into the read-only directory");
    }

    [Fact]
    public void NonAsciiSourceText_ReachesSlangcAsUtf8_OnEveryHost()
    {
        // .NET writes a child's stdin in the console code page unless told otherwise (the OEM
        // page on Windows); slangc reads UTF-8. A non-ASCII comment must round-trip cleanly.
        const string source = """
            // Grüße, こんにちは, déjà vu: non-ASCII text slangc must receive as UTF-8.
            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target
            {
                return float4(uv, 0.25, 1.0);
            }
            """;

        var result = new SlangCompiler().Compile(
            source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "utf8.slang" });

        result.IsSuccess.ShouldBeTrue(Errors(result));
    }

    [Fact]
    public void BinaryTheOsCannotStart_SD0622_WithTheOsReason()
    {
        string exe = CopyRestoredNative();
        File.WriteAllBytes(exe, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);   // not an executable format

        var result = CompileWith(exe);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0622");
        error.Message.ShouldContain("could not be started", Case.Sensitive);
    }
}
