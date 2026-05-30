#!/usr/bin/env python3
"""Decode an mgfxc golden .mgfx byte-by-byte against MonoGame 3.8's EffectReader
format, printing each field with its offset. If decoding lands EXACTLY on the
trailing "MGFX" footer with no bytes left over, the format model is correct.

This is the spec-pinning instrument for the MgfxWriter rework — run it on every
golden to confirm the byte layout before changing the C# writer.

Usage: python decode_mgfx.py <path-to.mgfx>
"""
import struct
import sys


class R:
    def __init__(self, data):
        self.d = data
        self.i = 0

    def byte(self):
        v = self.d[self.i]; self.i += 1; return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.i)[0]; self.i += 4; return v

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
    # EffectAnnotation list: count(int32) then per-annotation. Goldens use 0.
    n = r.i32()
    if n != 0:
        raise RuntimeError(f"annotations={n} not supported in decoder")
    return n


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

    # constant buffers
    ncb = r.i32()
    print(f"[{r.i}] constant buffers: {ncb}")
    for c in range(ncb):
        name = r.s7(); size = r.i16(); np = r.i32()
        idx = []; off = []
        for _ in range(np):            # interleaved: index(int32), offset(uint16)
            idx.append(r.i32()); off.append(r.u16())
        print(f"    cb[{c}] name={name!r} size={size} params={np} idx={idx} off={off}")

    # shaders
    nsh = r.i32()
    print(f"[{r.i}] shaders: {nsh}")
    for s in range(nsh):
        is_vs = r.byte()
        blen = r.i32()
        off0 = r.i
        body = r.d[r.i:r.i + blen]; r.i += blen
        nsamp = r.byte()
        print(f"    sh[{s}] isVertex={is_vs} bytecodeLen={blen} (@{off0}) samplers={nsamp}")
        # show a snippet of the GLSL
        txt = body.decode("latin-1", "replace")
        snippet = txt[:60].replace("\n", "\\n")
        print(f"        glsl: {snippet!r}")
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
            sparam = r.byte()   # parameter index is a single byte
            print(f"        sampler[{k}] type={stype} texSlot={tslot} sampSlot={sslot} "
                  f"hasState={has_state} name={sname!r} param={sparam} state={state}")
        ncbi = r.byte()
        cbis = [r.byte() for _ in range(ncbi)]
        print(f"        cbufferIndices: count={ncbi} {cbis}")
        natt = r.byte()
        print(f"        attributes: {natt}")
        for a in range(natt):
            an = r.s7(); ausage = r.byte(); aindex = r.byte(); aloc = r.i16()
            print(f"          attr[{a}] name={an!r} usage={ausage} index={aindex} loc={aloc}")

    # parameters
    npar = r.i32()
    print(f"[{r.i}] parameters: {npar}")
    for p in range(npar):
        pclass = r.byte(); ptype = r.byte(); pname = r.s7(); psem = r.s7()
        read_annotations(r)
        rows = r.byte(); cols = r.byte()
        nmem = r.i32(); members = [r.i32() for _ in range(nmem)]
        nelem = r.i32(); elems = [r.i32() for _ in range(nelem)]
        # Value params (Scalar/Vector/Matrix, no members/elements) carry a raw
        # default-value blob of rows*cols*4 bytes, NO length prefix.
        data = b""
        if pclass <= 2 and nmem == 0 and nelem == 0:
            dlen = rows * cols * 4
            data = r.d[r.i:r.i + dlen]; r.i += dlen
        print(f"    p[{p}] class={pclass} type={ptype} name={pname!r} sem={psem!r} "
              f"rows={rows} cols={cols} members={members} elems={elems} dataLen={len(data)}")

    # techniques
    ntech = r.i32()
    print(f"[{r.i}] techniques: {ntech}")
    for t in range(ntech):
        tname = r.s7(); read_annotations(r); npass = r.i32()
        print(f"    tech[{t}] name={tname!r} passes={npass}")
        for pa in range(npass):
            pname = r.s7(); read_annotations(r); vsi = r.i16(); psi = r.i16()
            print(f"        pass[{pa}] name={pname!r} vsIndex={vsi} psIndex={psi}")
            # render-state block: 3 optional state objects (byte present flag)
            for which in ("blend", "depth", "raster"):
                present = r.byte()
                if present:
                    # KV pairs terminated by 0xFF sentinel: (byte field, int32 value)*
                    kvs = []
                    while True:
                        fid = r.byte()
                        if fid == 0xFF:
                            break
                        val = r.i32()
                        kvs.append((fid, val))
                    print(f"            {which}: {kvs}")

    tail = r.d[r.i:r.i + 4]
    r.i += 4
    print(f"[{r.i - 4}] footer: {tail!r}")
    left = len(r.d) - r.i
    print(f"LEFTOVER BYTES: {left}  {'<<< MISMATCH' if left != 0 else 'OK (clean)'}")
    if tail != b"MGFX":
        print("FOOTER MISMATCH <<<")


if __name__ == "__main__":
    main()
