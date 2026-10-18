using Microsoft.Data.Sqlite;
using DiagnoX.Models;

namespace DiagnoX.Services;

public sealed class AnomalyDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public AnomalyDatabase()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiagnoX");
        Directory.CreateDirectory(folder);
        _connection = new SqliteConnection($"Data Source={Path.Combine(folder, "anomalies.db")}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Anomalies (
              Id INTEGER PRIMARY KEY, TimestampUtc TEXT NOT NULL, Severity INTEGER NOT NULL,
              Category TEXT NOT NULL, Title TEXT NOT NULL, Source TEXT NOT NULL, EventId INTEGER NOT NULL,
              Details TEXT NOT NULL, RawMessage TEXT NOT NULL, Fingerprint TEXT NOT NULL UNIQUE);
            CREATE INDEX IF NOT EXISTS IX_Anomalies_TimestampUtc ON Anomalies(TimestampUtc);
            """;
        command.ExecuteNonQuery();
    }

    public bool TryInsert(AnomalyRecord record)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO Anomalies
            (TimestampUtc, Severity, Category, Title, Source, EventId, Details, RawMessage, Fingerprint)
            VALUES ($time, $severity, $category, $title, $source, $eventId, $details, $raw, $fingerprint)
            """;
        command.Parameters.AddWithValue("$time", record.TimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$severity", (int)record.Severity);
        command.Parameters.AddWithValue("$category", record.Category);
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$eventId", record.EventId);
        command.Parameters.AddWithValue("$details", record.Details);
        command.Parameters.AddWithValue("$raw", record.RawMessage);
        command.Parameters.AddWithValue("$fingerprint", record.Fingerprint);
        return command.ExecuteNonQuery() > 0;
    }

    public List<AnomalyRecord> GetRecent(TimeSpan window)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Id, TimestampUtc, Severity, Category, Title, Source, EventId, Details, RawMessage, Fingerprint FROM Anomalies WHERE TimestampUtc >= $cutoff ORDER BY TimestampUtc DESC LIMIT 1000";
        command.Parameters.AddWithValue("$cutoff", (DateTime.UtcNow - window).ToString("O"));
        using var reader = command.ExecuteReader();
        var results = new List<AnomalyRecord>();
        while (reader.Read()) results.Add(new AnomalyRecord
        {
            Id = reader.GetInt64(0), TimestampUtc = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Severity = (AnomalySeverity)reader.GetInt32(2), Category = reader.GetString(3), Title = reader.GetString(4),
            Source = reader.GetString(5), EventId = reader.GetInt32(6), Details = reader.GetString(7), RawMessage = reader.GetString(8), Fingerprint = reader.GetString(9)
        });
        return results;
    }
    public void Dispose() => _connection.Dispose();
}
