#!/usr/bin/env bash
# Builds BALLxPIT Online Coop.
#
#   ./build.sh [path/to/original/BALLxPITLocalCoop.dll]
#
# The original Local Coop 0.1.0 DLL (from Nexus Mods) is needed as input: the build adds the online
# hooks to it. It defaults to original/BALLxPITLocalCoop.dll. Output: dist/BALLxPITOnlineCoop.zip.
# Needs the .NET 8 SDK; builds on Linux, macOS or Windows (Git Bash).
set -euo pipefail
cd "$(dirname "$0")"
ORIGINAL="${1:-original/BALLxPITLocalCoop.dll}"
if [ ! -f "$ORIGINAL" ]; then
  echo "Missing $ORIGINAL - download BALLxPIT: Local Coop 0.1.0 and pass the path to BALLxPITLocalCoop.dll." >&2
  exit 1
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

./tools/fetch-deps.sh

for p in Il2Cppmscorlib UnityEngine.CoreModule UnityEngine.InputLegacyModule UnityEngine.IMGUIModule UnityEngine.AudioModule Assembly-CSharp; do
  dotnet build "stubs/$p/$p.csproj" -c Release -v quiet -nologo
done

dotnet build patcher/Patcher.csproj -c Release -o out/patcher -v quiet -nologo
dotnet out/patcher/Patcher.dll "$ORIGINAL" out/patched out/ref

dotnet build src/OnlineCoop/OnlineCoop.csproj -c Release -o out/plugin -v quiet -nologo

rm -rf dist/stage
mkdir -p dist/stage/BepInEx/plugins/BALLxPITOnlineCoop
cp out/patched/BALLxPITLocalCoop.dll dist/stage/BepInEx/plugins/BALLxPITLocalCoop.dll
cp out/plugin/BALLxPITOnlineCoop.dll dist/stage/BepInEx/plugins/BALLxPITOnlineCoop/
cp docs/INSTALL.txt dist/stage/BALLxPITOnlineCoop-README.txt
rm -f dist/BALLxPITOnlineCoop.zip
(cd dist/stage && zip -qr ../BALLxPITOnlineCoop.zip .)
echo "Built dist/BALLxPITOnlineCoop.zip"
