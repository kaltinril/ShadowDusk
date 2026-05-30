#!/usr/bin/env python3
"""Phase 18 DirectX rung-4 comparison: 10 baseline (mgfxc DX goldens loaded in real
MonoGame.Framework.WindowsDX) vs 10 candidate (ShadowDusk DX .mgfx loaded in the SAME
WindowsDX Effect path).

Same-backend only: ShadowDusk-DX vs mgfxc-DX, both rendered by real WindowsDX. For each
shader, reports whether both images exist, the number of differing pixels, the max
per-channel delta, and the mean delta. Exit code is non-zero if any pair is missing or
differs beyond the tolerance.

Usage:  python compare_dx.py [--tolerance N]
Requires: pillow, numpy   (pip install pillow numpy)
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
BASELINE = os.path.join(HERE, "output-dx", "baseline")
CANDIDATE = os.path.join(HERE, "output-dx", "candidate")
DIFF_DIR = os.path.join(HERE, "output-dx", "diff")

SHADERS = ["Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
           "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve"]


def load(path):
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.int16)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tolerance", type=int, default=4,
                    help="max per-channel delta still counted as a match (default 4)")
    args = ap.parse_args()
    os.makedirs(DIFF_DIR, exist_ok=True)

    print(f"baseline:  {BASELINE}")
    print(f"candidate: {CANDIDATE}")
    print(f"tolerance: {args.tolerance}/255\n")
    print(f"{'shader':<12} {'status':<10} {'diff px':>10} {'total':>10} {'maxd':>5} {'mean':>7}")
    print("-" * 60)

    failures = 0
    for name in SHADERS:
        b = os.path.join(BASELINE, name + ".png")
        c = os.path.join(CANDIDATE, name + ".png")
        if not os.path.exists(b) or not os.path.exists(c):
            miss = []
            if not os.path.exists(b):
                miss.append("baseline")
            if not os.path.exists(c):
                miss.append("candidate")
            print(f"{name:<12} {'MISSING':<10} {'(' + ','.join(miss) + ')':>10}")
            failures += 1
            continue

        ba, ca = load(b), load(c)
        if ba.shape != ca.shape:
            print(f"{name:<12} {'SIZE-DIFF':<10} {str(ba.shape):>10} {str(ca.shape):>10}")
            failures += 1
            continue

        delta = np.abs(ba - ca)
        per_pixel_max = delta.max(axis=2)
        diff_px = int((per_pixel_max > args.tolerance).sum())
        total = per_pixel_max.size
        maxd = int(delta.max())
        mean = float(delta.mean())
        status = "MATCH" if diff_px == 0 else "DIFFER"
        if diff_px != 0:
            failures += 1
            vis = ba.copy().astype(np.uint8)
            mask = per_pixel_max > args.tolerance
            vis[mask] = [255, 0, 255, 255]
            Image.fromarray(vis, "RGBA").save(os.path.join(DIFF_DIR, name + "_diff.png"))

        print(f"{name:<12} {status:<10} {diff_px:>10} {total:>10} {maxd:>5} {mean:>7.3f}")

    print("-" * 60)
    if failures:
        print(f"\n{failures} shader(s) missing or over tolerance. Diffs in {DIFF_DIR}")
        return 1
    print("\nAll shaders match within tolerance.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
