// =============================================================================
// ComputeShader.fx
// -----------------------------------------------------------------------------
// Vendored third-party shader (provenance header added by ShadowDusk; the shader
// code below is UNMODIFIED from upstream).
//   Project    : KNI (kniEngine/kni)
//   Repo       : https://github.com/kniEngine/kni
//   Commit     : 1dda67cf2fc7c0cc68ea87ebc905f6ebb5d99468
//   Upstream   : Tests/Assets/Effects/ComputeShader.fx
//   License    : Ms-PL - Copyright (c) 2014-2025 Nick Kastellanos; Copyright (c) 2009-2021 The MonoGame Team (see ./LICENSE, ./NOTICE.md)
//   Exercises  : Compute shader (cs_5_0, StructuredBuffer/RWStructuredBuffer) - ShouldCompileComputeShader.
// =============================================================================
﻿// code from @cpt-max Compute fork.
// https://github.com/cpt-max/MonoGame/commit/eebfda5246bed106618c17eca416aae548547c04

struct Input
{
    float2 pos;
};

struct Output
{
    int collisions;
};

StructuredBuffer<Input> Inputs;
RWStructuredBuffer<Output> Outputs;

int ObjectCount;
float ObjectSize;

[numthreads(10, 1, 1)]
void CS(uint3 localID : SV_GroupThreadID, uint3 dispatchID : SV_GroupID,
	    uint localIndex : SV_GroupIndex, uint3 globalID : SV_DispatchThreadID)
{
    float2 pos = Inputs[globalID.x].pos;
    int collisions = 0;
    
    for (int i = 0; i < ObjectCount; i++)
    {
        if ((uint) i == globalID.x)
            continue;
        
        float2 posOther = Inputs[i].pos;
        float dist = distance(pos, posOther);
        if (dist < ObjectSize)
            collisions++;
    }
    
    Outputs[globalID.x].collisions = collisions;
}

technique Tech0
{
    pass Pass0
    {
        ComputeShader = compile cs_5_0 CS();
    }
}
