#!/usr/bin/env python3
"""Phase 44 D - pixel-compare the KNI DESKTOP (SDL2.GL) render against the references.

For each of the 10 corpus shaders this compares output/kni/<name>.png (ShadowDusk's
v10 .mgfx loaded + rendered in REAL KNI v4.2.9001) against:

  * baseline  = output/baseline/<name>.png  (the mgfxc golden, MonoGame render of the
                reference compiler's bytes) -> proves KNI renders ShadowDusk's v10
                output the same as the reference compiler's output (the product bar).
  * candidate = output/candidate/<name>.png (ShadowDusk -> MonoGame DesktopGL render of
                the SAME bytes) -> proves KNI == MonoGame for our bytes (arm-vs-arm,
                same backend GL, only the runtime differs).

Both are same-backend (GL <-> GL) comparisons, the only valid kind. The established bar
is max per-channel delta <= 4/255 (Phase 17 fidelity tolerance, reused by the Phase 24
KNI WebGL harness). Exit code is non-zero if any image is missing or over tolerance.

Run order:  dotnet run --project validation/Baseline      (mgfxc goldens -> output/baseline)
            dotnet run --project validation/Candidate     (ShadowDusk -> MonoGame -> output/candidate)
            dotnet run --project validation/KniDesktopGL  (ShadowDusk -> KNI -> output/kni)
            python validation/compare_kni.py

Usage:  python compare_kni.py [--tolerance N]
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
KNI = os.path.join(HERE, "output", "kni")
BASELINE = os.path.join(HERE, "output", "baseline")
CANDIDATE = os.path.join(HERE, "output", "candidate")
DIFF_DIR = os.path.join(HERE, "output", "kni-diff")

SHADERS = ["Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
           "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve"]


def load(path):
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.int16)


def compare(kni_path, ref_path, tolerance, diff_path):
    """Return (status, diff_px, total, maxd, mean). Writes a magenta diff if over tol."""
    if not os.path.exists(kni_path) or not os.path.exists(ref_path):
        miss = []
        if not os.path.exists(kni_path):
            miss.append("kni")
        if not os.path.exists(ref_path):
            miss.append("ref")
        return ("MISSING(" + ",".join(miss) + ")", None, None, None, None)

    ka, ra = load(kni_path), load(ref_path)
    if ka.shape != ra.shape:
        return (f"SIZE {ka.shape}!={ra.shape}", None, None, None, None)

    delta = np.abs(ka - ra)
    per_pixel_max = delta.max(axis=2)
    diff_px = int((per_pixel_max > tolerance).sum())
    total = per_pixel_max.size
    maxd = int(delta.max())
    mean = float(delta.mean())
    status = "MATCH" if diff_px == 0 else "DIFFER"
    if diff_px != 0:
        vis = ra.copy().astype(np.uint8)
        vis[per_pixel_max > tolerance] = [255, 0, 255, 255]
        os.makedirs(os.path.dirname(diff_path), exist_ok=True)
        Image.fromarray(vis, "RGBA").save(diff_path)
    return (status, diff_px, total, maxd, mean)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tolerance", type=int, default=4,
                    help="max per-channel delta still counted as a match (default 4)")
    args = ap.parse_args()

    print(f"kni:       {KNI}")
    print(f"baseline:  {BASELINE}  (mgfxc goldens)")
    print(f"candidate: {CANDIDATE}  (ShadowDusk -> MonoGame)")
    print(f"tolerance: {args.tolerance}/255\n")
    print(f"{'shader':<12} | {'KNI vs mgfxc-golden':<28} | {'KNI vs ShadowDusk@MonoGame':<28}")
    print(f"{'':<12} | {'status':<10}{'maxd':>6}{'mean':>8}    | {'status':<10}{'maxd':>6}{'mean':>8}")
    print("-" * 80)

    failures = 0
    for name in SHADERS:
        kp = os.path.join(KNI, name + ".png")
        bstat, _, _, bmaxd, bmean = compare(
            kp, os.path.join(BASELINE, name + ".png"), args.tolerance,
            os.path.join(DIFF_DIR, name + "_vs_baseline.png"))
        cstat, _, _, cmaxd, cmean = compare(
            kp, os.path.join(CANDIDATE, name + ".png"), args.tolerance,
            os.path.join(DIFF_DIR, name + "_vs_candidate.png"))

        if bstat != "MATCH" or cstat != "MATCH":
            failures += 1

        bcol = f"{bstat:<10}{(bmaxd if bmaxd is not None else '-'):>6}{(f'{bmean:.3f}' if bmean is not None else '-'):>8}"
        ccol = f"{cstat:<10}{(cmaxd if cmaxd is not None else '-'):>6}{(f'{cmean:.3f}' if cmean is not None else '-'):>8}"
        print(f"{name:<12} | {bcol}    | {ccol}")

    print("-" * 80)
    if failures:
        print(f"\n{failures} shader(s) missing or over tolerance. Diffs in {DIFF_DIR}")
        return 1
    print("\nAll 10 shaders: KNI renders ShadowDusk's v10 output within tolerance of BOTH "
          "the mgfxc golden and the MonoGame render. KNI v4.02 desktop render-proven.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
