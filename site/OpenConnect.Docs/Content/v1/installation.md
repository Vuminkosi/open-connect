---
title: Installation and package selection
version: v1
summary: Choose packages for a host or client and check release compatibility.
order: 5
---

# Installation and package selection

OpenConnect is composed of separate packages. Choose a published, mutually compatible release from your configured package feed; the source tree does not establish that every package or version has been published.

| Goal | Package |
| --- | --- |
| Register the framework in a .NET host | `OpenConnect.Core` |
| Expose the ASP.NET Core gateway | `OpenConnect.Hosting.AspNetCore` and `OpenConnect.Core` |
| Call a gateway from .NET | `OpenConnect.Client` |
| Call a gateway from TypeScript | `@openconnect/client` |
| Persist tasks, sessions, events and outbox | `OpenConnect.Persistence.EFCore` and your EF Core provider |
| Add agents, tools or Blazor components | `OpenConnect.Agents`, `OpenConnect.Tools` or `OpenConnect.Components.Blazor` |

For example, in an existing .NET project:

```bash
dotnet add package OpenConnect.Core --version '1.2.*'
dotnet add package OpenConnect.Hosting.AspNetCore --version '1.2.*'
```

For a .NET client use `dotnet add package OpenConnect.Client --version '1.2.*'`. This floating range selects an available 1.2 patch release at restore; use a lock file when reproducible restores are required, and check that the resolved OpenConnect packages are compatible. For TypeScript use `npm install @openconnect/client` when available in your registry; its versioning is separate from the NuGet package family. Restore from a trusted feed. The TypeScript package targets ESM and Node 24 or later; the .NET libraries target .NET 8, 9 and 10. Check the release assets before upgrading.

For source development, the repository pins its SDK in `global.json`; restore with `dotnet restore OpenConnect.slnx --locked-mode`. Source builds do not prove a published package exists or behaves identically.

Continue with [getting started](/docs/v1/getting-started), the [.NET client](/docs/v1/dotnet-client), or the [TypeScript client](/docs/v1/typescript-client).

## Related links

- [Getting started](/docs/v1/getting-started)
- [Examples and sample projects](/docs/v1/examples)
