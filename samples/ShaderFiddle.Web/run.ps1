Set-Location $PSScriptRoot
# Serves the KNI Blazor-WASM shader fiddle on a local dev server.
# Open the printed https://localhost:5xxx URL in a browser.
dotnet run 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nExited with code $LASTEXITCODE. Press any key to close..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
