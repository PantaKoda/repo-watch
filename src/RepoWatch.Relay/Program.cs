using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RepoWatch.Core.Relay;
using RepoWatch.Relay;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(RelayServerOptions.Section).Get<RelayServerOptions>() ?? new RelayServerOptions();
var errors = options.Validate();
if (errors.Count > 0)
{
    // Fail fast with an actionable message rather than accepting unverifiable webhooks.
    foreach (var error in errors)
    {
        Console.Error.WriteLine($"Configuration error: {error}");
    }

    return 1;
}

builder.Services.AddSingleton(options);
builder.Services.TryAddSingletonTimeProvider();
builder.Services.AddSingleton(sp => new DeliveryStore(options.DatabasePath, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<RelayHub>();
builder.Services.AddSingleton<DeliveryProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeliveryProcessor>());
builder.Services.AddHttpClient<GitHubAccessClient>(client =>
{
    client.BaseAddress = new Uri(options.GitHubApiBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

// GitHub webhooks: verify the signature on the raw bytes, store durably, then acknowledge (well within
// GitHub's 10-second limit). Processing happens in the background.
app.MapPost("/webhooks/github", async (HttpContext context, DeliveryStore store, DeliveryProcessor processor, ILogger<DeliveryStore> logger) =>
{
    using var buffer = new MemoryStream();
    await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
    var body = buffer.ToArray();
    if (!WebhookSignature.IsValid(options.WebhookSecret, body, context.Request.Headers[WebhookSignature.Header]))
    {
        logger.LogWarning("Rejected a webhook with a missing or invalid signature");
        return Results.Unauthorized();
    }

    var eventName = context.Request.Headers["X-GitHub-Event"].ToString();
    var deliveryId = context.Request.Headers["X-GitHub-Delivery"].ToString();
    if (eventName.Length is 0 or > 100 || deliveryId.Length is 0 or > 100)
    {
        return Results.BadRequest("X-GitHub-Event and X-GitHub-Delivery are required.");
    }

    if (!EventMapper.SubscribedEvents.Contains(eventName))
    {
        return Results.Accepted(); // e.g. ping: nothing to do
    }

    if (!store.TryAdd(deliveryId, eventName, body))
    {
        return Results.Ok("duplicate delivery"); // GitHub redelivery: already stored, processed once
    }

    processor.Signal();
    return Results.Accepted();
});

// Desktop clients create a short-lived session with their GitHub user token (Authorization header only);
// the relay asks GitHub which of the requested repositories this user and the app can access.
app.MapPost("/sessions", async (HttpContext context, RelayHub hub, GitHubAccessClient github, ILogger<RelayHub> logger) =>
{
    if (Bearer(context) is not { } userToken)
    {
        return Results.Unauthorized();
    }

    RelaySessionRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync(context.Request.Body, RelayJsonContext.Default.RelaySessionRequest, context.RequestAborted).ConfigureAwait(false);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Expected {\"repositoryIds\": [...]}.");
    }

    if (request?.RepositoryIds is not { Count: > 0 and <= 500 } requested)
    {
        return Results.BadRequest("Between 1 and 500 repository IDs are required.");
    }

    var access = await github.CheckAsync(userToken, context.RequestAborted).ConfigureAwait(false);
    if (access.Failure is { } failure)
    {
        return failure == HttpStatusCode.Unauthorized ? Results.Unauthorized() : Results.StatusCode(StatusCodes.Status502BadGateway);
    }

    var allowed = requested.Distinct().Where(access.Repositories.ContainsKey).ToDictionary(id => id, id => access.Repositories[id]);
    var (token, session) = hub.CreateSession(access.UserId, allowed);
    var rejected = requested.Distinct().Where(id => !allowed.ContainsKey(id)).ToList();
    logger.LogInformation("Session for user {UserId}: {Allowed} repositories allowed, {Rejected} rejected", access.UserId, allowed.Count, rejected.Count);
    return Results.Json(new RelaySessionResponse(token, session.ExpiresAt, allowed.Keys.Order().ToList(), rejected), RelayJsonContext.Default.RelaySessionResponse);
});

// Server-sent events for one session: replay after Last-Event-ID (or "reset" after a gap), then live events.
app.MapGet("/events", async (HttpContext context, RelayHub hub, TimeProvider time) =>
{
    if (Bearer(context) is not { } token || hub.Find(token) is not { } session)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var connection = hub.Connect(session);
    try
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await Write(context, "retry: 5000\n: connected\n\n").ConfigureAwait(false);

        var lastSent = hub.LastSeq;
        if (long.TryParse(context.Request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var lastEventId))
        {
            var (gap, events) = hub.Replay(lastEventId, session);
            if (gap)
            {
                await Write(context, Event(0, RelayProtocol.Reset, "{}")).ConfigureAwait(false);
            }
            else
            {
                foreach (var record in events)
                {
                    await Write(context, Event(record.Seq, record.Name, record.Data)).ConfigureAwait(false);
                }
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, connection.Ended.Token);
        var reader = connection.Outbox.Reader;
        while (!stop.IsCancellationRequested)
        {
            var untilExpiry = session.ExpiresAt - time.GetUtcNow();
            if (untilExpiry <= TimeSpan.Zero)
            {
                await Write(context, Event(0, RelayProtocol.Expired, "{}")).ConfigureAwait(false);
                return;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(Math.Min(options.KeepAliveSeconds, Math.Max(1, untilExpiry.TotalSeconds))));
            try
            {
                await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                await Write(context, ": keep-alive\n\n").ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (reader.TryRead(out var record))
            {
                if (record.Seq != 0 && record.Seq <= lastSent)
                {
                    continue; // already sent during replay
                }

                await Write(context, Event(record.Seq, record.Name, record.Data)).ConfigureAwait(false);
                lastSent = Math.Max(lastSent, record.Seq);
            }

            if (connection.Overflowed)
            {
                connection.Overflowed = false;
                await Write(context, Event(0, RelayProtocol.Reset, "{}")).ConfigureAwait(false);
            }
        }

        if (connection.EndEvent is { } reason && !context.RequestAborted.IsCancellationRequested)
        {
            // Revoked: drain what was queued first (per-repository revocations), then end.
            while (connection.Outbox.Reader.TryRead(out var record))
            {
                await Write(context, Event(record.Seq, record.Name, record.Data)).ConfigureAwait(false);
            }

            await Write(context, Event(0, reason, "{}")).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // Client went away.
    }
    finally
    {
        hub.Disconnect(session, connection);
    }
});

app.Run();
return 0;

static string? Bearer(HttpContext context)
{
    // Tokens are accepted only in the Authorization header, never from the URL.
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.Ordinal) && header.Length > 7 ? header[7..].Trim() : null;
}

static string Event(long seq, string name, string data) =>
    (seq > 0 ? string.Create(CultureInfo.InvariantCulture, $"id: {seq}\n") : "") + $"event: {name}\ndata: {data}\n\n";

static async Task Write(HttpContext context, string text)
{
    await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), context.RequestAborted).ConfigureAwait(false);
    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
}

public partial class Program;

internal static class ServiceCollectionTimeExtensions
{
    /// <summary>Registers the system clock unless a test registered its own.</summary>
    public static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(s => s.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
