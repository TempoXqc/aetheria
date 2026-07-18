#!/usr/bin/env bash
# Rebuilds the shared layer and refreshes the DLL the Unity client consumes.
# Run from the repository root after any change to src/Aetheria.Shared:
#   ./tools/sync-unity-shared.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

dotnet build "$ROOT/src/Aetheria.Shared/Aetheria.Shared.csproj" -c Release
cp "$ROOT/artifacts/bin/Aetheria.Shared/release_netstandard2.1/Aetheria.Shared.dll" \
   "$ROOT/unity/AetheriaClient/Assets/Plugins/Aetheria.Shared.dll"
echo "Copied netstandard2.1 Aetheria.Shared.dll into the Unity project."
