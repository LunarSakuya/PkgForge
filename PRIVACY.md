# Privacy Notice

This notice describes the data practices of the software distribution that
accompanies it. It applies to the software itself, not to independent websites,
repositories, hosting providers, or services operated by third parties.

## 1. No account or telemetry system

The software does not create an account, require a sign-in, or include a
telemetry, analytics, advertising, or crash-reporting service. It does not
transmit a device identifier, installation identifier, or usage history to the
maintainer.

## 2. Operator-controlled configuration

The operator supplies the source endpoint in a local configuration file next to
the executable. The software contacts that endpoint, and endpoints referenced by
the returned index metadata, to perform the requested operation. The operator is
responsible for choosing those endpoints and for the terms that apply to them.

## 3. Local state and logs

The software stores its index, cache, manifests, locks, and managed installations
under the program root selected by the operator. These files are local to that
root. The software may write diagnostic messages to standard output or standard
error, including paths, endpoint host names, HTTP status codes, and verification
details. Operators should avoid publishing logs that contain private paths or
credentials.

## 4. Credentials

Proxy credentials are supplied by the operator through command-line options,
environment variables, or system configuration. They are held in memory for the
request and are not written to the software's state files. Source URL credentials,
if configured by the operator, are used for requests but are excluded from the
display and persisted source metadata.

Credentials, private keys, tokens, and other secrets should never be placed in
public issues, logs, archives, or configuration files that are distributed to
others.

## 5. Third-party processing

Requests to index hosts, file hosts, proxy servers, and other services are
handled by those third parties under their own privacy policies. The maintainer
does not control their logging, retention, security, or data practices. A proxy
operator can observe connection metadata and, where TLS is terminated, request
content.

## 6. Children

The software is a developer and operator tool. It is not directed to children,
does not create accounts, and does not knowingly collect personal data from
children.

## 7. Security

No transmission or storage system is completely secure. The software verifies
declared index and file integrity data where available, but those values are not
a substitute for a trusted source, a secure operating environment, or a digital
signature. See `SECURITY.md` for the security model.

## 8. Changes

A later distribution may include a revised privacy notice. The revised notice
applies from the date it is published with that distribution.

## 9. Contact

Use the contact channel identified in the distribution metadata. Do not send
credentials, private keys, tokens, or other secrets.
