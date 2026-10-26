# Changelog

All notable changes to this project are documented in this file. The project
uses semantic versioning for release labels.

## Unreleased

The current version is sourced only from `VERSION`. Release headings below are
historical and are not updated automatically.

- Added GitHub Actions release automation. Releases now contain only the
  Windows binary archive, release manifest, and checksum file; source remains
  available from the tagged repository.
- Added a single-source semantic version file and monotonic version bump
  script. The release workflow advances the version from the commit intent.
- Corrected launcher library artifact paths so they are relative to the
  instance `libraries` root. This prevents launchers from resolving
  `libraries/libraries/...` and re-downloading libraries that are already
  present.
- Removed the redundant client path from generated version JSON.

## 0.4.1

- Added launcher-facing `versions/<version>/<version>.json` generation for
  client installs, including libraries, asset index, arguments, logging, and
  native rules derived from the index shards.

## 0.4.0

- Added a complete source distribution alongside the Windows x64 binary.
- Made the source tree self-contained by vendoring the SQLite shard schema
  fixtures under `schemas/`.
- Restricted unmanaged targets to locations outside the program root and
  application state.
- Normalized empty classification fields to `unknown`.
- Redacted source URL credentials from display, state, and manifests.
- Made platform probing lazy for server and explicit-platform installs.
- Added nested project-marker detection.
- Refused removal when two manifests reference the same target.
- Improved removal rollback when manifest or trash deletion fails.
- Added source and binary release manifests and checksums.
- Removed unused code and brought formatting and analyzer checks to a clean
  state.

## 0.3.5

- Hardened removal, source metadata, and index type handling.
- Expanded the regression suite.

## 0.3.4

- Added `installed` kind/platform/type filters.
- Normalized empty platform and type values to `unknown`.

## 0.3.0 - 0.3.3

- Iterative installer, index, cache, proxy, and CLI hardening releases.

## 0.2.0 and earlier

- Initial portable package provisioner implementation.
