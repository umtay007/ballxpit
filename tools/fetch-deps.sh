#!/usr/bin/env bash
# Downloads the BepInEx 6 (be.788) / Il2CppInterop 1.5.3 / HarmonyX 2.10.2 reference
# assemblies the plugin compiles against, plus Mono.Cecil for the patcher, into ./lib.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p lib/.pkgs
fetch() { # feed id version path-inside-package
  local url
  if [ "$1" = bepinex ]; then url="https://nuget.bepinex.dev/v3/package/$2/$3/$2.$3.nupkg"
  else url="https://api.nuget.org/v3-flatcontainer/$2/$3/$2.$3.nupkg"; fi
  local pkg="lib/.pkgs/$2.$3.nupkg"
  [ -f "$pkg" ] || curl -fsSL -o "$pkg" "$url"
  unzip -ojq "$pkg" "$4" -d lib
}
fetch bepinex bepinex.core 6.0.0-be.788 lib/netstandard2.0/BepInEx.Core.dll
fetch bepinex bepinex.unity.common 6.0.0-be.788 lib/netstandard2.0/BepInEx.Unity.Common.dll
fetch bepinex bepinex.unity.il2cpp 6.0.0-be.788 lib/net6.0/BepInEx.Unity.IL2CPP.dll
fetch nuget il2cppinterop.runtime 1.5.3 lib/net6.0/Il2CppInterop.Runtime.dll
fetch nuget il2cppinterop.common 1.5.3 lib/netstandard2.0/Il2CppInterop.Common.dll
fetch nuget harmonyx 2.10.2 lib/netstandard2.0/0Harmony.dll
fetch nuget mono.cecil 0.11.6 lib/netstandard2.0/Mono.Cecil.dll
fetch nuget mono.cecil 0.11.6 lib/netstandard2.0/Mono.Cecil.Rocks.dll
echo "Reference assemblies are in $(pwd)/lib"
