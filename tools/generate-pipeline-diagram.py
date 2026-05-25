"""
Generate docs/references/compilation-pipeline.png
Run from repo root: python tools/generate-pipeline-diagram.py
"""
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch
import matplotlib.patheffects as pe

# ── canvas ─────────────────────────────────────────────────────────────────
fig = plt.figure(figsize=(18, 22), dpi=150)
ax = fig.add_subplot(111)
W, H = 18, 22
ax.set_xlim(0, W)
ax.set_ylim(0, H)
ax.axis("off")
BG = "#f4f5f7"
fig.patch.set_facecolor(BG)
ax.set_facecolor(BG)

# ── palette ─────────────────────────────────────────────────────────────────
C_IN   = "#455a64"   # input
C_PRE  = "#1565c0"   # pre-parser     blue
C_PP   = "#6a1b9a"   # preprocessor   purple
C_DXC  = "#bf360c"   # DXC compiler   deep orange
C_SPV  = "#1b5e20"   # SPIRV-Cross    green
C_REF  = "#e65100"   # reflection     orange
C_WRI  = "#263238"   # binary writer  near-black
C_OUT  = "#37474f"   # output boxes
C_DX_A = "#ef9a9a"   # DX fork arrow
C_GL_A = "#a5d6a7"   # GL fork arrow
FORK_BG_DX = "#fff3f3"
FORK_BG_GL = "#f1f8f1"

# ── helpers ─────────────────────────────────────────────────────────────────

def rbox(ax, cx, cy, w, h, fc, ec="white", lw=2.5, radius=0.15, zorder=3):
    b = FancyBboxPatch(
        (cx - w / 2, cy - h / 2), w, h,
        boxstyle=f"round,pad={radius}",
        facecolor=fc, edgecolor=ec, linewidth=lw, zorder=zorder,
    )
    ax.add_patch(b)
    return b


def phase_box(cx, cy, w, h, title, sub, fc, fs_title=11, fs_sub=9):
    rbox(ax, cx, cy, w, h, fc)
    ax.text(cx, cy + h * 0.17, title, ha="center", va="center",
            fontsize=fs_title, fontweight="bold", color="white", zorder=4)
    ax.text(cx, cy - h * 0.2, sub, ha="center", va="center",
            fontsize=fs_sub, color="white", alpha=0.9, zorder=4)


def data_box(cx, cy, w, h, text, fc="#dde8f5", tc="#0d2a4a", fs=9):
    rbox(ax, cx, cy, w, h, fc, ec="#8baac8", lw=1.5, radius=0.1)
    ax.text(cx, cy, text, ha="center", va="center",
            fontsize=fs, color=tc, style="italic", zorder=4)


def io_box(cx, cy, w, h, title, sub, fc):
    rbox(ax, cx, cy, w, h, fc, ec="#b0bec5", lw=2.0, radius=0.15)
    ax.text(cx, cy + h * 0.18, title, ha="center", va="center",
            fontsize=12, fontweight="bold", color="white", zorder=4)
    ax.text(cx, cy - h * 0.2, sub, ha="center", va="center",
            fontsize=9, color="white", alpha=0.85, zorder=4)


def arr(x1, y1, x2, y2, color="#444", lw=2.2, style="-"):
    ax.annotate(
        "", xy=(x2, y2), xytext=(x1, y1),
        arrowprops=dict(
            arrowstyle="->", color=color, lw=lw,
            mutation_scale=20,
            linestyle=style,
        ),
        zorder=5,
    )


def seg(x1, y1, x2, y2, color="#888", lw=2.0, style="-"):
    ax.plot([x1, x2], [y1, y2], color=color, lw=lw, linestyle=style, zorder=4)


def note(cx, cy, text, color="#666", fs=8.5, style="italic"):
    ax.text(cx, cy, text, ha="center", va="center",
            fontsize=fs, color=color, style=style, zorder=5)


def fork_bg(x0, y0, x1, y1, fc):
    """Shaded background rectangle for a fork column."""
    b = FancyBboxPatch(
        (x0, y0), x1 - x0, y1 - y0,
        boxstyle="round,pad=0.1",
        facecolor=fc, edgecolor="none", linewidth=0, zorder=1, alpha=0.55,
    )
    ax.add_patch(b)


# ── coordinates ─────────────────────────────────────────────────────────────
CX = 9.0      # main centre
LX = 4.2      # DX path centre
RX = 13.8     # GL path centre

# y positions (high = top)
Y_TITLE = 21.4
Y_IN    = 20.6
Y_P2    = 19.1
Y_META  = 19.1  # metadata col same row as P2
Y_STRIP = 18.25
Y_P3    = 17.1
Y_P4    = 15.9
Y_FLAB  = 15.15 # fork labels
Y_FORK  = 14.55
Y_P6    = 12.8
Y_GLSL  = 11.85
Y_JOIN  = 10.9
Y_P5    = 10.0
Y_P7    = 8.65
Y_OLAB  = 7.9   # output path labels
Y_OUT   = 7.15

# ── title ───────────────────────────────────────────────────────────────────
ax.text(CX, Y_TITLE, "ShadowDusk — Shader Compilation Pipeline",
        ha="center", fontsize=18, fontweight="bold", color="#1a1a2e",
        path_effects=[pe.withSimplePatchShadow(offset=(1, -1), shadow_rgbFace="#cccccc", alpha=0.4)])

# ── INPUT ───────────────────────────────────────────────────────────────────
io_box(CX, Y_IN, 7.5, 0.85,
       "shader.fx",
       "HLSL source  +  FX9 technique / pass / sampler_state blocks",
       fc=C_IN)
arr(CX, Y_IN - 0.43, CX, Y_P2 + 0.55)

# ── PHASE 2 ─────────────────────────────────────────────────────────────────
phase_box(CX, Y_P2, 11.0, 0.95,
          "PHASE 2 — FX9 Pre-Parser",
          "Strips technique / pass / sampler_state blocks  ·  Returns clean HLSL + structured metadata",
          fc=C_PRE, fs_title=11, fs_sub=9)

# metadata branch — arrow right then data boxes stacked
arr(CX + 5.0, Y_P2, 14.85, Y_META + 0.62, color="#5c6bc0", lw=1.8)
data_box(15.6, Y_META + 0.62, 4.6, 0.46, "TechniqueInfo  ·  PassInfo",              fc="#ede7f6", tc="#311b92")
data_box(15.6, Y_META + 0.05, 4.6, 0.46, "VS / PS entry  ·  shader profile",         fc="#ede7f6", tc="#311b92")
data_box(15.6, Y_META - 0.52, 4.6, 0.46, "SamplerInfo  ·  RenderStates  ·  Annotations", fc="#ede7f6", tc="#311b92")

# StrippedHLSL badge — sits between P2 and P3
data_box(CX, Y_STRIP, 6.0, 0.44,
         "StrippedHLSL  —  pure HLSL, safe for DXC",
         fc="#e3f2fd", tc="#0d47a1", fs=9)
arr(CX, Y_P2 - 0.48, CX, Y_STRIP + 0.22)
arr(CX, Y_STRIP - 0.22, CX, Y_P3 + 0.55)

# ── PHASE 3 ─────────────────────────────────────────────────────────────────
phase_box(CX, Y_P3, 11.0, 0.95,
          "PHASE 3 — Preprocessor",
          "Flatten  #include  ·  Inject macros:  HLSL=1 / GLSL=1 / OPENGL=1 / MGFX=1 / SM4=1",
          fc=C_PP, fs_title=11, fs_sub=9)
arr(CX, Y_P3 - 0.48, CX, Y_P4 + 0.55)

# ── PHASE 4 ─────────────────────────────────────────────────────────────────
phase_box(CX, Y_P4, 11.0, 0.95,
          "PHASE 4 — DXC Compiler  (Vortice.Dxc)",
          "Compiles each pass VS + PS entry point independently per technique",
          fc=C_DXC, fs_title=11, fs_sub=9)

# ── fork background shading ──────────────────────────────────────────────────
fork_bg(1.2, Y_JOIN - 0.1, 7.0,  Y_FLAB + 0.05, FORK_BG_DX)
fork_bg(11.0, Y_JOIN - 0.1, 17.4, Y_FLAB + 0.05, FORK_BG_GL)

# fork labels
note(LX, Y_FLAB - 0.05, "DirectX target",   color="#b71c1c", fs=9.5)
note(RX, Y_FLAB - 0.05, "OpenGL / WebGL",   color="#1b5e20", fs=9.5)

# fork arrows from P4
arr(CX - 2.5, Y_P4 - 0.48, LX, Y_FORK + 0.3,  color="#c62828", lw=2.5)
arr(CX + 2.5, Y_P4 - 0.48, RX, Y_FORK + 0.3,  color="#2e7d32", lw=2.5)

# ── DX path ─────────────────────────────────────────────────────────────────
data_box(LX, Y_FORK, 4.5, 0.54, "DXBC bytecode", fc="#ffcdd2", tc="#7f0000", fs=10)
# DXBC travels straight down — thick dashed line
seg(LX, Y_FORK - 0.27, LX, Y_JOIN + 0.12, color="#c62828", lw=2.8, style="--")
arr(LX, Y_JOIN + 0.12, LX + 0.5, Y_JOIN - 0.02, color="#c62828", lw=2.5)

# ── GL/WebGL path ────────────────────────────────────────────────────────────
data_box(RX, Y_FORK, 4.5, 0.54, "SPIR-V  (binary IR)", fc="#c8e6c9", tc="#1b5e20", fs=10)
arr(RX, Y_FORK - 0.27, RX, Y_P6 + 0.55)

phase_box(RX, Y_P6, 5.5, 0.95,
          "PHASE 6 — SPIRV-Cross",
          "Y-flip  ·  depth remap  ·  combined samplers",
          fc=C_SPV, fs_title=10.5, fs_sub=8.5)
note(RX, Y_P6 - 0.65,
     "native P/Invoke (desktop CLI)   |   JS interop (WASM / browser)",
     color="#555", fs=8)

arr(RX, Y_P6 - 0.48, RX, Y_GLSL + 0.28)
data_box(RX, Y_GLSL, 5.2, 0.5,
         "#version 130  (desktop)  /  #version 300 es  (WebGL 2)",
         fc="#c8e6c9", tc="#1b5e20", fs=8.8)

arr(RX, Y_GLSL - 0.25, RX, Y_JOIN + 0.12, color="#2e7d32", lw=2.5)

# ── merge into Phase 5 ───────────────────────────────────────────────────────
arr(LX,  Y_JOIN - 0.01, CX - 2.0, Y_P5 + 0.5,  color="#555", lw=2.0)
arr(RX,  Y_JOIN - 0.01, CX + 2.0, Y_P5 + 0.5,  color="#555", lw=2.0)

# ── PHASE 5 ─────────────────────────────────────────────────────────────────
phase_box(CX, Y_P5, 11.0, 0.88,
          "PHASE 5 — Shader Reflection",
          "Extract parameter names · types · cbuffer layouts · sampler binding slots  (via DXC reflection API + SPIRV-Cross)",
          fc=C_REF, fs_title=11, fs_sub=9)
arr(CX, Y_P5 - 0.44, CX, Y_P7 + 0.55)

# ── PHASE 7 ─────────────────────────────────────────────────────────────────
phase_box(CX, Y_P7, 11.0, 0.95,
          "PHASE 7 — .mgfx Binary Writer",
          "Header  ·  Shader blobs (DXBC or GLSL text)  ·  Parameters  ·  Techniques  ·  Passes  ·  Render states",
          fc=C_WRI, fs_title=11, fs_sub=9)

# output fork
note((CX + LX) / 2, Y_OLAB, "CLI path",        color="#555", fs=9)
note((CX + RX) / 2, Y_OLAB, "WASM / KNI path", color="#555", fs=9)
arr(CX - 2.5, Y_P7 - 0.48, LX, Y_OUT + 0.43, color="#333", lw=2.2)
arr(CX + 2.5, Y_P7 - 0.48, RX, Y_OUT + 0.43, color="#333", lw=2.2)

io_box(LX, Y_OUT, 5.5, 0.80, "shader.mgfx",       "Written to disk  (CLI tool)", fc=C_OUT)
io_box(RX, Y_OUT, 5.5, 0.80, "byte[]  in memory",  "CompileAsync()  →  KNI / XNA Fiddle", fc=C_OUT)

# ── legend ───────────────────────────────────────────────────────────────────
legend_y = 0.88
legend_items = [
    (C_PRE, "PHASE 2 — Pre-Parser"),
    (C_PP,  "PHASE 3 — Preprocessor"),
    (C_DXC, "PHASE 4 — DXC Compiler"),
    (C_SPV, "PHASE 6 — SPIRV-Cross"),
    (C_REF, "PHASE 5 — Reflection"),
    (C_WRI, "PHASE 7 — Binary Writer"),
]
lw_l, lh_l = 2.8, 0.5
gap = (W - 0.6) / len(legend_items)
for i, (c, lbl) in enumerate(legend_items):
    bx = 0.3 + i * gap
    b = FancyBboxPatch((bx, legend_y - lh_l / 2), lw_l, lh_l,
                        boxstyle="round,pad=0.06",
                        facecolor=c, edgecolor="white", linewidth=1.8, zorder=3)
    ax.add_patch(b)
    ax.text(bx + lw_l / 2, legend_y, lbl, ha="center", va="center",
            fontsize=7.8, color="white", fontweight="bold", zorder=4)

# thin separator line above legend
ax.plot([0.3, W - 0.3], [1.5, 1.5], color="#cccccc", lw=1.0, zorder=2)

plt.tight_layout(pad=0.3)
out = r"c:\git\ShadowDusk\docs\references\compilation-pipeline.png"
plt.savefig(out, dpi=150, bbox_inches="tight", facecolor=BG)
print("Saved:", out)
