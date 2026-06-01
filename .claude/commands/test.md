# /test — Run the ShadowDusk test suite

Runs all tests and reports results with coverage summary.

> **Suite-level timeout guardrail (Phase 21).** `--settings ShadowDusk.runsettings`
> applies a 5-minute `TestSessionTimeout` so a regression that hangs the suite FAILS in
> bounded time instead of silently eating 20+ minutes. Keep it on every run that includes
> integration tests. (Per-test `CancellationTokenSource` timeouts are the first line of
> defense; this session cap is the backstop.)

## Default (unit tests only — fast)
```bash
dotnet test --configuration Release --settings ShadowDusk.runsettings --filter "Category!=Integration" --logger "console;verbosity=normal"
```

## With integration tests
```bash
dotnet test --configuration Release --settings ShadowDusk.runsettings --logger "console;verbosity=normal"
```

## Specific platform target
```bash
dotnet test --configuration Release --settings ShadowDusk.runsettings --filter "Category=Integration&Platform=OpenGL"
dotnet test --configuration Release --settings ShadowDusk.runsettings --filter "Category=Integration&Platform=DirectX"
dotnet test --configuration Release --settings ShadowDusk.runsettings --filter "Category=Integration&Platform=Metal"
```

## With coverage
```bash
dotnet test --configuration Release --settings ShadowDusk.runsettings --collect:"XPlat Code Coverage" --results-directory ./coverage
```

## After running tests, report:
1. Total passed / failed / skipped
2. Any failed test names with their failure message and the file:line of the assertion
3. If integration tests were skipped due to missing native binaries, call that out explicitly
4. Any tests marked `[Skip]` with the reason

## Common failures to diagnose:
- `FileNotFoundException` for native binaries → run `./tools/restore.sh` first
- Flaky timing tests → look for missing `CancellationToken` or hardcoded `Task.Delay`
- Platform-specific failures → check `RuntimeInformation` guards in the test
