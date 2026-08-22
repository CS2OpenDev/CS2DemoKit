# Releasing

Three packages ship together, versioned in lockstep, from a `v*` tag. `.github/workflows/nuget.yml`
does the work; this file is the procedure and the constraints it enforces.

## Two things must agree

`version.json` holds the version. Nerdbank.GitVersioning stamps it into the assemblies and the
nupkgs. The tag only selects which commit gets published, and the workflow refuses to publish if
the tag name and the stamped version disagree.

So a release is always two steps in one commit's worth of history: change `version.json`, then tag
that commit.

```sh
# edit version.json: "version": "0.9.2"
git commit -am "0.9.2"
git tag v0.9.2
git push origin main v0.9.2
```

Tagging a commit whose `version.json` still says something else fails the build at the first step,
before anything is packed.

## Prereleases

Same mechanism, prerelease label in both places:

```sh
# edit version.json: "version": "0.9.2-beta0001"
git commit -am "0.9.2-beta0001"
git tag v0.9.2-beta0001
git push origin main v0.9.2-beta0001
```

The packages land on nuget.org and on GitHub Packages exactly as a stable release does, and a
consumer takes one by naming it:

```xml
<PackageReference Include="CS2DemoKit.Analysis" Version="0.9.2-beta0001" />
```

Intra-family pins carry the label through, so `CS2DemoKit.Analysis 0.9.2-beta0001` depends on
`[CS2DemoKit.Parser 0.9.2-beta0001]` and the set installs together.

To go stable afterwards, set `version.json` to `0.9.2` and tag `v0.9.2`. Nothing needs undoing;
`0.9.2` sorts above every `0.9.2-*`.

### Label shape

**No dots in the label.** `nugetPackageVersion.semVer` is at its default of 1, so NBGV rewrites a
dotted label: `0.9.2-beta.1` stamps as `0.9.2-beta-0001`, which then disagrees with the tag and
fails the build.

**Pad the counter to four digits.** Prerelease labels containing letters are compared as ASCII
text, not numerically, so `beta10` sorts *below* `beta2`. `beta0001` through `beta9999` sort
correctly because the padding makes lexical order match numeric order.

`alpha0001`, `beta0001`, `rc0001` all work. So does `0.9.2-beta0001`, `0.9.2-beta0002`, `0.9.2`.

### nuget.org is unlist-only

A published version cannot be deleted, only hidden from search. A typo in a tag is permanent and
public. This is the reason there is no nightly push: a build per commit would accumulate versions
nobody can remove, to save a step that is already one commit and one tag.

## What the workflow checks before it uploads

In order, and any one of them fails the release rather than publishing something broken:

1. **`PublicRelease` is true.** If the ref does not match `publicReleaseRefSpec` in `version.json`,
   NBGV appends `-g<sha>` and the family pins point at versions that were never published. The
   `v\d+\.\d+` pattern is deliberately not anchored at the end, which is what lets a prerelease tag
   match; do not anchor it.
2. **Tag agrees with the stamp.**
3. **Both test suites pass.** Against the committed four-round sample only. CI has no demo corpus,
   so run the local one before tagging (see the README).
4. **`scripts/scan-nuget-artifacts.py`.** No build-machine paths, no loose rule files that should
   have been embedded, exact intra-family pins, unbracketed external pins, SourceLink metadata,
   expected embedded-resource count.
5. **`scripts/nuget-smoke`.** Restores the exact nupkgs about to be uploaded, from a local feed with
   the repo's sources cleared, and parses a demo with them. Packaging breakage that does not show up
   in a project-graph build shows up here.

`ci.yml` runs 3 through 5 on every pull request with `-p:PublicRelease=true`, so a version bump gets
its packaging exercised before the tag exists.

## Credentials

None to manage. nuget.org auth is a trusted-publishing policy tied to owner `sid2934`, repo
`CS2OpenDev/CS2DemoKit`, workflow `nuget.yml`; the login step trades the run's OIDC token for a
short-lived key. GitHub Packages uses the built-in `GITHUB_TOKEN`. Both pushes use
`--skip-duplicate`, so re-running a tag build is safe.

Symbol packages ship as a run artifact rather than to GitHub Packages, which rejects `.snupkg`.
