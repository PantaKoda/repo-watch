using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoWatch.Core.Identity;

/// <summary>
/// Stable identity of a signed-in account: the GitHub host plus the numeric user ID.
/// Logins can be renamed, so they are display data only and never part of a key.
/// </summary>
[JsonConverter(typeof(AccountKeyJsonConverter))]
public sealed record AccountKey
{
    /// <param name="host">A bare host name with optional port, e.g. "github.com". URLs are rejected so one account cannot get two keys.</param>
    public AccountKey(string host, long userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var hostName = host.Split(':', 2)[0];
        if (host.Contains("://", StringComparison.Ordinal) || host.Contains('/') || host.Any(char.IsWhiteSpace)
            || Uri.CheckHostName(hostName) == UriHostNameType.Unknown)
        {
            throw new ArgumentException($"'{host}' is not a bare host name. Use ForWebBase to derive the host from a URL.", nameof(host));
        }

        Host = host.ToLowerInvariant();
        UserId = userId;
    }

    /// <summary>Normalized host name with optional port, e.g. "github.com".</summary>
    public string Host { get; }

    public long UserId { get; }

    /// <summary>Key used to isolate per-account settings and cached data.</summary>
    public string StorageKey => $"{Host}/{UserId}";

    public static AccountKey ForWebBase(Uri webBase, long userId)
    {
        ArgumentNullException.ThrowIfNull(webBase);
        return new AccountKey(webBase.IsDefaultPort ? webBase.Host : $"{webBase.Host}:{webBase.Port}", userId);
    }

    public override string ToString() => StorageKey;
}

/// <summary>Reports invalid stored keys as <see cref="JsonException"/> so settings loading can drop just that value.</summary>
public sealed class AccountKeyJsonConverter : JsonConverter<AccountKey>
{
    public override AccountKey? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("An account key must be an object.");
        }

        string? host = null;
        long userId = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "host" when reader.TokenType == JsonTokenType.String:
                    host = reader.GetString();
                    break;
                case "userId" when reader.TokenType == JsonTokenType.Number:
                    userId = reader.GetInt64();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        try
        {
            return new AccountKey(host!, userId);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException($"Invalid account key: {ex.Message}", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, AccountKey value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("host", value.Host);
        writer.WriteNumber("userId", value.UserId);
        writer.WriteEndObject();
    }
}
