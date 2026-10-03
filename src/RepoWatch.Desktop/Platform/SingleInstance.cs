using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace RepoWatch.Desktop.Platform;

/// <summary>
/// One Repo Watch per user and data folder. A second launch asks the running one to show its widget
/// (over a named pipe, current user only) and exits. Different data folders (tests, demos) may run side by side.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowMessage = "show";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owned;

    public SingleInstance(string dataDirectory)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant())))[..16];
        _pipeName = $"RepoWatch-{Environment.UserName}-{id}";
        _mutex = new Mutex(initiallyOwned: true, $"Local\\RepoWatch-{id}", out _owned);
    }

    /// <summary>True for the first instance; false when another one already runs.</summary>
    public bool IsFirst => _owned;

    /// <summary>Raised (on a background thread) when another launch asked this instance to show itself.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Second instance: ask the first to show its widget. Returns false if it couldn't be reached.</summary>
    public bool SignalFirst(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(ShowMessage);
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>First instance: listen for activation requests until disposed.</summary>
    public void Listen() => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var buffer = new byte[16];
                var read = await server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(buffer, 0, read) == ShowMessage)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that disconnected early; keep listening.
            }
        }
    });

    public void Dispose()
    {
        _stop.Cancel();
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }

        _mutex.Dispose();
        _stop.Dispose();
    }
}
