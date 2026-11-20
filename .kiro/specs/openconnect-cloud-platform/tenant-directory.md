# Tenant directory access

The management API lists organizations for the authenticated issuer and subject. The member directory is restricted to owners and administrators of the requested organization; inaccessible organizations and unprivileged callers receive the same not-found response. Membership records are queried within the requested tenant and authorization is rechecked in the directory query. Invitations, role changes, removal, and audit of denied actions remain to be implemented.
