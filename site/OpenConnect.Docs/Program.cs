using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Core;
using OpenConnect.Docs.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<DocumentationContentStore>();
builder.Services.AddSingleton<DocumentationSearchService>();
builder.Services.AddSingleton<GeneratedApiStore>();
builder.Services.AddOpenConnect(options => options.Privacy.Enabled = true);
builder.Services.AddOpenConnectComponents();

var assistantEnabled = builder.Configuration.GetValue<bool>("OpenConnect:Assistant:Enabled");
var gatewayAddress = builder.Configuration["OpenConnect:Assistant:GatewayUrl"];
if (assistantEnabled && Uri.TryCreate(gatewayAddress, UriKind.Absolute, out var gatewayUri))
{
    builder.Services.AddOpenConnectClient(options =>
    {
        options.ApiBaseAddress = gatewayUri;
        options.AllowInsecureHttp = builder.Environment.IsDevelopment();
    });
    builder.Services.AddOpenConnectComposer();
}

var app = builder.Build();
var content = app.Services.GetRequiredService<DocumentationContentStore>();
content.Load();

app.UseExceptionHandler("/error");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (DocumentationContentStore store) =>
    store.IsLoaded
        ? Results.Ok(new { status = "ready", documents = store.Documents.Count })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Documentation content is not ready."));
app.MapGet("/sitemap.xml", (HttpContext context, DocumentationContentStore store) =>
{
    var origin = $"{context.Request.Scheme}://{context.Request.Host}";
    var urls = new[] { $"{origin}/", $"{origin}/docs" }
        .Concat(store.Documents.Select(d => $"{origin}/docs/{d.Version}/{d.Slug}"));
    var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        string.Concat(urls.Select(url => $"<url><loc>{System.Security.SecurityElement.Escape(url)}</loc></url>")) +
        "</urlset>";
    return Results.Text(xml, "application/xml");
});

app.MapRazorComponents<OpenConnect.Docs.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
