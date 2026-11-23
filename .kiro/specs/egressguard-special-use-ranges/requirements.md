# EgressGuard special-use ranges requirements

**Tier:** T1 (security boundary, no public API change). **Issue:** #135.

- **EGRESS-REQ-001:** IPv4 192.0.0.0/24, 192.0.2.0/24, 198.18.0.0/15, 198.51.100.0/24, 203.0.113.0/24, 240.0.0.0/4 and 255.255.255.255 are denied, in addition to existing private, loopback, link-local, shared and multicast ranges.
- **EGRESS-REQ-002:** IPv6 documentation (2001:db8::/32, 3fff::/20), IETF assignments including Teredo (2001::/23), local-use NAT64 (64:ff9b:1::/48), discard (100::/64), unspecified, loopback, IPv4-compatible (::/96), unique local, link-local, site-local and multicast addresses are denied.
- **EGRESS-REQ-003:** IPv4 embedded in IPv4-mapped (::ffff:0:0/96), NAT64 (64:ff9b::/96) and 6to4 (2002::/16) addresses is extracted and evaluated with the IPv4 rules.
- **EGRESS-REQ-004:** Every resolved record must be public; a mixed set is denied.
- **EGRESS-REQ-005:** Public IPv4 and global unicast IPv6 remain allowed. Public API is unchanged.
