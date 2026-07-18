#!/usr/bin/env python3
"""Decode a real mgfxc-Vulkan golden .mgfx byte-by-byte against MonoGame 3.8.5's
real container format, printing each field with its offset. If decoding lands
EXACTLY on the trailing "MGFX" footer with no bytes left over, the format model
is correct.

This is the spec-pinning instrument for ShadowDusk's Vulkan writer — the format
was read directly from MonoGame's own source (not reverse-engineered by hex
diffing): github.com/MonoGame/MonoGame v3.8.5,
Tools/MonoGame.Effect.Compiler/Effect/{ShaderProfile.Vulkan.cs,
EffectObject.writer.cs, ShaderData.writer.cs, ConstantBufferData.writer.cs}.
See plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md for the full write-up.

The one real deviation from decode_mgfx.py's v10/v11 model: for Vulkan, the
per-shader "ShaderCode" field is not raw SPIR-V — it's SPIR-V wrapped in a
Vulkan descriptor-layout header (uniform/texture/sampler bitmasks, 16 texture
types, and a VkDescriptorSetLayoutBinding table), decoded here as `vk_layout`.

Usage: python decode_mgfx_vulkan.py <path-to.mgfx>
"""
import struct
import sys

SPIRV_MAGIC = 0x07230203


class R:
    def __init__(self, data):
        self.d = data
        self.i = 0

    def byte(self):
        v = self.d[self.i]; self.i += 1; return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.i)[0]; self.i += 4; return v

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.i)[0]; self.i += 4; return v

    def u64(self):
        v = struct.unpack_from("<Q", self.d, self.i)[0]; self.i += 8; return v

    def u16(self):
        v = struct.unpack_from("<H", self.d, self.i)[0]; self.i += 2; return v

    def i16(self):
        v = struct.unpack_from("<h", self.d, self.i)[0]; self.i += 2; return v

    def f32(self):
        v = struct.unpack_from("<f", self.d, self.i)[0]; self.i += 4; return v

    def s7(self):
        # BinaryReader.ReadString: 7-bit encoded length prefix, then UTF8
        n = 0; shift = 0
        while True:
            b = self.byte()
            n |= (b & 0x7F) << shift
            if (b & 0x80) == 0:
                break
            shift += 7
        s = self.d[self.i:self.i + n].decode("utf-8", "replace"); self.i += n
        return s


def read_annotations(r):
    # Real mgfxc never emits populated annotations (annotation_handles is
    # always empty in practice), so this is the int32 count and nothing else.
    return r.i32()


def read_vulkan_shader_code(body, blen):
    """Decode the Vulkan descriptor-layout-prefixed ShaderCode blob. `body` is
    exactly `blen` bytes (the full ShaderCode field). Returns a dict plus the
    trailing raw SPIR-V bytes."""
    r = R(body)
    uniform_count = r.i32()
    uniform_slots = r.u32()
    texture_slots = r.u32()
    sampler_slots = r.u32()
    texture_types = [r.u32() for _ in range(16)]
    binding_count = r.u32()
    bindings = []
    for _ in range(binding_count):
        binding = r.u32()
        desc_type = r.u32()
        desc_count = r.u32()
        stage_flags = r.u32()
        immutable_samplers = r.u64()
        bindings.append((binding, desc_type, desc_count, stage_flags, immutable_samplers))
    spirv = body[r.i:]
    return {
        "uniformBufferCount": uniform_count,
        "uniformSlots": uniform_slots,
        "textureSlots": texture_slots,
        "samplerSlots": sampler_slots,
        "textureTypes": texture_types,
        "bindings": bindings,
        "spirvLen": len(spirv),
        "spirvMagicOk": len(spirv) >= 4 and struct.unpack_from("<I", spirv, 0)[0] == SPIRV_MAGIC,
    }


def main():
    path = sys.argv[1]
    with open(path, "rb") as f:
        r = R(f.read())
    print(f"== {path} ({len(r.d)} bytes) ==")

    sig = r.d[0:4]
    r.i = 4
    ver = r.byte()
    prof = r.byte()
    key = r.i32()
    print(f"[0] header: sig={sig!r} version={ver} profile={prof} effectKey=0x{key & 0xffffffff:08x}")
    if prof != 80:
        print(f"PROFILE MISMATCH <<< expected 80 (Vulkan), got {prof}")

    # constant buffers — byte-identical shape to the v10/v11 MgfxWriter model
    ncb = r.i32()
    print(f"[{r.i}] constant buffers: {ncb}")
    for c in range(ncb):
        name = r.s7(); size = r.u16(); np = r.i32()
        idx = []; off = []
        for _ in range(np):
            idx.append(r.i32()); off.append(r.u16())
        print(f"    cb[{c}] name={name!r} size={size} params={np} idx={idx} off={off}")
    if ncb > 1:
        print("WARNING: real mgfxc only supports ONE constant buffer per Vulkan shader stage")

    # shaders — always v11-shaped (SourceFile/Entrypoint unconditional in 3.8.5)
    nsh = r.i32()
    print(f"[{r.i}] shaders: {nsh}")
    for s in range(nsh):
        is_vs = r.byte()
        source_file = r.s7()
        entrypoint = r.s7()
        blen = r.i32()
        off0 = r.i
        body = r.d[r.i:r.i + blen]; r.i += blen
        vk = read_vulkan_shader_code(body, blen)
        print(f"    sh[{s}] isVertex={is_vs} sourceFile={source_file!r} entrypoint={entrypoint!r} "
              f"shaderCodeLen={blen} (@{off0})")
        print(f"        vkLayout: uniformBufferCount={vk['uniformBufferCount']} "
              f"uniformSlots=0x{vk['uniformSlots']:08x} textureSlots=0x{vk['textureSlots']:08x} "
              f"samplerSlots=0x{vk['samplerSlots']:08x}")
        print(f"        vkLayout: bindings={vk['bindings']}")
        print(f"        spirv: len={vk['spirvLen']} magicOk={vk['spirvMagicOk']}")
        if not vk["spirvMagicOk"]:
            print("SPIR-V MAGIC MISMATCH <<<")

        nsamp = r.byte()
        print(f"        samplers: {nsamp}")
        for k in range(nsamp):
            stype = r.byte(); tslot = r.byte(); sslot = r.byte()
            has_state = r.byte()
            state = None
            if has_state:
                au, av, aw = r.byte(), r.byte(), r.byte()
                bc = (r.byte(), r.byte(), r.byte(), r.byte())
                filt = r.byte(); maxaniso = r.i32(); maxmip = r.i32(); bias = r.f32()
                state = (au, av, aw, bc, filt, maxaniso, maxmip, bias)
            sname = r.s7()
            sparam = r.byte()
            print(f"            sampler[{k}] type={stype} texSlot={tslot} sampSlot={sslot} "
                  f"hasState={has_state} name={sname!r} param={sparam} state={state}")
        ncbi = r.byte()
        cbis = [r.byte() for _ in range(ncbi)]
        print(f"        cbufferIndices: count={ncbi} {cbis}")
        natt = r.byte()
        print(f"        attributes: {natt}")
        for a in range(natt):
            an = r.s7(); ausage = r.byte(); aindex = r.byte(); aloc = r.i16()
            print(f"          attr[{a}] name={an!r} usage={ausage} index={aindex} loc={aloc}")

    # parameters — recursive, identical shape to decode_mgfx.py's model
    def read_parameter_list(depth):
        n = r.i32()
        for p in range(n):
            pclass = r.byte(); ptype = r.byte(); pname = r.s7(); psem = r.s7()
            read_annotations(r)
            rows = r.byte(); cols = r.byte()
            nelem = read_parameter_list(depth + 1)
            nmem = read_parameter_list(depth + 1)
            dlen = 0
            if pclass <= 2 and nelem == 0 and nmem == 0:
                dlen = rows * cols * 4
                r.i += dlen
            print(f"    {'    ' * depth}p[{p}] class={pclass} type={ptype} name={pname!r} "
                  f"sem={psem!r} rows={rows} cols={cols} elems={nelem} members={nmem} dataLen={dlen}")
        return n

    print(f"[{r.i}] parameters:")
    read_parameter_list(0)

    # techniques — identical shape to decode_mgfx.py's model (render state
    # confirmed still baked in for Vulkan, same as GL/DX)
    ntech = r.i32()
    print(f"[{r.i}] techniques: {ntech}")
    for t in range(ntech):
        tname = r.s7(); read_annotations(r); npass = r.i32()
        print(f"    tech[{t}] name={tname!r} passes={npass}")
        for pa in range(npass):
            pname = r.s7(); read_annotations(r); vsi = r.i32(); psi = r.i32()
            print(f"        pass[{pa}] name={pname!r} vsIndex={vsi} psIndex={psi}")
            if r.byte():
                blend = dict(
                    alphaFunc=r.byte(), alphaDst=r.byte(), alphaSrc=r.byte(),
                    blendFactor=(r.byte(), r.byte(), r.byte(), r.byte()),
                    colorFunc=r.byte(), colorDst=r.byte(), colorSrc=r.byte(),
                    cwc=(r.byte(), r.byte(), r.byte(), r.byte()),
                    multiSampleMask=r.i32())
                print(f"            blend: {blend}")
            if r.byte():
                depth = dict(
                    ccwZFail=r.byte(), ccwFail=r.byte(), ccwFunc=r.byte(), ccwPass=r.byte(),
                    zEnable=r.byte(), zFunc=r.byte(), zWrite=r.byte(),
                    stencilRef=r.i32(), stencilZFail=r.byte(), stencilEnable=r.byte(),
                    stencilFail=r.byte(), stencilFunc=r.byte(), stencilMask=r.i32(),
                    stencilPass=r.byte(), stencilWriteMask=r.i32(), twoSided=r.byte())
                print(f"            depth: {depth}")
            if r.byte():
                raster = dict(
                    cullMode=r.byte(), depthBias=r.f32(), fillMode=r.byte(),
                    msaa=r.byte(), scissor=r.byte(), slopeScaleDepthBias=r.f32())
                print(f"            raster: {raster}")

    tail = r.d[r.i:r.i + 4]
    r.i += 4
    print(f"[{r.i - 4}] footer: {tail!r}")
    left = len(r.d) - r.i
    print(f"LEFTOVER BYTES: {left}  {'<<< MISMATCH' if left != 0 else 'OK (clean)'}")
    if tail != b"MGFX":
        print("FOOTER MISMATCH <<<")


if __name__ == "__main__":
    main()
