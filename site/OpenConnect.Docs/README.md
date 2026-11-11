# OpenConnect.Docs

The official OpenConnect developer portal is an ASP.NET Core .NET 10 Blazor Web App.

- Static SSR is the default.
- Interactive Server is limited to search and theme controls.
- Documentation content is versioned Markdown under `Content/<version>/`.
- `/health/live`, `/health/ready` and `/sitemap.xml` are available without AI infrastructure.
- The optional OpenConnect assistant is disabled unless explicitly configured.

Build with:

```powershell
dotnet restore site/OpenConnect.Docs/OpenConnect.Docs.csproj --locked-mode
dotnet build site/OpenConnect.Docs/OpenConnect.Docs.csproj -c Release --no-restore -warnaserror
```
