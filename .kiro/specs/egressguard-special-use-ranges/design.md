# EgressGuard special-use ranges design

`EgressGuard.IsPublicAddress` stays private. It unwraps IPv4-mapped addresses, then splits into `IsPublicIPv4(byte[])` and an IPv6 path.
The IPv6 path denies ::/96 first, then evaluates NAT64 (last 4 bytes) and 6to4 (bytes 2-5) through `IsPublicIPv4`, then applies a deny-list of prefixes.
Decision: Teredo is denied outright because its client address is obfuscated and the server address is attacker-chosen. Local-use NAT64 is denied because its mapping is operator-defined. Unrecognized address families fail closed.
