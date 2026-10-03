#!/usr/bin/env bash
# tools/ci/hang-watchdog.sh: out-of-process evidence for a stalled test host (issue #312).
#
# Started in the background right before `dotnet test` (see .github/workflows/ci.yml, the
# integration job). After <delay> seconds it looks for every test host that is still running
# and, for each one, captures from OUTSIDE the process:
#   * native stacks of every thread (lldb on macOS, with and without sudo; gdb on Linux),
#   * a `sample` profile on macOS as the fallback,
#   * each Linux thread's kernel wait channel,
#   * a dump written by the createdump that ships with the runtime the process has mapped
#     (`--withheap`: what `dotnet-dump collect` writes by default and what
#     `dotnet-dump analyze` needs for `clrstack -all`, `syncblk` and `threads`),
#   * a second native-stack snapshot a little later, so a frozen thread can be told from a
#     slow one.
# It then exits 0. If no test host is running when the delay expires (a healthy run), it
# logs that and exits 0. Nothing in here can fail the CI step: every tool failure is written
# to the log and the script continues.
#
# Why out of process: `dotnet test --blame-hang-timeout` dumps through the runtime's own
# diagnostics server, which forks createdump FROM INSIDE the hung process. On macOS a fork
# runs the pthread_atfork handlers, DxcForkGate's included, so a process that is hung with a
# DXC compile stuck under that gate cannot dump itself. A sibling shell process can.
#
# Usage: hang-watchdog.sh <out-dir> [delay-seconds] [second-snapshot-after-seconds]
set -u

out=${1:?out-dir}
delay=${2:-300}
second_after=${3:-45}
shift $(( $# < 3 ? $# : 3 ))

mkdir -p "$out"
self=$$

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

# run_bounded <seconds> <file> <cmd...>: run a tool with its stdout+stderr in <file>, kill it
# if it outlives <seconds>. Appends how it ended to <file>.
run_bounded() {
  local secs=$1 file=$2; shift 2
  "$@" >"$file" 2>&1 &
  local pid=$! i=0
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$i" -ge "$secs" ]; then
      kill -9 "$pid" 2>/dev/null
      printf '\n[watchdog] %s killed after %s s\n' "$1" "$secs" >>"$file"
      return 124
    fi
    sleep 1; i=$((i + 1))
  done
  wait "$pid"; local rc=$?
  printf '\n[watchdog] %s exited %s\n' "$1" "$rc" >>"$file"
  return $rc
}

# Every pid whose command line names a test host or one of the test assemblies (the probe
# children re-run the test assembly under `dotnet exec`, so they are included on purpose).
find_hosts() {
  {
    pgrep -f 'testhost' 2>/dev/null
    pgrep -f 'ShadowDusk\.Integration\.Tests\.dll' 2>/dev/null
    pgrep -f 'ShadowDusk\.Slang\.Tests\.dll' 2>/dev/null
    pgrep -f 'ShadowDusk\.[A-Za-z]*\.Tests\.dll' 2>/dev/null
  } | sort -un | while read -r pid; do
    [ "$pid" = "$self" ] && continue
    # pgrep -f can match itself or this script's own command line; keep only live, foreign pids.
    kill -0 "$pid" 2>/dev/null || continue
    case "$(ps -o command= -p "$pid" 2>/dev/null)" in
      *hang-watchdog*|pgrep*|*' grep '*) continue ;;
    esac
    echo "$pid"
  done
}

# The directory of the runtime the process has mapped, so the matching createdump is used.
runtime_dir_of() {
  local pid=$1 path=""
  if [ -r "/proc/$pid/maps" ]; then
    path=$(grep -m1 -o '/[^ ]*libcoreclr\.so' "/proc/$pid/maps" 2>/dev/null || true)
  elif command -v lsof >/dev/null 2>&1; then
    path=$(lsof -p "$pid" 2>/dev/null | awk '/libcoreclr\.dylib/ { print $NF; exit }')
  fi
  [ -n "$path" ] && dirname "$path"
}

createdump_for() {
  local pid=$1 dir
  dir=$(runtime_dir_of "$pid" || true)
  if [ -n "$dir" ] && [ -x "$dir/createdump" ]; then echo "$dir/createdump"; return; fi
  for root in "${DOTNET_ROOT:-}" "$HOME/.dotnet" /usr/local/share/dotnet /usr/share/dotnet /usr/lib/dotnet; do
    [ -n "$root" ] && [ -d "$root/shared/Microsoft.NETCore.App" ] || continue
    find "$root/shared/Microsoft.NETCore.App" -name createdump -type f 2>/dev/null | sort -V | tail -n1
    return
  done
}

native_stacks() {
  local pid=$1 file=$2
  if [ "$(uname -s)" = "Darwin" ]; then
    local lldb
    lldb=$(command -v lldb || echo /usr/bin/lldb)
    if sudo -n true 2>/dev/null; then
      log "  lldb (sudo) -> $file"
      run_bounded 150 "$file" sudo -n "$lldb" --batch --no-lldbinit -p "$pid" \
        -o 'thread list' -o 'thread backtrace all' -o 'detach' -o 'quit'
    fi
    if ! grep -q 'frame #' "$file" 2>/dev/null; then
      log "  lldb (no sudo) -> $file"
      run_bounded 150 "$file" "$lldb" --batch --no-lldbinit -p "$pid" \
        -o 'thread list' -o 'thread backtrace all' -o 'detach' -o 'quit'
    fi
    if ! grep -q 'frame #' "$file" 2>/dev/null; then
      log "  lldb gave no frames; sample -> ${file%.txt}-sample.txt"
      run_bounded 90 "${file%.txt}-sample.txt" /usr/bin/sample "$pid" 5 -mayDie
    fi
  else
    {
      echo "threads (tid comm: kernel wait channel):"
      for task in /proc/"$pid"/task/*; do
        [ -d "$task" ] || continue
        printf '  %s %s: %s\n' "$(basename "$task")" "$(cat "$task/comm" 2>/dev/null)" "$(cat "$task/wchan" 2>/dev/null)"
      done
    } >"${file%.txt}-wchan.txt" 2>&1
    if command -v gdb >/dev/null 2>&1; then
      log "  gdb -> $file"
      run_bounded 150 "$file" gdb -p "$pid" -batch -nx -ex 'info threads' -ex 'thread apply all bt'
      if ! grep -q '^#0' "$file" 2>/dev/null && sudo -n true 2>/dev/null; then
        log "  gdb (sudo) -> $file"
        run_bounded 150 "$file" sudo -n gdb -p "$pid" -batch -nx -ex 'info threads' -ex 'thread apply all bt'
      fi
    else
      echo "gdb: not installed" >"$file"
    fi
  fi
}

capture() {
  local pid=$1 tag=$2
  local name
  name=$(ps -o command= -p "$pid" 2>/dev/null | tr -s ' ' | cut -c1-200)
  log "capturing pid $pid ($tag): $name"
  {
    echo "pid $pid: $name"
    ps -o pid,ppid,etime,time,stat,%cpu,rss -p "$pid" 2>/dev/null
  } >"$out/host-$pid-process.txt" 2>&1

  native_stacks "$pid" "$out/host-$pid-stacks-$tag.txt"

  if [ "$tag" = "1" ]; then
    local createdump
    createdump=$(createdump_for "$pid" || true)
    if [ -n "$createdump" ]; then
      log "  $createdump --withheap -> $out/host-$pid.dmp"
      run_bounded 240 "$out/host-$pid-createdump.txt" "$createdump" --withheap -f "$out/host-$pid.dmp" "$pid"
      if [ ! -s "$out/host-$pid.dmp" ] && sudo -n true 2>/dev/null; then
        log "  createdump (sudo) -> $out/host-$pid.dmp"
        run_bounded 240 "$out/host-$pid-createdump-sudo.txt" sudo -n "$createdump" --withheap -f "$out/host-$pid.dmp" "$pid"
      fi
    else
      log "  createdump: none found next to the process's runtime or under the dotnet roots"
    fi
  fi
}

log "watchdog started: pid $self, delay ${delay}s, second snapshot after ${second_after}s, out $out"
sleep "$delay"

pids=$(find_hosts)
if [ -z "$pids" ]; then
  log "no test host running after ${delay}s: healthy run, nothing to capture"
  exit 0
fi

log "test hosts still running after ${delay}s:"
ps -axo pid,ppid,etime,time,stat,%cpu,command 2>/dev/null \
  | grep -E 'dotnet|testhost|ShadowDusk' | grep -v -E 'grep|hang-watchdog' \
  | tee "$out/process-tree.txt"

for pid in $pids; do capture "$pid" 1; done

sleep "$second_after"
for pid in $pids; do
  if kill -0 "$pid" 2>/dev/null; then
    capture "$pid" 2
  else
    log "pid $pid exited before the second snapshot"
  fi
done

# Files written through sudo are root-owned; the upload step runs as the runner user.
if sudo -n true 2>/dev/null; then sudo -n chmod -R a+rX "$out" 2>/dev/null || true; fi
log "watchdog done; files:"
ls -la "$out" 2>/dev/null
exit 0
