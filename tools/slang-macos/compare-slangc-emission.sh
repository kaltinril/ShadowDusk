#!/usr/bin/env bash
# Prove two slangc builds emit byte-identical output for the whole Slang corpus (issue #237).
#
# usage: compare-slangc-emission.sh <reference-dir> <candidate-dir> [repo-root]
#
# Each dir holds a slangc executable plus its libslang-compiler library (the flat layout
# tools/restore.sh produces). The reference is upstream's release build; the candidate is our
# lower-deployment-target build of the same tag. Both run with the EXACT argument list
# ShadowDusk.Slang's SlangCompiler.RunSlangc uses (-lang slang, platform macros, -target hlsl,
# -no-hlsl-pack-constant-buffer-elements, -no-mangle, -entry, -stage, source on stdin), for
# every [shader(...)] entry in every corpus file, under every PlatformMacros set (and the
# __KNIFX__ variants). stdout, stderr and the exit code must all match, byte for byte.
#
# The corpus is the one SlangCrossHostByteIdentityTests and validation/SlangFullCorpus share:
# tests/fixtures/shaders/slang/*.slang plus the four Phase 65 probe shaders.
set -euo pipefail

ref_dir="$(cd "$1" && pwd)"
cand_dir="$(cd "$2" && pwd)"
repo_root="$(cd "${3:-$(dirname "$0")/../..}" && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Each binary runs from a private copy of its directory: slangc writes a cache file beside
# itself on first compile (SlangCompiler runs it the same way, from a writable directory).
cp -R "$ref_dir" "$work/ref"
cp -R "$cand_dir" "$work/cand"
chmod +x "$work/ref/slangc" "$work/cand/slangc"

macro_sets=(
    ""
    "-DMGFX=1 -DHLSL=1 -DSM4=1"
    "-DMGFX=1 -DGLSL=1 -DOPENGL=1"
    "-DMGFX=1 -DHLSL=1 -DVULKAN=1 -DSM6=1"
    "-DFNA=1 -DHLSL=1 -DSM3=1"
    "-DMGFX=1 -DHLSL=1 -DSM6=1"
    "-DMGFX=1 -DHLSL=1 -DSM4=1 -D__KNIFX__=1"
    "-DMGFX=1 -DGLSL=1 -DOPENGL=1 -D__KNIFX__=1"
)

files=()
while IFS= read -r f; do files+=("$f"); done < <(
    { find "$repo_root/tests/fixtures/shaders/slang" -name '*.slang'
      for n in GumGrayscale GumTint GumBlur GenericsProbe; do
          echo "$repo_root/plan/PHASE-65-appendix/slang-probe/shaders/$n.slang"
      done; } | LC_ALL=C sort)

runs=0
diffs=0
for f in "${files[@]}"; do
    [ -f "$f" ] || { echo "missing corpus file: $f" >&2; exit 2; }
    # "<stage> <entry>" per [shader("...")] attribute: the function name is the identifier
    # right before the first '(' on the next non-blank line.
    entries="$(awk '
        match($0, /\[shader\("[a-z]+"\)\]/) {
            s = substr($0, RSTART + 9, RLENGTH - 12); want = 1; next
        }
        want && NF {
            line = $0; sub(/[ \t]*\(.*/, "", line); n = split(line, w, /[ \t]+/)
            print (s == "vertex" ? "vertex" : "fragment"), w[n]; want = 0
        }' "$f")"
    [ -n "$entries" ] || { echo "no [shader(...)] entries found in $f" >&2; exit 2; }

    while read -r stage entry; do
        for macros in "${macro_sets[@]}"; do
            args=(-lang slang)
            # shellcheck disable=SC2206 # deliberate word split of the macro list
            [ -n "$macros" ] && args+=($macros)
            args+=(-target hlsl -no-hlsl-pack-constant-buffer-elements -no-mangle
                   -entry "$entry" -stage "$stage" -- -)
            for side in ref cand; do
                set +e
                (cd "$work/$side" && ./slangc "${args[@]}" < "$f" \
                    > "$work/$side.out" 2> "$work/$side.err")
                echo $? > "$work/$side.code"
                set -e
            done
            runs=$((runs + 1))
            for part in out err code; do
                if ! cmp -s "$work/ref.$part" "$work/cand.$part"; then
                    diffs=$((diffs + 1))
                    echo "DIFF [$part] $(basename "$f") $stage $entry macros='$macros'"
                    diff "$work/ref.$part" "$work/cand.$part" | head -20 || true
                fi
            done
            if [ "$(cat "$work/ref.code")" != 0 ]; then
                echo "note: reference exit $(cat "$work/ref.code") for $(basename "$f") $entry macros='$macros' (both sides must agree)"
            fi
        done
    done <<< "$entries"
done

echo "slangc emission comparison: ${#files[@]} files, $runs invocations, $diffs differences"
[ "$diffs" -eq 0 ]
