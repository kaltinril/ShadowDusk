#!/usr/bin/env python3
"""Phase 35 Area A — forward-compat pixel comparison.

Compares the SAME ShadowDusk-compiled v10 GL .mgfx bytes rendered on two
different MonoGame runtimes:

  - output/candidate/     ShadowDusk bytes rendered on MonoGame 3.8.2.1105 (product pin)
  - output/forwardcompat/ ShadowDusk bytes rendered on MonoGame 3.8.4.1 (newer)

Because both sides use IDENTICAL .mgfx bytes (the product is unchanged) and the
SAME renderer, any pixel difference is attributable solely to the newer MonoGame
runtime. A MATCH proves the existing v10 output keeps working forward with zero
consumer action.

Optionally (--vs-baseline) it also re-checks forwardcompat against the mgfxc
goldens (output/baseline/) so the forward run is held to the same bar as the
original Phase 17 candidate-vs-baseline comparison.

Usage:  python compare_forwardcompat.py [--tolerance N] [--vs-baseline]
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
CANDIDATE = os.path.join(HERE, "output", "candidate")        # 3.8.2.1105
FORWARD = os.path.join(HERE, "output", "forwardcompat")      # 3.8.4.1
BASELINE = os.path.join(HERE, "output", "baseline")          # mgfxc goldens, 3.8.2.1105
DIFF_DIR = os.path.join(HERE, "output", "diff-forwardcompat")

SHADERS = ["Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
           "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve"]


def load(path):
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.int16)


def compare_set(ref_dir, ref_label, tol):
    """Compare forwardcompat (3.8.4.1) against ref_dir, return failure count."""
    print(f"\n=== forwardcompat (3.8.4.1)  vs  {ref_label} ===")
    print(f"{'shader':<12} {'status':<10} {'diff px':>10} {'total':>10} {'maxd':>5} {'mean':>7}")
    print("-" * 60)
    os.makedirs(DIFF_DIR, exist_ok=True)
    failures = 0
    for name in SHADERS:
        r = os.path.join(ref_dir, name + ".png")
        f = os.path.join(FORWARD, name + ".png")
        if not os.path.exists(r) or not os.path.exists(f):
            miss = []
            if not os.path.exists(r):
                miss.append(ref_label)
            if not os.path.exists(f):
                miss.append("forwardcompat")
            print(f"{name:<12} {'MISSING':<10} {'(' + ','.join(miss) + ')':>10}")
            failures += 1
            continue
        ra, fa = load(r), load(f)
        if ra.shape != fa.shape:
            print(f"{name:<12} {'SIZE-DIFF':<10} {str(ra.shape):>10} {str(fa.shape):>10}")
            failures += 1
            continue
        delta = np.abs(ra - fa)
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
                os.path.join(DIFF_DIR, f"{name}_{ref_label}_diff.png"))
        print(f"{name:<12} {status:<10} {diff_px:>10} {total:>10} {maxd:>5} {mean:>7.3f}")
    print("-" * 60)
    return failures


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tolerance", type=int, default=4,
                    help="max per-channel delta still counted as a match (default 4)")
    ap.add_argument("--vs-baseline", action="store_true",
                    help="also compare forwardcompat against the mgfxc goldens")
    args = ap.parse_args()

    print("Phase 35 Area A — forward-compat validation")
    print(f"tolerance: {args.tolerance}/255")

    # Primary proof: same ShadowDusk v10 bytes, old runtime vs new runtime.
    failures = compare_set(CANDIDATE, "candidate-3.8.2", args.tolerance)

    if args.vs_baseline:
        failures += compare_set(BASELINE, "baseline-mgfxc", args.tolerance)

    if failures:
        print(f"\n{failures} comparison(s) missing or over tolerance. "
              f"Diffs in {DIFF_DIR}")
        return 1
    print("\nForward-compat PASS: ShadowDusk v10 .mgfx renders pixel-equivalent "
          "on MonoGame 3.8.4.1 as on 3.8.2.1105.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
