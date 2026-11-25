using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using OpenConnect.Hosting.AspNetCore.Execution;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;
using OpenConnect.Core;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public class GatewayEndpointMappingTests
{
    [Fact]
    public void REG_AT_030_EndpointMappingIdempotencySafeAgainstDuplicateCalls()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        services.AddOpenConnect();
        services.AddOpenConnectTaskExecution();

        using var sp = services.BuildServiceProvider();
        var builder = new DefaultEndpointRouteBuilder(sp);

        // Call MapOpenConnect first
        builder.MapOpenConnect();

        // Call sub-feature mappings explicitly; must be idempotent and not throw duplicate route collisions
        builder.MapOpenConnectVoice();
        builder.MapOpenConnectNavigator();
        builder.MapOpenConnectMcp();
        builder.MapOpenConnect();

        Assert.NotEmpty(builder.DataSources);
        var route = Assert.Single(builder.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/openconnect/v1/capabilities/ready");
        Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public void OpenApi_http_operations_and_reasoned_exclusions_match_mapped_host_routes()
    {
        const string prefix = "/openconnect/v1";
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        services.AddOpenConnect();
        services.AddOpenConnectTaskExecution();

        using var provider = services.BuildServiceProvider();
        var builder = new DefaultEndpointRouteBuilder(provider);
        builder.MapOpenConnect();

        var contract = FindOpenApiDirectory();
        var declared = ReadOpenApiOperationIndex(File.ReadLines(Path.Combine(contract, "openconnect.v1.yaml")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(contract, "host-route-exclusions.json")));
        var exclusions = new HashSet<(string Method, string Path)>();
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var operation = (entry.GetProperty("method").GetString()!, NormalizePath(entry.GetProperty("path").GetString()!));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("reason").GetString()), "Every exclusion needs a reason.");
            Assert.True(exclusions.Add(operation), "Duplicate host route exclusion.");
        }
        Assert.False(declared.Overlaps(exclusions), "A route cannot be both published and excluded.");

        var mapped = new HashSet<(string Method, string Path)>();
        foreach (var endpoint in builder.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var raw = endpoint.RoutePattern.RawText;
            if (raw is null || (raw != prefix && !raw.StartsWith(prefix + "/", StringComparison.Ordinal)))
                continue;
            var path = raw == prefix ? "/" : NormalizePath(raw[prefix.Length..]);
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            foreach (var method in methods is { Count: > 0 } ? methods : ["UPGRADE"])
            {
                var operation = (method, path);
                Assert.True(mapped.Add(operation), "Duplicate mapped host method/path.");
                if (declared.Contains(operation))
                {
                    Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
                    Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                }
            }
        }

        Assert.True(declared.IsSubsetOf(mapped), "OpenAPI declares a method/path the host does not map.");
        Assert.True(exclusions.IsSubsetOf(mapped), "A host route exclusion is stale.");
        Assert.True(mapped.SetEquals(declared.Concat(exclusions)),
            "Every mapped versioned host operation must be documented or have an explicit reason for exclusion.");
    }

    private static string FindOpenApiDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "contracts", "openapi");
            if (File.Exists(Path.Combine(candidate, "openconnect.v1.yaml")))
                return candidate;
        }

        throw new DirectoryNotFoundException("OpenConnect application gateway OpenAPI document was not found.");
    }

    // This is a strict index of this repository's OpenAPI block layout, not a YAML
    // parser. Unsupported path or operation declarations fail rather than being skipped.
    private static HashSet<(string Method, string Path)> ReadOpenApiOperationIndex(IEnumerable<string> lines)
    {
        var result = new HashSet<(string Method, string Path)>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var paths = false;
        string? path = null;
        var hasOperation = false;
        foreach (var line in lines)
        {
            if (!paths)
            {
                paths = line == "paths:";
                continue;
            }
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            Assert.DoesNotContain('\t', line.AsSpan(0, line.Length - line.TrimStart().Length).ToString());
            if (line[0] != ' ')
                break;

            var indent = line.Length - line.TrimStart(' ').Length;
            if (indent == 2)
            {
                Assert.True(path is null || hasOperation, "Every path needs a method.");
                Assert.Matches(@"^  /[^\s:]*:$", line);
                path = NormalizePath(line[2..^1]);
                Assert.True(seenPaths.Add(path), "Duplicate OpenAPI path template.");
                hasOperation = false;
            }
            else if (indent == 4)
            {
                Assert.NotNull(path);
                Assert.Matches(@"^    (get|put|post|delete|options|head|patch|trace):$", line);
                Assert.True(result.Add((line[4..^1].ToUpperInvariant(), path)), "Duplicate OpenAPI operation.");
                hasOperation = true;
            }
            else
            {
                Assert.True(indent >= 6 && hasOperation, "Unsupported OpenAPI paths block layout.");
            }
        }
        Assert.True(paths && hasOperation, "No complete OpenAPI operation index was found.");
        return result;
    }

    private static string NormalizePath(string path) =>
        Regex.Replace(path, @"\{[A-Za-z_][A-Za-z0-9_]*\}", "{}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public void REG_AT_008_012_LeadAgentSingleRegistrationInvariant()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLeadAgent("lead-agent-primary", b =>
        {
            b.Name = "Primary Lead";
            b.ExecutionProfile = AgentExecutionProfile.Direct;
            b.EnablePlanning = true;
            b.EnableTodoTracking = true;
        });

        // Attempting to register a second lead agent throws
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddOpenConnectLeadAgent("lead-agent-secondary", b => b.Name = "Secondary Lead");
        });

        Assert.Contains("Only one lead agent may be registered", ex.Message);
    }

    private sealed class DefaultEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() =>
            throw new NotImplementedException();
    }
}
