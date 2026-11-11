---
title: Tools, approvals and MCP
version: v1
summary: Register read-only or effectful tools through the trusted host authorization boundary.
order: 80
---

# Tools, approvals and MCP

Tool discovery is filtered by the agent allowlist, principal grants, tenant policy and task context. Every invocation is reauthorized at the effect boundary.

Model output and tool results never grant permissions. Unregistered tools, malformed arguments, forged approvals and unsafe destinations must fail closed with stable error codes such as `tool_not_allowed`, `approval_required` or `privacy_blocked`.

Remote MCP descriptions and tool schemas are untrusted input. The host must enforce destination, scope, privacy and approval policy independently.
