#!/usr/bin/env python3
"""Leak/shape scan for the packed CS2DemoKit.* nupkgs (docs/distribution/nuget-packaging-plan.md).

    ./scripts/scan-nuget-artifacts.py <dir-of-nupkgs-and-snupkgs>
    ./scripts/scan-nuget-artifacts.py artifacts/nuget-smoke-feed/*.nupkg artifacts/nuget-smoke-feed/*.snupkg

Sibling to scripts/scan-release-artifacts.py (same byte-level-grep philosophy for build-path
leaks — see that file's docstring for why BSD grep under a UTF-8 locale silently declines to
match inside binaries, which is why this reads bytes directly rather than shelling out to grep),
but a DIFFERENT gate: that script scans Velopack installers for stray build-machine paths in
first-party binaries; this one scans the NuGet packages themselves for the specific invariants
the packaging plan's empirical pack smoke established:

  1. Exact file count per archive: 8 files in a .nupkg, 5 in a .snupkg (a stray extra file —
     e.g. a rules/proto source that leaked in unembedded — changes this count).
  2. Zero build-machine home-directory paths in ANY file in the archive (unlike the Velopack
     scan, nothing here is scoped/exempted: every file in a CS2DemoKit.* package is first-party).
  3. No loose *.proto / *.yaml / *.yml / *.json payload files. The shipped rulesets and catalog
     are EMBEDDED RESOURCES inside CS2DemoKit.Analysis.dll (see its csproj), never loose files in
     the package tree; a loose one of these extensions showing up means something regressed to
     shipping unembedded content. (The core-properties `.psmdcp` file is not one of these
     extensions and is never flagged — noted because a chance substring check on "json"-like
     endings could otherwise get confused; a plain extension compare does not.)
  4. Only .nupkg (not .snupkg) nuspecs: intra-family dependencies (id starts with "CS2DemoKit")
     are exact-pinned (`[x.y.z]`); every other dependency is an unbracketed floor version. This is
     Directory.Build.targets' UseExactProjectReferenceVersions target's contract, checked from the
     packed artifact rather than the source.
  5. The `<repository>` element carries a `commit` attribute and NO `url` attribute — the repo is
     private, so NBGV emits commit-only, and that is CORRECT, not a
     leak; this scan must not flag it.

Manifest-resource count (`CS2DemoKit.Analysis.dll` must expose exactly 6 embedded resources — the
4 baseline `*.rules.yaml`, `cs2demokit-rules.schema.json`, and `catalog.json`) needs real .NET reflection,
not zip/text inspection, so it shells out to a throwaway `dotnet run` of a single-file helper
written to a temp directory outside the repo tree (so it inherits none of the repo's
Directory.Build.props/Directory.Packages.props). Skipped gracefully if `dotnet` is unavailable.
"""
import glob
import os
import re
import subprocess
import sys
import tempfile
import textwrap
import zipfile
import xml.etree.ElementTree as ET

HOME_PATHS = [b'/Users/', b'/home/', b'/root/', b'C:\\Users\\']
FORBIDDEN_LOOSE_EXTENSIONS = ('.proto', '.yaml', '.yml', '.json')

EXPECTED_NUPKG_FILES = 8
EXPECTED_SNUPKG_FILES = 5


def discover(argv):
    """Expand directory args to their *.nupkg/*.snupkg; pass explicit file args through."""
    paths = []
    for arg in argv:
        if os.path.isdir(arg):
            paths += sorted(glob.glob(os.path.join(arg, '*.nupkg')))
            paths += sorted(glob.glob(os.path.join(arg, '*.snupkg')))
        else:
            paths.append(arg)
    return paths


def read_all(path):
    """-> {entry_name: bytes} for every non-directory zip entry."""
    z = zipfile.ZipFile(path)
    return {n: z.read(n) for n in z.namelist() if not n.endswith('/')}


def check_file_count(path, entries, problems):
    is_symbols = path.lower().endswith('.snupkg')
    expected = EXPECTED_SNUPKG_FILES if is_symbols else EXPECTED_NUPKG_FILES
    if len(entries) != expected:
        problems.append(f"expected exactly {expected} files, found {len(entries)}: "
                         f"{sorted(entries)}")


def check_home_paths(entries, problems):
    for name, data in entries.items():
        found = {p.decode('latin1'): data.count(p) for p in HOME_PATHS if data.count(p)}
        if found:
            problems.append(f"{name}: build-machine path(s) found: {found}")


def check_no_loose_source_files(entries, problems):
    for name in entries:
        base = os.path.basename(name)
        if base.lower().endswith(FORBIDDEN_LOOSE_EXTENSIONS):
            problems.append(f"{name}: loose {os.path.splitext(base)[1]} file — shipped rules/"
                             f"catalog/proto content must be embedded, never packed as a file")


def find_nuspec(entries):
    for name, data in entries.items():
        if name.lower().endswith('.nuspec'):
            return name, data
    return None, None


def check_nuspec(path, entries, problems):
    if path.lower().endswith('.snupkg'):
        return  # dependency pinning is a .nuspec (main package) concern only

    name, data = find_nuspec(entries)
    if data is None:
        problems.append("no .nuspec found")
        return

    try:
        root = ET.fromstring(data)
    except ET.ParseError as ex:
        problems.append(f"{name}: could not parse as XML: {ex}")
        return

    # NuGet emits the MINIMAL nuspec schema version the content needs — a dependency-free package
    # (CS2DemoKit.Analysis.Rules) gets .../2012/06/nuspec.xsd while one with bracketed version
    # ranges (CS2DemoKit.Analysis) needs .../2013/05/nuspec.xsd. Read the namespace from the root
    # element itself rather than hardcoding one, so both are handled.
    match = re.match(r'\{(.+)\}package', root.tag)
    if not match:
        problems.append(f"{name}: unrecognized nuspec root element {root.tag!r}")
        return
    nuspec_ns = {'n': match.group(1)}

    metadata = root.find('n:metadata', nuspec_ns)
    if metadata is None:
        problems.append(f"{name}: no <metadata> element")
        return

    # The repo is public, so <repository> must carry BOTH url and commit: that pair is what lets
    # SourceLink resolve a consumer's step-into to the exact source that built the binary. A
    # missing url means PublishRepositoryUrl did not take effect and the shipped symbols are inert.
    repo_elem = metadata.find('n:repository', nuspec_ns)
    if repo_elem is None:
        problems.append(f"{name}: no <repository> element — SourceLink metadata is missing")
    else:
        if not repo_elem.get('url'):
            problems.append(f"{name}: <repository> has no url — SourceLink cannot resolve sources")
        if not repo_elem.get('commit'):
            problems.append(f"{name}: <repository> is missing its commit attribute")

    deps_group = metadata.find('n:dependencies/n:group', nuspec_ns)
    if deps_group is None:
        return  # CS2DemoKit.Analysis.Rules ships with zero dependencies — nothing to check

    for dep in deps_group.findall('n:dependency', nuspec_ns):
        dep_id = dep.get('id', '')
        version = dep.get('version', '')
        bracketed = version.startswith('[') and version.endswith(']')
        is_family = dep_id.startswith('CS2DemoKit')
        if is_family and not bracketed:
            problems.append(f"{name}: family dependency {dep_id} is NOT exact-pinned "
                             f"(version={version!r}, expected brackets)")
        if not is_family and bracketed:
            problems.append(f"{name}: external dependency {dep_id} is exact-pinned "
                             f"(version={version!r}) — externals must stay an unbracketed floor")


# ── Manifest-resource count (reflection, via a throwaway dotnet helper) ─────────────────────────

_RESOURCE_PROBE_SOURCE = textwrap.dedent("""
    using System.Reflection;
    string path = args[0];
    Assembly asm = Assembly.LoadFrom(path);
    Console.WriteLine(asm.GetManifestResourceNames().Length);
    """).strip()

_RESOURCE_PROBE_CSPROJ = textwrap.dedent("""
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
        <EnableNETAnalyzers>false</EnableNETAnalyzers>
      </PropertyGroup>
    </Project>
    """).strip()

# name -> (nupkg filename prefix that must match exactly, i.e. not also match a longer sibling
# id like "CS2DemoKit.Analysis.Rules", required manifest-resource count)
RESOURCE_COUNT_EXPECTATIONS = {
    'CS2DemoKit.Analysis': 6,
}


def resource_prefix_matches(basename, package_id):
    """True if `basename` (a .nupkg filename) is a versioned package of exactly `package_id` —
    not a differently-named sibling that merely shares the prefix (Analysis vs Analysis.Rules)."""
    if not basename.startswith(package_id + '.'):
        return False
    remainder = basename[len(package_id) + 1:]
    return bool(remainder) and remainder[0].isdigit()


def check_manifest_resource_counts(nupkg_paths, problems):
    targets = [p for p in nupkg_paths
               if any(resource_prefix_matches(os.path.basename(p), pkg_id)
                      for pkg_id in RESOURCE_COUNT_EXPECTATIONS)]
    if not targets:
        return

    try:
        subprocess.run(['dotnet', '--version'], capture_output=True, check=True)
    except (OSError, subprocess.CalledProcessError):
        print("  (dotnet not available — skipping manifest-resource-count check)")
        return

    with tempfile.TemporaryDirectory(prefix='cs2demokit-resprobe-') as tmp:
        # Deliberately OUTSIDE the repo tree (tempfile.TemporaryDirectory defaults to the OS temp
        # root) so this throwaway project does not pick up Directory.Build.props/
        # Directory.Packages.props from the repo — it needs neither and both would only add risk.
        with open(os.path.join(tmp, 'resprobe.csproj'), 'w', encoding='utf-8') as f:
            f.write(_RESOURCE_PROBE_CSPROJ)
        with open(os.path.join(tmp, 'Program.cs'), 'w', encoding='utf-8') as f:
            f.write(_RESOURCE_PROBE_SOURCE)

        for nupkg_path in targets:
            basename = os.path.basename(nupkg_path)
            package_id = next(pkg_id for pkg_id in RESOURCE_COUNT_EXPECTATIONS
                               if resource_prefix_matches(basename, pkg_id))
            expected = RESOURCE_COUNT_EXPECTATIONS[package_id]

            entries = read_all(nupkg_path)
            dll_entries = [n for n in entries
                           if n.startswith('lib/') and n.endswith(f'/{package_id}.dll')]
            if not dll_entries:
                problems.append(f"{basename}: no lib/**/{package_id}.dll found to probe")
                continue
            dll_path = os.path.join(tmp, f'{package_id}.dll')
            with open(dll_path, 'wb') as f:
                f.write(entries[dll_entries[0]])

            result = subprocess.run(
                ['dotnet', 'run', '--project', tmp, '-c', 'Release', '--', dll_path],
                capture_output=True, text=True)
            if result.returncode != 0:
                problems.append(f"{basename}: manifest-resource probe failed to run: "
                                 f"{result.stderr.strip()[-500:]}")
                continue
            try:
                count = int(result.stdout.strip().splitlines()[-1])
            except (ValueError, IndexError):
                problems.append(f"{basename}: could not parse resource count from probe "
                                 f"output: {result.stdout!r}")
                continue
            if count != expected:
                problems.append(f"{basename}: manifest resource count is {count}, expected "
                                 f"exactly {expected}")
            else:
                print(f"  ok   {basename}: manifest resource count == {count}")


def main(argv):
    if not argv:
        print("usage: scan-nuget-artifacts.py <dir-or-file> [dir-or-file ...]", file=sys.stderr)
        return 2

    paths = discover(argv)
    if not paths:
        print("!! no .nupkg/.snupkg files found", file=sys.stderr)
        return 2

    failed = False
    for path in paths:
        if not os.path.exists(path):
            print(f"!! missing artifact: {path}", file=sys.stderr)
            failed = True
            continue

        name = os.path.basename(path)
        problems = []
        entries = read_all(path)
        check_file_count(path, entries, problems)
        check_home_paths(entries, problems)
        check_no_loose_source_files(entries, problems)
        check_nuspec(path, entries, problems)

        if problems:
            failed = True
            print(f"FAIL {name}:")
            for p in problems:
                print(f"       {p}")
        else:
            print(f"  ok   {name}: {len(entries)} files, clean")

    print()
    nupkg_paths = [p for p in paths if p.lower().endswith('.nupkg')]
    resource_problems = []
    check_manifest_resource_counts(nupkg_paths, resource_problems)
    if resource_problems:
        failed = True
        for p in resource_problems:
            print(f"FAIL {p}")

    if failed:
        print("\nOne or more packed artifacts failed the leak/shape scan.", file=sys.stderr)
        return 1
    print("\nAll scanned NuGet artifacts clean.")
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
