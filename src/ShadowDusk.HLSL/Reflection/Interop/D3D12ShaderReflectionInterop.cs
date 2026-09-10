#nullable enable

using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace ShadowDusk.HLSL.Reflection.Interop;

// ShadowDusk-OWNED declarations of the D3D12 shader-reflection interfaces DXC hands back from
// IDxcUtils::CreateReflection (Phase 63, issue #203).
//
// WHY THESE EXIST. `Vortice.Direct3D12` used to contribute exactly one thing to the compiler:
// the managed wrapper TYPES for the reflection object `IDxcUtils.CreateReflection` returns.
// That assembly also carries `VersionedDeviceRemovedExtendedData+Union`, an explicit-layout
// struct the CLR refuses to load ("object field at offset 0 that is incorrectly aligned or
// overlapped by a non-object field"), and MonoGame 3.8.5's Content Builder calls an UNGUARDED
// `Assembly.GetTypes()` over every assembly in the consumer's dependency graph - so any
// consumer whose Builder referenced ShadowDusk died before touching a shader. Dropping the
// package is the fix; these declarations are what the extractor consumes instead.
//
// WHAT THEY ARE NOT. Not a substitute reflector and not a different compiler: the SAME pinned
// DXC creates the SAME reflection object through the SAME `IDxcUtils::CreateReflection` call
// (still `Vortice.Dxc`); only the managed projection of the COM vtables changed. The vtable
// slot order and the struct layouts are transcribed from `d3d12shader.h`, which DXC's own
// reflection implementation honours on every OS (its Linux/macOS builds implement the same
// COM-style ABI). Acceptance was zero byte change across every golden and the cross-host
// byte-identity manifest.
//
// SharpGen.Runtime is already in the graph through Vortice.Dxc; `ComObject` gives the
// ref-counted root object its `Dispose` (Release) exactly as Vortice's wrapper had, and
// `CppObject` models the three non-IUnknown reflection classes exactly as Vortice modelled them
// (plain vtables, never released). The `[Guid]` is what `IDxcUtils.CreateReflection<T>` passes
// to DXC as the requested IID.

/// <summary>
/// <c>ID3D12ShaderReflection</c>: the root reflection object for a DXIL module. Ref-counted
/// (an <c>IUnknown</c>), so it is disposed like any <see cref="ComObject"/>.
/// </summary>
[Guid("5a58797d-a72c-478d-8ba2-efc6b0efe88e")]
internal sealed unsafe class ID3D12ShaderReflection : ComObject
{
    // d3d12shader.h vtable: 0-2 IUnknown; 3 GetDesc; 4 GetConstantBufferByIndex;
    // 5 GetConstantBufferByName; 6 GetResourceBindingDesc; 7 GetInputParameterDesc;
    // 8 GetOutputParameterDesc; ... (the rest are unused here).
    private const int SlotGetDesc                 = 3;
    private const int SlotGetConstantBufferByIndex = 4;
    private const int SlotGetResourceBindingDesc  = 6;
    private const int SlotGetInputParameterDesc   = 7;
    private const int SlotGetOutputParameterDesc  = 8;

    public ID3D12ShaderReflection(nint nativePointer) : base(nativePointer) { }

    /// <summary>The module-level description (<c>D3D12_SHADER_DESC</c>).</summary>
    public D3D12ShaderDesc GetDesc()
    {
        D3D12ShaderDesc desc;
        int hr = ((delegate* unmanaged[Stdcall]<void*, D3D12ShaderDesc*, int>)Vtbl[SlotGetDesc])(This, &desc);
        new Result(hr).CheckError();
        return desc;
    }

    /// <summary>The constant buffer at <paramref name="index"/>, in reflection order.</summary>
    public ID3D12ShaderReflectionConstantBuffer GetConstantBufferByIndex(int index)
    {
        void* cb = ((delegate* unmanaged[Stdcall]<void*, uint, void*>)Vtbl[SlotGetConstantBufferByIndex])(This, (uint)index);
        return new ID3D12ShaderReflectionConstantBuffer((nint)cb);
    }

    /// <summary>The bound resource at <paramref name="index"/> (<c>D3D12_SHADER_INPUT_BIND_DESC</c>).</summary>
    public D3D12ShaderInputBindDesc GetResourceBindingDesc(int index)
    {
        D3D12ShaderInputBindDesc.Native native;
        int hr = ((delegate* unmanaged[Stdcall]<void*, uint, D3D12ShaderInputBindDesc.Native*, int>)Vtbl[SlotGetResourceBindingDesc])(This, (uint)index, &native);
        new Result(hr).CheckError();
        return D3D12ShaderInputBindDesc.From(native);
    }

    /// <summary>The input-signature parameter at <paramref name="index"/>.</summary>
    public D3D12SignatureParameterDesc GetInputParameterDesc(int index)
        => GetSignatureParameterDesc(SlotGetInputParameterDesc, index);

    /// <summary>The output-signature parameter at <paramref name="index"/>.</summary>
    public D3D12SignatureParameterDesc GetOutputParameterDesc(int index)
        => GetSignatureParameterDesc(SlotGetOutputParameterDesc, index);

    private D3D12SignatureParameterDesc GetSignatureParameterDesc(int slot, int index)
    {
        D3D12SignatureParameterDesc.Native native;
        int hr = ((delegate* unmanaged[Stdcall]<void*, uint, D3D12SignatureParameterDesc.Native*, int>)Vtbl[slot])(This, (uint)index, &native);
        new Result(hr).CheckError();
        return D3D12SignatureParameterDesc.From(native);
    }

    private void*  This => (void*)NativePointer;
    private void** Vtbl => *(void***)NativePointer;
}

/// <summary>
/// <c>ID3D12ShaderReflectionConstantBuffer</c>. A plain vtable class owned by the root
/// reflection object (not an <c>IUnknown</c>): never released, valid while the root lives.
/// </summary>
internal sealed unsafe class ID3D12ShaderReflectionConstantBuffer : CppObject
{
    // d3d12shader.h vtable: 0 GetDesc; 1 GetVariableByIndex; 2 GetVariableByName.
    private const int SlotGetDesc            = 0;
    private const int SlotGetVariableByIndex = 1;

    public ID3D12ShaderReflectionConstantBuffer(nint nativePointer) : base(nativePointer) { }

    /// <summary>The buffer description (<c>D3D12_SHADER_BUFFER_DESC</c>).</summary>
    public D3D12ShaderBufferDesc GetDesc()
    {
        D3D12ShaderBufferDesc.Native native;
        int hr = ((delegate* unmanaged[Stdcall]<void*, D3D12ShaderBufferDesc.Native*, int>)Vtbl[SlotGetDesc])(This, &native);
        new Result(hr).CheckError();
        return D3D12ShaderBufferDesc.From(native);
    }

    /// <summary>The variable at <paramref name="index"/>, in reflection order.</summary>
    public ID3D12ShaderReflectionVariable GetVariableByIndex(int index)
    {
        void* variable = ((delegate* unmanaged[Stdcall]<void*, uint, void*>)Vtbl[SlotGetVariableByIndex])(This, (uint)index);
        return new ID3D12ShaderReflectionVariable((nint)variable);
    }

    private void*  This => (void*)NativePointer;
    private void** Vtbl => *(void***)NativePointer;
}

/// <summary><c>ID3D12ShaderReflectionVariable</c>. Plain vtable class; see the constant-buffer remarks.</summary>
internal sealed unsafe class ID3D12ShaderReflectionVariable : CppObject
{
    // d3d12shader.h vtable: 0 GetDesc; 1 GetType; 2 GetBuffer; 3 GetInterfaceSlot.
    private const int SlotGetDesc = 0;
    private const int SlotGetType = 1;

    public ID3D12ShaderReflectionVariable(nint nativePointer) : base(nativePointer) { }

    /// <summary>The variable description (<c>D3D12_SHADER_VARIABLE_DESC</c>).</summary>
    public D3D12ShaderVariableDesc GetDesc()
    {
        D3D12ShaderVariableDesc.Native native;
        int hr = ((delegate* unmanaged[Stdcall]<void*, D3D12ShaderVariableDesc.Native*, int>)Vtbl[SlotGetDesc])(This, &native);
        new Result(hr).CheckError();
        return D3D12ShaderVariableDesc.From(native);
    }

    /// <summary>The variable's type object.</summary>
    public ID3D12ShaderReflectionType GetVariableType()
    {
        void* type = ((delegate* unmanaged[Stdcall]<void*, void*>)Vtbl[SlotGetType])(This);
        return new ID3D12ShaderReflectionType((nint)type);
    }

    private void*  This => (void*)NativePointer;
    private void** Vtbl => *(void***)NativePointer;
}

/// <summary><c>ID3D12ShaderReflectionType</c>. Plain vtable class; see the constant-buffer remarks.</summary>
internal sealed unsafe class ID3D12ShaderReflectionType : CppObject
{
    // d3d12shader.h vtable: 0 GetDesc; 1 GetMemberTypeByIndex; 2 GetMemberTypeByName;
    // 3 GetMemberTypeName; 4 IsEqual; 5 GetSubType; 6 GetBaseClass; ... (unused here).
    private const int SlotGetDesc              = 0;
    private const int SlotGetMemberTypeByIndex = 1;
    private const int SlotGetMemberTypeName    = 3;

    public ID3D12ShaderReflectionType(nint nativePointer) : base(nativePointer) { }

    /// <summary>The type description (<c>D3D12_SHADER_TYPE_DESC</c>).</summary>
    public D3D12ShaderTypeDesc GetDesc()
    {
        D3D12ShaderTypeDesc.Native native;
        int hr = ((delegate* unmanaged[Stdcall]<void*, D3D12ShaderTypeDesc.Native*, int>)Vtbl[SlotGetDesc])(This, &native);
        new Result(hr).CheckError();
        return D3D12ShaderTypeDesc.From(native);
    }

    /// <summary>The struct member type at <paramref name="index"/>.</summary>
    public ID3D12ShaderReflectionType GetMemberTypeByIndex(int index)
    {
        void* type = ((delegate* unmanaged[Stdcall]<void*, uint, void*>)Vtbl[SlotGetMemberTypeByIndex])(This, (uint)index);
        return new ID3D12ShaderReflectionType((nint)type);
    }

    /// <summary>The struct member name at <paramref name="index"/>.</summary>
    public string GetMemberTypeName(int index)
    {
        sbyte* name = ((delegate* unmanaged[Stdcall]<void*, uint, sbyte*>)Vtbl[SlotGetMemberTypeName])(This, (uint)index);
        return Marshal.PtrToStringAnsi((nint)name)!;
    }

    private void*  This => (void*)NativePointer;
    private void** Vtbl => *(void***)NativePointer;
}

// ---------------------------------------------------------------------------------------------
// The D3D12_*_DESC structs, transcribed from d3d12shader.h. Each is blittable exactly as the
// header lays it out (LPCSTR = pointer, UINT = 32-bit, enums = 32-bit, BYTE = 8-bit); the
// nested `Native` is what the vtable call fills, and `From` projects it to the managed view
// with the same Int32 field widths and the same `Marshal.PtrToStringAnsi` string decoding the
// previous Vortice projection used, so every consumer computation is unchanged.
// ---------------------------------------------------------------------------------------------

/// <summary><c>D3D12_SHADER_DESC</c>. Fully blittable (its one string, <c>Creator</c>, is left as a pointer - unused).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct D3D12ShaderDesc
{
    public int  Version;
    public nint Creator;
    public int  Flags;
    public int  ConstantBuffers;
    public int  BoundResources;
    public int  InputParameters;
    public int  OutputParameters;
    public int  InstructionCount;
    public int  TempRegisterCount;
    public int  TempArrayCount;
    public int  DefCount;
    public int  DclCount;
    public int  TextureNormalInstructions;
    public int  TextureLoadInstructions;
    public int  TextureCompInstructions;
    public int  TextureBiasInstructions;
    public int  TextureGradientInstructions;
    public int  FloatInstructionCount;
    public int  IntInstructionCount;
    public int  UintInstructionCount;
    public int  StaticFlowControlCount;
    public int  DynamicFlowControlCount;
    public int  MacroInstructionCount;
    public int  ArrayInstructionCount;
    public int  CutInstructionCount;
    public int  EmitInstructionCount;
    public int  GSOutputTopology;
    public int  GSMaxOutputVertexCount;
    public int  InputPrimitive;
    public int  PatchConstantParameters;
    public int  GSInstanceCount;
    public int  ControlPoints;
    public int  HSOutputPrimitive;
    public int  HSPartitioning;
    public int  TessellatorDomain;
    public int  BarrierInstructions;
    public int  InterlockedInstructions;
    public int  TextureStoreInstructions;
}

/// <summary><c>D3D12_SHADER_INPUT_BIND_DESC</c>.</summary>
internal readonly struct D3D12ShaderInputBindDesc
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public nint Name;
        public uint Type;
        public int  BindPoint;
        public int  BindCount;
        public uint Flags;
        public uint ReturnType;
        public uint Dimension;
        public int  NumSamples;
        public int  Space;
        public int  Id;
    }

    public string Name      { get; private init; }
    /// <summary>Raw <c>D3D_SHADER_INPUT_TYPE</c>; compare against <see cref="D3DShaderInputType"/>.</summary>
    public uint   Type      { get; private init; }
    public int    BindPoint { get; private init; }
    /// <summary>Raw <c>D3D_SRV_DIMENSION</c>; mapped by <c>D3DReflectionMaps.MapSrvDimension</c>.</summary>
    public uint   Dimension { get; private init; }

    internal static D3D12ShaderInputBindDesc From(in Native native) => new()
    {
        Name      = Marshal.PtrToStringAnsi(native.Name)!,
        Type      = native.Type,
        BindPoint = native.BindPoint,
        Dimension = native.Dimension,
    };
}

/// <summary><c>D3D12_SHADER_BUFFER_DESC</c>.</summary>
internal readonly struct D3D12ShaderBufferDesc
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public nint Name;
        public uint Type;
        public int  Variables;
        public int  Size;
        public uint Flags;
    }

    public string Name          { get; private init; }
    public int    VariableCount { get; private init; }
    public int    Size          { get; private init; }

    internal static D3D12ShaderBufferDesc From(in Native native) => new()
    {
        Name          = Marshal.PtrToStringAnsi(native.Name)!,
        VariableCount = native.Variables,
        Size          = native.Size,
    };
}

/// <summary><c>D3D12_SHADER_VARIABLE_DESC</c>.</summary>
internal readonly struct D3D12ShaderVariableDesc
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public nint Name;
        public int  StartOffset;
        public int  Size;
        public uint Flags;
        public nint DefaultValue;
        public int  StartTexture;
        public int  TextureSize;
        public int  StartSampler;
        public int  SamplerSize;
    }

    public string Name        { get; private init; }
    public int    StartOffset { get; private init; }
    public int    Size        { get; private init; }

    internal static D3D12ShaderVariableDesc From(in Native native) => new()
    {
        Name        = Marshal.PtrToStringAnsi(native.Name)!,
        StartOffset = native.StartOffset,
        Size        = native.Size,
    };
}

/// <summary><c>D3D12_SHADER_TYPE_DESC</c>.</summary>
internal readonly struct D3D12ShaderTypeDesc
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public uint Class;
        public uint Type;
        public int  Rows;
        public int  Columns;
        public int  Elements;
        public int  Members;
        public int  Offset;
        public nint Name;
    }

    /// <summary>Raw <c>D3D_SHADER_VARIABLE_CLASS</c>; mapped by <c>D3DReflectionMaps.TryMapClass</c>.</summary>
    public uint Class        { get; private init; }
    /// <summary>Raw <c>D3D_SHADER_VARIABLE_TYPE</c>; mapped by <c>D3DReflectionMaps.TryMapType</c>.</summary>
    public uint Type         { get; private init; }
    public int  RowCount     { get; private init; }
    public int  ColumnCount  { get; private init; }
    public int  ElementCount { get; private init; }
    public int  MemberCount  { get; private init; }
    public int  Offset       { get; private init; }

    internal static D3D12ShaderTypeDesc From(in Native native) => new()
    {
        Class        = native.Class,
        Type         = native.Type,
        RowCount     = native.Rows,
        ColumnCount  = native.Columns,
        ElementCount = native.Elements,
        MemberCount  = native.Members,
        Offset       = native.Offset,
    };
}

/// <summary><c>D3D12_SIGNATURE_PARAMETER_DESC</c>.</summary>
internal readonly struct D3D12SignatureParameterDesc
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Native
    {
        public nint Name;
        public int  SemanticIndex;
        public int  Register;
        public uint SystemValueType;
        public uint ComponentType;
        public byte Mask;
        public byte ReadWriteMask;
        public int  Stream;
        public uint MinPrecision;
    }

    public string                 SemanticName    { get; private init; }
    public int                    SemanticIndex   { get; private init; }
    public int                    Register        { get; private init; }
    public D3DSystemValueType     SystemValueType { get; private init; }
    public D3DRegisterComponentType ComponentType { get; private init; }
    public byte                   UsageMask       { get; private init; }

    internal static D3D12SignatureParameterDesc From(in Native native) => new()
    {
        SemanticName    = Marshal.PtrToStringAnsi(native.Name)!,
        SemanticIndex   = native.SemanticIndex,
        Register        = native.Register,
        SystemValueType = (D3DSystemValueType)native.SystemValueType,
        ComponentType   = (D3DRegisterComponentType)native.ComponentType,
        UsageMask       = native.Mask,
    };
}

/// <summary>The <c>D3D_SHADER_INPUT_TYPE</c> values the extractor distinguishes.</summary>
internal static class D3DShaderInputType
{
    public const uint ConstantBuffer = 0; // D3D_SIT_CBUFFER
    public const uint Texture        = 2; // D3D_SIT_TEXTURE
    public const uint Sampler        = 3; // D3D_SIT_SAMPLER
}

/// <summary>The <c>D3D_SHADER_VARIABLE_CLASS</c> values the extractor distinguishes.</summary>
internal static class D3DShaderVariableClass
{
    public const uint Struct = 5; // D3D_SVC_STRUCT
}

/// <summary>
/// <c>D3D_NAME</c>. The member names are load-bearing: <c>SignatureParameterReflection.SystemValue</c>
/// is this enum's <c>ToString()</c>, and the pure-managed <c>RdefReader</c> reproduces the same
/// spellings for DXBC, so the two reflection routes agree. Undefined values render numerically,
/// as .NET renders any undefined enum value.
/// </summary>
internal enum D3DSystemValueType : uint
{
    Undefined                  = 0,
    Position                   = 1,
    ClipDistance               = 2,
    CullDistance               = 3,
    RenderTargetArrayIndex     = 4,
    ViewportArrayIndex         = 5,
    VertexId                   = 6,
    PrimitiveId                = 7,
    InstanceId                 = 8,
    IsFrontFace                = 9,
    SampleIndex                = 10,
    FinalQuadEdgeTessfactor    = 11,
    FinalQuadInsideTessfactor  = 12,
    FinalTriEdgeTessfactor     = 13,
    FinalTriInsideTessfactor   = 14,
    FinalLineDetailTessfactor  = 15,
    FinalLineDensityTessfactor = 16,
    Barycentrics               = 23,
    Shadingrate                = 24,
    Cullprimitive              = 25,
    Target                     = 64,
    Depth                      = 65,
    Coverage                   = 66,
    DepthGreaterEqual          = 67,
    DepthLessEqual             = 68,
    StencilRef                 = 69,
    InnerCoverage              = 70,
}

/// <summary>
/// <c>D3D_REGISTER_COMPONENT_TYPE</c>. Member names are load-bearing for the same reason as
/// <see cref="D3DSystemValueType"/> (<c>SignatureParameterReflection.ComponentType</c>).
/// </summary>
internal enum D3DRegisterComponentType : uint
{
    Unknown = 0,
    UInt32  = 1,
    SInt32  = 2,
    Float32 = 3,
    UInt16  = 4,
    SInt16  = 5,
    Float16 = 6,
    UInt64  = 7,
    SInt64  = 8,
    Float64 = 9,
}
