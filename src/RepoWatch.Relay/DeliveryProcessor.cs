using System.Text.Json;

namespace RepoWatch.Relay;

/// <summary>
/// Processes stored deliveries in order: maps each to invalidations and access changes and applies them to
/// the hub. A failed attempt is retried with exponential backoff (at most <see cref="RelayServerOptions.MaxDeliveryAttempts"/>);
/// a malformed payload fails at once, since retrying can't fix it.
/// </summary>
public sealed class DeliveryProcessor(DeliveryStore store, RelayHub hub, RelayServerOptions options, TimeProvider time, ILogger<DeliveryProcessor> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>Test hook: runs before applying a delivery (e.g. to simulate a failure).</summary>
    internal Action<StoredDelivery>? BeforeApply { get; set; }

    /// <summary>A new delivery was stored.</summary>
    public void Signal() => _signal.Release();

    /// <summary>Processes everything due now. Public for tests.</summary>
    public void ProcessDue()
    {
        foreach (var delivery in store.Due(100))
        {
            try
            {
                BeforeApply?.Invoke(delivery);
                Apply(EventMapper.Map(delivery.Event, delivery.Payload));
                store.Complete(delivery.Id);
            }
            catch (JsonException)
            {
                store.Fail(delivery.Id, "malformed payload", TimeSpan.Zero, final: true);
                logger.LogWarning("Delivery {DeliveryId} ({Event}) has a malformed payload and was dropped", delivery.DeliveryId, delivery.Event);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var final = delivery.Attempts + 1 >= options.MaxDeliveryAttempts;
                var retry = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, delivery.Attempts + 1)));
                store.Fail(delivery.Id, ex.GetType().Name, retry, final);
                logger.LogWarning("Processing delivery {DeliveryId} ({Event}) failed ({Error}); {Next}", delivery.DeliveryId, delivery.Event, ex.GetType().Name,
                    final ? "giving up" : $"retrying in {retry.TotalSeconds:0} s");
            }
        }

        if (time.GetUtcNow() - _lastPrune > TimeSpan.FromHours(1))
        {
            _lastPrune = time.GetUtcNow();
            store.Prune(TimeSpan.FromDays(options.DeliveryRetentionDays));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ProcessDue();
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Apply(MappedDelivery mapped)
    {
        foreach (var invalidation in mapped.Invalidations)
        {
            hub.Publish(invalidation);
        }

        if (mapped.RemovedRepositoryIds.Count > 0)
        {
            hub.RemoveRepositories(mapped.RemovedRepositoryIds);
        }

        if (mapped.RevokedInstallationId is { } installation)
        {
            hub.RevokeInstallation(installation);
        }

        if (mapped.RevokedUserId is { } user)
        {
            hub.RevokeUser(user);
        }
    }
}
