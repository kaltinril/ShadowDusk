#nullable enable

using System.Runtime.InteropServices;
using System.Text;
// (System.Runtime.InteropServices is used by the macOS gate below.)
using FluentAssertions;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using Vortice.Dxc;
using Xunit;
using static Vortice.Dxc.Dxc;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Phase 3 §7.4 (closed by Phase 27): smoke test for the internal
/// <see cref="DxcIncludeHandler"/> — constructed over an <see cref="InMemoryIncludeResolver"/>,
/// <c>LoadSource</c> must return success and a blob carrying the resolver's exact UTF-8 bytes,
/// and an unresolvable include must map to a DXC failure HRESULT with a null blob.
/// Integration-tagged: constructing <c>IDxcUtils</c> initializes the native DXC library
/// (which is why this could not be a pure unit test in Phase 3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcIncludeHandlerTests
{
    private const string HeaderText = "float4 SharedHelper(float4 v) { return v; }\n";

    private static DxcIncludeHandler CreateHandler(IDxcUtils utils) =>
        new(
            utils,
            new InMemoryIncludeResolver(new Dictionary<string, string>
            {
                ["Helpers.fxh"] = HeaderText,
            }),
            rootFilePath: "main.fx",
            additionalPaths: []);

    [DxcSmokeFact]
    public void LoadSource_KnownFile_ReturnsCorrectBlobBytes()
    {
        ShadowDusk.HLSL.Dxc.DxcLoader.Register();
        using IDxcUtils utils = CreateDxcUtils();
        DxcIncludeHandler handler = CreateHandler(utils);
        try
        {
            SharpGen.Runtime.Result result = handler.LoadSource("Helpers.fxh", out IDxcBlob? blob);

            result.Success.Should().BeTrue(because: "the resolver holds Helpers.fxh");
            blob.Should().NotBeNull();

            byte[] bytes = blob!.AsBytes();

            bytes.Should().Equal(Encoding.UTF8.GetBytes(HeaderText),
                because: "the blob must carry the resolved header text verbatim as UTF-8");
            blob!.Dispose();
        }
        finally
        {
            handler.FreeNativeBuffers();
        }
    }

    [DxcSmokeFact]
    public void LoadSource_UnknownFile_ReturnsFailureWithNullBlob()
    {
        ShadowDusk.HLSL.Dxc.DxcLoader.Register();
        using IDxcUtils utils = CreateDxcUtils();
        DxcIncludeHandler handler = CreateHandler(utils);
        try
        {
            SharpGen.Runtime.Result result = handler.LoadSource("Missing.fxh", out IDxcBlob? blob);

            result.Failure.Should().BeTrue(because: "an unresolvable include must fail loudly");
            blob.Should().BeNull();
        }
        finally
        {
            handler.FreeNativeBuffers();
        }
    }
}

/// <summary>
/// macOS-only DXC availability gate (the Vortice.Dxc NuGet ships no macOS native;
/// ours is restored via <c>tools/restore</c>). Mirrors the Integration.Tests
/// <c>DxcFactAttribute</c> so the smoke test skips cleanly, never hard-fails,
/// on a fresh clone (Phase 27 native-gating rule).
/// </summary>
public sealed class DxcSmokeFactAttribute : FactAttribute
{
    public DxcSmokeFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        bool found = File.Exists(Path.Combine(AppContext.BaseDirectory, arch, "libdxcompiler.dylib"))
                  || File.Exists(Path.Combine(AppContext.BaseDirectory, "libdxcompiler.dylib"));
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); !found && dir is not null; dir = dir.Parent)
            found = File.Exists(Path.Combine(dir.FullName, "tools", "dxc", arch, "libdxcompiler.dylib"));

        if (!found)
            Skip = "macOS DXC native (libdxcompiler.dylib) not found — run tools/restore.sh first.";
    }
}
