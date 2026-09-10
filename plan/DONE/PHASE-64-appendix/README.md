# Phase 64 appendix: the probes and their raw output

Evidence record for [PHASE-64-xnb-fna-kni-content-load-proof.md](../PHASE-64-xnb-fna-kni-content-load-proof.md), in the [Phase 39 appendix](../PHASE-39-appendix/README.md) style. Everything here was measured on 2026-09-09 on the developer's Windows box (RTX 3080) against the worktree build of `ShadowDuskCLI` at commit 445fddb. The probes are throwaway scratch code, recorded so the implementation wave can start from them rather than rediscover the recipe; they are not built by anything in the repository.

| File | What it holds |
|---|---|
| [A-kni-content-load-probe.md](A-kni-content-load-probe.md) | KNI probe source + output on KNI 4.2.9001 and 4.3.9001 (the reader-name resolver finding) |
| [B-fna-content-load-probe.md](B-fna-content-load-probe.md) | FNA probe source + output (the shipped `/Profile:FNA` `.xnb` loads and renders maxd 0 vs the fxc oracle) |
| [C-monogame-desktopgl-content-load-probe.md](C-monogame-desktopgl-content-load-probe.md) | MonoGame DesktopGL probe source + output on three versions (the manifest matrix, and the most-common-consumer proof the DX-only gate never made) |
| [D-reference-artifacts.md](D-reference-artifacts.md) | How the ShadowDusk and stock-mgcb `.xnb` inputs were produced, the hex dumps, and where each runtime's resolver/whitelist source was read |
