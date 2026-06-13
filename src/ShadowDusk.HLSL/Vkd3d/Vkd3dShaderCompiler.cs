#nullable enable

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>
/// Cross-platform DXBC backend: compiles preprocessed HLSL to SM5 DXBC
/// (DXBC_TPF / "Tokenized Program Format" — the format MonoGame's DX11 runtime
/// loads) via the native vkd3d-shader library. This is the SECOND
/// <see cref="IDxbcShaderCompiler"/>, slotting in behind the same seam as the
/// Windows-only d3dcompiler_47 oracle (<see cref="D3DCompilerShaderCompiler"/>),
/// but it runs on Linux/macOS/Windows with no Wine, Windows SDK, or fxc.exe — the
/// whole reason the DirectX backend exists.
///
/// With an SM ≤ 3 <see cref="D3DCompileRequest.ProfileOverride"/> (e.g. "ps_2_0",
/// "vs_3_0") it instead emits the bare legacy D3D9 token stream
/// (VKD3D_SHADER_TARGET_D3D_BYTECODE) the FNA fx_2_0 effects container embeds —
/// same library, same seam, different target type.
///
/// It still needs the native lib present at runtime; on a host where the lib
/// cannot be resolved the compile fails with a clear <see cref="ShaderError"/>
/// (SD0211) rather than a raw <see cref="DllNotFoundException"/>.
/// </summary>
public sealed class Vkd3dShaderCompiler : IDxbcShaderCompiler
{
    // Matches a whole #line directive line (keeps the trailing newline so blanking it
    // leaves an empty line, preserving overall line numbering for vkd3d diagnostics).
    private static readonly Regex LineDirectivePattern =
        new(@"(?m)^[ \t]*#[ \t]*line\b[^\n]*", RegexOptions.Compiled);


    /// <inheritdoc/>
    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CompileCore(request), cancellationToken);
    }

    /// <inheritdoc/>
    public Result<PlatformBlob, ShaderError> Compile(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CompileCore(request);
    }

    private static Result<PlatformBlob, ShaderError> CompileCore(D3DCompileRequest request)
    {
        Vkd3dLoader.Register();

        // Request→ABI mapping is the SHARED Vkd3dCompileContract (Phase 4.1): the same
        // profile defaults and SM ≤ 3 → D3D_BYTECODE routing the WASM backend uses, so
        // the two hosts can never drift apart.
        string profile = Vkd3dCompileContract.ResolveProfile(request);

        // SM ≤ 3 profiles compile to the bare D3D9 token stream (the FNA fx_2_0 path);
        // SM4/5 profiles keep the DXBC_TPF container MonoGame's DX11 runtime loads.
        var targetType    = (Vkd3dTargetType)Vkd3dCompileContract.ResolveTargetType(profile);
        BlobKind blobKind = Vkd3dCompileContract.ResolveBlobKind(profile);

        // vkd3d-shader's HLSL preprocessor does not honor #line directives; it ignores
        // each one and (at LogLevel >= Warning / VKD3D_DEBUG on) prints
        // "vkd3d:NNNN:fixme:preproc_yyparse #line directive." to the process stderr, once
        // per directive. An include-heavy effect (e.g. the MonoGame stock effects, which
        // pull in Macros.fxh/Structures.fxh/Common.fxh/Lighting.fxh) carries hundreds of
        // them from the include flattener, so a SUCCESSFUL compile would spew hundreds of
        // stderr lines and break the mgfxc silent-success contract. (The VKD3D_DEBUG=none
        // default in Vkd3dLoader does not reliably reach the native getenv at runtime,
        // which masked this until an include-heavy effect first reached the vkd3d backend.)
        // Blank every #line directive line before handing the source to vkd3d: it never
        // used them, ShadowDusk maps no diagnostics through them on this path, and BLANKING
        // (not deleting) preserves line numbering so vkd3d's own error line numbers are
        // unchanged. The DXC/GL and d3dcompiler_47 paths keep their #line directives (those
        // compilers honor them for diagnostics) — this strip is vkd3d-only.
        string vkd3dSource = LineDirectivePattern.Replace(request.HlslSource, string.Empty);

        // Marshal source / strings as UTF-8. vkd3d_shader_code carries raw bytes +
        // size (NOT null-terminated for source); the char* strings are C strings.
        byte[] sourceBytes = Encoding.UTF8.GetBytes(vkd3dSource);

        IntPtr sourcePtr     = Marshal.AllocHGlobal(sourceBytes.Length == 0 ? 1 : sourceBytes.Length);
        IntPtr entryPointPtr = MarshalCString(request.EntryPoint);
        IntPtr profilePtr    = MarshalCString(profile);
        IntPtr sourceNamePtr = MarshalCString(request.SourceFileName);

        // Pin the chained struct so its address stays valid for CompileInfo.Next.
        IntPtr hlslInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Vkd3dHlslSourceInfo>());

        try
        {
            Marshal.Copy(sourceBytes, 0, sourcePtr, sourceBytes.Length);

            var hlslInfo = new Vkd3dHlslSourceInfo
            {
                Type          = Vkd3dStructureType.HlslSourceInfo,
                Next          = IntPtr.Zero,
                EntryPoint    = entryPointPtr,
                SecondaryCode = default,
                Profile       = profilePtr,
            };
            Marshal.StructureToPtr(hlslInfo, hlslInfoPtr, fDeleteOld: false);

            var compileInfo = new Vkd3dCompileInfo
            {
                Type        = Vkd3dStructureType.CompileInfo,
                Next        = hlslInfoPtr,
                Source      = new Vkd3dShaderCode { Code = sourcePtr, Size = (nuint)sourceBytes.Length },
                SourceType  = Vkd3dSourceType.Hlsl,
                TargetType  = targetType,
                Options     = IntPtr.Zero,
                OptionCount = 0,
                // WARNING surfaces non-fatal diagnostics too; constraint 5 (fail loudly).
                LogLevel    = Vkd3dLogLevel.Warning,
                SourceName  = sourceNamePtr,
            };

            int rc;
            Vkd3dShaderCode output;
            IntPtr messagesPtr;
            try
            {
                rc = Vkd3dNative.Compile(in compileInfo, out output, out messagesPtr);
            }
            catch (DllNotFoundException ex)
            {
                return Result<PlatformBlob, ShaderError>.Fail(new ShaderError(
                    File:    request.SourceFileName,
                    Line:    0,
                    Column:  0,
                    Code:    "SD0211",
                    Message: "Cross-platform DXBC backend (vkd3d-shader) native library not found. " +
                             "Restore it via tools/restore.ps1 (places tools/vkd3d/libvkd3d-shader-1.dll). " +
                             "Underlying error: " + ex.Message));
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException)
            {
                return Result<PlatformBlob, ShaderError>.Fail(new ShaderError(
                    File:    request.SourceFileName,
                    Line:    0,
                    Column:  0,
                    Code:    "SD0211",
                    Message: "Cross-platform DXBC backend (vkd3d-shader) could not be loaded: " + ex.Message));
            }

            string messages = ReadAndFreeMessages(messagesPtr);

            try
            {
                if (rc != 0 || output.Code == IntPtr.Zero || output.Size == 0)
                {
                    // Shared error mapping (Vkd3dCompileContract): verbatim diagnostics
                    // first, SD0212 fallback — identical on desktop and WASM.
                    return Result<PlatformBlob, ShaderError>.Fail(
                        Vkd3dCompileContract.MapCompileFailure(
                            messages,
                            request.SourceFileName,
                            $"vkd3d-shader DXBC compilation failed (rc={rc}) with no diagnostics"));
                }

                var dxbc = new byte[checked((int)output.Size)];
                Marshal.Copy(output.Code, dxbc, 0, dxbc.Length);
                return Result<PlatformBlob, ShaderError>.Ok(new PlatformBlob(blobKind, dxbc));
            }
            finally
            {
                if (output.Code != IntPtr.Zero)
                    Vkd3dNative.FreeShaderCode(ref output);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(hlslInfoPtr);
            Marshal.FreeHGlobal(sourcePtr);
            FreeCString(entryPointPtr);
            FreeCString(profilePtr);
            FreeCString(sourceNamePtr);
        }
    }

    private static string ReadAndFreeMessages(IntPtr messagesPtr)
    {
        if (messagesPtr == IntPtr.Zero)
            return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(messagesPtr) ?? string.Empty;
        }
        finally
        {
            Vkd3dNative.FreeMessages(messagesPtr);
        }
    }

    private static IntPtr MarshalCString(string? value) =>
        value is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);

    private static void FreeCString(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
            Marshal.FreeCoTaskMem(ptr);
    }
}
