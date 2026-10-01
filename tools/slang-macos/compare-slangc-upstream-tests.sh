#!/usr/bin/env bash
# Breadth check for issue #237: run two slangc builds over slang's OWN test shaders
# (<slang-src>/tests/**/*.slang) with -target hlsl and demand byte-identical stdout, stderr and
# exit code. compare-slangc-emission.sh pins ShadowDusk's exact invocation over its corpus;
# that corpus is small and simple (v2026.14.1 and v2026.19 agree on all of it), so this
# exercises far more of the compiler to show the two builds are the same compiler.
#
# usage: compare-slangc-upstream-tests.sh <reference-dir> <candidate-dir> <slang-src> [jobs]
#
# Each file is compiled with every [shader(...)] entry it declares (slangc's default when no
# -entry is given). Files slangc rejects still count: the diagnostics must match too. Paths in
# diagnostics are the same relative path on both sides.
set -euo pipefail

ref_dir="$(cd "$1" && pwd)"
cand_dir="$(cd "$2" && pwd)"
src="$(cd "$3" && pwd)"
jobs="${4:-$(sysctl -n hw.ncpu 2>/dev/null || nproc)}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp -R "$ref_dir" "$work/ref"
cp -R "$cand_dir" "$work/cand"
chmod +x "$work/ref/slangc" "$work/cand/slangc"

export work src
one() {
    rel="$1"
    key="$(printf '%s' "$rel" | tr '/' '_')"
    for side in ref cand; do
        set +e
        # perl alarm = a portable per-file timeout (macOS has no coreutils timeout). A few
        # upstream tests never finish under a bare -target hlsl; both sides must then time
        # out alike (exit 142), which still compares equal.
        (cd "$src" && perl -e 'alarm shift; exec @ARGV or exit 127' 30 \
            "$work/$side/slangc" -target hlsl "$rel" \
            > "$work/$side.$key.out" 2> "$work/$side.$key.err")
        echo $? > "$work/$side.$key.code"
        set -e
        # The slangc path itself can appear in a crash/usage message; normalize it.
        sed -i '' "s#$work/$side/#SLANGC/#g" "$work/$side.$key.err" 2>/dev/null \
            || sed -i "s#$work/$side/#SLANGC/#g" "$work/$side.$key.err"
    done
    for part in out err code; do
        if ! cmp -s "$work/ref.$key.$part" "$work/cand.$key.$part"; then
            echo "DIFF [$part] $rel"
        fi
    done
    case "$(cat "$work/ref.$key.code")" in
        0) echo "OK0 $rel" ;; 142) echo "TMO $rel" ;; *) echo "OKN $rel" ;;
    esac
    rm -f "$work"/ref."$key".* "$work"/cand."$key".*
}
export -f one

(cd "$src" && find tests -name '*.slang' | LC_ALL=C sort) > "$work/list"
total=$(wc -l < "$work/list" | tr -d ' ')
xargs -P "$jobs" -I{} bash -c 'one "$@"' _ {} < "$work/list" > "$work/results"

diffs=$(grep -c '^DIFF' "$work/results" || true)
ok0=$(grep -c '^OK0' "$work/results" || true)
tmo=$(grep -c '^TMO' "$work/results" || true)
grep '^DIFF' "$work/results" | head -50 || true
grep '^TMO' "$work/results" || true
echo "slangc upstream-test comparison: $total files ($ok0 compiled cleanly on the reference, $tmo timed out on it), $diffs differences"
[ "$diffs" -eq 0 ] && [ "$total" -gt 0 ]
