#nullable enable

namespace ShadowDusk.Core.Reflection.Spirv;

/// <summary>
/// The subset of SPIR-V opcodes the reflection parser consumes.
/// Values are the canonical numeric opcodes from the SPIR-V specification.
/// </summary>
internal enum SpirvOpcode : ushort
{
    OpName              = 5,
    OpMemberName        = 6,
    OpEntryPoint        = 15,
    OpTypeVoid          = 19,
    OpTypeBool          = 20,
    OpTypeInt           = 21,
    OpTypeFloat         = 22,
    OpTypeVector        = 23,
    OpTypeMatrix        = 24,
    OpTypeImage         = 25,
    OpTypeSampler       = 26,
    OpTypeSampledImage  = 27,
    OpTypeArray         = 28,
    OpTypeRuntimeArray  = 29,
    OpTypeStruct        = 30,
    OpTypePointer       = 32,
    OpConstant          = 43,
    OpVariable          = 59,
    OpDecorate          = 71,
    OpMemberDecorate    = 72,
}

/// <summary>SPIR-V decoration tokens used by the reflection parser.</summary>
internal enum SpirvDecoration
{
    Block          = 2,
    RowMajor       = 4,
    ColMajor       = 5,
    ArrayStride    = 6,
    MatrixStride   = 7,
    BufferBlock    = 3,
    Binding        = 33,
    DescriptorSet  = 34,
    Offset         = 35,
}

/// <summary>SPIR-V storage classes relevant to resource classification.</summary>
internal enum SpirvStorageClass
{
    UniformConstant = 0,
    Uniform         = 2,
    PushConstant    = 9,
    StorageBuffer   = 12,
}

/// <summary>SPIR-V image <c>Dim</c> operand values.</summary>
internal enum SpirvDim
{
    Dim1D   = 0,
    Dim2D   = 1,
    Dim3D   = 2,
    Cube    = 3,
}
