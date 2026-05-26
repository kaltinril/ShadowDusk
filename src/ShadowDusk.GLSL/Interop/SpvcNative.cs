#nullable enable

// SPIRV-Cross C API bindings
// Source: KhronosGroup/SPIRV-Cross spirv_cross_c.h (main branch)
// Option constant values verified from header: COMMON_BIT=0x1000000, GLSL_BIT=0x2000000

using System.Runtime.InteropServices;

namespace ShadowDusk.GLSL.Interop;

internal enum SpvcBackend : uint { None = 0, Glsl = 1, Hlsl = 2, Msl = 3, Cpp = 4, Json = 5 }
internal enum SpvcCaptureMode : uint { Copy = 0, TakeOwnership = 1 }
internal enum SpvcResult : int
{
    Success = 0,
    ErrorInvalidSpirv = -1,
    ErrorUnsupportedSpirv = -2,
    ErrorOutOfMemory = -3,
    ErrorInvalidArgument = -4,
}

internal static class SpvcCompilerOption
{
    public const uint FlipVertexY          = 0x1000004u;
    public const uint FixupDepthConvention = 0x1000003u;
    public const uint GlslVersion          = 0x2000008u;
    public const uint GlslEs               = 0x2000009u;
    public const uint GlslVulkanSemantics  = 0x200000Au;
}

internal static class SpvcNative
{
    private const string LibName = "spirv-cross-c-shared";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_context_create(out IntPtr context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void spvc_context_destroy(IntPtr context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr spvc_context_get_last_error_string(IntPtr context);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_context_parse_spirv(
        IntPtr context,
        [In] uint[] spirvWords,
        nuint wordCount,
        out IntPtr parsedIr);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_context_create_compiler(
        IntPtr context,
        SpvcBackend backend,
        IntPtr parsedIr,
        SpvcCaptureMode captureMode,
        out IntPtr compiler);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_compiler_create_compiler_options(
        IntPtr compiler,
        out IntPtr options);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_compiler_options_set_bool(
        IntPtr options,
        uint option,
        [MarshalAs(UnmanagedType.U1)] bool value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_compiler_options_set_uint(
        IntPtr options,
        uint option,
        uint value);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_compiler_install_compiler_options(
        IntPtr compiler,
        IntPtr options);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_compiler_build_combined_image_samplers(
        IntPtr compiler);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern SpvcResult spvc_compiler_compile(
        IntPtr compiler,
        out IntPtr glslSource);
}
