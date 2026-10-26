# Release automation

The release workflow is `.github/workflows/release.yml`.

## Trigger

The workflow runs on:

- every push to `main` or `master`;
- an explicit `workflow_dispatch` run.

It does not run on a schedule.

The build job resolves the next version and builds with it. Only after all
build checks pass does it commit the new `VERSION`, push that commit, and let the
publish job create the tag and release. The version-bump commit contains
`[skip release]`; a second run caused by that push is skipped.

## Version

`VERSION` is the single source of the full `major.minor.patch` version. No
document, script, workflow, asset name, or tag hardcodes the current version.

`scripts/version.ps1` performs monotonic version changes:

```powershell
pwsh ./scripts/version.ps1 -Bump patch
pwsh ./scripts/version.ps1 -Bump minor
pwsh ./scripts/version.ps1 -Bump major
```

The workflow does not guess silently when the commit declares its intent. Use a
commit trailer:

```text
Version-Bump: major
```

Allowed trailer values are `major`, `minor`, and `patch`. If the trailer is
absent, the workflow uses conventional commit signals:

| Commit signal | Bump |
| --- | --- |
| `BREAKING CHANGE:` or a `type!:` subject | major |
| `feat:` or `feat(scope):` subject | minor |
| Everything else | patch |

Examples:

```text
feat: add a new command
Version-Bump: minor
```

or:

```text
fix: repair a cache race
```

The latter defaults to patch.

`scripts/version.ps1` refuses to move to an equal or lower version. It also
supports an explicit value for controlled local use:

```powershell
pwsh ./scripts/version.ps1 -Set 1.0.0
```

The workflow passes the resolved version to MSBuild through `PkgForgeVersion`;
the AOT binary reports exactly that value.

## Pipeline

1. Cache and restore NuGet packages.
2. Resolve the next version with `version.ps1 -DryRun`; `VERSION` is not changed
   yet.
3. Verify formatting with `dotnet format --verify-no-changes`.
4. Run the complete regression suite with the next version.
5. Build the Windows x64 NativeAOT archive with `release.ps1 -NoSource`.
6. Run the regression suite again against the packaged AOT executable.
7. Commit the new `VERSION` and push it with a `[skip release]` commit message.
8. Upload three files as a workflow artifact:
   - `pkgforge-<version>-win-x64.zip`
   - `release-manifest.json`
   - `SHA256SUMS.txt`
9. Only after the build job succeeds, replace the GitHub release:
   - delete all existing releases;
   - delete remaining `v*` tags;
   - create `v<version>` at the version-bump commit;
   - upload the three assets.

The release body is generated from the latest `CHANGELOG.md` section plus a
short asset summary. The current changelog section is `Unreleased`; older
numeric headings are historical records.

## Release assets

The release contains the Windows x64 binary archive, release manifest, and SHA-256
checksum file. Source code is not uploaded as a release asset. The tagged commit
and GitHub's automatically generated source archives are the source delivery.

## Permissions

The workflow uses the automatic `GITHUB_TOKEN` when `RELEASE_TOKEN` is absent,
and requests:

```yaml
permissions:
  contents: write
```

That permission covers the version-bump commit, release creation/deletion, tag
creation/deletion, and asset upload.

The repository must allow GitHub Actions to obtain a write token:

```text
Settings -> Actions -> General -> Workflow permissions
  Read and write permissions
```

If an organization policy pins the token to read-only, use a fine-grained PAT.
For a repository-scoped fine-grained token:

| Permission | Access | Why |
| --- | --- | --- |
| Contents | Read and write | Version commit, releases, tags, assets |
| Metadata | Read | Mandatory repository metadata |
| Actions | Read | Optional, only for workflow-run diagnostics |

No Administration, Workflows, Packages, Environments, or organization
permissions are needed. For a classic PAT, the `repo` scope is sufficient for a
private repository; `public_repo` is the reduced public-repository equivalent.

The workflow supports an optional `RELEASE_TOKEN` secret. It is used for
checkout, the version commit push, tag deletion, and `gh` when present, and
ignored otherwise, so adding a PAT later requires no workflow edit.

Branch protection can prevent the automatic version commit from being pushed.
If the branch requires review or has protected tags, either allow the workflow
token to bypass that rule or provide a repository-scoped PAT through
`RELEASE_TOKEN`.

## AOT

The current job uses `windows-latest`. NativeAOT is platform-specific and must
run on the target platform. Windows x64 is the only release target currently
configured. Linux and macOS would require matching runners and
platform-specific packaging; they are not cross-compiled from Windows.

If NativeAOT is unavailable on the runner, the build fails before `VERSION` is
committed and before any release is changed. A framework-dependent fallback
would require changing `release.ps1` and the asset layout; it is not silently
substituted.

## Failure behavior

Build, formatting, regression, and packaged-AOT checks all run before the
version commit and publish job. A failure in those checks leaves both `VERSION`
and the current release/tags untouched.

Once the build succeeds, the version commit and publish are separate steps. A
failure after the version commit can advance `VERSION` without a new release.
The publish stage is the only destructive stage; a failure there may leave the
release page without its previous contents.

A later push waits for an in-flight release run rather than cancelling it.

## Local equivalent

The local release command reads `VERSION` when `-Version` is omitted:

```powershell
pwsh ./scripts/release.ps1
```

The workflow uses:

```powershell
pwsh ./scripts/release.ps1 -Version <resolved-version> -NoSource
```

`-NoSource` produces only the binary archive, root release manifest, and root
SHA-256 file, matching the workflow assets.
