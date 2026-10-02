// Byte-identity gate for the in-process slangc module (issue #257).
//
// For every .slang shader in the corpora below, every [shader(...)] entry point, and every
// target's platform macros, runs the SAME argument list SlangCompiler.RunSlangc builds through
//   (a) the native pinned slangc (tools/slang/<rid>/), source piped over stdin, and
//   (b) shadowdusk-slangc.wasm's runSlangc(source, args),
// then compares exit code, stdout and stderr after the per-line '\n' join that
// Process.OutputDataReceived applies on the managed side (SlangOutputLines in C#).
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

// PlatformMacros.For (ShadowDusk.Core), value 1 each.
const targets = {
  DirectX: ['MGFX', 'HLSL', 'SM4'],
  OpenGL: ['MGFX', 'GLSL', 'OPENGL'],
  Vulkan: ['MGFX', 'HLSL', 'VULKAN', 'SM6'],
  DirectX12: ['MGFX', 'HLSL', 'SM6'],
  Fna: ['FNA', 'HLSL', 'SM3'],
};

// SlangCompiler.RunSlangc's argument list, in order.
function slangcArgs(macros, entry, stage) {
  return ['-lang', 'slang', ...macros.map(m => `-D${m}=1`), '-target', 'hlsl',
    '-no-hlsl-pack-constant-buffer-elements', '-no-mangle', '-entry', entry, '-stage', stage, '--', '-'];
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
    for (const [target, macros] of Object.entries(targets)) {
      for (const e of entries) {
        const args = slangcArgs(macros, e.name, e.stage);
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
