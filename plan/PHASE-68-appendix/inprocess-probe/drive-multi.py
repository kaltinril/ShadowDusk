# Phase 68 research probe (throwaway, win-x64 only, not product code).
# ONE shared global session: every .slang under <root> x 4 macro sets (OpenGL and DirectX exact, Vulkan- and
# FNA-like approximations) x every entry plus -E, file-path input on both sides, compared run by run with slangc.exe.
# Usage: python drive-multi.py <dir holding probe.py> <dir holding slangc.exe + slang-compiler.dll> <root>
import subprocess, json, sys, os, glob, re, tempfile
S, lib, root = sys.argv[1], sys.argv[2], sys.argv[3]
slangc = os.path.join(lib, "slangc.exe")
def join(t):
    if not t: return t
    parts = re.split(r"\r\n|\r|\n", t)
    if parts[-1] == "": parts = parts[:-1]
    return "".join(p + "\n" for p in parts)
macrosets = [["MGFX","GLSL","OPENGL"], ["MGFX","HLSL","SM4"], ["MGFX","GLSL","VULKAN","SM6"], ["FNA","SM3"]]
runs = []
files = sorted(glob.glob(os.path.join(root, "**", "*.slang"), recursive=True))
for f in files:
    src = open(f, "rb").read()
    ents = re.findall(rb'\[shader\("(\w+)"\)\]\s*[\w<>]+\s+(\w+)\s*\(', src)
    for ms in macrosets:
        base = ["-lang","slang"] + [f"-D{m}=1" for m in ms] + ["-target","hlsl","-no-hlsl-pack-constant-buffer-elements","-no-mangle"]
        for s, e in ents:
            st = {"pixel":"fragment"}.get(s.decode(), s.decode())
            runs.append(base + ["-entry", e.decode(), "-stage", st, "--", os.path.abspath(f)])
        runs.append(base + ["-E", "--", os.path.abspath(f)])
open(os.path.join(tempfile.gettempdir(),"phase68-runs.json"),"w").write(json.dumps(runs)); q = subprocess.run([sys.executable, os.path.join(S, "probe.py"), lib, "multi", "@" + os.path.join(tempfile.gettempdir(),"phase68-runs.json")], capture_output=True, cwd=S)
r = json.loads(q.stdout)
same = 0; ts = []
for argv, got in zip(runs, r["results"]):
    p = subprocess.run([slangc] + argv, capture_output=True, cwd=lib)
    ex = p.returncode if p.returncode < 2**31 else p.returncode - 2**32
    a = (ex, join(p.stdout.decode("utf-8","replace")), join(p.stderr.decode("utf-8","replace")))
    b = (got["exit"], join(got["stdout"]), join(got["stderr"]))
    ts.append(got["t"])
    if a == b: same += 1
    else: print("DIFF", argv[-1], argv[-5:], a[0], b[0])
ts.sort()
print(f"ONE shared session, {len(runs)} sequential runs (4 macro sets): identical {same}/{len(runs)}; session create {r['session']*1000:.0f} ms; per-run median {ts[len(ts)//2]*1000:.1f} ms, p95 {ts[int(len(ts)*0.95)]*1000:.1f} ms, total {sum(ts):.2f} s")

