---
title: Installation from NuGet
version: v1
summary: Install OpenConnect packages and verify the target framework before building.
order: 5
---

# Installation from NuGet

Install only the packages required by the application. The package family is version-aligned; verify the selected release's target-framework assets before deploying.

```powershell
dotnet add package OpenConnect.Core --version 1.2.1
dotnet add package OpenConnect.Hosting.AspNetCore --version 1.2.1
dotnet add package OpenConnect.Client --version 1.2.1
```

The current source repository also supports source integration through project references. Published-package consumer tests remain a separate release gate from source builds.
