Set-Location $PSScriptRoot
dotnet run 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "`nExited with code $LASTEXITCODE. Press any key to close..."
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}
