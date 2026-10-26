# Test matrix

Environment: Windows 10.0.26300, .NET SDK 10.0.402, Release,
`UseSharedCompilation=false`.

Command:

```powershell
pwsh ./scripts/test.ps1
```

Latest result:

```text
83 passed, 0 failed, 0 skipped
```

The suite is a self-contained console harness. It creates a real temporary
**program root** under `pkgforge/.artifacts/tests`, sets `MCPKG_HOME` to it, writes
`pkgforge.config.json` there, starts a loopback HTTP fixture server, generates real
SQLite shards from the vendored canonical schemas in `schemas/`, uses a local
authenticated HTTP forward proxy and redirect fixture, and invokes the built CLI
as a child process for E2E coverage.

The suite was also run against the packaged 0.4.1 AOT executable copied to a
writable temporary program root; all 83 tests passed against that binary.

## Requirement mapping

| Requirement | Coverage |
| --- | --- |
| Single program-root state | `PortableProgramRootOwnsAllState`, `ManagedStateLivesUnderProgramRoot` |
| Empty classification fields normalize to `unknown` | `CommandSpecificFilterMatrix`, server platform list/search, `installed` kind/platform/type filters |
| Program-root manifests | `CustomDirectoryClientAndNormalRemoval`, install assertions, `CorruptManifestStopsInsteadOfBeingIgnored` |
| Launcher metadata | `LauncherVersionJsonIsGenerated` |
| Program-root default instances | all install tests; `LegacyResourcesModeMaterializesLinksAndNatives` target assertion |
| Relative managed `--dir` | `CustomDirectoryClientAndNormalRemoval`, `VersionIdWithSpacesAndQuotedDirInstall` |
| Absolute managed path rejected | `ManagedAbsoluteTargetIsRejected`, `ManagedAndUnmanagedDirectoryBoundary` |
| Traversal/escape rejected | `ManagedRelativePathBoundaryIsEnforced`, `ManifestValidationRejectsEscapingFileAndTarget`, `PathSafetyRejectsTraversalAndReservedNames` |
| Unmanaged boundary | `UnmanagedInstallWritesNoManifestOrMarker`, `ManagedAndUnmanagedDirectoryBoundary` |
| Unmanaged has no remove | `UnmanagedInstallWritesNoManifestOrMarker`, CLI boundary test |
| Corrupt/missing manifest | `CorruptManifestStopsInsteadOfBeingIgnored`, `RemoveMissingManifestFailsClosed`, `RemoveMarkerMismatchFailsClosed` |
| Deletion protection | `RemoveRequiresForceForExtraFilesAndDeletesWholeFolder`, `RemoveTamperedManifestPathFailsClosed` |
| Program root unavailable | `UnavailableProgramRootFailsClosed`, `ReadOnlyProgramRootFailsClosed` |
| Source config local-only | `LocalConfigSourceDrivesAutoUpdate`, `LocalSourceConfigAcceptsHttpAndHttpsWithoutHostPolicy`, missing/invalid tests |
| HTTP/HTTPS and no domain policy | `IndexSourceAllowsAnyHostAndBothHttpSchemes`, `LocalSourceConfigAcceptsHttpAndHttpsWithoutHostPolicy` |
| Redirects | `HttpDownloadFollowsRedirects` |
| Index integrity | `IndexUpdateAndQueriesListBothKinds`, `CorruptIndexShardKeepsPreviousIndex`, `MalformedIndexManifestFailsWithIntegrityError` |
| File integrity | install/truncated/corrupt-cache tests |
| Atomic install/removal | dirty staging, failed reinstall rollback, successful reinstall, extra-file removal |
| Concurrency | `ConcurrentCacheRequestsDownloadOnce`, `ConcurrentInstallOnSameTargetDoesNotCorrupt` |
| Dead-owner lock recovery | `ProcessLockReclaimsDeadOwnerImmediately`, `ProcessLockTimesOutAndReclaimsStale` |
| Case-variant manifest ID | `ManifestLookupAcceptsCaseVariant` |
| Proxy retained | `EnvironmentProxyAndNoProxyResolution`, `HttpDownloadUsesAuthenticatedProxyAndNoProxyBypass`, `CliEnvironmentProxyAndNoProxyFlag`, `CliProxyFlagAndInvalidProxy`, `ProxyAuthFailureLeavesNoCacheState`, `ProxyCredentialsAreRedacted` |
| Parser/quoting/spaces | `CliRejectsEmptyKeywordsAndOptionLikeValues`, `CliRejectsMalformedArgumentShapes`, `VersionIdWithSpacesAndQuotedDirInstall`, `CliAcceptsOptionsInEitherOrderAndHelp` |
| Disk preflight/reservation | `DiskSpaceReportsInsufficientFreeSpace`, `PreallocatedFileIsReservedAndExclusive` |
| Command-specific filters | `CommandSpecificFilterMatrix`, `EmptyVersionTypeNormalizesToUnknown`, `installed --kind/--platform/--type`, `list/search --type`, server platform `unknown` |
| Portable install selectors | `PackageSelectorProbesPlatformLazily` |
| Source credential redaction | `IndexSourceRedactsCredentials` |
| Unmanaged/program-state boundary | `UnmanagedTargetInsideProgramStateIsRejected`, `ExistingUnmanagedDirectoryIsNeverOverwritten` |
| Shared manifest target refusal | `SharedManifestTargetIsRejected` |
| Removal rollback | `FailedManifestDeleteRestoresTarget` |
| Nested project markers | `NestedProjectMarkersAreDetectedAndProtected` |
| Native temporary cleanup | `NativeExtractorCleansTemporaryOnMoveCollision` |
| Path/ZIP/native safety | path, symlink, ZIP-slip, native extraction tests |

## Test inventory

- **Unit (34):** source config/URL rules and credential redaction, selector
  lazy platform probing, program-root ownership, relative path boundary,
  unavailable and real read-only root, manifest relative/case/integrity
  validation, path/ZIP/symlink/nested-project-marker safety, disk preflight,
  exclusive reservation, process-lock dead-owner/stale recovery, log sanitation,
  filesystem errors, Windows device-name rejection, canonical schema resources,
  generated version.
- **Integration (5):** index update/query, same-shard refresh, corrupt shard
  rollback, empty version-type normalization, missing-index error.
- **Install (22):** launcher version JSON generation, legacy resources, virtual assets, custom relative target,
  server, extra-file force remove, missing manifest, marker mismatch, tampered
  path, unmanaged non-empty target refusal, unmanaged program-state refusal,
  shared manifest target refusal, malicious index path, truncated download
  cleanup, dirty staging/cache repair, failed reinstall rollback, successful
  reinstall, latest release/snapshot, absolute managed target refusal,
  corrupt-manifest stop, failed manifest-delete rollback, unmanaged
  no-marker/no-manifest.
- **Concurrency (2):** cache dedupe, simultaneous managed installs.
- **Isolation (1):** all managed state remains under the program root; no hidden
  per-user state directory is created.
- **Proxy (5):** environment/no-proxy rules, redirects, authenticated HTTP proxy,
  auth failure cleanup, credential redaction.
- **E2E (14):** full CLI flow, noninteractive refusal, local-config source, cached
  fallback, proxy flags/env, invalid proxy, parser refusals, space-containing
  version and relative path, managed/unmanaged boundary, command-specific
  type/platform filter matrix, incompatible combinations, malformed matrix, option
  order/help.

## Real command coverage

The E2E and install tests invoke real subprocesses and real file operations for:

```text
update
list
search
install (default, relative --dir, absolute rejection, --unmanaged,
         --download-only, -y/--yes, --force, --threads, --no-update)
remove (normal, --force, missing manifest, marker mismatch, tampered manifest)
installed
proxy flags/environment/invalid proxy
missing/invalid source config
concurrent cache and install
```

All destructive tests operate inside `pkgforge/.artifacts/tests` program roots or
explicit external test targets under the same test run. No real user state,
system directory, project directory, or existing program root is used as a
deletion target.

## Live smoke (not part of the regular suite)

A small live smoke is used to validate the real source and an actual binary
download. It is deliberately bandwidth-bounded:

```text
update -> 920 versions, 6,440 client plans, 853 server plans
list/search -> representative IDs
install 1.7.10@server -> official size/SHA-1 verified
remove -> managed record and folder removed
1.14 Pre-Release 1@server -> install and mixed-case removal succeeded
```

## Not executed / residual test gaps

- Linux/macOS runtime execution.
- A true OS ACL read-only program root; the suite uses an unavailable/non-directory
  root to prove fail-closed and no fallback.
- HTTPS CONNECT through an authenticating proxy; HTTP proxy auth is tested.
- SOCKS4/4a/SOCKS5 network round-trips.
- Real TLS interception/certificate failure through a proxy.
- Physical disk-full, remote SMB/NFS, and antivirus file-lock injection.
- Full live client asset downloads; complete client flows use deterministic local
  fixtures.
