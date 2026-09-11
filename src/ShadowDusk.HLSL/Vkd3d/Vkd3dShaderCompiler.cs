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
        // used them, and BLANKING (not deleting) keeps the blanked text and
        // request.HlslSource line-for-line aligned, which is what lets Vkd3dSourceLocator
        // map vkd3d's coordinates back through the directives below (issue #202). The
        // DXC/GL and d3dcompiler_47 paths keep their #line directives (those compilers
        // honor them for diagnostics) — this strip is vkd3d-only.
        string vkd3dSource = LineDirectivePattern.Replace(request.HlslSource, string.Empty);

        NativeOutcome outcome;
        try
        {
            outcome = InvokeNative(vkd3dSource, request, profile, targetType);
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

        // vkd3d's line numbers are its own (issue #202: skipped #if arms vanish from its
        // count and every template-implemented intrinsic call inflates it) and its columns
        // count in its re-spaced token stream. Vkd3dSourceLocator asks vkd3d itself where
        // each diagnostic sits, with parse-abort probes of the SAME request; the real
        // compile above is untouched, so the emitted bytes cannot move.
        ShaderError? Probe(string source)
        {
            NativeOutcome o = InvokeNative(source, request, profile, targetType);
            return o.Failed
                ? Vkd3dCompileContract.MapCompileFailure(o.Messages, request.SourceFileName, string.Empty)
                : null;
        }

        if (outcome.Failed)
        {
            // Shared error mapping (Vkd3dCompileContract): verbatim diagnostics
            // first, SD0212 fallback — identical on desktop and WASM.
            ShaderError primary = Vkd3dCompileContract.MapCompileFailure(
                outcome.Messages,
                request.SourceFileName,
                $"vkd3d-shader DXBC compilation failed (rc={outcome.Rc}) with no diagnostics");
            return Result<PlatformBlob, ShaderError>.Fail(
                Vkd3dSourceLocator.Relocate(primary, vkd3dSource, request.HlslSource, request.SourceFileName, Probe));
        }

        // vkd3d's message buffer is populated on SUCCESS too (LogLevel is
        // Warning) — non-fatal diagnostics were previously discarded here.
        // Capture verbatim; the pipeline surfaces them via
        // CompiledShader.Warnings (constraint 5).
        IReadOnlyList<ShaderError> warnings = string.IsNullOrWhiteSpace(outcome.Messages)
            ? Array.Empty<ShaderError>()
            : Vkd3dSourceLocator.Relocate(
                D3DCompilerDiagnosticReformatter.ReformatAsWarnings(outcome.Messages, request.SourceFileName),
                vkd3dSource, request.HlslSource, request.SourceFileName, Probe);

        return Result<PlatformBlob, ShaderError>.Ok(
            new PlatformBlob(blobKind, outcome.Code!) { Warnings = warnings });
    }

    /// <summary>
    /// One <c>vkd3d_shader_compile</c> call, fully marshalled: the code bytes
    /// (<see langword="null"/> on failure) and vkd3d's verbatim message text.
    /// </summary>
    private readonly record struct NativeOutcome(int Rc, byte[]? Code, string Messages)
    {
        public bool Failed => Rc != 0 || Code is null || Code.Length == 0;
    }

    private static NativeOutcome InvokeNative(
        string source, D3DCompileRequest request, string profile, Vkd3dTargetType targetType)
    {
        // Marshal source / strings as UTF-8. vkd3d_shader_code carries raw bytes +
        // size (NOT null-terminated for source); the char* strings are C strings.
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

        IntPtr sourcePtr     = Marshal.AllocHGlobal(sourceBytes.Length == 0 ? 1 : sourceBytes.Length);
        IntPtr entryPointPtr = MarshalCString(request.EntryPoint);
        IntPtr profilePtr    = MarshalCString(profile);
        IntPtr sourceNamePtr = MarshalCString(request.SourceFileName);

        // Pin the chained struct so its address stays valid for CompileInfo.Next.
        IntPtr hlslInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Vkd3dHlslSourceInfo>());

        // SM1-3 semantics on an SM4+ target: `fxc` accepts them (that is how every
        // MonoGame `.fx` written against SM3 still builds at ps_4_0), and vkd3d only
        // does with BACKWARD_COMPATIBILITY/MAP_SEMANTIC_NAMES — without it, vkd3d 2.1
        // rejects a user semantic on a pixel-shader output outright ("E5013: Invalid
        // semantic 'COLOR'"). FxPreParser rewrites the `) : COLOR<n>` RETURN semantic
        // in RewriteToSm4 mode, but it deliberately cannot touch the same semantic on
        // a struct FIELD (the struct may be a VS output, where COLOR is legal), so the
        // option is what covers a `struct { float4 c : COLOR0; }` pixel-shader return.
        // Scoped to the SM4+ target: on d3dbc these ARE the native semantics.
        bool mapSemanticNames = targetType == Vkd3dTargetType.DxbcTpf;
        IntPtr optionsPtr = mapSemanticNames
            ? Marshal.AllocHGlobal(Marshal.SizeOf<Vkd3dCompileOption>())
            : IntPtr.Zero;

        try
        {
            if (mapSemanticNames)
            {
                Marshal.StructureToPtr(
                    new Vkd3dCompileOption
                    {
                        Name  = Vkd3dCompileOptionName.BackwardCompatibility,
                        Value = (uint)Vkd3dBackwardCompatibility.MapSemanticNames,
                    },
                    optionsPtr, fDeleteOld: false);
            }

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
                Options     = optionsPtr,
                OptionCount = mapSemanticNames ? 1u : 0u,
                // WARNING surfaces non-fatal diagnostics too; constraint 5 (fail loudly).
                LogLevel    = Vkd3dLogLevel.Warning,
                SourceName  = sourceNamePtr,
            };

            int rc = Vkd3dNative.Compile(in compileInfo, out Vkd3dShaderCode output, out IntPtr messagesPtr);
            string messages = ReadAndFreeMessages(messagesPtr);

            try
            {
                if (rc != 0 || output.Code == IntPtr.Zero || output.Size == 0)
                    return new NativeOutcome(rc, null, messages);

                var bytes = new byte[checked((int)output.Size)];
                Marshal.Copy(output.Code, bytes, 0, bytes.Length);
                return new NativeOutcome(rc, bytes, messages);
            }
            finally
            {
                if (output.Code != IntPtr.Zero)
                    Vkd3dNative.FreeShaderCode(ref output);
            }
        }
        finally
        {
            if (optionsPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(optionsPtr);
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
