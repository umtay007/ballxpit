#!/usr/bin/env bash
# Builds BALLxPIT Online Coop.
#
#   ./build.sh [path/to/original/BALLxPITLocalCoop.dll]
#
# The original Local Coop 0.1.0 DLL (from Nexus Mods) is needed as input: the build adds the online
# hooks to it. It defaults to original/BALLxPITLocalCoop.dll. Output:
#   dist/BALLxPITOnlineCoop.zip                 the two mod DLLs and a starting BepInEx.cfg
#   dist/BALLxPITOnlineCoop-with-BepInEx.zip    the same plus BepInEx 6.0.0-be.788 for IL2CPP, ready to
#                                               extract into a clean game folder
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
# BALL x PIT is a Unity 6 game: BepInEx needs UnityLogListening off there.
mkdir -p dist/stage/BepInEx/config
cp docs/BepInEx.cfg dist/stage/BepInEx/config/BepInEx.cfg
rm -f dist/BALLxPITOnlineCoop.zip dist/BALLxPITOnlineCoop-with-BepInEx.zip
(cd dist/stage && zip -qr ../BALLxPITOnlineCoop.zip .)
echo "Built dist/BALLxPITOnlineCoop.zip"

# All-in-one: BepInEx itself (LGPL-2.1) with the Unity 6 setting BALL x PIT needs, plus the mods.
BEPINEX_URL="https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip"
BEPINEX_ZIP="lib/.pkgs/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788.zip"
[ -f "$BEPINEX_ZIP" ] || curl -fsSL -o "$BEPINEX_ZIP" "$BEPINEX_URL"
[ -f lib/.pkgs/BepInEx-LICENSE.txt ] || curl -fsSL -o lib/.pkgs/BepInEx-LICENSE.txt https://raw.githubusercontent.com/BepInEx/BepInEx/master/LICENSE
rm -rf dist/stage-full
mkdir -p dist/stage-full
unzip -q "$BEPINEX_ZIP" -d dist/stage-full
cp -r dist/stage/. dist/stage-full/
cp lib/.pkgs/BepInEx-LICENSE.txt dist/stage-full/BepInEx/LICENSE-BepInEx.txt
(cd dist/stage-full && zip -qr ../BALLxPITOnlineCoop-with-BepInEx.zip .)
echo "Built dist/BALLxPITOnlineCoop-with-BepInEx.zip"
