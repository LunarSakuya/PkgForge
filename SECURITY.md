# Security model

## Trust boundaries

- The local `pkgforge.config.json` beside the executable is the only source
  configuration; there is no per-user, environment, or command-line source
  override. Its directory and file are part of the trust boundary. Userinfo in
  the source URL is used for requests but is stripped from display, logs, state,
  and installation manifests.
- The configured source is trusted for **metadata**. It is authenticated only by
  the transport the user chooses; shard bytes are pinned by size and SHA-256.
- Download URLs come from that source/index and are not restricted by hostname,
  suffix, origin, port, path, or query policy. A compromised source/index can
  redirect downloads to arbitrary HTTP(S) hosts; the user is explicitly trusting
  that source. HTTP and HTTPS are both accepted, and redirects are followed.
- Downloaded game files are verified against the indexed size and SHA-1 before
  they enter the cache or staging tree.
- The local filesystem is trusted only inside the explicit managed roots. Every
  path component is normalized and checked.
- The program root and its `data/` tree are shared with any process that can write
  the extracted folder. These checks protect against malicious metadata and
  accidental deletion, not against an attacker who already has write access to the
  program root.

## Path rules

- Relative index paths: no `.`/`..`, no backslashes, no absolute paths, no
  drive prefixes, no empty segments, no reserved Windows device names, no
  control/bidi/invisible characters, no trailing dots/spaces, length limits.
- Absolute managed paths are not accepted. Managed installs are relative paths
  under `data/instances`, direct children of that root, and must remain under the
  program root after normalization.
- Absolute/drive/UNC/device paths are accepted only in explicit
  `--unmanaged`/`--download-only` mode; those are not managed, have no marker or
  manifest, and are not removable by pkgforge.
- Symbolic links and reparse points are rejected along create, install, cache,
  manifest, index, and delete paths.
- Install directory is a direct child of the controlled instances root. Removal
  requires that relation to match exactly.
- Directory deletion is implemented in-process with a direct-child constraint,
  project-marker rejection, tree reparse-point rejection, and no wildcard or
  shell expansion.

## Integrity and atomicity

- Index: size + SHA-256 per shard; SQLite `application_id`, `user_version`,
  `quick_check`, `foreign_key_check`, shard tag, database schema, and counts.
- Files: size + SHA-1; copied again from cache into staging with a streaming
  re-hash.
- Natives: ZIP-slip, symlink metadata, entry-count, per-entry and total expansion
  limits; exclusions applied; conflict detection.
- Marker/manifest: schema validation, canonical paths, direct-child relation,
  file list uniqueness, total summaries, IDs, source, bundle, plan.
- Installation directory promotion and manifest write use atomic rename/move.
- Index pointer replacement and manifest writes use temp files + flush + atomic
  move.

## Download controls

- HTTP and HTTPS are accepted for index and game traffic. There is no HTTPS-only
  policy and no official-host allow-list.
- There is no hostname, suffix, origin, port, path, query, or same-origin check on
  download URLs. The client uses the URL supplied by the trusted index.
- Automatic redirects are enabled. The .NET handler applies its bounded default
  redirect limit.
- Index shard size/SHA-256 and game-file size/SHA-1 remain mandatory.
- Bounded retries, 10-minute request timeout, 30-second connect timeout, no
  unlimited retry loops.
- No Range/resume support; partial files are deleted.

## Proxy security

- Proxy precedence is explicit flags, environment, system, direct.
- Standard environment variables are read in both case forms; invalid proxy
  syntax fails closed rather than silently connecting directly.
- `NO_PROXY` supports exact hosts, domain suffixes, ports, and `*`; malformed
  entries are ignored with a warning.
- Proxy credentials are held in `NetworkCredential`/`ICredentials` and are never
  emitted in descriptions or exception text. URL userinfo is redacted.
- TLS certificate validation remains enabled; the application does not install a
  proxy CA or disable certificate validation. HTTP downloads may be redirected.
- A user-supplied proxy is a trust decision: a TLS-terminating proxy can observe
  or modify traffic if its certificate is trusted by the OS. This is outside the
  application's protection boundary.
- Failed proxy downloads use the same bounded temporary/cache/staging cleanup as
  direct downloads.

## Deletion controls

`remove` refuses to act when:

- no program-root manifest matches;
- the target does not exist;
- the in-folder marker is missing or does not match the manifest;
- the target is not a direct child of the controlled instances root;
- the target is or contains protected system/user/app-state directory data;
- the target contains project markers;
- the tree contains a reparse point;
- the folder contents differ from the manifest and `--force` was not supplied.

`--force` permits a content mismatch, but never bypasses identity, path, root,
project-marker, or reparse-point checks. Project markers are detected at any
depth. The folder is renamed to a controlled trash path before deletion. A
failed manifest or trash deletion restores the target and, where necessary, the
manifest.

## Managed versus unmanaged

Managed installs are confined to `data/instances`, recorded in
`data/manifests`, and removable only through a matching manifest and marker.
Unmanaged downloads (`--unmanaged`/`--download-only`) are an explicit export
boundary: they warn, write only to the supplied `--dir`, create no marker or
manifest, do not appear in `installed`, and are never removed or cleaned by
pkgforge. The target must be outside the portable program root and pkgforge
application state, and it still rejects protected system paths.

## Test-only behavior

There is no test-only download-host or source override. Tests set
`MCPKG_HOME` to an isolated temporary program root, write a local
`pkgforge.config.json` there, and use a loopback HTTP fixture. Production has no
equivalent source switch; HTTP is a supported source/download scheme.

## Known residual risks

- No index signature. TLS and SHA-256 are the integrity chain.
- SHA-1 for game files is the published content identifier, not a modern
  collision-resistant signature.
- No malware inspection of downloaded jars/assets.
- Local processes running as the same user, or an administrator, can bypass
  application checks.
- The program root is portable (`data` beside the executable). If it is read-only,
  the command fails with `E5001` naming the exact path; there is no per-user
  fallback. A writable program root is shared by every process running from it.
- The release executable/ZIP and index are not code-signed or index-signed yet;
  TLS plus published SHA-256 hashes are the current trust chain. Verify the ZIP
  hash through a separate trusted channel.
- The index does not provide raw launcher JSON; this tool does not create it.
- System-proxy discovery, HTTPS CONNECT proxy tunnels, SOCKS network paths, TLS
  interception, and live redirectors were not reproduced end-to-end in this
  environment; see `docs/TEST-MATRIX.md`.
- Not all physical failure modes (disk-full, remote network filesystem, OS-level
  file locks) were reproduced end-to-end; mapped error paths and tests are
  documented in `docs/TEST-MATRIX.md`.
- The index is public data; source/proxy choices are local trust decisions, not a
  guarantee of anonymity.
- A writable `pkgforge.config.json` is a trust root. If an attacker can modify it
  or the executable, they control the source and can redirect downloads. Use
  directory ACLs, HTTPS, and code signing where available.

Report security issues privately to the repository maintainers rather than in a
public issue.
