#!/usr/bin/env bash
# Smoke-test a freshly built vkd3d-compiler. Called by build-vkd3d-natives.yml on
# every RID with the path to the binary as $1.
#
# The dynamic-loop case is the issue #212 repro at the native level: a `[loop]` with
# a runtime trip count. vkd3d <= 1.17 fails it with
# "E5017: Aborting due to not yet implemented feature: Instruction type HLSL_IR_LOOP";
# SM2-3 loop support landed in 2.0. Keeping it here means a native that cannot do
# loops can never be published as a >= 2.0 artifact by accident.
#
# SM1-3 wants a `COLOR` pixel-shader output; SM4+ requires `SV_Target` and, from 2.1,
# explicitly rejects user-defined semantics there. Hence two sources, not one.
set -euo pipefail

compiler="${1:?usage: vkd3d-smoke.sh <path-to-vkd3d-compiler>}"

cat > smoke.hlsl <<'HLSL'
float4 main() : COLOR { return float4(1, 0, 0, 1); }
HLSL

cat > smoke_sm5.hlsl <<'HLSL'
float4 main() : SV_Target { return float4(1, 0, 0, 1); }
HLSL

cat > smoke_loop.hlsl <<'HLSL'
int iterations;

float4 main() : COLOR
{
    float acc = 0;
    [loop]
    for (int i = 0; i < iterations; i++)
    {
        acc += 0.01;
    }
    return float4(acc, 0, 0, 1);
}
HLSL

"$compiler" -e main -p ps_2_0 -b d3dbc -o smoke.fxo smoke.hlsl
test -s smoke.fxo && echo "d3dbc ps_2_0 smoke OK ($(wc -c < smoke.fxo) bytes)"

"$compiler" -e main -p ps_5_0 -b dxbc-tpf -o smoke.dxbc smoke_sm5.hlsl
test -s smoke.dxbc && echo "dxbc-tpf ps_5_0 smoke OK ($(wc -c < smoke.dxbc) bytes)"

"$compiler" -e main -p ps_3_0 -b d3dbc -o smoke_loop.fxo smoke_loop.hlsl
test -s smoke_loop.fxo && echo "d3dbc ps_3_0 dynamic-loop smoke OK ($(wc -c < smoke_loop.fxo) bytes)"
