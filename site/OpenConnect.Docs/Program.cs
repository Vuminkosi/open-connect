using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Core;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Cloud;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-OpenConnect.Cloud";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.LoginPath = "/cloud/sign-in";
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/cloud/api/v1"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/cloud/api/v1"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy =>
{
    policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
    policy.RequireAuthenticatedUser();
}));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddSingleton(TimeProvider.System);

var cloudOidc = builder.Configuration.GetSection("OpenConnect:Cloud:Identity");
var cloudIdentityEnabled = cloudOidc.GetValue<bool>("Enabled");
var googleIdentityEnabled = cloudOidc.GetValue<bool>("Google:Enabled");
var cloudDatabaseConnection = builder.Configuration.GetConnectionString("CloudControlPlane");
if (!string.IsNullOrWhiteSpace(cloudDatabaseConnection))
{
    builder.Services.AddDbContextFactory<CloudControlPlaneDbContext>(options => options.UseNpgsql(cloudDatabaseConnection));
    builder.Services.AddScoped<ICloudTenantStore, EfCloudTenantStore>();
    builder.Services.AddScoped<ICloudInvitationStore, EfCloudInvitationStore>();
    builder.Services.AddScoped<CloudInvitationService>();
    builder.Services.AddScoped<CloudControlPlaneService>();
    builder.Services.AddScoped<CloudCapabilityService>();
    builder.Services.AddScoped<CloudCredentialService>();
    builder.Services.AddScoped<CloudReadinessService>();
}
builder.Services.AddCloudIdentityProviders(cloudOidc);

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
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/cloud/api/v1"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }
    try
    {
        await next();
    }
    catch (Exception exception) when (
        context.Request.Path.StartsWithSegments("/cloud/api/v1") &&
        CloudDatabaseFailure.IsDatabaseFailure(exception))
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenConnect.Cloud.Api")
            .LogWarning("Cloud control-plane request failed because its database is unavailable.");
        context.Response.Clear();
        await Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Cloud control plane is temporarily unavailable.").ExecuteAsync(context);
    }
});
app.MapCloudControlPlaneEndpoints(cloudOidc["Authority"], !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
app.MapCloudIdentityEndpoints(cloudIdentityEnabled, googleIdentityEnabled);

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
