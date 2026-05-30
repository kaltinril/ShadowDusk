#!/usr/bin/env python3
"""DX-aware byte decoder for MonoGame .mgfx (DirectX_11 profile). Throwaway
spike: prints bytecode as hex magic + length (not as GLSL text), reads pass
shader indices as int32 (matching MonoGame EffectReader.ReadInt32). Confirms the
DX shader-record layout against the goldens.

Usage: python decode_mgfx_dx.py <path-to.mgfx>
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
    n = r.i32()
    if n != 0:
        raise RuntimeError(f"annotations={n} not supported")
    return n


def main():
    path = sys.argv[1]
    with open(path, "rb") as f:
        r = R(f.read())
    print(f"== {path} ({len(r.d)} bytes) ==")

    sig = r.d[0:4]; r.i = 4
    ver = r.byte(); prof = r.byte(); key = r.i32()
    print(f"[0] header: sig={sig!r} version={ver} profile={prof} effectKey=0x{key & 0xffffffff:08x}")

    ncb = r.i32()
    print(f"[{r.i}] constant buffers: {ncb}")
    for c in range(ncb):
        name = r.s7(); size = r.i16(); np = r.i32()
        idx = []; off = []
        for _ in range(np):
            idx.append(r.i32()); off.append(r.u16())
        print(f"    cb[{c}] name={name!r} size={size} params={np} idx={idx} off={off}")

    nsh = r.i32()
    print(f"[{r.i}] shaders: {nsh}")
    for s in range(nsh):
        is_vs = r.byte()
        blen = r.i32()
        off0 = r.i
        body = r.d[r.i:r.i + blen]; r.i += blen
        magic = body[0:4]
        print(f"    sh[{s}] isVertex={is_vs} bytecodeLen={blen} (@{off0}) magic={magic!r}")
        nsamp = r.byte()
        print(f"        samplers={nsamp}")
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
            print(f"        sampler[{k}] type={stype} texSlot={tslot} sampSlot={sslot} "
                  f"hasState={has_state} name={sname!r} param={sparam} state={state}")
        ncbi = r.byte()
        cbis = [r.byte() for _ in range(ncbi)]
        print(f"        cbufferIndices: count={ncbi} {cbis}")
        natt = r.byte()
        print(f"        attributes: count={natt}")
        for a in range(natt):
            an = r.s7(); ausage = r.byte(); aindex = r.byte(); aloc = r.i16()
            print(f"          attr[{a}] name={an!r} usage={ausage} index={aindex} loc={aloc}")

    npar = r.i32()
    print(f"[{r.i}] parameters: {npar}")
    for p in range(npar):
        pclass = r.byte(); ptype = r.byte(); pname = r.s7(); psem = r.s7()
        read_annotations(r)
        rows = r.byte(); cols = r.byte()
        nmem = r.i32(); members = [r.i32() for _ in range(nmem)]
        nelem = r.i32(); elems = [r.i32() for _ in range(nelem)]
        data = b""
        if pclass <= 2 and nmem == 0 and nelem == 0:
            dlen = rows * cols * 4
            data = r.d[r.i:r.i + dlen]; r.i += dlen
        print(f"    p[{p}] class={pclass} type={ptype} name={pname!r} sem={psem!r} "
              f"rows={rows} cols={cols} members={members} elems={elems} dataLen={len(data)}")

    ntech = r.i32()
    print(f"[{r.i}] techniques: {ntech}")
    for t in range(ntech):
        tname = r.s7(); read_annotations(r); npass = r.i32()
        print(f"    tech[{t}] name={tname!r} passes={npass}")
        for pa in range(npass):
            pname = r.s7(); read_annotations(r); vsi = r.i32(); psi = r.i32()
            print(f"        pass[{pa}] name={pname!r} vsIndex={vsi} psIndex={psi}")
            for which in ("blend", "depth", "raster"):
                present = r.byte()
                if present:
                    kvs = []
                    while True:
                        fid = r.byte()
                        if fid == 0xFF:
                            break
                        val = r.i32()
                        kvs.append((fid, val))
                    print(f"            {which}: {kvs}")

    tail = r.d[r.i:r.i + 4]; r.i += 4
    print(f"[{r.i - 4}] footer: {tail!r}")
    left = len(r.d) - r.i
    print(f"LEFTOVER BYTES: {left}  {'<<< MISMATCH' if left != 0 else 'OK (clean)'}")
    if tail != b"MGFX":
        print("FOOTER MISMATCH <<<")


if __name__ == "__main__":
    main()
