using Microsoft.Data.Sqlite;
using DeepGrab.Models;

namespace DeepGrab.Services;

public class DatabaseService
{
    readonly string _dbPath;

    public DatabaseService(string basePath)
    {
        _dbPath = Path.Combine(basePath, "downloads.db");
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS DownloadRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Url TEXT UNIQUE NOT NULL,
                Title TEXT NOT NULL, FilePath TEXT NOT NULL,
                FileSize INTEGER DEFAULT 0, DownloadedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void InsertRecord(string url, string title, string filePath, long fileSize = 0)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO DownloadRecords (Url,Title,FilePath,FileSize,DownloadedAt) VALUES (@u,@t,@p,@s,@d)";
        cmd.Parameters.AddWithValue("@u", url); cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@p", filePath); cmd.Parameters.AddWithValue("@s", fileSize);
        cmd.Parameters.AddWithValue("@d", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public DownloadRecord? GetByUrl(string url)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM DownloadRecords WHERE Url=@u LIMIT 1";
        cmd.Parameters.AddWithValue("@u", url);
        using var r = cmd.ExecuteReader();
        if (r.Read()) return new() { Id=r.GetInt64(0), Url=r.GetString(1), Title=r.GetString(2), FilePath=r.GetString(3), FileSize=r.GetInt64(4), DownloadedAt=DateTime.Parse(r.GetString(5)) };
        return null;
    }

    public List<DownloadRecord> GetAllRecords()
    {
        var list = new List<DownloadRecord>();
        using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM DownloadRecords ORDER BY DownloadedAt DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new() { Id=r.GetInt64(0), Url=r.GetString(1), Title=r.GetString(2), FilePath=r.GetString(3), FileSize=r.GetInt64(4), DownloadedAt=DateTime.Parse(r.GetString(5)) });
        return list;
    }

    public void DeleteRecord(string url)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM DownloadRecords WHERE Url=@u";
        cmd.Parameters.AddWithValue("@u", url);
        cmd.ExecuteNonQuery();
    }

    public string GetSetting(string key, string def = "")
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key=@k LIMIT 1";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar()?.ToString() ?? def;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Settings (Key,Value) VALUES (@k,@v)";
        cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }
}
