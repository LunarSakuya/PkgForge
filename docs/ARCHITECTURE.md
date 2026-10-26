# pkgforge architecture

## Scope

`pkgforge` is a package provisioner, not a launcher. It turns one plan from the
published SQLite-shard index into a verified, isolated directory tree, records
the result in a program-root manifest, and can remove that folder safely. It does
not construct launch commands, choose Java, authenticate accounts, or implement
version upgrades.

## Layers

```text
PkgForge.Cli
  command parsing, local source config loading, auto-refresh policy, confirmations,
  stdout tables, exit-code mapping

PkgForge.Core
  IndexSource / LocalSourceConfig
  IndexContract / IndexStore / IndexUpdater
  IndexDatabase (read-only SQLite shard queries)
  ProxyConfiguration / NoProxyMatcher / CompositeWebProxy
  HttpDownloader / ContentCache
  InstallService / RemovalService / InstallManifestStore
  PathSafety / SafeDirectory / ProcessLock / AtomicFile / NativeExtractor
  SecureText (control/ANSI/credential redaction)
```

`LocalSourceConfig` reads only `pkgforge.config.json` beside the executable.
There is no compiled default source, no user config, no source environment
variable, and no source command line flag.

## Source configuration

```text
<program directory>/pkgforge.config.json
  { "schema": 1, "indexSource": "http(s)://..." }
```

The loader validates schema 1, an absolute HTTP/HTTPS URL, a host, and rejects a
query or fragment. It does not restrict the host or path. A missing or malformed
file is `E2003`, exit 2; there is no fallback.

The source and the index are the trust root. Shard and game-file URLs may use any
HTTP/HTTPS host and redirects are followed. The client no longer has an official
host allow-list, suffix check, same-origin shard check, or download-host policy.
Integrity still comes from index shard SHA-256/size and game-file SHA-1/size.

## Command filter surface

Filters are command-specific and parsed by the command dispatcher; they are not
injected into commands where they have no meaning.

| Command | `--kind` | `--platform` | `--type` |
| --- | --- | --- | --- |
| `update` | no | no | no |
| `list` | yes | yes | yes |
| `search` | yes | yes | yes |
| `install` | no (package selector carries kind) | no | no |
| `remove` | no | no | no |
| `installed` | yes | yes | yes |

Server plans have no platform value in the index; empty fields are normalized to
`unknown` for display, manifests, and filtering. They are not assumed to be `any`.
A platform filter matches only an exact normalized value (`unknown` for server
plans); it never silently treats an empty field as every platform. `--type`
accepts `all`, `release`, `snapshot`, `unknown`, or any other type present in the
current index; list/search validate the requested type against the index and
report available types on a typo. `--type` and `--platform` are rejected on
commands where they have no meaning.

`search` still matches the query against version/type/kind/platform/asset text;
`search linux --kind server` does not mean "Linux server" because server plans
have no Linux platform. Use `search 1.2 --kind server` or `list --kind server`.

## Index lifecycle

```text
GET <source>/index.json
  -> validate schema 2, format sqlite-shards, exactly three known shard ids,
     sizes, SHA-256 values, userVersion, safe relative URL paths
  -> download catalog.idx / launch.idx / assets.idx in parallel to <program>/data/tmp
  -> verify each SHA-256 and size
  -> open read-only and verify application_id, user_version, quick_check,
     foreign_key_check, meta.shard, database_schema, and manifest counts
  -> move the whole verified directory to <program>/data/db/<bundle-id>
  -> write verified.json
  -> atomically replace <program>/data/index/current.json
```

An index lock serializes updaters. A failed fetch or failed shard leaves the old
`current.json` untouched. The previous bundle is intentionally not garbage
collected, so a crash or later offline command can still use it.

When the new manifest has exactly the same shard hashes, the existing verified
manifest object is retained and only source metadata may be refreshed. This
avoids pairing a new manifest with an older `verified.json`.

## Package query

`IndexDatabase` opens each shard read-only with:

```sql
PRAGMA temp_store=MEMORY;
PRAGMA trusted_schema=OFF;
PRAGMA query_only=ON;
```

`list` aggregates plan rows and file counts; asset counts come from the assets
shard. `search` applies case-insensitive all-keyword matching in memory over the
plan rows. A resolved plan reads:

- `plans`, `versions`, `natives`, `extract_exclude`;
- `files`, `contents`, `content_urls`;
- `launch.plan_directories`;
- `asset_docs`, `asset_objects`, `asset_links`.

All URLs are parsed as absolute HTTP(S); hosts, domains, ports, paths, queries,
and origins are not restricted. Every path is validated before it can enter the
install plan.

## Download and cache

`ContentCache` is content-addressed by SHA-1, matching the published file model.
For each object:

1. Validate manifest path/hash/size and URL.
2. Reuse a valid cache object if size and SHA-1 match.
3. Acquire `<cache>/locks/<sha1>.lock` with a heartbeat and timeout.
4. Recheck the cache after the lock.
5. Preflight free space, create the `.part` exclusively (`FileShare.None`), reserve
   its exact length, then download with bounded retries; verify size and SHA-1.
6. Atomically move to `<cache>/objects/<sha1>`.
7. Remove partial state/reservation on every failure.

Downloads are bounded by `--threads` (1..32, default 8). There is no Range/resume
support. `DiskSpace` adds a 64 MiB or 5% safety margin, checks the actual
destination volume (including UNC on Windows), and `SetLength` reserves the
logical size. Staged and native-extracted files use the same exclusive reservation
pattern. A failed reservation/transfer is deleted and retried; it never becomes a
cache object or staged file. Network operations retain bounded connect/operation
timeouts and at most three attempts.

The HTTP client disables automatic redirects, so a redirect cannot silently leave
the approved host set. Game URLs must be HTTPS on official Mojang/Minecraft
endpoints (or loopback only in the explicit test mode). Index shard URLs must stay
on the configured source origin and path.

## Installation transaction

```text
resolve plan (all paths/hashes/URLs validated)
  -> determine relative target under data/instances and absolute program-root target
  -> validate target, marker, manifest, overlap, roots, reparse points
  -> print plan, require confirmation unless -y
  -> acquire target lock (heartbeat, timeout)
  -> revalidate target
  -> staging directory under <program>/data/instances/.mcpkg-staging
  -> download all cache objects
  -> create plan/skeleton directories
  -> copy + re-hash every object into staging
  -> copy verified asset links
  -> extract native JARs with zip-slip/symlink/bomb defenses
  -> write .mcpkg-install.json marker with relative install path
  -> if target existed: rename target to .mcpkg-backup/<id>-<guid>
  -> atomically rename staging to target
  -> atomically write data/manifests/<id>.json with relative path
  -> remove backup
```

No file from a failed download is promoted. If the manifest write fails, the new
target is moved out and the old backup is restored. A crash can leave a target
without a manifest; removal then fails closed with a clear message instead of
guessing.

The target's `.mcpkg-install.json` contains only identity information. The full
file list, hashes, source, bundle, plan id, and relative target/root live in the
program-root manifest.

Unmanaged downloads (`--unmanaged`/`--download-only`) use a separate staging
directory next to the explicit target, do not write a marker or manifest, and
never enter removal management. Their target must be outside both the portable
program root and every pkgforge application-state directory.

## Removal

`RemovalService`:

1. Finds exactly one program-root manifest by install id or package id.
2. Resolves the recorded relative target under `data/instances`, validates it is
   a direct child of the controlled root, and rejects protected/system/app-state
   directories.
3. Requires the in-folder marker and checks it against the manifest.
4. Rejects reparse points and project markers.
5. Compares actual files with the manifest:
   - extra files;
   - missing files;
   - changed size or SHA-1.
6. Any mismatch requires `--force`; a missing record or marker never proceeds.
   A target referenced by another manifest is refused.
7. Confirms, locks, revalidates, renames the whole folder to a controlled trash
   directory, removes the program-root manifest, deletes the trash folder, and
   restores both target and manifest if either deletion step fails.

`--force` allows a mismatched folder to be deleted and never bypasses the marker,
path, root, or reparse-point checks.

## Legacy reference validation

The old Rust prototype was read only for layout validation. Its file placement
(`versions/<id>/<id>.jar`, `libraries/...`, `assets/indexes`, `assets/objects`,
virtual/resources mappings, log configs, and skeleton directories) matches the
paths in the new SQLite plans. The new installer additionally extracts native
JARs and materializes every asset link. It intentionally does not write the old
raw version JSON because the published schema-2 shards store derived plans, not
original Mojang version documents; launcher metadata remains the launcher's
responsibility.

The legacy removal scope (`versions/<id>` plus a shared registry entry) was
replaced with a whole isolated folder plus a program-root manifest/marker, because
the user-visible contract here is one package per folder and no PCL/HMCL residue.

## Network routing

### Proxy resolution

`CommandRuntime.ConfigureNetwork` resolves `ProxyConfiguration` once per network
command, before any HTTP client is created. Network-free operations
(`list --no-update`, `search --no-update`, `remove --no-update`, `installed`) do
not initialize proxy state.

Priority:

```text
--proxy / --no-proxy
  > lowercase or uppercase HTTP_PROXY / HTTPS_PROXY
    > ALL_PROXY
      > operating-system proxy
        > direct
```

`NO_PROXY` is applied as a bypass before selecting any proxy, including explicit
and system proxies. It supports `*`, exact hosts, leading-dot/`*.` domain
suffixes, optional ports, and bracketed IPv6; malformed entries warn and are
ignored. Empty proxy values are unset.

`CompositeWebProxy` implements scheme-specific environment proxies plus a system
proxy fallback. It implements `IWebProxy` and a credential provider so an
authenticated proxy can be selected without exposing credentials in logs.
`ProxyConfiguration` validates proxy syntax up front; invalid syntax is a
configuration error (`E2004`, exit 2), not a silent direct fallback. Unreachable,
TLS/rejection, timeout, and mid-stream failures remain bounded network failures
(`E3001`, exit 5). `SecureText` strips control/bidi characters and redacts URL
userinfo from diagnostics.

System proxy discovery uses `WebRequest.GetSystemWebProxy()`. It does not contain
vendor-specific ports. On Linux, environment variables are the practical source.

### Download and redirect policy

`HttpDownloader` uses `SocketsHttpHandler` with `AllowAutoRedirect = true`. It
accepts absolute HTTP and HTTPS URLs; it does not validate hosts, suffixes,
origins, ports, paths, or query strings. The only mandatory post-download checks
are the index-provided size and SHA-1 (game files) or size and SHA-256 (index
shards).

This is intentional: the configured source and its index are trusted. A malicious
source/index can redirect downloads elsewhere; hash/size checks only prove that
bytes match the index. Deployment protection is HTTPS plus control over
`pkgforge.config.json` and the executable directory.

## State roots

- The program root is the only state root. `data/` beside `pkgforge.exe` contains
  index, database, cache, manifests, locks, temporary files, and default managed
  instances.
- There is no per-user state, hidden `.mcpkg`, LocalAppData install root, or
  fallback. If the program root is read-only, the command fails with `E5001` and
  names the path.
- `MCPKG_HOME` is an explicit absolute portable-root override for tests and
  prepared portable deployments. It is not a user-state location.
- Managed installs are `data/instances/<id>`; their manifests are
  `data/manifests/<id>.json`; locks are `data/locks/`.
- Source configuration remains `pkgforge.config.json` beside the executable.

## Locking

- Global updater lock: timeout 2 minutes, stale after 15 minutes.
- Per-target install lock: timeout 5 seconds, stale after 30 minutes.
- Per-content cache lock: timeout 10 minutes, stale after 20 minutes.
- Lock holder writes pid/token, refreshes mtime every 5 seconds.
- Stale locks are renamed out of the way before deletion; disposal only deletes a
  lock whose token is still present.

## Logging and exit behavior

- Data on stdout, diagnostics on stderr.
- `[INFO]`, `[WARN]`, `[ERROR]`, `[HINT]` prefixes.
- UTF-8 console output without BOM and platform-native line endings.
- Control, ANSI escape, and bidirectional characters are stripped from log/data
  lines; proxy credentials are redacted.
- Non-interactive stdin fails confirmation with exit 9 instead of hanging.
- No TUI, no progress bar, no animated display.
- Arguments are OS-parsed; the parser rejects empty option values, empty search
  keywords, option-like values without `=`, overlong arguments, duplicate flags or
  values, unknown options, extra positionals,
  `--no-update` with proxy options on local-only commands, and
  `--proxy --no-proxy`.
- Error handlers print a concrete `[ERROR]` line followed by a general `[HINT]`
  with recovery guidance; hints use placeholders/generic examples rather than
  one-off paths.
- `-y --force` operations emit an explicit warning.

## Reuse boundary

The manifest and SQLite schema are treated as a published contract. The test
project embeds the canonical schema SQL files from `schemas/`; those fixtures
are part of this source distribution, so no external repository is required to
build or test the source tree.

The only intentional extension is an optional `Func<Uri, Uri>` asset-URL mapper on
`IndexDatabase`, used by the test harness because `v_asset_objects` canonicalizes
asset URLs to the official CDN. Production CLI construction passes `null`.
