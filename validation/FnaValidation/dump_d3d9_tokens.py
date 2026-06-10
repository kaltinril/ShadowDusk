#!/usr/bin/env python3
"""Diagnostic scratch tool (Phase 39 Dissolve bisection): dump the D3D9 SM2/SM3
token stream(s) embedded in an fx_2_0 .fxb (or a bare shader blob).

Usage: python dump_d3d9_tokens.py <file.fxb> [...]

Finds every 0xFFFF03xx / 0xFFFE03xx (and 02xx) version token in the container and
disassembles tokens until the END token, decoding opcode, instruction-specific
controls (the IFC/BREAKC comparison in bits 16-23), operand registers, writemasks,
swizzles, and source modifiers — enough to compare vkd3d's idioms vs fxc's.
"""
import struct
import sys

OPS = {
    0x00: "nop", 0x01: "mov", 0x02: "add", 0x03: "sub", 0x04: "mad", 0x05: "mul",
    0x06: "rcp", 0x07: "rsq", 0x08: "dp3", 0x09: "dp4", 0x0A: "min", 0x0B: "max",
    0x0C: "slt", 0x0D: "sge", 0x0E: "exp", 0x0F: "log", 0x10: "lit", 0x11: "dst",
    0x12: "lrp", 0x13: "frc", 0x14: "m4x4", 0x15: "m4x3", 0x16: "m3x4", 0x17: "m3x3",
    0x18: "m3x2", 0x19: "call", 0x1A: "callnz", 0x1B: "loop", 0x1C: "ret",
    0x1D: "endloop", 0x1E: "label", 0x1F: "dcl", 0x20: "pow", 0x21: "crs",
    0x22: "sgn", 0x23: "abs", 0x24: "nrm", 0x25: "sincos", 0x26: "rep", 0x27: "endrep",
    0x28: "if", 0x29: "ifc", 0x2A: "else", 0x2B: "endif", 0x2C: "break",
    0x2D: "breakc", 0x2E: "mova", 0x2F: "defb", 0x30: "defi",
    0x40: "texcoord", 0x41: "texkill", 0x42: "texld", 0x43: "texbem", 0x44: "texbeml",
    0x45: "texreg2ar", 0x46: "texreg2gb", 0x47: "texm3x2pad", 0x48: "texm3x2tex",
    0x49: "texm3x3pad", 0x4A: "texm3x3tex", 0x4C: "texm3x3spec", 0x4D: "texm3x3vspec",
    0x4E: "expp", 0x4F: "logp", 0x50: "cnd", 0x51: "def", 0x52: "texreg2rgb",
    0x53: "texdp3tex", 0x54: "texm3x2depth", 0x55: "texdp3", 0x56: "texm3x3",
    0x57: "texdepth", 0x58: "cmp", 0x59: "bem", 0x5A: "dp2add", 0x5B: "dsx",
    0x5C: "dsy", 0x5D: "texldd", 0x5E: "setp", 0x5F: "texldl", 0x60: "breakp",
}
COMPARE = {0: "??0", 1: "_gt", 2: "_eq", 3: "_ge", 4: "_lt", 5: "_ne", 6: "_le"}
REGTYPES = {
    0: "r", 1: "v", 2: "c", 3: "t",   # temp, input, const, texture/addr
    4: "rastout", 5: "attrout", 6: "texcrdout/o", 7: "constint(i)",
    8: "colorout(oC)", 9: "depthout(oDepth)", 10: "s", 11: "const2",
    12: "const3", 13: "const4", 14: "constbool(b)", 15: "loopcnt(aL)",
    16: "tempfloat16", 17: "misc", 18: "label", 19: "predicate(p)",
}
SRCMOD = {
    0: "", 1: "-", 2: "_bias", 3: "-_bias", 4: "_sign", 5: "-_sign", 6: "_comp(1-x)",
    7: "_x2", 8: "-_x2", 9: "_dz", 10: "_dw", 11: "_abs", 12: "-_abs", 13: "!",
}
CHANS = "xyzw"


def regtype_of(tok):
    return ((tok >> 28) & 0x7) | (((tok >> 11) & 0x3) << 3)


def dest_str(tok):
    rt, rn = regtype_of(tok), tok & 0x7FF
    mask = (tok >> 16) & 0xF
    m = "".join(CHANS[i] for i in range(4) if mask & (1 << i))
    sat = "_sat" if tok & (1 << 20) else ""
    pp = "_pp" if tok & (1 << 21) else ""
    rel = "[rel!]" if tok & 0x2000 else ""
    return f"{REGTYPES.get(rt, f'rt{rt}')}{rn}{rel}{sat}{pp}.{m}"


def src_str(tok):
    rt, rn = regtype_of(tok), tok & 0x7FF
    swz = (tok >> 16) & 0xFF
    s = "".join(CHANS[(swz >> (2 * i)) & 0x3] for i in range(4))
    mod = (tok >> 24) & 0xF
    rel = "[rel!]" if tok & 0x2000 else ""
    name = f"{REGTYPES.get(rt, f'rt{rt}')}{rn}{rel}.{s}"
    m = SRCMOD.get(mod, f"_mod{mod}")
    return f"{m}{name}" if m in ("", "-", "!") else f"{m}({name})"


# ops whose operands are: dest then sources; texkill has only a dest.
DEST_FIRST_EXCEPTIONS = {"if", "ifc", "break", "breakc", "call", "callnz", "rep",
                         "loop", "label", "ret", "else", "endif", "endrep", "endloop", "nop"}


def dump_stream(b, start, label):
    ver = struct.unpack_from("<I", b, start)[0]
    kind = "ps" if (ver >> 16) == 0xFFFF else "vs"
    print(f"  === {label} {kind}_{(ver >> 8) & 0xFF}_{ver & 0xFF} @0x{start:X}")
    pos = start + 4
    while pos + 4 <= len(b):
        tok = struct.unpack_from("<I", b, pos)[0]
        if tok == 0x0000FFFF:
            print(f"    {pos - start:04X}: end")
            return pos + 4
        if (tok & 0xFFFF) == 0xFFFE and not (tok >> 31):
            n = (tok >> 16) & 0x7FFF
            tag = b[pos + 4:pos + 8].decode("ascii", "replace")
            print(f"    {pos - start:04X}: comment {n} dwords ({tag})")
            pos += 4 + n * 4
            continue
        op = tok & 0xFFFF
        nops = (tok >> 24) & 0xF
        ctrl = (tok >> 16) & 0xFF
        pred = " (pred)" if tok & 0x10000000 else ""
        name = OPS.get(op, f"op{op:02X}")
        if name in ("ifc", "breakc", "setp"):
            name += COMPARE.get(ctrl, f"_c{ctrl}")
        elif name == "texld" and ctrl:
            name += {1: "p", 2: "b"}.get(ctrl, f"_c{ctrl}")
        operands = [struct.unpack_from("<I", b, pos + 4 + i * 4)[0] for i in range(nops)]
        if name == "def":
            d = dest_str(operands[0])
            vals = struct.unpack_from("<4f", b, pos + 8)
            txt = f"def {d}, " + ", ".join(f"{v:g}" for v in vals)
        elif name in ("defi", "defb"):
            d = dest_str(operands[0])
            txt = f"{name} {d}, " + ", ".join(str(v) for v in operands[1:])
        elif name == "dcl":
            usage = operands[0]
            txt = f"dcl (usage=0x{usage:08X}) {dest_str(operands[1])}"
        elif name.split("_")[0] in DEST_FIRST_EXCEPTIONS or op in (0x28, 0x29, 0x2D):
            txt = name + " " + ", ".join(src_str(o) for o in operands)
        elif name == "texkill":
            txt = f"texkill {dest_str(operands[0])}"
        else:
            parts = [dest_str(operands[0])] + [src_str(o) for o in operands[1:]]
            txt = name + pred + " " + ", ".join(parts)
        raw = " ".join(f"{o:08X}" for o in operands)
        print(f"    {pos - start:04X}: {txt:<58} | {tok:08X} {raw}")
        pos += 4 + nops * 4
    return pos


def main(paths):
    for path in paths:
        b = open(path, "rb").read()
        print(f"--- {path} ({len(b)} bytes)")
        i = 0
        while i + 4 <= len(b):
            v = struct.unpack_from("<I", b, i)[0]
            if v in (0xFFFF0300, 0xFFFE0300, 0xFFFF0200, 0xFFFE0200):
                # require a plausible following token (comment or dcl/def/instr)
                i = dump_stream(b, i, "shader")
            else:
                i += 4


if __name__ == "__main__":
    main(sys.argv[1:])
