#nullable enable

// vkd3d-shader C API bindings (vkd3d-shader 1.17).
// Source of truth: tools/vkd3d/vkd3d_shader.h (read & verified against this file).
//
// ABI confirmed from the header:
//   - Calling convention: cdecl (x64).
//   - All enums are 4-byte (VKD3D_FORCE_32_BIT_ENUM), marshalled as int.
//   - `size_t` widths -> nuint.
//   - vkd3d_shader_compile() returns 0 (VKD3D_OK) on success; negative on error.
//
// Struct field order matches the header exactly (StructLayout.Sequential), which is
// what determines the C layout for these plain (non-bitfield) structs.

using System.Runtime.InteropServices;

namespace ShadowDusk.HLSL.Vkd3d;

/// <summary>vkd3d_shader_structure_type — header enum order (0-based).</summary>
internal enum Vkd3dStructureType
{
    CompileInfo = 0,                  // VKD3D_SHADER_STRUCTURE_TYPE_COMPILE_INFO
    InterfaceInfo = 1,
    ScanDescriptorInfo = 2,
    SpirvDomainShaderTargetInfo = 3,
    SpirvTargetInfo = 4,
    TransformFeedbackInfo = 5,
    HlslSourceInfo = 6,               // VKD3D_SHADER_STRUCTURE_TYPE_HLSL_SOURCE_INFO
    PreprocessInfo = 7,
    DescriptorOffsetInfo = 8,
}

/// <summary>vkd3d_shader_source_type.</summary>
internal enum Vkd3dSourceType
{
    None = 0,
    DxbcTpf = 1,
    Hlsl = 2,                         // VKD3D_SHADER_SOURCE_HLSL
    D3dBytecode = 3,
    DxbcDxil = 4,
}

/// <summary>vkd3d_shader_target_type.</summary>
internal enum Vkd3dTargetType
{
    None = 0,
    SpirvBinary = 1,
    SpirvText = 2,
    D3dAsm = 3,
    D3dBytecode = 4,
    DxbcTpf = 5,                      // VKD3D_SHADER_TARGET_DXBC_TPF (SM4/5 DXBC)
    Glsl = 6,
}

/// <summary>vkd3d_shader_log_level.</summary>
internal enum Vkd3dLogLevel
{
    None = 0,
    Error = 1,
    Warning = 2,
    Info = 3,
}

/// <summary>struct vkd3d_shader_code { const void *code; size_t size; }.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vkd3dShaderCode
{
    public IntPtr Code;   // const void*
    public nuint Size;    // size_t
}

/// <summary>
/// struct vkd3d_shader_compile_info — chained base structure.
/// Field order is load-bearing (it defines the C layout).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vkd3dCompileInfo
{
    public Vkd3dStructureType Type;     // enum (4 bytes) — must be CompileInfo
    public IntPtr Next;                 // const void* -> &Vkd3dHlslSourceInfo
    public Vkd3dShaderCode Source;      // HLSL bytes + size
    public Vkd3dSourceType SourceType;  // enum -> Hlsl
    public Vkd3dTargetType TargetType;  // enum -> DxbcTpf
    public IntPtr Options;              // const vkd3d_shader_compile_option* (NULL)
    public uint OptionCount;            // unsigned int
    public Vkd3dLogLevel LogLevel;      // enum
    public IntPtr SourceName;           // const char* (UTF-8, may be NULL)
}

/// <summary>
/// struct vkd3d_shader_hlsl_source_info — chained via CompileInfo.Next.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vkd3dHlslSourceInfo
{
    public Vkd3dStructureType Type;     // enum -> HlslSourceInfo
    public IntPtr Next;                 // const void* (NULL)
    public IntPtr EntryPoint;           // const char* ("main" if NULL)
    public Vkd3dShaderCode SecondaryCode; // {NULL, 0}
    public IntPtr Profile;              // const char* ("ps_5_0"/"vs_5_0")
}

internal static class Vkd3dNative
{
    // Logical name; the real per-OS file is resolved by Vkd3dLoader's import resolver.
    internal const string LibName = "vkd3d-shader-1";

    /// <summary>
    /// int vkd3d_shader_compile(const vkd3d_shader_compile_info *info,
    ///                          vkd3d_shader_code *out, char **messages);
    /// Returns 0 (VKD3D_OK) on success.
    /// </summary>
    [DllImport(LibName, EntryPoint = "vkd3d_shader_compile", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Compile(
        in Vkd3dCompileInfo compileInfo,
        out Vkd3dShaderCode output,
        out IntPtr messages);

    /// <summary>void vkd3d_shader_free_shader_code(vkd3d_shader_code *code);</summary>
    [DllImport(LibName, EntryPoint = "vkd3d_shader_free_shader_code", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeShaderCode(ref Vkd3dShaderCode code);

    /// <summary>void vkd3d_shader_free_messages(char *messages);</summary>
    [DllImport(LibName, EntryPoint = "vkd3d_shader_free_messages", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeMessages(IntPtr messages);
}
