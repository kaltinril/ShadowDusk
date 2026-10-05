# Throwaway Phase 68 probe: replay slangc's innerMain against the shipped slang-compiler.dll via ctypes.
import ctypes as C, sys, os, time, json
from ctypes import c_void_p, c_char_p, c_int, c_size_t, c_uint32, c_bool, POINTER, byref

libdir = sys.argv[1]; mode = sys.argv[2]; args = json.loads(open(sys.argv[3][1:]).read()) if sys.argv[3].startswith("@") else json.loads(sys.argv[3]); reps = int(sys.argv[4]) if len(sys.argv) > 4 else 1
t0 = time.perf_counter()
lib = C.CDLL(os.path.join(libdir, "slang-compiler.dll"))   # absolute path only
t_load = time.perf_counter() - t0

class Desc(C.Structure):
    _fields_ = [("structureSize", c_uint32), ("apiVersion", c_uint32), ("minLanguageVersion", c_uint32),
                ("enableGLSL", c_bool), ("reserved", c_uint32 * 16)]
# ISlangWriter implemented with ctypes callbacks (the managed-writer analogue)
QI = C.CFUNCTYPE(c_int, c_void_p, c_void_p, POINTER(c_void_p)); AR = C.CFUNCTYPE(c_uint32, c_void_p)
BAB = C.CFUNCTYPE(c_void_p, c_void_p, c_size_t); EAB = C.CFUNCTYPE(c_int, c_void_p, c_void_p, c_size_t)
WR = C.CFUNCTYPE(c_int, c_void_p, c_void_p, c_size_t); FL = C.CFUNCTYPE(None, c_void_p)
ISC = C.CFUNCTYPE(c_bool, c_void_p); SM = C.CFUNCTYPE(c_int, c_void_p, c_uint32)
class Writer:
    def __init__(self):
        self.chunks = []; self.buf = None
        def qi(this, iid, out): out[0] = this; return 0
        def ar(this): return 1
        def bab(this, n): self.buf = C.create_string_buffer(n + 1); return C.addressof(self.buf)
        def eab(this, p, n): self.chunks.append(C.string_at(p, n)); return 0
        def wr(this, p, n): self.chunks.append(C.string_at(p, n)); return 0
        def fl(this): pass
        def isc(this): return False
        def sm(this, m): return 0
        self.fns = [QI(qi), AR(ar), AR(ar), BAB(bab), EAB(eab), WR(wr), FL(fl), ISC(isc), SM(sm)]
        self.vtbl = (c_void_p * 9)(*[C.cast(f, c_void_p) for f in self.fns])
        self.obj = (c_void_p * 1)(C.addressof(self.vtbl))
    def ptr(self): return C.addressof(self.obj)
    def text(self): return b"".join(self.chunks)

lib.slang_createGlobalSession2.argtypes = [POINTER(Desc), POINTER(c_void_p)]
lib.spCreateCompileRequest.restype = c_void_p; lib.spCreateCompileRequest.argtypes = [c_void_p]
lib.spAddSearchPath.argtypes = [c_void_p, c_char_p]
lib.spSetWriter.argtypes = [c_void_p, c_int, c_void_p]
setcl = getattr(lib, "?spSetCommandLineCompilerMode@@YAXPEAUICompileRequest@slang@@@Z"); setcl.argtypes = [c_void_p]
lib.spProcessCommandLineArguments.argtypes = [c_void_p, POINTER(c_char_p), c_int]
lib.spCompile.argtypes = [c_void_p]; lib.spDestroyCompileRequest.argtypes = [c_void_p]

d = Desc(); d.structureSize = C.sizeof(Desc); d.apiVersion = 0; d.minLanguageVersion = 2025; d.enableGLSL = True
sess = c_void_p()
t0 = time.perf_counter(); r = lib.slang_createGlobalSession2(byref(d), byref(sess)); t_sess = time.perf_counter() - t0
assert r == 0, r

def run_once(argv):
    out, err = Writer(), Writer()
    req = lib.spCreateCompileRequest(sess)
    lib.spAddSearchPath(req, libdir.encode())
    lib.spSetWriter(req, 0, err.ptr())   # DIAGNOSTIC -> stderr writer (slangc does this)
    lib.spSetWriter(req, 2, err.ptr())   # STD_ERROR
    lib.spSetWriter(req, 1, out.ptr())   # STD_OUTPUT
    setcl(req)
    a = (c_char_p * len(argv))(*[x.encode() for x in argv])
    res = lib.spProcessCommandLineArguments(req, a, len(argv))
    if res >= 0:
        res = lib.spCompile(req)
        res = -1 if res < 0 else res   # slangc: failed compile -> SLANG_E_INTERNAL_FAIL -> ToolReturnCode -1
    else:
        res = 1
    lib.spDestroyCompileRequest(req)
    return res, out.text(), err.text()

if mode == "multi":
    results = []
    for argv in args:
        t0 = time.perf_counter(); res, o, e = run_once(argv); results.append({"t": time.perf_counter() - t0, "exit": res, "stdout": o.decode("utf-8","replace"), "stderr": e.decode("utf-8","replace")})
    sys.stdout.buffer.write(json.dumps({"session": t_sess, "results": results}).encode()); sys.exit(0)
times = []
for i in range(reps):
    t0 = time.perf_counter(); res, o, e = run_once(args); times.append(time.perf_counter() - t0)
sys.stdout.buffer.write(json.dumps({"load": t_load, "session": t_sess, "times": times, "exit": res,
    "stdout": o.decode("utf-8", "replace"), "stderr": e.decode("utf-8", "replace")}).encode())

