using System.Globalization;
using DN42Atlas.IO;
using DN42Atlas.Publishing;
using Microsoft.Data.Sqlite;

namespace DN42Atlas.OptOut.ManualRequests;

public enum ManualRequestType { OptOut, BroaderOrWildcard, OwnershipOrAuthentication, Correction, Other }
public enum ManualRequestStatus { Pending, Reviewed, Resolved, Rejected }
public sealed record ManualRequest(long Id, DateTimeOffset CreatedUtc, string Resource, string Contact,
    ManualRequestType RequestType, string Message, string Status, DateTimeOffset? ReviewedUtc);
public sealed record RequestInput(string Resource, string Contact, ManualRequestType RequestType, string Message);

public sealed class ManualRequestStore(string path)
{
    public static string ConfiguredPath(IConfiguration configuration)
    {
        var path = configuration["DN42ATLAS_MANUAL_REQUEST_DB_PATH"];
        var published = configuration["DN42ATLAS_PUBLISHED_PATH"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            string.IsNullOrWhiteSpace(published) || !Path.IsPathFullyQualified(published))
            throw new InvalidOperationException("Manual request DB and published paths must be explicit absolute paths.");
        PublicationState.EnsureOutsidePublicRoot(path, published);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (configuration["DN42ATLAS_RUNTIME_EXCLUSIONS_PATH"] is string runtime &&
            Path.GetFullPath(runtime + ".reconciliation-pending").Equals(Path.GetFullPath(path), comparison))
            throw new InvalidOperationException("Manual request storage must not use the reconciliation fence path.");
        foreach (var key in new[] { "DN42ATLAS_EXCLUSION_DB_PATH", "DN42ATLAS_RUNTIME_EXCLUSIONS_PATH",
            "DN42ATLAS_PUBLICATION_STATE_PATH", "DN42ATLAS_EXCLUDED_HOSTS_PATH", "DN42ATLAS_EXCLUDED_PREFIXES_PATH" })
            if (configuration[key] is string other && Path.GetFullPath(other).Equals(Path.GetFullPath(path), comparison))
                throw new InvalidOperationException("Manual request storage must be separate from exclusion and publication files.");
        return path;
    }

    public static async Task InitializeAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) throw new InvalidOperationException("Manual request database already exists.");
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (PrivateFile.CreateNew(temp)) { }
            await using (var connection = Connect(temp))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ManualRequests (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        CreatedUtc TEXT NOT NULL,
                        Resource TEXT NOT NULL,
                        Contact TEXT NOT NULL,
                        RequestType TEXT NOT NULL CHECK(RequestType IN ('OptOut','BroaderOrWildcard','OwnershipOrAuthentication','Correction','Other')),
                        Message TEXT NOT NULL,
                        Status TEXT NOT NULL CHECK(Status IN ('Pending','Reviewed','Resolved','Rejected')),
                        ReviewedUtc TEXT NULL);
                    CREATE INDEX IX_ManualRequests_Status_Id ON ManualRequests(Status, Id);
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            File.Move(temp, path);
        }
        finally { File.Delete(temp); }
    }

    private static SqliteConnection Connect(string file) => new(new SqliteConnectionStringBuilder
        { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());

    private SqliteConnection Open()
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Initialized manual request database is required.");
        var connection = Connect(path);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Unsupported manual request database schema.");
            command.CommandText = "SELECT Id, CreatedUtc, Resource, Contact, RequestType, Message, Status, ReviewedUtc FROM ManualRequests LIMIT 0;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public void ValidateExisting() { using var connection = Open(); }

    public async Task<ManualRequest> AddAsync(RequestInput input, CancellationToken token = default)
    {
        if (!ContactValidation.IsValid(input)) throw new ArgumentException("Invalid manual request.");
        var created = DateTimeOffset.UtcNow;
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ManualRequests(CreatedUtc,Resource,Contact,RequestType,Message,Status)
            VALUES($created,$resource,$contact,$type,$message,'Pending'); SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$created", created.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$resource", input.Resource);
        command.Parameters.AddWithValue("$contact", input.Contact);
        command.Parameters.AddWithValue("$type", input.RequestType.ToString());
        command.Parameters.AddWithValue("$message", input.Message);
        var id = (long)(await command.ExecuteScalarAsync(token))!;
        return new(id, created, input.Resource, input.Contact, input.RequestType, input.Message, "Pending", null);
    }

    public async Task<bool> SetStatusAsync(long id, ManualRequestStatus status)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ManualRequests
            SET Status = $status,
                ReviewedUtc = CASE WHEN $status = 'Pending' THEN NULL
                                   WHEN Status = 'Pending' THEN $now
                                   ELSE ReviewedUtc END
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ManualRequests WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync() == 1;
    }

    public async Task<IReadOnlyList<ManualRequest>> ReadAsync(long? id = null)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,CreatedUtc,Resource,Contact,RequestType,Message,Status,ReviewedUtc FROM ManualRequests WHERE " +
            (id == null ? "Status = 'Pending' ORDER BY Id;" : "Id = $id;");
        if (id != null) command.Parameters.AddWithValue("$id", id.Value);
        var result = new List<ManualRequest>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2), reader.GetString(3), Enum.Parse<ManualRequestType>(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)));
        return result;
    }
}
