using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace RepoWatch.Desktop.Platform;

/// <summary>Downloads the signed-in user's avatar from GitHub's avatar host only. Failures just mean no picture.</summary>
public sealed class AvatarLoader(HttpClient http, ILogger<AvatarLoader> logger)
{
    private const long MaxBytes = 1024 * 1024;

    public async Task<Bitmap?> LoadAsync(Uri? url, CancellationToken cancellationToken)
    {
        if (url is null || url.Scheme != Uri.UriSchemeHttps
            || !(url.Host == "avatars.githubusercontent.com" || url.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            // Bounded read: a body without Content-Length (chunked) is never buffered past the cap.
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return Bitmap.DecodeToWidth(buffer, 96);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            logger.LogInformation("Avatar not loaded: {Reason}", ex.Message);
            return null;
        }
    }
}
