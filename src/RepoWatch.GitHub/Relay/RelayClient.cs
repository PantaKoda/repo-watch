using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RepoWatch.Core.Relay;

namespace RepoWatch.GitHub.Relay;

/// <summary>One server-sent event from the relay.</summary>
public sealed record RelayMessage(long? Id, string Name, string Data);

/// <summary>
/// Client for the Repo Watch relay: creates a short-lived session with the user's GitHub token (sent only
/// in the Authorization header, over HTTPS) and reads the session's event stream. Outbound connections only.
/// The HttpClient must not have a request timeout, since the stream stays open; cancellation ends it.
/// </summary>
public sealed class RelayClient(HttpClient http, Uri baseUrl)
{
    public async Task<(RelaySessionResponse? Session, HttpStatusCode? Failure)> CreateSessionAsync(string userToken, IReadOnlyCollection<long> repositoryIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "sessions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new RelaySessionRequest(repositoryIds.ToList()), RelayJsonContext.Default.RelaySessionRequest), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode);
            }

            var session = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                RelayJsonContext.Default.RelaySessionResponse, timeout.Token).ConfigureAwait(false);
            return session is null ? (null, HttpStatusCode.BadGateway) : (session, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Streams events until the relay ends the stream or <paramref name="cancellationToken"/> fires.
    /// <paramref name="connected"/> is called once the relay accepted the stream. The relay sends keep-alives,
    /// so silence longer than <paramref name="idleTimeout"/> means a dead connection (sleep, network switch,
    /// NAT timeout) that TCP alone would not notice for hours: the stream then fails with <see cref="TimeoutException"/>.
    /// </summary>
    /// <exception cref="HttpRequestException">The relay couldn't be reached or refused the stream.</exception>
    /// <exception cref="TimeoutException">Nothing arrived for <paramref name="idleTimeout"/>.</exception>
    public async IAsyncEnumerable<RelayMessage> StreamAsync(string sessionToken, long? lastEventId, Action connected, TimeSpan idleTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connected);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "events"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId is { } id)
        {
            request.Headers.Add("Last-Event-ID", id.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The relay refused the event stream ({(int)response.StatusCode}).", null, response.StatusCode);
        }

        connected();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8);
        var parser = new SseParser();
        while (true)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                idle.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"The relay sent nothing for {idleTimeout.TotalSeconds:0} s.");
                }
            }

            if (line is null)
            {
                yield break;
            }

            if (parser.Feed(line) is { } message)
            {
                yield return message;
            }
        }
    }
}

/// <summary>Incremental server-sent events parser (the subset the relay uses: id, event, data, comments).</summary>
public sealed class SseParser
{
    private long? _id;
    private string? _event;
    private readonly StringBuilder _data = new();

    /// <summary>Feeds one line; returns an event when a blank line completes one.</summary>
    public RelayMessage? Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0)
        {
            var complete = _event is not null || _data.Length > 0 ? new RelayMessage(_id, _event ?? "message", _data.ToString()) : null;
            (_id, _event) = (null, null);
            _data.Clear();
            return complete;
        }

        if (line[0] == ':')
        {
            return null; // comment / keep-alive
        }

        var colon = line.IndexOf(':', StringComparison.Ordinal);
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
        switch (field)
        {
            case "id" when long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id):
                _id = id;
                break;
            case "event":
                _event = value;
                break;
            case "data":
                if (_data.Length > 0)
                {
                    _data.Append('\n');
                }

                _data.Append(value);
                break;
        }

        return null;
    }
}
