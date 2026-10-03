// Phase 4.1 byte-identity gate (the bar) — vkd3d->WASM == desktop vkd3d.
//
// Mirrors the Phase 23 G1 mechanism (.wasm-build/node-test-dxc-shim.mjs): drive the
// PRODUCT shim (src/ShadowDusk.Wasm/wwwroot/shadowdusk-vkd3d.js) through its real
// contract surface under node — await ensureReady() (twice, to exercise idempotency),
// then compile(sourceUtf8, entryPoint, profile, sourceName, targetType, options) — and
// assert every output byte-identical to the DESKTOP vkd3d backend's output over the DX
// (SM5 DXBC_TPF) and FNA (SM1-3 D3D_BYTECODE) byte-identity corpus.
//
// The desktop ground truth is captured fresh each run by Vkd3dCorpusProbe (dotnet),
// which records every vkd3d compile the REAL pipeline issues (the source bytes the
// desktop really handed vkd3d, entry point, profile, target type, the vkd3d compile
// options the desktop really passed, output blob) through the same dxbcCompilerFactory
// seam the WASM host uses — so the comparison sits at the exact seam that differs
// between hosts.
//
// SOURCE PREPARATION (issue #319). Both hosts hand vkd3d Vkd3dCompileContract.PrepareSource
// of the preprocessed text: every #line directive line blanked, because vkd3d's
// preprocessor ignores the directive and prints "vkd3d:NNNN:fixme:vkd3d:preproc_yyparse
// #line directive." to stderr (the browser console) for each one. The browser host used
// to hand vkd3d the directives. The manifest's 'sourceFile' is the text read back from
// the desktop's native call, so the replay IS the desktop's text; three controls hold it:
//   - no replayed source may contain a #line line (the desktop really prepared it), and
//     the whole prepared corpus pass must print ZERO fixme lines (stderr is intercepted);
//   - the directive-carrying request text ('requestSourceFile') is replayed too and must
//     give the SAME bytes (the directives never changed vkd3d's output: this is the
//     measurement behind "console noise only") while printing exactly one fixme line per
//     directive, which proves the stderr tripwire above is live.
//
// COMPILE OPTIONS (issue #295). The browser module used to compile with NO vkd3d
// options while the desktop passed BACKWARD_COMPATIBILITY = MAP_SEMANTIC_NAMES for
// DXBC_TPF, and this gate stayed green: no corpus shader changed a byte with the
// option. Sm3SemanticStructs.fx is the corpus case that does, and three things now
// hold it in place:
//   - each compile is replayed with the options the DESKTOP passed (manifest
//     'options'), which the shim and the wrapper must forward untouched;
//   - an option-sensitivity control replays Sm3SemanticStructs with NO options and
//     requires the result to DIFFER from the desktop (a fixture that stopped
//     depending on the options would have silently disarmed the gate);
//   - the shim refuses to load a module without sdw_vkd3d_compile_options (the
//     option-less pre-#295 entry point), so no module can silently drop them.
//
// MESSAGES ON SUCCESS (issue #335). vkd3d's message buffer is populated on a SUCCESSFUL
// compile too (W5300 implicit truncation, W5302 unrecognized attribute, ...); the desktop
// turns it into PlatformBlob.Warnings / CompiledShader.Warnings, and the shim used to
// read it and drop it, returning the bytes alone. Byte-identity could not see that. Now:
//   - the probe records the VERBATIM message text the desktop got back from each native
//     call (Vkd3dShaderCompiler.NativeMessagesObserver, manifest 'messages'), and every
//     replayed compile's shim result must carry the identical text beside the bytes;
//   - the corpus must hold at least one compile with a NON-EMPTY message
//     (ImplicitTruncationWarning.fx), or '' === '' on every entry would prove nothing.
// The relocated Warnings themselves are compared in the real browser
// (browser-vkd3d-gate.mjs, against warnings-manifest.json): relocation is managed code
// the shim never runs.
//
// SKIP-WITH-NOTICE (never a fabricated pass):
//   - vkd3d-shader.{js,wasm} not restored     -> loud SKIP, exit 0.
//   - desktop vkd3d native not restored (probe exit 3 / SD0211) -> loud SKIP, exit 0.
// Any compile failure or byte mismatch -> FAIL, exit 1.
//
// Usage:  cd tests/ShadowDusk.BrowserTests && node node-test-vkd3d-wasm.mjs
//         (or: npm run vkd3d-gate)

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, mkdirSync, mkdtempSync, copyFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');

const shimPath = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'shadowdusk-vkd3d.js');
const moduleJs = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'vkd3d', 'vkd3d-shader.js');
const moduleWasm = path.join(repoRoot, 'src', 'ShadowDusk.Wasm', 'wwwroot', 'vkd3d', 'vkd3d-shader.wasm');
const probeDir = path.join(__dirname, 'Vkd3dCorpusProbe');
const outDir = path.join(__dirname, '.vkd3d-gate');

// The corpus fixtures whose DXBC_TPF output depends on the vkd3d compile options (SM1-3
// semantics on struct fields: MAP_SEMANTIC_NAMES). Their FNA (D3D_BYTECODE) compiles take
// no options and are ordinary corpus entries.
const OPTION_DEPENDENT = new Set(['Sm3SemanticStructs.fx']);
const optionDependent = (entry) => OPTION_DEPENDENT.has(entry.fixture) && entry.options.length > 0;
// The corpus fixture that compiles WITH a non-fatal vkd3d diagnostic (issue #335): its
// manifest 'messages' must be non-empty on both targets, which is what makes a host
// that drops the text on success visible here.
const WARNING_BEARING = 'ImplicitTruncationWarning.fx';
const sameBytes = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const LINE_DIRECTIVE = /^[ \t]*#[ \t]*line\b/gm;
const countLineDirectives = (bytes) => (new TextDecoder().decode(bytes).match(LINE_DIRECTIVE) || []).length;

// vkd3d's per-directive fixme reaches the process stderr (emscripten's printErr defaults to
// console.warn, bound at module load, which writes to process.stderr). Intercept the stream
// itself: count the fixme lines and keep them out of the gate output; everything else passes.
const LINE_FIXME = /fixme:.*#line directive/;
let lineFixmes = 0;
{
  const write = process.stderr.write.bind(process.stderr);
  process.stderr.write = (chunk, ...rest) => {
    const text = typeof chunk === 'string' ? chunk : new TextDecoder().decode(chunk);
    const kept = text.split('\n').filter((l) => {
      if (LINE_FIXME.test(l)) { lineFixmes++; return false; }
      return true;
    }).join('\n');
    return kept.length > 0 ? write(kept, ...rest) : true;
  };
}

function skip(reason) {
  console.log('');
  console.log('='.repeat(78));
  console.log('[vkd3d-wasm gate] SKIPPED — NOT RUN, NOT A PASS.');
  console.log(`[vkd3d-wasm gate] ${reason}`);
  console.log('='.repeat(78));
  console.log('');
  process.exit(0);
}

// ---------------------------------------------------------------------------
// 0. Module-ABSENT load-failure path THROUGH THE PRODUCT SHIM (Phase 27 review
//    input: the SD1902 trigger). Runs even when the real module IS restored, by
//    importing a copy of the shim from a temp dir with no ./vkd3d/ next to it:
//      - ensureReady() must REJECT (this rejection is what .NET maps to SD1902);
//      - a stray compile() after the failed load must throw the sticky
//        "failed to initialize" error, never silently return.
//    Needs only the committed shim, so it runs before the artifact gate below.
// ---------------------------------------------------------------------------
{
  const tmpDir = mkdtempSync(path.join(os.tmpdir(), 'sd-vkd3d-absent-'));
  const absentShimPath = path.join(tmpDir, 'shadowdusk-vkd3d.js');
  copyFileSync(shimPath, absentShimPath);
  try {
    const absentShim = await import(pathToFileURL(absentShimPath).href);
    let rejected = false;
    try {
      await absentShim.ensureReady();
    } catch (e) {
      rejected = true;
      console.log('  [OK]   module-absent: ensureReady() rejected (the SD1902 trigger) — ' +
        String(e?.message ?? e).split('\n')[0]);
    }
    if (!rejected) {
      console.error('  [FAIL] module-absent: ensureReady() RESOLVED with no ./vkd3d/ module present.');
      process.exit(1);
    }
    try {
      absentShim.compile(new TextEncoder().encode('float4 main() : COLOR { return 0; }'),
        'main', 'ps_2_0', 'absent.fx', 4, []);
      console.error('  [FAIL] module-absent: compile() after a failed load returned instead of throwing.');
      process.exit(1);
    } catch (e) {
      const msg = String(e?.message ?? e);
      if (!msg.includes('failed to initialize')) {
        console.error(`  [FAIL] module-absent: compile() threw, but without the sticky load error — got: ${msg.slice(0, 300)}`);
        process.exit(1);
      }
      console.log('  [OK]   module-absent: compile() after the failed load surfaced the sticky init error');
    }
  } finally {
    rmSync(tmpDir, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------------------
// 1. Artifact gate: the vkd3d->WASM module must be restored.
// ---------------------------------------------------------------------------
if (!existsSync(moduleJs) || !existsSync(moduleWasm)) {
  skip(
    'src/ShadowDusk.Wasm/wwwroot/vkd3d/vkd3d-shader.{js,wasm} is not restored. ' +
    'Run tools/restore.ps1 / tools/restore.sh (or place a local build in ' +
    '.wasm-build/vkd3d-wasm-out/) and re-run this gate. See ' +
    'src/ShadowDusk.Wasm/wwwroot/vkd3d/RESTORE.md.');
}

// ---------------------------------------------------------------------------
// 2. Capture the desktop ground truth (Vkd3dCorpusProbe).
// ---------------------------------------------------------------------------
mkdirSync(outDir, { recursive: true });
console.log('[vkd3d-wasm gate] capturing desktop vkd3d ground truth (Vkd3dCorpusProbe)…');
const probe = spawnSync(
  'dotnet', ['run', '--project', probeDir, '--', repoRoot, outDir],
  { stdio: 'inherit', cwd: repoRoot, shell: false });

if (probe.status === 3) {
  skip('desktop vkd3d-shader native not restored (probe exit 3 / SD0211) — run tools/restore.*.');
}
if (probe.status !== 0) {
  console.error(`[vkd3d-wasm gate] FAIL — Vkd3dCorpusProbe exited ${probe.status}.`);
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(path.join(outDir, 'manifest.json'), 'utf8'));
if (!Array.isArray(manifest) || manifest.length === 0) {
  console.error('[vkd3d-wasm gate] FAIL — probe produced an empty manifest.');
  process.exit(1);
}

// ---------------------------------------------------------------------------
// 3. Replay every captured compile through the PRODUCT shim and byte-compare.
// ---------------------------------------------------------------------------
const shim = await import(pathToFileURL(shimPath).href);

// Idempotency: the contract allows (and the C# backend performs) repeated awaits.
await shim.ensureReady();
await shim.ensureReady();

let pass = 0;
const failures = [];

// For the record: which module was gated.
const wasmSha = createHash('sha256').update(readFileSync(moduleWasm)).digest('hex');
console.log(`  restored vkd3d-shader.wasm sha256 ${wasmSha}`);

for (const entry of manifest) {
  if (!Array.isArray(entry.options) || entry.options.length % 2 !== 0) {
    console.error('[vkd3d-wasm gate] FAIL — a manifest entry has no (name, value) compile-option list; the probe is out of date.');
    process.exit(1);
  }
  if (typeof entry.requestSourceFile !== 'string') {
    console.error('[vkd3d-wasm gate] FAIL — a manifest entry has no requestSourceFile (the directive-carrying text); the probe is out of date.');
    process.exit(1);
  }
  if (typeof entry.messages !== 'string') {
    console.error('[vkd3d-wasm gate] FAIL — a manifest entry has no messages (vkd3d\'s verbatim text on the desktop, issue #335); the probe is out of date.');
    process.exit(1);
  }
}
const sensitive = manifest.filter(optionDependent);
if (sensitive.length === 0) {
  failures.push('no option-dependent compile in the corpus (Sm3SemanticStructs.fx for DirectX): ' +
    'nothing here would notice a host passing different vkd3d compile options (issue #295)');
  console.error('  [FAIL] the corpus has no option-dependent compile (Sm3SemanticStructs.fx, DirectX)');
}
// Issue #335 control: the corpus must carry a compile whose desktop message text is
// NON-EMPTY, or the message comparison below is '' === '' everywhere and a host that
// drops the text on success stays invisible.
const warningBearing = manifest.filter((e) => e.messages.length > 0);
if (!warningBearing.some((e) => e.fixture === WARNING_BEARING)) {
  failures.push(`the corpus has no warning-bearing compile (${WARNING_BEARING} with a non-empty desktop message text): ` +
    'nothing here would notice a host dropping vkd3d\'s non-fatal diagnostics on success (issue #335)');
  console.error(`  [FAIL] the corpus has no warning-bearing compile (${WARNING_BEARING}, issue #335)`);
} else {
  pass++;
  console.log(`  [OK]   messages control: ${warningBearing.length} corpus compile(s) carry a non-empty desktop message text ` +
    `(${[...new Set(warningBearing.map((e) => `${e.target}/${e.fixture}`))].join(', ')}); the first: ${JSON.stringify(warningBearing[0].messages.split('\n')[0])}`);
}

let messagesIdentical = 0;
for (const entry of manifest) {
  const label = `${entry.target}/${entry.fixture} ${entry.stage} ${entry.entryPoint} (${entry.profile} -> tt${entry.targetType}` +
    `${entry.options.length > 0 ? `, options [${entry.options.join(',')}]` : ''})`;
  const source = new Uint8Array(readFileSync(path.join(outDir, entry.sourceFile)));
  const expected = new Uint8Array(readFileSync(path.join(outDir, entry.blobFile)));
  // The desktop prepared this text (issue #319): what it handed vkd3d carries no #line line.
  if (countLineDirectives(source) !== 0) {
    failures.push(`${label}: the source the desktop handed vkd3d still contains a #line directive line (Vkd3dCompileContract.PrepareSource did not run, issue #319)`);
    console.error(`  [FAIL] ${label}: the desktop handed vkd3d a #line directive (issue #319)`);
  }
  let verdict;
  try {
    const { code: actual, messages } = shim.compile(source, entry.entryPoint, entry.profile, entry.sourceName, entry.targetType, entry.options);
    if (!(actual instanceof Uint8Array) || typeof messages !== 'string') {
      verdict = `CONTRACT — compile() returned ${actual instanceof Uint8Array ? 'the code' : 'no Uint8Array code'} and ` +
        `${typeof messages === 'string' ? 'the messages' : 'no string messages'} (expected { code, messages }, issue #335)`;
    } else if (!sameBytes(actual, expected)) {
      const firstDiff = actual.findIndex((b, i) => b !== expected[i]);
      verdict = `MISMATCH — desktop ${expected.length} B, wasm ${actual.length} B, ` +
        `first differing byte index ${firstDiff < 0 ? expected.length : firstDiff}`;
    } else if (messages !== entry.messages) {
      // Same bytes, different message text: the issue #335 shape itself.
      verdict = `MESSAGES DIFFER — bytes identical, but the desktop got ${JSON.stringify(entry.messages.slice(0, 200))} ` +
        `and the shim handed back ${JSON.stringify(messages.slice(0, 200))} (issue #335)`;
    } else {
      verdict = null;
      pass++;
      if (messages.length > 0) messagesIdentical++;
      console.log(`  [OK]   ${label} — ${actual.length} bytes, byte-identical to desktop vkd3d (via SHIM)` +
        (messages.length > 0 ? `; vkd3d's ${messages.split('\n').filter((l) => l.length > 0).length} message line(s) identical too` : ''));
    }
  } catch (e) {
    verdict = `THREW — ${String(e?.message ?? e).trim()}`;
  }
  if (verdict === null) continue;
  failures.push(`${label}: ${verdict}`);
  console.error(`  [FAIL] ${label}: ${verdict}`);
}

// Issue #335: the warning-bearing compiles must have been replayed and matched, text and
// bytes, not skipped around (a replay that threw would already be a failure above).
if (warningBearing.length > 0 && messagesIdentical < warningBearing.length) {
  failures.push(`only ${messagesIdentical} of the ${warningBearing.length} warning-bearing compile(s) handed back the desktop's message text (issue #335)`);
  console.error(`  [FAIL] ${messagesIdentical}/${warningBearing.length} warning-bearing compiles handed back the desktop's message text (issue #335)`);
}

// The prepared corpus pass is SILENT: vkd3d printed no per-directive fixme (issue #319).
if (lineFixmes !== 0) {
  failures.push(`the prepared corpus pass printed ${lineFixmes} "fixme: ... #line directive" line(s) to stderr; ` +
    'vkd3d was shown a #line directive, which both hosts must blank (Vkd3dCompileContract.PrepareSource, issue #319)');
  console.error(`  [FAIL] ${lineFixmes} vkd3d #line fixme line(s) during the prepared corpus pass (issue #319)`);
} else {
  pass++;
  console.log(`  [OK]   #line control: the prepared corpus pass (${manifest.length} compiles) printed 0 vkd3d #line fixme lines`);
}

// ---------------------------------------------------------------------------
// 3a. #line control (issue #319): the directive-carrying REQUEST text, what the browser
//     host used to hand vkd3d. (1) It must compile to the SAME bytes as the prepared text,
//     the measurement behind "the directives change no output byte": a vkd3d that started
//     honouring #line would show here. (2) vkd3d must print exactly one fixme line per
//     directive, which proves the stderr interception the silent pass above relies on is
//     live, and the corpus must carry directives at all. Also the compile-time cost of the
//     directives in this host, for the record.
// ---------------------------------------------------------------------------
{
  let rawPass = 0, directives = 0, rawMs = 0, preparedMs = 0;
  const rawFixmesBefore = lineFixmes;
  for (const entry of manifest) {
    const label = `#line control: ${entry.target}/${entry.fixture} ${entry.stage} ${entry.entryPoint} with the directives`;
    const raw = new Uint8Array(readFileSync(path.join(outDir, entry.requestSourceFile)));
    const prepared = new Uint8Array(readFileSync(path.join(outDir, entry.sourceFile)));
    const expected = new Uint8Array(readFileSync(path.join(outDir, entry.blobFile)));
    const n = countLineDirectives(raw);
    directives += n;
    const fixmesBefore = lineFixmes;
    try {
      let t = performance.now();
      const actual = shim.compile(raw, entry.entryPoint, entry.profile, entry.sourceName, entry.targetType, entry.options).code;
      rawMs += performance.now() - t;
      const printed = lineFixmes - fixmesBefore;
      t = performance.now();
      shim.compile(prepared, entry.entryPoint, entry.profile, entry.sourceName, entry.targetType, entry.options);
      preparedMs += performance.now() - t;
      if (!sameBytes(actual, expected)) {
        failures.push(`${label}: the #line directives CHANGED vkd3d's output (${actual.length} B vs the desktop's ${expected.length} B); ` +
          'the shared PrepareSource is no longer console-noise-only');
        console.error(`  [FAIL] ${label} — the directives changed the bytes`);
      } else if (printed !== n) {
        failures.push(`${label}: ${n} #line directive(s) but ${printed} vkd3d fixme line(s) intercepted; the stderr tripwire is not live`);
        console.error(`  [FAIL] ${label} — ${n} directive(s), ${printed} fixme line(s)`);
      } else {
        rawPass++;
      }
    } catch (e) {
      failures.push(`${label}: THREW — ${String(e?.message ?? e).trim().split('\n')[0]}`);
      console.error(`  [FAIL] ${label} — threw`);
    }
  }
  if (directives === 0) {
    failures.push('#line control: the corpus request text carries no #line directive at all, so nothing here would notice a host handing them to vkd3d');
    console.error('  [FAIL] #line control: no #line directive in the corpus');
  } else {
    pass++;
    console.log(`  [OK]   #line control: ${rawPass} compiles with their ${directives} directive(s) gave the desktop's bytes and ` +
      `${lineFixmes - rawFixmesBefore} fixme line(s), one per directive (intercepted); ` +
      `with the directives ${rawMs.toFixed(0)} ms, prepared ${preparedMs.toFixed(0)} ms`);
  }
}

// ---------------------------------------------------------------------------
// 3b. Option-sensitivity control (issue #295). The option-dependent compiles are
//     what make a host passing different vkd3d compile options visible above, so
//     prove they still depend on them: replayed with NO options they must NOT
//     reproduce the desktop. And the shim must refuse a call that hands it no
//     option list at all, rather than compile with none.
// ---------------------------------------------------------------------------
for (const entry of sensitive) {
  const label = `option control: ${entry.target}/${entry.fixture} ${entry.stage} ${entry.entryPoint} with NO options`;
  const source = new Uint8Array(readFileSync(path.join(outDir, entry.sourceFile)));
  const expected = new Uint8Array(readFileSync(path.join(outDir, entry.blobFile)));
  let outcome;
  try {
    const actual = shim.compile(source, entry.entryPoint, entry.profile, entry.sourceName, entry.targetType, []).code;
    outcome = sameBytes(actual, expected) ? null : `${actual.length} B, differs from the desktop's ${expected.length} B`;
  } catch (e) {
    outcome = `refused: ${String(e?.message ?? e).trim().split('\n')[0]}`;
  }
  if (outcome === null) {
    failures.push(`${label}: byte-identical to the desktop anyway, so this compile no longer depends on the ` +
      'vkd3d compile options and the corpus would not notice a host passing different ones');
    console.error(`  [FAIL] ${label} — still byte-identical to the desktop`);
  } else {
    pass++;
    console.log(`  [OK]   ${label} — ${outcome} (the compile depends on the options)`);
  }
}
{
  const entry = manifest[0];
  const source = new Uint8Array(readFileSync(path.join(outDir, entry.sourceFile)));
  for (const [what, options] of [['no option list', undefined], ['an odd-length option list', [8]]]) {
    const label = `option control: compile() handed ${what}`;
    try {
      shim.compile(source, entry.entryPoint, entry.profile, entry.sourceName, entry.targetType, options);
      failures.push(`${label}: compiled instead of being refused`);
      console.error(`  [FAIL] ${label} — compiled instead of being refused`);
    } catch (e) {
      const msg = String(e?.message ?? e);
      if (msg.includes('needs the vkd3d compile options')) {
        pass++;
        console.log(`  [OK]   ${label} — refused by the shim, not compiled with none`);
      } else {
        failures.push(`${label}: threw, but not the shim's refusal — got: ${msg.slice(0, 200)}`);
        console.error(`  [FAIL] ${label} — unexpected error: ${msg.slice(0, 200)}`);
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 4. Error path THROUGH THE PRODUCT SHIM: a broken shader must throw an Error
//    whose message is vkd3d's VERBATIM diagnostic (source name + line:col), the
//    text .NET parses for constraint 5. Without this, a regression in the shim's
//    failure branch (e.g. dropping/garbling the messages) reddens nothing — the
//    corpus above only exercises the success path.
// ---------------------------------------------------------------------------
{
  const brokenName = 'broken.fx';
  // Same deliberately broken shader as tools/vkd3d-wasm/smoke-test.mjs (missing ')').
  const brokenSource = new TextEncoder().encode(
    'float4 main() : COLOR { return float4(1,0,0,1; }\n');
  const label = `error-path/${brokenName} (ps_2_0 -> tt4, via SHIM)`;
  try {
    shim.compile(brokenSource, 'main', 'ps_2_0', brokenName, 4, []);
    failures.push(`${label}: a broken shader COMPILED instead of throwing`);
    console.error(`  [FAIL] ${label} — compiled instead of throwing`);
  } catch (e) {
    const msg = String(e?.message ?? e);
    // Verbatim vkd3d diagnostics carry "<sourceName>:<line>:<col>:" locations.
    if (new RegExp(`${brokenName.replace('.', '\\.')}:\\d+:\\d+`).test(msg)) {
      pass++;
      console.log(`  [OK]   ${label} — threw with verbatim diagnostics: ${msg.split('\n')[0]}`);
    } else {
      failures.push(`${label}: thrown message lacks the verbatim ` +
        `'${brokenName}:<line>:<col>' diagnostic — got: ${msg.slice(0, 300)}`);
      console.error(`  [FAIL] ${label} — message lacks file:line:col — got: ${msg.slice(0, 300)}`);
    }
  }
}
// ---------------------------------------------------------------------------
// 5. Empty-source THROUGH THE PRODUCT SHIM (Phase 27 review input): the shim
//    must NOT pre-judge an empty source with its own message — it goes to vkd3d
//    (pointer + length 0) and vkd3d speaks for itself, mirroring the desktop
//    backend (Vkd3dShaderCompiler passes empty source through the same way).
// ---------------------------------------------------------------------------
{
  const label = 'empty-source/empty.fx (ps_2_0 -> tt4, via SHIM)';
  try {
    shim.compile(new Uint8Array(0), 'main', 'ps_2_0', 'empty.fx', 4, []);
    failures.push(`${label}: an empty source COMPILED instead of failing`);
    console.error(`  [FAIL] ${label} — compiled instead of failing`);
  } catch (e) {
    const msg = String(e?.message ?? e);
    if (msg.includes('empty HLSL source')) {
      failures.push(`${label}: the shim pre-judged the empty source itself ` +
        `('${msg.slice(0, 120)}') instead of letting vkd3d speak`);
      console.error(`  [FAIL] ${label} — shim pre-judged: ${msg.slice(0, 120)}`);
    } else if (msg.trim().length === 0) {
      failures.push(`${label}: threw with an empty message`);
      console.error(`  [FAIL] ${label} — empty failure message`);
    } else {
      pass++;
      console.log(`  [OK]   ${label} — vkd3d spoke for itself: ${msg.split('\n')[0].slice(0, 120)}`);
    }
  }
}

console.log('');
if (failures.length > 0) {
  console.error(`[vkd3d-wasm gate] FAIL — ${pass} checks passed; ${failures.length} failures:`);
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}

console.log(`ALL ${manifest.length}/${manifest.length} CORPUS COMPILES BYTE-IDENTICAL VIA THE FAITHFUL SHIM, ` +
  `WITH VKD3D'S MESSAGE TEXT IDENTICAL ON EVERY COMPILE (${messagesIdentical} of them non-empty) ` +
  '(+ the option-dependent compiles differ without their options, + the shim refuses a missing ' +
  'option list, + the prepared text is silent and the #line directives change no byte, + the shim error path ' +
  'surfaces verbatim diagnostics, + empty source reaches vkd3d unjudged, + the module-absent load path rejects loudly) — ' +
  'WASM vkd3d == desktop vkd3d. ' +
  'Phase 4.1 byte-identity gate PASSED.');
process.exit(0);
