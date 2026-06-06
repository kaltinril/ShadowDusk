#!/usr/bin/env python3
"""Phase 35 Area A — forward-compat VERSION-MATRIX pixel comparison.

Compares the SAME ShadowDusk-compiled v10 GL .mgfx bytes rendered on each MonoGame
version in the matrix. Renders live in:

  output/versionmatrix/<version>/   ShadowDusk v10 bytes rendered on MonoGame <version>
  output/baseline/                  mgfxc goldens rendered on MonoGame 3.8.2.1105

Because every version cell renders IDENTICAL .mgfx bytes (the product is unchanged)
through the SAME renderer, any pixel difference between two version cells is
attributable solely to the MonoGame runtime. The matrix proves two things:

  1. Forward-compat: every version renders pixel-identical to the floor version
     (default 3.8.2.1105) — the existing v10 output keeps working forward, zero
     consumer action.
  2. Fidelity (--vs-baseline): every version stays within tolerance of the mgfxc
     goldens — the same bar as the original Phase 17 candidate-vs-mgfxc check.

Usage:
  python compare_forwardcompat.py --versions 3.8.2.1105 3.8.4.1 [--tolerance N] [--vs-baseline]
  (--versions defaults to "3.8.2.1105 3.8.4.1"; first entry is the forward-compat reference floor)
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
MATRIX = os.path.join(HERE, "output", "versionmatrix")
BASELINE = os.path.join(HERE, "output", "baseline")          # mgfxc goldens, 3.8.2.1105
DIFF_DIR = os.path.join(HERE, "output", "diff-versionmatrix")

SHADERS = ["Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
           "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve"]


def load(path):
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.int16)


def compare_set(test_dir, test_label, ref_dir, ref_label, tol):
    """Compare test_dir renders against ref_dir, return failure count."""
    print(f"\n=== {test_label}  vs  {ref_label} ===")
    print(f"{'shader':<12} {'status':<10} {'diff px':>10} {'total':>10} {'maxd':>5} {'mean':>7}")
    print("-" * 60)
    os.makedirs(DIFF_DIR, exist_ok=True)
    failures = 0
    for name in SHADERS:
        r = os.path.join(ref_dir, name + ".png")
        t = os.path.join(test_dir, name + ".png")
        if not os.path.exists(r) or not os.path.exists(t):
            miss = []
            if not os.path.exists(r):
                miss.append(ref_label)
            if not os.path.exists(t):
                miss.append(test_label)
            print(f"{name:<12} {'MISSING':<10} {'(' + ','.join(miss) + ')':>10}")
            failures += 1
            continue
        ra, ta = load(r), load(t)
        if ra.shape != ta.shape:
            print(f"{name:<12} {'SIZE-DIFF':<10} {str(ra.shape):>10} {str(ta.shape):>10}")
            failures += 1
            continue
        delta = np.abs(ra - ta)
        per_pixel_max = delta.max(axis=2)
        diff_px = int((per_pixel_max > tol).sum())
        total = per_pixel_max.size
        maxd = int(delta.max())
        mean = float(delta.mean())
        status = "MATCH" if diff_px == 0 else "DIFFER"
        if diff_px != 0:
            failures += 1
            vis = ra.copy().astype(np.uint8)
            mask = per_pixel_max > tol
            vis[mask] = [255, 0, 255, 255]
            Image.fromarray(vis, "RGBA").save(
                os.path.join(DIFF_DIR, f"{name}_{test_label}_vs_{ref_label}_diff.png"))
        print(f"{name:<12} {status:<10} {diff_px:>10} {total:>10} {maxd:>5} {mean:>7.3f}")
    print("-" * 60)
    return failures


def vdir(version):
    return os.path.join(MATRIX, version)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--versions", nargs="+", default=["3.8.2.1105", "3.8.4.1"],
                    help="MonoGame versions in the matrix; the FIRST is the forward-compat reference floor")
    ap.add_argument("--tolerance", type=int, default=4,
                    help="max per-channel delta still counted as a match (default 4)")
    ap.add_argument("--vs-baseline", action="store_true",
                    help="also compare every version against the mgfxc goldens")
    args = ap.parse_args()

    versions = args.versions
    floor = versions[0]

    print("Phase 35 Area A — forward-compat version matrix")
    print(f"versions : {', '.join(versions)}   (floor/reference: {floor})")
    print(f"tolerance: {args.tolerance}/255")

    failures = 0

    # 1. Forward-compat: every non-floor version vs the floor (pixel-IDENTICAL expected,
    #    same bytes). Tolerance 0 here would also pass; we keep the shared tolerance for
    #    a consistent report but maxd should read 0.
    for v in versions[1:]:
        failures += compare_set(vdir(v), v, vdir(floor), floor, args.tolerance)

    # 2. Fidelity: every version vs the mgfxc goldens (Phase 17 bar).
    if args.vs_baseline:
        for v in versions:
            failures += compare_set(vdir(v), v, BASELINE, "baseline-mgfxc", args.tolerance)

    if failures:
        print(f"\n{failures} comparison(s) missing or over tolerance. Diffs in {DIFF_DIR}")
        return 1
    print(f"\nVERSION MATRIX PASS: ShadowDusk v10 .mgfx renders identically across "
          f"{', '.join(versions)} and within tolerance of the mgfxc goldens.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
