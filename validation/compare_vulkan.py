#!/usr/bin/env python3
"""Phase 32 Vulkan rung-4 comparison: candidate (ShadowDusk Vulkan .mgfx loaded in real
MonoGame DesktopVK) vs baseline (real mgfxc Vulkan .mgfx golden, same runtime), where
available.

Same-backend only: ShadowDusk-Vulkan vs mgfxc-Vulkan, both rendered by real DesktopVK.

IMPORTANT ASYMMETRY vs. every other compare_*.py in this directory: real mgfxc's own
Vulkan output currently crashes (IndexOutOfRangeException in TextureCollection.set_Item)
on ALL 10 corpus shaders when rendered in real DesktopVK — a confirmed, separate MonoGame
bug in VulkanShaderProfile.CreateShader's SlotOffset arithmetic for auto-numbered
(non-explicit-register) resources, independent of ShadowDusk (see
plan/PHASE-32-appendix/vulkan-mgfx-format-spec.md). So a missing BASELINE image is not
a ShadowDusk regression signal here and does not fail this script; a missing CANDIDATE
image (ShadowDusk's own output failing to render) does. If MonoGame fixes that bug
upstream and baseline images start appearing, this script starts doing real pixel-diffs
against them with no changes needed.

Usage:  python compare_vulkan.py [--tolerance N]
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
BASELINE = os.path.join(HERE, "output", "baseline-vulkan")
CANDIDATE = os.path.join(HERE, "output", "candidate-vulkan")
DIFF_DIR = os.path.join(HERE, "output", "diff-vulkan")

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
    print(f"{'shader':<12} {'status':<18} {'diff px':>10} {'total':>10} {'maxd':>5} {'mean':>7}")
    print("-" * 68)

    candidate_failures = 0
    baseline_missing = 0
    for name in SHADERS:
        b = os.path.join(BASELINE, name + ".png")
        c = os.path.join(CANDIDATE, name + ".png")

        if not os.path.exists(c):
            print(f"{name:<12} {'CANDIDATE MISSING':<18}")
            candidate_failures += 1
            continue

        if not os.path.exists(b):
            # Known, separate, upstream mgfxc bug - not a ShadowDusk signal. Report and move on.
            print(f"{name:<12} {'no baseline':<18} {'(upstream mgfxc SlotOffset bug, see appendix doc)':>10}")
            baseline_missing += 1
            continue

        ba, ca = load(b), load(c)
        if ba.shape != ca.shape:
            print(f"{name:<12} {'SIZE-DIFF':<18} {str(ba.shape):>10} {str(ca.shape):>10}")
            candidate_failures += 1
            continue

        delta = np.abs(ba - ca)
        per_pixel_max = delta.max(axis=2)
        diff_px = int((per_pixel_max > args.tolerance).sum())
        total = per_pixel_max.size
        maxd = int(delta.max())
        mean = float(delta.mean())
        status = "MATCH" if diff_px == 0 else "DIFFER"
        if diff_px != 0:
            candidate_failures += 1
            vis = ba.copy().astype(np.uint8)
            mask = per_pixel_max > args.tolerance
            vis[mask] = [255, 0, 255, 255]
            Image.fromarray(vis, "RGBA").save(os.path.join(DIFF_DIR, name + "_diff.png"))

        print(f"{name:<12} {status:<18} {diff_px:>10} {total:>10} {maxd:>5} {mean:>7.3f}")

    print("-" * 68)
    if baseline_missing:
        print(f"\n{baseline_missing} shader(s) had no baseline render (upstream mgfxc bug, not scored).")
    if candidate_failures:
        print(f"{candidate_failures} shader(s) failed on ShadowDusk's own (candidate) side. Diffs in {DIFF_DIR}")
        return 1
    print("\nAll candidate renders succeeded" +
          (" and matched baseline within tolerance where a baseline existed." if baseline_missing == 0 else "."))
    return 0


if __name__ == "__main__":
    sys.exit(main())
