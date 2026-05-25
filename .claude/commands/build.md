# /build — Build the ShadowDusk solution

Builds the full solution in Release configuration and reports any errors.

```bash
dotnet build --configuration Release
```

If the build fails, read the error output carefully:
- **CS errors**: type/namespace issues — check `using` directives and project references
- **NETSDK errors**: missing SDK, wrong TFM, or NuGet restore failure — run `dotnet restore` first
- **Native tool errors**: if `tools/` binaries are missing, run `./tools/restore.sh` (or `.\tools\restore.ps1`) first

After a successful build, report:
1. Which projects were built
2. Any warnings (treat CA/analyzer warnings as worth mentioning)
3. Output binary locations under `bin/Release/`
