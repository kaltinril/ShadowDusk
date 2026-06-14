#!/usr/bin/env python3
"""Phase 35 Area B - pixel-compare ShadowDusk's MGFX v11 render against v10, both in
MonoGame 3.8.5 (a v11-capable runtime).

The v11 body adds two diagnostic-only per-shader strings (SourceFile, Entrypoint); they
must be present and correctly length-prefixed or the file won't parse, but they do NOT
affect rendering. So a correct v11 file renders pixel-identical to the v10 file in the
same runtime. This compares:

  * output/mgfx-v11      = ShadowDusk MgfxVersion 11 -> MonoGame 3.8.5 render
  * output/mgfx-v10-385  = ShadowDusk MgfxVersion 10 -> MonoGame 3.8.5 render (same runtime)
  * output/baseline      = the mgfxc v10 goldens (MonoGame 3.8.2 render) - cross-runtime, FYI

Bar: max per-channel delta <= 4/255 (the established fidelity tolerance). Exit non-zero if
any pair is missing or over tolerance.

Run order:  dotnet run --project validation/MonoGameV11           # -> output/mgfx-v11
            dotnet run --project validation/MonoGameV11 -- v10    # -> output/mgfx-v10-385
            python validation/compare_mgfxv11.py
"""
import argparse
import os
import sys

try:
    import numpy as np
    from PIL import Image
except ImportError as e:
    sys.exit(f"Missing dependency: {e}. Run: pip install pillow numpy")

HERE = os.path.dirname(os.path.abspath(__file__))
V11 = os.path.join(HERE, "output", "mgfx-v11")
V10 = os.path.join(HERE, "output", "mgfx-v10-385")
BASELINE = os.path.join(HERE, "output", "baseline")
DIFF_DIR = os.path.join(HERE, "output", "mgfx-v11-diff")

SHADERS = ["Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
           "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve"]


def load(path):
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.int16)


def compare(a_path, b_path, tolerance, diff_path):
    if not os.path.exists(a_path) or not os.path.exists(b_path):
        miss = [n for n, p in (("v11", a_path), ("ref", b_path)) if not os.path.exists(p)]
        return ("MISSING(" + ",".join(miss) + ")", None, None)
    a, b = load(a_path), load(b_path)
    if a.shape != b.shape:
        return (f"SIZE {a.shape}!={b.shape}", None, None)
    delta = np.abs(a - b)
    per_pixel_max = delta.max(axis=2)
    maxd = int(delta.max())
    mean = float(delta.mean())
    status = "MATCH" if int((per_pixel_max > tolerance).sum()) == 0 else "DIFFER"
    if status == "DIFFER":
        vis = b.copy().astype(np.uint8)
        vis[per_pixel_max > tolerance] = [255, 0, 255, 255]
        os.makedirs(os.path.dirname(diff_path), exist_ok=True)
        Image.fromarray(vis, "RGBA").save(diff_path)
    return (status, maxd, mean)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tolerance", type=int, default=4)
    args = ap.parse_args()

    print(f"v11:       {V11}  (MGFX v11 -> MonoGame 3.8.5)")
    print(f"v10@385:   {V10}  (MGFX v10 -> MonoGame 3.8.5, same runtime)")
    print(f"baseline:  {BASELINE}  (mgfxc v10 goldens, MonoGame 3.8.2)")
    print(f"tolerance: {args.tolerance}/255\n")
    print(f"{'shader':<12} | {'v11 vs v10 (both 3.8.5)':<26} | {'v11 vs mgfxc golden':<24}")
    print(f"{'':<12} | {'status':<10}{'maxd':>6}{'mean':>8}  | {'status':<10}{'maxd':>6}{'mean':>8}")
    print("-" * 78)

    failures = 0
    for name in SHADERS:
        v11p = os.path.join(V11, name + ".png")
        s1, d1, m1 = compare(v11p, os.path.join(V10, name + ".png"), args.tolerance,
                             os.path.join(DIFF_DIR, name + "_v11_vs_v10.png"))
        s2, d2, m2 = compare(v11p, os.path.join(BASELINE, name + ".png"), args.tolerance,
                             os.path.join(DIFF_DIR, name + "_v11_vs_golden.png"))
        if s1 != "MATCH" or s2 != "MATCH":
            failures += 1
        c1 = f"{s1:<10}{(d1 if d1 is not None else '-'):>6}{(f'{m1:.3f}' if m1 is not None else '-'):>8}"
        c2 = f"{s2:<10}{(d2 if d2 is not None else '-'):>6}{(f'{m2:.3f}' if m2 is not None else '-'):>8}"
        print(f"{name:<12} | {c1}  | {c2}")

    print("-" * 78)
    if failures:
        print(f"\n{failures} shader(s) missing or over tolerance. Diffs in {DIFF_DIR}")
        return 1
    print("\nAll 10 shaders: ShadowDusk's MGFX v11 loads + renders in real MonoGame 3.8.5, "
          "pixel-equivalent to v10 (the v11 SourceFile/Entrypoint strings are diagnostic-only). "
          "MGFX v11 render-proven.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
