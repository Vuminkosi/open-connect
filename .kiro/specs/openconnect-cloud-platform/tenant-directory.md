# Tenant directory access

The management API lists organizations for the authenticated issuer and subject. The member directory is restricted to owners and administrators of the requested organization; inaccessible organizations and unprivileged callers receive the same not-found response. Membership records are queried within the requested tenant and authorization is rechecked in the directory query.

The Cloud portal exposes the directory from organization navigation for eligible roles and resolves membership again on the server when loading. Identity-provider and subject values are rendered as text. Owners can change roles and confirm removals; invitations remain unavailable.

Membership records have a revision initialized to one. The authenticated management API requires an `If-Match` revision and antiforgery token for owner-only role changes and removals. The interactive portal passes the loaded revision to the same service. A serializable transaction checks the actor and prevents removal or demotion of the last owner. Successful changes record the actor, role transition, and a hashed target reference in the immutable audit table. Invitations and audit records for denied or failed changes remain outstanding. Membership recreation must not reuse an old revision when invitations are implemented.

Removal deletes the active membership and atomically records its next revision in a separate tenant-scoped history row keyed by a hash of issuer and subject. Existing instances continue to see only active membership rows. Future invitation acceptance must use a revision greater than the recorded value; it must also reject or reconcile historical removals that lack a history row, including removals performed by older instances during a rolling deployment.

The revision-history migration is forward-only because dropping this table would permit old revision reuse. Recovery requires a coordinated backup or explicit history reconciliation before reverting schema; do not enable reinvitation until all writers maintain history.

Apply Cloud migrations using one deployment runner. Concurrent migrations against an empty disposable database produced duplicate schema-object errors; the PostgreSQL test classes are serialized and a single-runner upgrade preserved an existing membership and its revision.

Local verification covered upgrade of an existing membership, concurrent owner demotions, and an OIDC browser session exercising role changes, removal, CSRF rejection, stale revisions, administrator denial, and last-owner protection. Production identity-provider and assistive-technology checks remain outstanding.

The Cloud header wraps on compact screens, and member identifiers wrap without horizontal overflow. Local OIDC browser verification covered the directory and inaccessible-tenant state at 320px in dark mode and 1280px in light mode; screen-reader and production-provider verification remain outstanding.
