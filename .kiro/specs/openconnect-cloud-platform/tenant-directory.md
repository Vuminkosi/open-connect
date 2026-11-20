# Tenant directory access

The management API lists organizations for the authenticated issuer and subject. The member directory is restricted to owners and administrators of the requested organization; inaccessible organizations and unprivileged callers receive the same not-found response. Membership records are queried within the requested tenant and authorization is rechecked in the directory query.

The Cloud portal exposes the directory from organization navigation for eligible roles and resolves membership again on the server when loading. Identity-provider and subject values are rendered as text. Owners can change roles and confirm removals; invitations remain unavailable.

Membership records have a revision initialized to one. The authenticated management API requires an `If-Match` revision and antiforgery token for owner-only role changes and removals. The interactive portal passes the loaded revision to the same service. A serializable transaction checks the actor and prevents removal or demotion of the last owner. Successful changes record the actor, role transition, and a hashed target reference in the immutable audit table. Invitations and audit records for denied or failed changes remain outstanding. Membership recreation must not reuse an old revision when invitations are implemented.

Local verification covered upgrade of an existing membership, concurrent owner demotions, and an OIDC browser session exercising role changes, removal, CSRF rejection, stale revisions, administrator denial, and last-owner protection. Production identity-provider and assistive-technology checks remain outstanding.

The Cloud header wraps on compact screens, and member identifiers wrap without horizontal overflow. Local OIDC browser verification covered the directory and inaccessible-tenant state at 320px in dark mode and 1280px in light mode; screen-reader and production-provider verification remain outstanding.
