# Release automation

The release workflow is `.github/workflows/release.yml`.

## Trigger

The workflow runs on:

- every push to `main` or `master`;
- an explicit `workflow_dispatch` run.

It does not run on a schedule. A workflow-triggered release does not push a
commit, so it does not re-trigger itself.

## Version

`VERSION` contains the fixed `major.minor` line. The workflow constructs the
release version as:

```text
<major.minor>.<github.run_number>
```

Example: with `VERSION` containing `0.4`, the first run is `0.4.1`, the next is
`0.4.2`, and so on. Change `VERSION` only when deliberately moving to a new
major/minor line. Security and maintenance releases can therefore advance the
patch component without editing the source version.

The workflow passes the resolved version to MSBuild through
`PkgForgeVersion`; the AOT binary reports exactly the same value.

## Pipeline

1. Cache and restore NuGet packages.
2. Verify formatting with `dotnet format --verify-no-changes`.
3. Run the complete regression suite.
4. Build the Windows x64 NativeAOT archive with `release.ps1 -NoSource`.
5. Run the regression suite again against the packaged AOT executable.
6. Upload three files as a workflow artifact:
   - `pkgforge-<version>-win-x64.zip`
   - `release-manifest.json`
   - `SHA256SUMS.txt`
7. Only after the build job succeeds, replace the GitHub release:
   - delete all existing releases;
   - delete remaining `v*` tags;
   - create `v<version>` at the triggering commit;
   - upload the three assets.

The release body is generated from the latest `CHANGELOG.md` section plus a
short asset summary.

## Release assets

The release contains the Windows x64 binary archive, release manifest, and SHA-256
checksum file. Source code is not uploaded as a release asset. The tagged commit
and GitHub's automatically generated source archives are the source delivery.

## Permissions

No user PAT is required for the default configuration. The workflow uses the
automatic `GITHUB_TOKEN` when `RELEASE_TOKEN` is absent, and requests:

```yaml
permissions:
  contents: write
```

That permission covers release creation/deletion, tag creation/deletion, and
asset upload.

The repository must allow GitHub Actions to obtain a write token:

```text
Settings -> Actions -> General -> Workflow permissions
  Read and write permissions
```

If an organization policy pins the token to read-only, use a fine-grained PAT.
For a repository-scoped fine-grained token:

| Permission | Access | Why |
| --- | --- | --- |
| Contents | Read and write | Releases, tags, assets |
| Metadata | Read | Mandatory repository metadata |
| Actions | Read | Optional, only for workflow-run diagnostics |

No Administration, Workflows, Packages, Environments, or organization
permissions are needed. For a classic PAT, the `repo` scope is sufficient for a
private repository; `public_repo` is the reduced public-repository equivalent.

The workflow already supports an optional `RELEASE_TOKEN` secret. It is used for
both checkout and `gh` when present, and ignored otherwise, so adding a PAT later
requires no workflow edit. Use a token scoped to this repository only.

Tag protection rules can prevent automated deletion or creation of `v*` tags.
If tag protection is enabled, grant the token the required repository
permission or remove the protection for the release tag pattern.

## AOT

The current job uses `windows-latest`. NativeAOT is platform-specific and must
run on the target platform. Windows x64 is the only release target currently
configured. Linux and macOS can be added later with matching runners and
platform-specific packaging, but they must not be cross-compiled from Windows.

If NativeAOT is unavailable on the runner, the build fails before any release
is changed. A framework-dependent fallback would require changing
`release.ps1` and the asset layout; it is not silently substituted.

## Failure behavior

Build, formatting, regression, and packaged-AOT checks all run before the
publish job. A failure in the build job leaves the current release and tags
untouched. The publish job is the only destructive stage.

Release replacement is intentionally irreversible. The workflow removes
history instead of retaining it. A later push waits for an in-flight release
run rather than cancelling it, so a partial release page is less likely. Run
this workflow only from the repository whose releases you intend to replace.

## Local equivalent

The local release command supports source packaging:

```powershell
pwsh ./scripts/release.ps1 -Version 0.4.2
```

The workflow uses:

```powershell
pwsh ./scripts/release.ps1 -Version 0.4.2 -NoSource
```

`-NoSource` produces only the binary archive, root release manifest, and root
SHA-256 file, matching the workflow assets.
