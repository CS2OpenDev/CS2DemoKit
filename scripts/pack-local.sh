#!/usr/bin/env bash
# Packs the three CS2DemoKit libraries for local, off-nuget.org consumption.
#
# PublicRelease=true matches ci.yml's pack step: without it a branch ref stamps
# <version>-g<sha>, which disagrees with the DLL informational version a host checks against its
# pinned PackageReference and fails ExtensionHostTests, CompatibilityMatrixTests and the Strat
# Book load check.
#
# Every repack on this branch stamps the same version, so a prior copy already extracted into
# the global NuGet cache survives a plain `dotnet pack` and must be purged first. No --no-build:
# the Release DLLs on disk may have been built under the old -g stamp, and packing them as-is
# would ship a nupkg labelled right with a DLL stamped wrong.
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
