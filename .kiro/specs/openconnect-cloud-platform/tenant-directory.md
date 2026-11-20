# Tenant directory access

The management API lists organizations for the authenticated issuer and subject. The member directory is restricted to owners and administrators of the requested organization; inaccessible organizations and unprivileged callers receive the same not-found response. Membership records are queried within the requested tenant and authorization is rechecked in the directory query. Invitations, role changes, removal, and audit of denied actions remain to be implemented.

The Cloud portal exposes the directory from organization navigation for eligible roles and resolves membership again on the server when loading. Identity-provider and subject values are rendered as text. The view does not offer invitation or role-edit controls until those workflows exist.

The Cloud header wraps on compact screens, and member identifiers wrap without horizontal overflow. Local OIDC browser verification covered the directory and inaccessible-tenant state at 320px in dark mode and 1280px in light mode; screen-reader and production-provider verification remain outstanding.
