# Rebuilds the shared layer and refreshes the DLL the Unity client consumes.
# Run from the repository root after any change to src/Aetheria.Shared:
#   powershell -ExecutionPolicy Bypass -File tools/sync-unity-shared.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

dotnet build "$root/src/Aetheria.Shared/Aetheria.Shared.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit 1 }

$dll = Join-Path $root "artifacts/bin/Aetheria.Shared/release_netstandard2.1/Aetheria.Shared.dll"
$dest = Join-Path $root "unity/AetheriaClient/Assets/Plugins/Aetheria.Shared.dll"
Copy-Item $dll $dest -Force
Write-Host "Copied netstandard2.1 Aetheria.Shared.dll into the Unity project." -ForegroundColor Green
