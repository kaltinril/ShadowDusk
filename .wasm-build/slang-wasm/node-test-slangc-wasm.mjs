// Byte-identity gate for the in-process slangc module (issue #257).
//
// For every .slang shader in the corpora below, every [shader(...)] entry point, and every
// target's platform macros, runs the SAME argument list SlangCompiler.RunSlangc builds through
//   (a) the native pinned slangc (tools/slang/<rid>/), source piped over stdin, and
//   (b) shadowdusk-slangc.wasm's runSlangc(source, args),
// then compares exit code, stdout and stderr after the per-line '\n' join that
// Process.OutputDataReceived applies on the managed side (SlangcArguments.JoinOutputLines in C#).
//
// Usage: node node-test-slangc-wasm.mjs <dir containing shadowdusk-slangc.js> [repo root]
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const moduleDir = path.resolve(process.argv[2] ?? path.join(import.meta.dirname, 'out'));
const repoRoot = path.resolve(process.argv[3] ?? path.join(import.meta.dirname, '..', '..'));

const rid = process.platform === 'win32' ? 'win-x64'
  : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64') : 'linux-x64';
const slangc = path.join(repoRoot, 'tools', 'slang', rid, process.platform === 'win32' ? 'slangc.exe' : 'slangc');
if (!fs.existsSync(slangc)) { console.error(`native slangc not found at ${slangc} (run tools/restore)`); process.exit(2); }

const { default: factory } = await import(pathToFileURL(path.join(moduleDir, 'shadowdusk-slangc.js')).href);
const t0 = performance.now();
const mod = await factory();
const loadMs = performance.now() - t0;
console.log(`module loaded in ${loadMs.toFixed(0)} ms, slang ${mod.getSlangVersion()}`);

// The per-target command lines come from tests/fixtures/golden/slangc-args.json, which
// SlangInProcessRouteTests.CommittedSlangcArgs_MatchTheSharedArgumentBuilder pins to
// SlangcArguments.Build + PlatformMacros.For (the C# source of truth); nothing is re-typed here.
const argTemplates = JSON.parse(fs.readFileSync(path.join(repoRoot, 'tests', 'fixtures', 'golden', 'slangc-args.json'), 'utf8')).targets;
const targets = Object.keys(argTemplates);
if (targets.length < 5) { console.error(`slangc-args.json lists only ${targets.length} targets`); process.exit(2); }
function slangcArgs(target, entry, stage) {
  return argTemplates[target].map(a => a === '{entry}' ? entry : a === '{stage}' ? stage : a);
}
// Process.OutputDataReceived splits on \n, \r\n or \r and the managed side re-joins every
// line with '\n'; an unterminated last line still counts as a line.
function managedLines(text) {
  if (text.length === 0) return '';
  const lines = text.split(/\r\n|\r|\n/);
  if (lines[lines.length - 1] === '') lines.pop();
  return lines.map(l => l + '\n').join('');
}

const entryRe = /\[shader\(\s*"(\w+)"\s*\)\]\s*(?:\[[^\]]*\]\s*)*[\w<>, ]+?\s+(\w+)\s*\(/g;
const corpora = [
  'tests/fixtures/shaders/slang',
  'tests/fixtures/shaders/slang-adversarial',
  'plan/PHASE-65-appendix/slang-probe/shaders',
  'plan/PHASE-66-appendix/a6-residue-sweep/shaders',
];

let runs = 0, identical = 0, nativeFailures = 0, wasmMs = 0, nativeMs = 0;
const mismatches = [];
for (const dir of corpora) {
  const abs = path.join(repoRoot, dir);
  if (!fs.existsSync(abs)) continue;
  for (const file of fs.readdirSync(abs).filter(f => f.endsWith('.slang')).sort()) {
    const source = fs.readFileSync(path.join(abs, file), 'utf8').replace(/\r\n/g, '\n');
    const entries = [...source.matchAll(entryRe)].map(m => ({ stage: m[1], name: m[2] }));
    if (entries.length === 0) entries.push({ stage: 'fragment', name: 'main' }); // still compare the failure
    for (const target of targets) {
      for (const e of entries) {
        const args = slangcArgs(target, e.name, e.stage);
        let t = performance.now();
        const n = spawnSync(slangc, args, { input: Buffer.from(source, 'utf8'), cwd: path.dirname(slangc) });
        nativeMs += performance.now() - t;
        const native = { exitCode: n.status === 0 ? 0 : 1, stdout: managedLines(n.stdout.toString('utf8')), stderr: managedLines(n.stderr.toString('utf8')) };
        t = performance.now();
        const w = mod.runSlangc(source, args);
        wasmMs += performance.now() - t;
        const wasm = { exitCode: w.exitCode, stdout: managedLines(w.stdout), stderr: managedLines(w.stderr) };
        runs++;
        if (native.exitCode !== 0) nativeFailures++;
        const same = native.exitCode === wasm.exitCode && native.stdout === wasm.stdout && native.stderr === wasm.stderr;
        if (same) identical++;
        else mismatches.push({ key: `${dir}/${file} ${target} ${e.stage}:${e.name}`, native, wasm });
      }
    }
  }
}

console.log(`runs ${runs}, identical ${identical}, native non-zero exits ${nativeFailures} (all compared incl. stderr)`);
console.log(`time: native slangc spawn total ${nativeMs.toFixed(0)} ms, in-process wasm total ${wasmMs.toFixed(0)} ms`);

// ---------------------------------------------------------------------------------------
// Depth probe (PR #266 review). slang's parser and IR passes recurse once per nesting level,
// so the module's stack size decides how deep a valid shader can go. With emscripten's 64 KB
// default the module trapped at ~205 added terms, ~98 nested parens, ~70 nested ternaries
// and ~156 nested ifs, where native slangc compiles twice that. Each case below sits well past
// those old limits and below where native Windows slangc (1 MB stack) itself gives out
// (~2000 added terms, ~3000 parens), and must match native exactly.
const ps = body => `[shader("fragment")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { ${body} }\n`;
const depthCases = {
  'add chain x400': ps(`float x = ${Array(400).fill('uv.x').join(' + ')}; return float4(x,0,0,1);`),
  'nested parens x300': ps(`float x = ${'('.repeat(300)}uv.x${')'.repeat(300)}; return float4(x,0,0,1);`),
  'nested ternary x200': ps(`float x = ${Array.from({ length: 200 }).reduce((e, _, i) => `(uv.y > ${i}.0 ? ${e} : uv.x)`, 'uv.x')}; return float4(x,0,0,1);`),
  'nested if x300': ps(`float x = 0; ${Array.from({ length: 300 }).reduce((b, _, i) => `if (uv.x > ${i}.0) { ${b} }`, 'x = 1;')} return float4(x,0,0,1);`),
  'else-if chain x400': ps(`float x = 0; ${Array.from({ length: 400 }, (_, i) => `${i ? 'else ' : ''}if (uv.x < ${i}.01) x = ${i}.0;`).join(' ')} return float4(x,0,0,1);`),
};
for (const [label, source] of Object.entries(depthCases)) {
  const args = slangcArgs('OpenGL', 'MainPS', 'fragment');
  const n = spawnSync(slangc, args, { input: Buffer.from(source, 'utf8'), cwd: path.dirname(slangc), maxBuffer: 1 << 28 });
  const native = { exitCode: n.status === 0 ? 0 : 1, stdout: managedLines(n.stdout.toString('utf8')), stderr: managedLines(n.stderr.toString('utf8')) };
  let wasm;
  try {
    const w = mod.runSlangc(source, args);
    wasm = { exitCode: w.exitCode, stdout: managedLines(w.stdout), stderr: managedLines(w.stderr) };
  } catch (e) {
    wasm = { exitCode: 'TRAP', stdout: '', stderr: String(e) };
  }
  const same = native.exitCode === 0 && native.exitCode === wasm.exitCode && native.stdout === wasm.stdout && native.stderr === wasm.stderr;
  console.log(`depth ${same ? 'OK  ' : 'FAIL'} ${label}: native exit ${native.exitCode}, wasm exit ${wasm.exitCode}`);
  if (!same) mismatches.push({ key: `depth ${label}`, native, wasm });
  if (wasm.exitCode === 'TRAP') break; // the instance is unusable after a trap
}

// ---------------------------------------------------------------------------------------
// Trap recovery through the committed [JSImport] shim (PR #266 review). A trap leaves the
// module unusable, so the shim must drop the instance and the next ensureReady() must load a
// fresh one: a trap followed by an ordinary compile on the same page must succeed and still
// match native slangc.
const shim = await import(pathToFileURL(path.join(moduleDir, '..', 'shadowdusk-slangc.js')).href);
await shim.ensureReady();
const trivial = ps('return float4(uv, 0, 1);');
const trivialArgs = slangcArgs('OpenGL', 'MainPS', 'fragment');
const deep = ps(`float x = ${'('.repeat(200000)}uv.x${')'.repeat(200000)}; return float4(x,0,0,1);`);
let trapped = false;
try { shim.runSlangc(deep, trivialArgs); } catch (e) { trapped = /slangc trapped/.test(String(e)); console.log(`trap: ${String(e).slice(0, 160)}`); }
let notLoaded = false;
try { shim.runSlangc(trivial, trivialArgs); } catch (e) { notLoaded = /not loaded/.test(String(e)); }
await shim.ensureReady();
const after = shim.runSlangc(trivial, trivialArgs);
const nTrivial = spawnSync(slangc, trivialArgs, { input: Buffer.from(trivial, 'utf8'), cwd: path.dirname(slangc) });
const recovered = trapped && notLoaded && after[0] === '0' && managedLines(after[1]) === managedLines(nTrivial.stdout.toString('utf8'));
console.log(`trap recovery ${recovered ? 'OK  ' : 'FAIL'}: trapped=${trapped}, discarded=${notLoaded}, reloaded compile exit ${after[0]} matches native=${managedLines(after[1]) === managedLines(nTrivial.stdout.toString('utf8'))}`);
if (!recovered) mismatches.push({ key: 'trap recovery', native: { exitCode: 0, stdout: '', stderr: '' }, wasm: { exitCode: after[0], stdout: '', stderr: '' } });

for (const m of mismatches.slice(0, 5)) {
  console.log(`MISMATCH ${m.key}: exit ${m.native.exitCode}/${m.wasm.exitCode}`);
  for (const k of ['stdout', 'stderr']) {
    if (m.native[k] !== m.wasm[k]) {
      const a = m.native[k], b = m.wasm[k];
      let i = 0; while (i < a.length && a[i] === b[i]) i++;
      console.log(`  ${k} differs at ${i}:\n  native: ${JSON.stringify(a.slice(Math.max(0, i - 80), i + 120))}\n  wasm:   ${JSON.stringify(b.slice(Math.max(0, i - 80), i + 120))}`);
    }
  }
}
process.exit(mismatches.length === 0 ? 0 : 1);