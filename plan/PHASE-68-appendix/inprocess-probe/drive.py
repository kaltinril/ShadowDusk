# Phase 68 research probe (throwaway, win-x64 only, not product code).
# Verbatim SlangcArguments-shaped lists INCLUDING '-- -': for every .slang under <root>, every [shader] entry plus
# the -E pass, OpenGL macros, run real slangc.exe and probe.py (fresh process, source piped to its stdin) and compare
# exit code, stdout and stderr after the JoinOutputLines normalization.
# Usage: python drive.py <dir holding probe.py> <dir holding slangc.exe + slang-compiler.dll> <root>
import subprocess, json, sys, os, glob, time, re
S, lib = sys.argv[1], sys.argv[2]
slangc = os.path.join(lib, "slangc.exe")
def join(t):  # SlangcArguments.JoinOutputLines
    if not t: return t
    parts = re.split(r"\r\n|\r|\n", t)
    if parts[-1] == "": parts = parts[:-1]
    return "".join(p + "\n" for p in parts)
targets = {"OpenGL": ["MGFX","GLSL","OPENGL"], "DirectX_11": None}
def build(macros, entry, stage, tail):
    a = ["-lang","slang"] + [f"-D{m}=1" for m in macros] + ["-target","hlsl","-no-hlsl-pack-constant-buffer-elements","-no-mangle"]
    if entry: a += ["-entry", entry, "-stage", stage]
    else: a += ["-E"]
    return a + ["--"] + tail
files = sorted(glob.glob(os.path.join(sys.argv[3], "**", "*.slang"), recursive=True))
same = total = 0; spawn_t = []; inproc_t = []
for f in files:
    src = open(f, "rb").read()
    entries = re.findall(rb'\[shader\("(\w+)"\)\]\s*[\w<>]+\s+(\w+)\s*\(', src)
    runs = [(e.decode(), s.decode()) for s, e in entries] + [(None, None)]
    for entry, stage in runs:
        stage = {"fragment":"fragment","pixel":"fragment","vertex":"vertex"}.get(stage, stage)
        args = build(["MGFX","GLSL","OPENGL"], entry, stage, ["-"])
        t0 = time.perf_counter(); p = subprocess.run([slangc] + args, input=src, capture_output=True, cwd=lib); spawn_t.append(time.perf_counter() - t0)
        q = subprocess.run([sys.executable, os.path.join(S, "probe.py"), lib, "x", json.dumps(args), "1"], input=src, capture_output=True, cwd=S)
        try: r = json.loads(q.stdout)
        except Exception: print("PROBE FAIL", f, entry, q.stderr[-400:]); continue
        inproc_t.append(r["times"][0])
        a = (p.returncode if p.returncode < 2**31 else p.returncode - 2**32, join(p.stdout.decode("utf-8","replace")), join(p.stderr.decode("utf-8","replace")))
        b = (r["exit"], join(r["stdout"]), join(r["stderr"]))
        total += 1
        if a == b: same += 1
        else: print("DIFF", os.path.basename(f), entry, "exit", a[0], b[0], "out", a[1]==b[1], "err", a[2]==b[2], repr(a[2][:200]), "|", repr(b[2][:200]))
print(f"identical {same}/{total}")
spawn_t.sort(); inproc_t.sort()
print("spawn per run median ms %.1f  (min %.1f max %.1f)" % (1000*spawn_t[len(spawn_t)//2], 1000*spawn_t[0], 1000*spawn_t[-1]))
print("in-process FIRST compile in a fresh session median ms %.1f" % (1000*inproc_t[len(inproc_t)//2]))
