using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RepoWatch.Relay;

public sealed record StoredDelivery(long Id, string DeliveryId, string Event, byte[] Payload, int Attempts);

/// <summary>
/// Durable webhook deliveries. A delivery is written before GitHub gets its 2xx answer, so nothing is lost
/// if processing fails or the relay restarts; GitHub's delivery ID is unique, so redeliveries are ignored.
/// Processing failures are retried with backoff up to a limit.
/// </summary>
public sealed class DeliveryStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public DeliveryStore(string path, TimeProvider time)
    {
        _time = time;
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS deliveries (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                delivery_id     TEXT    NOT NULL UNIQUE,
                event           TEXT    NOT NULL,
                payload         BLOB    NOT NULL,
                received_at     TEXT    NOT NULL,
                status          TEXT    NOT NULL,
                attempts        INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT    NOT NULL,
                error           TEXT
            );
            CREATE INDEX IF NOT EXISTS deliveries_pending ON deliveries (status, next_attempt_at);
            """);
    }

    /// <returns>False if this delivery ID was received before (a redelivery).</returns>
    public bool TryAdd(string deliveryId, string eventName, byte[] payload)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO deliveries (delivery_id, event, payload, received_at, status, next_attempt_at)
                VALUES ($id, $event, $payload, $now, 'pending', $now);
                """;
            command.Parameters.AddWithValue("$id", deliveryId);
            command.Parameters.AddWithValue("$event", eventName);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$now", Now());
            return command.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Pending deliveries whose next attempt is due, oldest first.</summary>
    public IReadOnlyList<StoredDelivery> Due(int limit)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, delivery_id, event, payload, attempts FROM deliveries WHERE status = 'pending' AND next_attempt_at <= $now ORDER BY id LIMIT $limit;";
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var due = new List<StoredDelivery>();
            while (reader.Read())
            {
                due.Add(new StoredDelivery(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3], reader.GetInt32(4)));
            }

            return due;
        }
    }

    public void Complete(long id) => Update(id, "UPDATE deliveries SET status = 'done', attempts = attempts + 1, error = NULL WHERE id = $id;");

    /// <summary>Records a failed attempt; retried after <paramref name="retryAfter"/> unless <paramref name="final"/>.</summary>
    public void Fail(long id, string error, TimeSpan retryAfter, bool final)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE deliveries SET status = $status, attempts = attempts + 1, error = $error, next_attempt_at = $next WHERE id = $id;";
            command.Parameters.AddWithValue("$status", final ? "failed" : "pending");
            command.Parameters.AddWithValue("$error", error.Length > 500 ? error[..500] : error);
            command.Parameters.AddWithValue("$next", (_time.GetUtcNow() + retryAfter).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public (string Status, int Attempts)? Find(string deliveryId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, attempts FROM deliveries WHERE delivery_id = $id;";
            command.Parameters.AddWithValue("$id", deliveryId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetInt32(1)) : null;
        }
    }

    /// <summary>Deletes finished deliveries older than <paramref name="maxAge"/>.</summary>
    public void Prune(TimeSpan maxAge)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM deliveries WHERE status <> 'pending' AND received_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", (_time.GetUtcNow() - maxAge).ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private void Update(long id, string sql)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
