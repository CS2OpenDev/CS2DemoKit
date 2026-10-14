#!/usr/bin/env bash
# Packs the three CS2DemoKit libraries for local, off-nuget.org consumption, always at the
# version.json stamp: PublicRelease=true drops the -g<sha> dev suffix a branch ref otherwise
# appends, so a host's exact intra-family pins stay satisfiable across repacks.
#
# Every repack shares that one version, so a prior copy already extracted into the global NuGet
# cache survives a plain `dotnet pack` and must be purged first.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' version.json)
cache_root="${NUGET_PACKAGES:-$HOME/.nuget/packages}"

for id in parser analysis analysis.rules; do
  rm -rf "$cache_root/cs2demokit.$id/$version"
done
rm -f artifacts/pack/CS2DemoKit.*.nupkg artifacts/pack/CS2DemoKit.*.snupkg

dotnet pack src/CS2DemoKit.Parser         -c Release -o artifacts/pack -p:PublicRelease=true
dotnet pack src/CS2DemoKit.Analysis       -c Release -o artifacts/pack -p:PublicRelease=true
dotnet pack src/CS2DemoKit.Analysis.Rules -c Release -o artifacts/pack -p:PublicRelease=true

test -f "artifacts/pack/CS2DemoKit.Parser.$version.nupkg"
echo "packed $version into artifacts/pack"
