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
# Runs WITHOUT errexit and reports every outcome as one line, so a helper failure can never
# make a file silently vanish from the tally: each listed file must produce exactly one
# OK0/OKN/TMO verdict line, and the totals are checked against the file list below.
one() {
    rel="$1"
    key="$(printf '%s' "$rel" | tr '/' '_')"
    for side in ref cand; do
        # perl alarm = a portable per-file timeout (macOS has no coreutils timeout). A few
        # upstream tests never finish under a bare -target hlsl; both sides must then time
        # out alike (exit 142), which still compares equal.
        (cd "$src" && perl -e 'alarm shift; exec @ARGV or exit 127' 30 \
            "$work/$side/slangc" -target hlsl "$rel" \
            > "$work/$side.$key.out" 2> "$work/$side.$key.err")
        echo $? > "$work/$side.$key.code"
        # The slangc path itself can appear in a crash/usage message; normalize it. perl, not
        # sed: BSD sed aborts on the non-UTF-8 bytes some diagnostics contain.
        if ! LC_ALL=C perl -pi -e "s#\\Q$work/$side/\\E#SLANGC/#g" "$work/$side.$key.err"; then
            echo "ERR normalize $side $rel"
        fi
    done
    for part in out err code; do
        if ! cmp -s "$work/ref.$key.$part" "$work/cand.$key.$part"; then
            echo "DIFF [$part] $rel"
            if [ "$part" != code ]; then
                diff "$work/ref.$key.$part" "$work/cand.$key.$part" | head -10 | sed 's/^/    /'
            else
                echo "    ref=$(cat "$work/ref.$key.code") cand=$(cat "$work/cand.$key.code")"
            fi
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
# Job-control "Segmentation fault" notices from bash go to stderr; they are expected (some
# upstream tests crash both builds alike) and the exit codes are compared instead.
xargs -P "$jobs" -I{} bash -c 'one "$@"' _ {} < "$work/list" > "$work/results" 2> "$work/xargs.err" \
    || echo "xargs exited non-zero (a one() invocation failed)" >> "$work/results.fail"

count() { grep -c "$1" "$work/results" || true; }
diffs=$(count '^DIFF')
errs=$(count '^ERR')
ok0=$(count '^OK0')
okn=$(count '^OKN')
tmo=$(count '^TMO')
verdicts=$((ok0 + okn + tmo))
grep -A10 '^DIFF' "$work/results" | grep -v -E '^(OK0|OKN|TMO) ' | head -200 || true
grep -E '^(TMO|ERR) ' "$work/results" || true
cat "$work/results.fail" 2>/dev/null || true
echo "slangc upstream-test comparison: $total files, $verdicts verdicts ($ok0 compiled cleanly on the reference, $okn rejected or crashed on the reference, $tmo timed out), $diffs differences, $errs harness errors"
[ "$diffs" -eq 0 ] && [ "$errs" -eq 0 ] && [ "$total" -gt 0 ] && [ "$verdicts" -eq "$total" ] \
    && [ ! -f "$work/results.fail" ]
