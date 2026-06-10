# Integration-test performance (Phase 21)

> Linked from [`CLAUDE.md`](../CLAUDE.md) → *Build & Test*. Read this if an integration-test
> run is mysteriously slow or hangs.

`ShadowDusk.Integration.Tests` is the only project that touches heavyweight external machinery (CLI child-process spawn, native DXC + SPIRV-Cross). If a full run is intermittently very slow (one outlier hit **21m43s** vs the usual single-digit seconds), the cause is **environmental, not algorithmic** — the test logic is identical:

- **Antivirus / Defender on-access scanning** of freshly-built native binaries (`dxcompiler.dll`, SPIRV-Cross) and just-spawned executables is the prime suspect (warm cache → seconds; cold → minutes). **Dev-time mitigation:** add the repo's `**/bin`, `**/obj`, `tools/`, and the test `%TEMP%` paths to the Defender exclusion list (do **not** disable AV globally). Phase 30 CI should account for this.
- `CliBinaryFixture` now **reuses the CLI binary from the normal build** (the test project has a `ReferenceOutputAssembly=false` ProjectReference to `ShadowDusk.Cli`) instead of running a per-run `dotnet publish -c Release` into a fresh temp dir — that nested cold-Release build + fresh native-binary copy was the dominant structural cost.
- **Suite-level timeout guardrail:** pass `--settings ShadowDusk.runsettings` to `dotnet test` (repo-root file) to apply a 5-minute `TestSessionTimeout`. If the suite ever hangs again it now fails fast in bounded time instead of silently eating 20+ minutes. Per-test `CancellationTokenSource` timeouts (30 s/60 s/120 s) remain the first line of defense; this session cap is the backstop. Phase 30 CI uses the same value.
