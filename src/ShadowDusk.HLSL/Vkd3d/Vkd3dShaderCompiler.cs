#nullable enable

using System.Runtime.InteropServices;
using System.Text;
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
    /// <inheritdoc/>
    public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
        D3DCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => CompileCore(request), cancellationToken);
    }

    private static Result<PlatformBlob, ShaderError> CompileCore(D3DCompileRequest request)
    {
        Vkd3dLoader.Register();

        string profile = request.ProfileOverride ?? request.Stage switch
        {
            ShaderStage.Vertex => "vs_5_0",
            ShaderStage.Pixel  => "ps_5_0",
            _ => throw new ArgumentOutOfRangeException(
                nameof(request), $"Unsupported shader stage for DXBC: {request.Stage}"),
        };

        // SM ≤ 3 profiles compile to the bare D3D9 token stream (the FNA fx_2_0 path);
        // SM4/5 profiles keep the DXBC_TPF container MonoGame's DX11 runtime loads.
        bool d3d9 = IsSm3OrBelow(profile);
        Vkd3dTargetType targetType = d3d9 ? Vkd3dTargetType.D3dBytecode : Vkd3dTargetType.DxbcTpf;
        BlobKind blobKind          = d3d9 ? BlobKind.D3dBytecode        : BlobKind.Dxbc;

        // Marshal source / strings as UTF-8. vkd3d_shader_code carries raw bytes +
        // size (NOT null-terminated for source); the char* strings are C strings.
        byte[] sourceBytes = Encoding.UTF8.GetBytes(request.HlslSource);

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
                    IReadOnlyList<ShaderError> errors =
                        D3DCompilerDiagnosticReformatter.Reformat(messages, request.SourceFileName);

                    ShaderError primary = errors.Count > 0
                        ? errors[0]
                        : new ShaderError(
                            File:    request.SourceFileName,
                            Line:    0,
                            Column:  0,
                            Code:    "SD0212",
                            Message: string.IsNullOrWhiteSpace(messages)
                                ? $"vkd3d-shader DXBC compilation failed (rc={rc}) with no diagnostics"
                                : messages.Trim(),
                            RawDiagnostics: string.IsNullOrWhiteSpace(messages) ? null : messages);

                    return Result<PlatformBlob, ShaderError>.Fail(primary);
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

    // Profile strings look like "vs_3_0" / "ps_2_b" / "vs_5_0": the digit after the first
    // underscore is the shader-model major version. SM ≤ 3 selects the D3D9 token-stream
    // target; anything unparseable falls through to DXBC_TPF (vkd3d then rejects a bad
    // profile with its own diagnostic — fail loudly, constraint 5).
    private static bool IsSm3OrBelow(string profile)
    {
        int underscore = profile.IndexOf('_');
        return underscore >= 0
            && underscore + 1 < profile.Length
            && profile[underscore + 1] is >= '1' and <= '3';
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
