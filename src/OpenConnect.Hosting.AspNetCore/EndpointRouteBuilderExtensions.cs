using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore.Execution;

[assembly: InternalsVisibleTo("OpenConnect.Hosting.Tests")]

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Maps the versioned OpenConnect HTTP API.</summary>
public static class EndpointRouteBuilderExtensions
{
    private static readonly ConditionalWeakTable<IEndpointRouteBuilder, HashSet<string>> MappedEndpointGroups = new();

    private static bool TryMarkMapped(IEndpointRouteBuilder endpoints, string groupName)
    {
        var set = MappedEndpointGroups.GetOrCreateValue(endpoints);
        lock (set)
        {
            return set.Add(groupName);
        }
    }

    /// <summary>Maps OpenConnect endpoints under <c>/openconnect/v1</c>.</summary>
    public static IEndpointRouteBuilder MapOpenConnect(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (TryMarkMapped(endpoints, "core"))
        {
            var group = endpoints.MapGroup("/openconnect/v1");

            group.MapPost("/tasks", async (HttpContext context, TaskRequest request, CancellationToken cancellationToken) =>
            {
                var store = context.RequestServices.GetService<InMemoryTaskStore>();
                return store is null
                    ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Task execution is not configured. Register bounded task execution to enable task submission.")
                    : await HandleCreateTaskAsync(context, request, store, cancellationToken);
            }).RequireAuthorization();
            group.MapGet("/tasks/{id}", HandleGetTaskAsync).RequireAuthorization();
            group.MapPost("/tasks/{id}/cancel", HandleCancelTaskAsync).RequireAuthorization();
            group.MapGet("/tasks/{id}/tools/{invocationId}", (string id, string invocationId, HttpContext context, IServiceProvider services) =>
            {
                var tracker = services.GetService<DurableToolExecutionTracker>();
                return tracker is null ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Tool tracking is not configured.") : HandleGetToolInvocationStatus(id, invocationId, context, tracker);
            }).RequireAuthorization();
            group.MapPost("/sessions", HandleCreateSessionAsync).RequireAuthorization();
            group.MapGet("/sessions/{id}", HandleGetSession).RequireAuthorization();
            group.MapGet("/sessions/{id}/events", HandleGetSessionEventsFromRequestAsync).RequireAuthorization();
            group.MapGet("/capabilities", HandleCapabilities).RequireAuthorization();
            group.MapPost("/speech/synthesize", HandleSpeechSynthesisAsync).RequireAuthorization();
            group.MapGet("/components", HandleComponents).RequireAuthorization();
            group.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
            group.MapGet("/health/ready", HandleReady).RequireAuthorization();
        }

        // Sub-feature endpoints are idempotent and safe to map here or call explicitly
        endpoints.MapOpenConnectVoice();
        endpoints.MapOpenConnectNavigator();
        endpoints.MapOpenConnectMcp();

        return endpoints;
    }

    /// <summary>Maps OpenConnect voice endpoints under <c>/openconnect/v1/voice</c>.</summary>
    public static IEndpointRouteBuilder MapOpenConnectVoice(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!TryMarkMapped(endpoints, "voice")) return endpoints;

        var group = endpoints.MapGroup("/openconnect/v1/voice");
        group.MapGet("/status", (IServiceProvider services) =>
        {
            var vad = services.GetService<ServerSileroVadEngine>();
            return Results.Ok(new { vadConfigured = vad is not null });
        }).RequireAuthorization();

        group.MapPost("/audio", (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType && !context.Request.ContentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Audio payload must be audio/* or multipart form.");
            }
            if (context.Request.ContentLength > 10 * 1024 * 1024)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Audio payload exceeds maximum allowed size.");
            }
            return Results.Ok(new { status = "accepted" });
        }).RequireAuthorization();

        group.Map("/realtime/{sessionId}", async (HttpContext context, string sessionId, CancellationToken cancellationToken) =>
        {
            await VoiceRealtimeWebSocketHandler.HandleAsync(context, sessionId, cancellationToken).ConfigureAwait(false);
        }).RequireAuthorization();

        return endpoints;
    }

    /// <summary>Maps OpenConnect navigator endpoints under <c>/openconnect/v1/navigator</c>.</summary>
    public static IEndpointRouteBuilder MapOpenConnectNavigator(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!TryMarkMapped(endpoints, "navigator")) return endpoints;

        var group = endpoints.MapGroup("/openconnect/v1/navigator");
        group.MapGet("/status", (IServiceProvider services) =>
        {
            var registry = services.GetService<IWorkerCapabilityRegistry>();
            var ready = registry?.GetReady().Where(w => w.Kind == OpenConnectWorkerKind.NavigatorPlaywright).ToArray() ?? [];
            return Results.Ok(new { ready = ready.Length > 0, count = ready.Length });
        }).RequireAuthorization();

        return endpoints;
    }

    /// <summary>Maps OpenConnect MCP endpoints under <c>/openconnect/v1/mcp</c>.</summary>
    public static IEndpointRouteBuilder MapOpenConnectMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!TryMarkMapped(endpoints, "mcp")) return endpoints;

        var group = endpoints.MapGroup("/openconnect/v1/mcp");
        group.MapGet("/tools", (IServiceProvider services) =>
        {
            var catalog = services.GetService<IToolCatalog>();
            return Results.Ok(catalog is not null ? catalog.GetForAgent(new AgentId("default")) : Array.Empty<ToolDescriptor>());
        }).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> HandleSpeechSynthesisAsync(
        SpeechRequest request,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000 ||
            !string.Equals(request.VoiceId, "af_heart", StringComparison.Ordinal) ||
            request.Format is not ("pcm" or "wav"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["speech"] = ["Only af_heart PCM or WAV synthesis is supported."] });

        var endpoint = configuration["Workers:Kokoro:Endpoint"];
        var token = configuration["OPENCONNECT_WORKER_TOKEN"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var workerUri) || string.IsNullOrWhiteSpace(token))
            return Results.Problem(statusCode: 503, title: "Kokoro speech worker is not configured.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(workerUri, "/internal/v1/execute"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Content = JsonContent.Create(new
        {
            schemaVersion = "openconnect.v1",
            taskId = $"tts-{Guid.NewGuid():N}",
            kind = "speech.synthesize",
            payload = new
            {
                schemaVersion = "openconnect.v1",
                text = request.Text,
                voiceId = "af_heart",
                locale = request.Locale,
                format = request.Format,
                speechEpoch = request.SpeechEpoch,
                idempotencyKey = request.IdempotencyKey
            }
        });

        using var response = await httpClientFactory.CreateClient("openconnect-kokoro").SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Results.Problem(statusCode: (int)response.StatusCode, title: "Kokoro speech synthesis failed.");

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("result", out var result))
            return Results.Problem(statusCode: 502, title: "Kokoro returned an invalid synthesis response.");
        return Results.Json(result.Clone());
    }

    private sealed record SpeechRequest(string Text, string VoiceId = "af_heart", string Locale = "en-US", string Format = "pcm", long SpeechEpoch = 0, string? IdempotencyKey = null);

    internal static async Task<IResult> HandleCreateTaskAsync(HttpContext context, TaskRequest request, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ClientTaskId) || request.ClientTaskId.Length > 128)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["clientTaskId"] = ["A client task ID of at most 128 characters is required."] });

        var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["A valid Idempotency-Key header is required."] });

        var tenantClaim = context.User.FindFirstValue("tenant_id");
        var principalClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!UlidTryParse(tenantClaim, out var tenant) || !UlidTryParse(principalClaim, out var principal))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The authenticated identity is missing required tenant or subject claims.");

        if (request.Message?.Length > 32_000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["message"] = ["A message of at most 32000 characters is allowed."] });
        if (request.SessionId is not null && !UlidTryParse(request.SessionId, out _))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sessionId"] = ["A valid session ID is required."] });

        var services = context.RequestServices;
        if (services.GetService<IHostTaskExecutionDispatcher>() is not { } dispatcher ||
            services.GetService<IHostTaskExecutionAuthorizer>() is null || services.GetService<IHostTaskExecutor>() is null)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Task execution is not configured. Register an executor and authorizer to enable task submission.");
        if (request.SessionId is not null && (!UlidTryParse(request.SessionId, out var sessionId) ||
            services.GetService<InMemorySessionStore>()?.Get(sessionId) is not { } session || session.TenantId.Value != tenant || session.PrincipalId.Value != principal))
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Session not found.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.ClientTaskId}\n{request.Message}\n{request.SessionId}")));
        var result = await dispatcher.AcceptAsync(request, key, fingerprint, new TenantId(tenant), new PrincipalId(principal), cancellationToken);
        return result.Status switch
        {
            ExecutionAdmissionStatus.Conflict => Results.Problem(statusCode: 409, title: "The idempotency key was already used with a different request.", type: "https://openconnect.dev/problems/idempotency-conflict"),
            ExecutionAdmissionStatus.Unavailable => Results.Problem(statusCode: 503, title: "Task execution is at capacity. Try again shortly."),
            _ => IdentifierJson(result.Task!, StatusCodes.Status202Accepted, $"/openconnect/v1/tasks/{result.Task!.Id.Value}")
        };
    }

    internal static async Task<IResult> HandleGetTaskAsync(string id, HttpContext context, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The task ID is invalid.");
        if (!UlidTryParse(context.User.FindFirstValue("tenant_id"), out var tenant) ||
            !UlidTryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out var principal))
            return Results.Problem(statusCode: 403, title: "The authenticated identity is missing required tenant or subject claims.");
        var task = await store.GetAsync(value, cancellationToken);
        return task is null || task.TenantId.Value != tenant || task.PrincipalId.Value != principal
            ? Results.Problem(statusCode: 404, title: "Task not found.")
            : IdentifierJson(task, StatusCodes.Status200OK);
    }

    internal static async Task<IResult> HandleCancelTaskAsync(string id, HttpContext context, InMemoryTaskStore store, CancellationToken cancellationToken)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The task ID is invalid.");
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var existing = await store.GetAsync(value, cancellationToken);
        if (existing is null || existing.TenantId.Value != tenant || existing.PrincipalId.Value != principal)
            return Results.Problem(statusCode: 404, title: "Task not found.");
        var dispatcher = context.RequestServices.GetService<IHostTaskExecutionDispatcher>();
        if (dispatcher is null) return Results.Problem(statusCode: 503, title: "Task execution is not configured.");
        var cancelled = await dispatcher.CancelAsync(value, cancellationToken);
        return cancelled is null ? Results.Json(null, options: OpenConnectHttpJson.CreateOptions()) : IdentifierJson(cancelled, StatusCodes.Status200OK);
    }

    internal static IResult HandleGetToolInvocationStatus(string id, string invocationId, HttpContext context, DurableToolExecutionTracker tracker)
    {
        if (!UlidTryParse(id, out var taskId) || !UlidTryParse(invocationId, out var invId))
            return Results.Problem(statusCode: 400, title: "The task or tool invocation ID is invalid.");

        if (!TryIdentity(context, out var tenant, out var principal))
            return IdentityError();

        var record = tracker.GetSanitized(invId, new TenantId(tenant), new PrincipalId(principal));
        if (record is null || record.TaskId.Value != taskId)
            return Results.Problem(statusCode: 404, title: "Tool invocation not found.");

        return IdentifierJson(record, StatusCodes.Status200OK);
    }

    internal static IResult HandleCreateSession(HttpContext context, SessionRequest? request, InMemorySessionStore store)
    {
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var session = store.Create(new TenantId(tenant), new PrincipalId(principal));
        return IdentifierJson(session, StatusCodes.Status201Created, $"/openconnect/v1/sessions/{session.Id.Value}");
    }

    /// <summary>Creates a session and registers its genuine public UUID in the durable mapping when available.</summary>
    /// <remarks>
    /// Wire-identical to <see cref="HandleCreateSession"/> (same 201 body/location): the added
    /// <c>ISessionUuidMappingStore.TryAddAsync</c> side-effect only links a freshly generated UUID
    /// (never derived from the ULID) to the new runtime session. A mapping <c>Conflict</c> fails
    /// closed with 409 and the half-created session is removed; an absent store keeps legacy behavior.
    /// </remarks>
    internal static async Task<IResult> HandleCreateSessionAsync(HttpContext context, SessionRequest? request, InMemorySessionStore store, CancellationToken cancellationToken = default)
    {
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var session = store.Create(new TenantId(tenant), new PrincipalId(principal));

        var mapping = context.RequestServices.GetService<ISessionUuidMappingStore>();
        if (mapping is not null)
        {
            var publicId = Guid.NewGuid();
            SessionUuidMappingSaveOutcome outcome;
            try
            {
                outcome = await mapping.TryAddAsync(publicId, session.Id, new TenantId(tenant), new PrincipalId(principal), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                store.Remove(session.Id.Value);
                throw;
            }

            if (outcome == SessionUuidMappingSaveOutcome.Conflict)
            {
                store.Remove(session.Id.Value);
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Session could not be created due to a mapping conflict.",
                    type: "https://openconnect.dev/problems/session-mapping-conflict");
            }

            if (context.RequestServices.GetService<ISessionUuidResolver>() is InMemorySessionUuidResolver memory)
                memory.Register(publicId, session.Id.Value);
        }

        return IdentifierJson(session, StatusCodes.Status201Created, $"/openconnect/v1/sessions/{session.Id.Value}");
    }

    internal static IResult HandleGetSession(string id, HttpContext context, InMemorySessionStore store)
    {
        if (!UlidTryParse(id, out var value)) return Results.Problem(statusCode: 400, title: "The session ID is invalid.");
        if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
        var session = store.Get(value);
        return session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != principal
            ? Results.Problem(statusCode: 404, title: "Session not found.") : IdentifierJson(session, StatusCodes.Status200OK);
    }

    internal static IResult HandleGetSessionEvents(string id, long? after, HttpContext context, InMemorySessionStore store)
    {
        if (after < 0) return Results.Problem(statusCode: 400, title: "The session or event position is invalid.");
        if (UlidTryParse(id, out var value))
        {
            if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
            var session = store.Get(value);
            if (session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != principal) return Results.Problem(statusCode: 404, title: "Session not found.");
            return Results.Ok(store.GetEvents(value, after ?? 0));
        }

        // UUID public identity path (contract EVENT-V1-PUBLIC): the route UUID is the
        // genuine public session id. It resolves to a runtime ULID for storage lookup
        // only and is never derived from a ULID. Unmapped UUIDs answer 404 so session
        // existence stays concealed, exactly like the legacy ownership check.
        if (!Guid.TryParseExact(id, "D", out var sessionUuid) || sessionUuid == Guid.Empty)
            return Results.Problem(statusCode: 400, title: "The session or event position is invalid.");
        if (!TryIdentity(context, out var uuidTenant, out var uuidPrincipal)) return IdentityError();
        var resolver = context.RequestServices.GetService<ISessionUuidResolver>();
        if (resolver is null || !resolver.TryResolve(sessionUuid, out var mapped))
            return Results.Problem(statusCode: 404, title: "Session not found.");
        var mappedSession = store.Get(mapped);
        if (mappedSession is null || mappedSession.TenantId.Value != uuidTenant || mappedSession.PrincipalId.Value != uuidPrincipal)
            return Results.Problem(statusCode: 404, title: "Session not found.");

        var records = store.GetEvents(mapped, after ?? 0);
        var canonical = new List<CanonicalAgentEvent>(records.Count);
        foreach (var record in records)
        {
            // Records without genuine stored event UUID/time cannot be projected;
            // answer 422 rather than fabricate metadata (contract EVENT-V1-PUBLIC).
            if (!CanonicalEventEndpointAdapter.TryToCanonical(record, sessionUuid, out var envelope) || envelope is null)
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "A stored event lacks genuine canonical identity and cannot be projected.",
                    type: "https://openconnect.dev/problems/legacy-event-without-canonical-identity");
            canonical.Add(envelope);
        }

        return Results.Json(canonical, CanonicalJsonOptions);
    }

    internal static IResult HandleGetSessionEventsFromRequest(string id, HttpContext context, InMemorySessionStore store)
    {
        var query = context.Request.Query;
        var hasAfterSequence = query.ContainsKey("afterSequence");
        var hasAfter = query.ContainsKey("after");
        if (hasAfterSequence && hasAfter ||
            !TryReadCursor(query, hasAfterSequence ? "afterSequence" : "after", out var cursor))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The event position is invalid.", type: "https://openconnect.dev/problems/invalid_request");

        return HandleGetSessionEvents(id, cursor, context, store);
    }

    /// <summary>Durable-first UUID resolve for the canonical event route (bridge read side).</summary>
    /// <remarks>
    /// ULID legacy, cursor handling, 404 concealment, 422 projection, and post-resolve dual
    /// ownership checks are identical to <see cref="HandleGetSessionEvents"/>. The only difference
    /// is resolve order: <c>IDurableSessionUuidResolver</c> first (fail-closed null on absent or
    /// foreign), then the process-local <c>ISessionUuidResolver</c> fallback.
    /// </remarks>
    internal static async Task<IResult> HandleGetSessionEventsAsync(string id, long? after, HttpContext context, InMemorySessionStore store, CancellationToken cancellationToken = default)
    {
        if (after < 0) return Results.Problem(statusCode: 400, title: "The session or event position is invalid.");
        if (UlidTryParse(id, out var value))
        {
            if (!TryIdentity(context, out var tenant, out var principal)) return IdentityError();
            var session = store.Get(value);
            if (session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != principal) return Results.Problem(statusCode: 404, title: "Session not found.");
            return Results.Ok(store.GetEvents(value, after ?? 0));
        }

        // UUID public identity path (contract EVENT-V1-PUBLIC): the route UUID is the
        // genuine public session id. It resolves to a runtime ULID for storage lookup
        // only and is never derived from a ULID. Unmapped UUIDs answer 404 so session
        // existence stays concealed, exactly like the legacy ownership check.
        if (!Guid.TryParseExact(id, "D", out var sessionUuid) || sessionUuid == Guid.Empty)
            return Results.Problem(statusCode: 400, title: "The session or event position is invalid.");
        if (!TryIdentity(context, out var uuidTenant, out var uuidPrincipal)) return IdentityError();
        var tenantId = new TenantId(uuidTenant);
        var principalId = new PrincipalId(uuidPrincipal);

        Ulid? mapped = null;
        var durable = context.RequestServices.GetService<IDurableSessionUuidResolver>();
        if (durable is not null)
        {
            var resolved = await durable.ResolveAsync(sessionUuid, tenantId, principalId, cancellationToken).ConfigureAwait(false);
            if (resolved is not null) mapped = resolved.Value.Value;
        }

        if (mapped is null)
        {
            var sync = context.RequestServices.GetService<ISessionUuidResolver>();
            if (sync is null || !sync.TryResolve(sessionUuid, out var fallback))
                return Results.Problem(statusCode: 404, title: "Session not found.");
            mapped = fallback;
        }

        var mappedSession = store.Get(mapped.Value);
        if (mappedSession is null || mappedSession.TenantId.Value != uuidTenant || mappedSession.PrincipalId.Value != uuidPrincipal)
            return Results.Problem(statusCode: 404, title: "Session not found.");

        var records = store.GetEvents(mapped.Value, after ?? 0);
        var canonical = new List<CanonicalAgentEvent>(records.Count);
        foreach (var record in records)
        {
            // Records without genuine stored event UUID/time cannot be projected;
            // answer 422 rather than fabricate metadata (contract EVENT-V1-PUBLIC).
            if (!CanonicalEventEndpointAdapter.TryToCanonical(record, sessionUuid, out var envelope) || envelope is null)
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "A stored event lacks genuine canonical identity and cannot be projected.",
                    type: "https://openconnect.dev/problems/legacy-event-without-canonical-identity");
            canonical.Add(envelope);
        }

        return Results.Json(canonical, CanonicalJsonOptions);
    }

    internal static async Task<IResult> HandleGetSessionEventsFromRequestAsync(string id, HttpContext context, InMemorySessionStore store, CancellationToken cancellationToken = default)
    {
        var query = context.Request.Query;
        var hasAfterSequence = query.ContainsKey("afterSequence");
        var hasAfter = query.ContainsKey("after");
        if (hasAfterSequence && hasAfter ||
            !TryReadCursor(query, hasAfterSequence ? "afterSequence" : "after", out var cursor))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The event position is invalid.", type: "https://openconnect.dev/problems/invalid_request");

        if (context.WebSockets.IsWebSocketRequest)
        {
            await Streaming.SessionWebSocketHandler.HandleAsync(context, id, cursor, store, cancellationToken).ConfigureAwait(false);
            return Results.Empty;
        }

        return await HandleGetSessionEventsAsync(id, cursor, context, store, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadCursor(IQueryCollection query, string name, out long? cursor)
    {
        cursor = 0;
        if (!query.ContainsKey(name)) return true;

        var values = query[name];
        if (values.Count != 1 || !long.TryParse(values[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;

        cursor = parsed;
        return parsed >= 0;
    }

    // Canonical public envelope serialization (contract EVENT-V1-PUBLIC only):
    // emits occurredAt as Zulu UTC ("...Z"), preserving the genuine instant.
    // Legacy EventRecord responses above intentionally keep Results.Ok untouched.
    private static readonly JsonSerializerOptions CanonicalJsonOptions = CreateCanonicalJsonOptions();

    private static JsonSerializerOptions CreateCanonicalJsonOptions()
    {
        var options = OpenConnectHttpJson.CreateOptions();
        options.Converters.Add(new ZuluDateTimeOffsetConverter());
        return options;
    }

    private sealed class ZuluDateTimeOffsetConverter : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            var text = value.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (text.EndsWith("Z", StringComparison.Ordinal) && text.Contains('.'))
                text = text.TrimEnd('Z').TrimEnd('0').TrimEnd('.') + "Z";
            writer.WriteStringValue(text);
        }
    }

    private static bool TryIdentity(HttpContext context, out Ulid tenant, out Ulid principal)
    {
        var tenantValid = UlidTryParse(context.User.FindFirstValue("tenant_id"), out tenant);
        var principalValid = UlidTryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out principal);
        return tenantValid && principalValid;
    }

    private static IResult IdentityError() => Results.Problem(statusCode: 403, title: "The authenticated identity is missing required tenant or subject claims.");

    private static IResult IdentifierJson(object value, int statusCode, string? location = null) =>
        new IdentifierJsonResult(value, statusCode, location);

    internal static IResult HandleCapabilities(IServiceProvider services) => Results.Ok(services.GetService<ICapabilityRegistry>()?.GetReady(CapabilityKind.Model) ?? Array.Empty<CapabilityDescriptor>());

    internal static IResult HandleComponents(IServiceProvider services) => Results.Ok(services.GetService<IAgentDefinitionRegistry>()?.GetAll() ?? Array.Empty<AgentDefinition>());

    internal static IResult HandleReady(IServiceProvider services) => services.GetService<InMemoryTaskStore>() is not null && services.GetService<InMemorySessionStore>() is not null
        ? Results.Ok(new { status = "ready" }) : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service is not ready.");

    private static bool UlidTryParse(string? text, out Ulid value)
    {
        try
        {
            var parsed = Ulid.Parse(text ?? string.Empty);
            if (parsed == default || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            {
                value = default;
                return false;
            }

            value = parsed;
            return true;
        }
        catch (FormatException) { value = default; return false; }
    }

    private sealed class IdentifierJsonResult(object value, int statusCode, string? location) : IResult
    {
        private static readonly JsonSerializerOptions JsonOptions = OpenConnectHttpJson.CreateOptions();

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            if (location is not null)
                httpContext.Response.Headers["Location"] = location;
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, value, value.GetType(), JsonOptions, httpContext.RequestAborted);
        }
    }
}

/// <summary>Task submission body. Tenant and principal are always taken from authenticated claims.</summary>
public sealed record TaskRequest(string ClientTaskId, string? Message = null, string? SessionId = null);
public sealed record SessionRequest(string? Name = null);
public sealed record SessionRecord(SessionId Id, TenantId TenantId, PrincipalId PrincipalId, DateTimeOffset CreatedAt);

/// <summary>Process-local task/idempotency store, replaceable by durable persistence.</summary>
public sealed class InMemoryTaskStore : ITaskLifecycleStore
{
    private readonly ConcurrentDictionary<Ulid, TaskRecord> _byId = new();

    internal Task<TaskRecord?> GetAsync(Ulid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _byId.TryGetValue(id, out var task);
        return Task.FromResult(task);
    }

    internal TaskRecord? Find(Ulid id) => _byId.GetValueOrDefault(id);
    internal IReadOnlyCollection<TaskRecord> All() => _byId.Values.ToArray();
    internal void Put(TaskRecord record) => _byId[record.Id.Value] = record;
    internal bool TryRemove(Ulid id) => _byId.TryRemove(id, out _);

    /// <inheritdoc />
    public Task<TaskRecord?> TransitionAsync(TaskId taskId, TenantId tenantId, PrincipalId principalId, AgentRunState expectedState, AgentRunState newState, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_byId.TryGetValue(taskId.Value, out var current))
        {
            if (current.TenantId != tenantId || current.PrincipalId != principalId || current.State != expectedState)
                return Task.FromResult<TaskRecord?>(null);
            var updated = current with { State = newState };
            if (_byId.TryUpdate(taskId.Value, updated, current)) return Task.FromResult<TaskRecord?>(updated);
        }
        return Task.FromResult<TaskRecord?>(null);
    }

    internal Task<TaskRecord?> CancelAsync(Ulid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_byId.TryGetValue(id, out var current))
        {
            if (current.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled) return Task.FromResult<TaskRecord?>(current);
            var updated = current with { State = AgentRunState.Cancelled };
            if (_byId.TryUpdate(id, updated, current)) return Task.FromResult<TaskRecord?>(updated);
        }
        return Task.FromResult<TaskRecord?>(null);
    }

}

/// <summary>Process-local session and bounded event store.</summary>
public sealed class InMemorySessionStore
{
    private const int EventLimit = 1000;
    private readonly ConcurrentDictionary<Ulid, SessionRecord> _sessions = new();
    private readonly InMemoryEventJournal _journal;

    public InMemorySessionStore() : this(new InMemoryEventJournal()) { }

    public InMemorySessionStore(InMemoryEventJournal journal) => _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    public SessionRecord Create(TenantId tenant, PrincipalId principal)
    {
        var session = new SessionRecord(new SessionId(Ulid.NewUlid()), tenant, principal, DateTimeOffset.UtcNow);
        _sessions[session.Id.Value] = session;
        return session;
    }
    public SessionRecord? Get(Ulid id) => _sessions.GetValueOrDefault(id);
    internal int Count => _sessions.Count;
    internal bool Remove(Ulid id) => _sessions.TryRemove(id, out _);
    public IReadOnlyList<EventRecord> GetEvents(Ulid id, long afterSequence) => _journal.GetEvents(new SessionId(id), afterSequence);

    internal long AppendEvent(Ulid id, string type, string data)
    {
        if (!_sessions.ContainsKey(id)) throw new KeyNotFoundException("Session not found.");
        return _journal.Append(new EventRecord(new SessionId(id), 0, type, data));
    }

    public Task<long> AppendEventAsync(Ulid id, string type, string data, CancellationToken cancellationToken = default)
    {
        if (!_sessions.ContainsKey(id)) throw new KeyNotFoundException("Session not found.");
        return _journal.AppendAsync(new EventRecord(new SessionId(id), 0, type, data), cancellationToken);
    }
}
