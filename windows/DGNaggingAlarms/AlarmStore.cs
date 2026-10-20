using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace DGNaggingAlarms;

public sealed class AlarmStore
{
    private readonly string _databasePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    public AlarmStore(string? databasePath = null)
    {
        var folder = databasePath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DGNaggingAlarms")
            : Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(folder);
        _databasePath = databasePath ?? Path.Combine(folder, "alarms.db");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS app_state (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    public AlarmState Load()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM app_state WHERE id = 1";
        var json = command.ExecuteScalar() as string;
        return json is null ? new AlarmState() : JsonSerializer.Deserialize<AlarmState>(json, _json) ?? new AlarmState();
    }

    public void Save(AlarmState state)
    {
        var json = JsonSerializer.Serialize(state, _json);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO app_state (id, json) VALUES (1, $json) ON CONFLICT(id) DO UPDATE SET json = excluded.json";
        command.Parameters.AddWithValue("$json", json);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
