using System.Globalization;
using System.Net.Sockets;
using DN42Atlas.Policy;
using Microsoft.Data.Sqlite;

namespace DN42Atlas.OptOut.Exclusions;

public sealed class ExclusionStore(string databasePath)
{
    private const int SchemaVersion = 1;

    private readonly string databasePath = Path.GetFullPath(databasePath);

    public static async Task InitializeAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
            throw new InvalidOperationException(
                "The exclusion database already exists and is not empty.");

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            // Create the empty database privately before SQLite opens it (including on Unix).
            using (DN42Atlas.IO.PrivateFile.CreateNew(temporaryPath)) { }
            await using (var connection = CreateConnection(
                temporaryPath,
                SqliteOpenMode.ReadWriteCreate))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $$"""
                    CREATE TABLE Exclusions (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ResourceType TEXT NOT NULL CHECK (
                            ResourceType IN ('Domain', 'IPv4Prefix', 'IPv6Prefix')),
                        ResourceValue TEXT NOT NULL,
                        Subject TEXT NOT NULL,
                        Maintainer TEXT NOT NULL,
                        Asn INTEGER NOT NULL CHECK (Asn > 0 AND Asn <= 4294967295),
                        RegistryCommitSha TEXT NOT NULL,
                        RegistryObservedAtUtc TEXT NOT NULL,
                        CreatedUtc TEXT NOT NULL,
                        RevokedUtc TEXT NULL
                    );

                    CREATE UNIQUE INDEX UX_Exclusions_ActiveResource
                    ON Exclusions (ResourceType, ResourceValue)
                    WHERE RevokedUtc IS NULL;

                    PRAGMA user_version = {{SchemaVersion}};
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
                throw new InvalidOperationException(
                    "The exclusion database appeared during initialization.");

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void ValidateExisting()
    {
        using var connection = OpenExisting();
    }

    public async Task<ExclusionRecord> AddAsync(
        NewExclusionRecord input,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(input);

        await using var connection = OpenExisting();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Exclusions (
                ResourceType,
                ResourceValue,
                Subject,
                Maintainer,
                Asn,
                RegistryCommitSha,
                RegistryObservedAtUtc,
                CreatedUtc,
                RevokedUtc)
            VALUES (
                $resourceType,
                $resourceValue,
                $subject,
                $maintainer,
                $asn,
                $registryCommitSha,
                $registryObservedAtUtc,
                $createdUtc,
                NULL);

            SELECT last_insert_rowid();
            """;
        AddParameters(command, normalized);

        try
        {
            var id = (long)(await command.ExecuteScalarAsync(
                cancellationToken))!;
            return ToRecord(id, normalized, revokedUtc: null);
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 19)
        {
            var existing = await FindActiveAsync(
                connection,
                normalized.ResourceType,
                normalized.ResourceValue,
                cancellationToken);

            if (existing is not null)
                return existing;

            throw;
        }
    }

    public async Task<IReadOnlyList<ExclusionRecord>> GetActiveAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenExisting();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id,
                ResourceType,
                ResourceValue,
                Subject,
                Maintainer,
                Asn,
                RegistryCommitSha,
                RegistryObservedAtUtc,
                CreatedUtc,
                RevokedUtc
            FROM Exclusions
            WHERE RevokedUtc IS NULL
            ORDER BY ResourceType, ResourceValue, Id;
            """;

        return await ReadRecordsAsync(command, cancellationToken);
    }

    public async Task<ExclusionRecord?> GetLatestAsync(ExclusionResourceType resourceType, string resourceValue,
        CancellationToken cancellationToken = default)
    {
        var normalized = resourceType switch
        {
            ExclusionResourceType.Domain => ExclusionResourceNormalizer.NormalizeDomain(resourceValue),
            ExclusionResourceType.IPv4Prefix => ExclusionResourceNormalizer.NormalizePrefix(resourceValue, AddressFamily.InterNetwork),
            ExclusionResourceType.IPv6Prefix => ExclusionResourceNormalizer.NormalizePrefix(resourceValue, AddressFamily.InterNetworkV6),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType))
        };
        await using var connection = OpenExisting();
        await using var command = connection.CreateCommand();
        // AUTOINCREMENT IDs provide a monotonic generation, independent of timestamps or revocation.
        command.CommandText = """
            SELECT Id, ResourceType, ResourceValue, Subject, Maintainer, Asn,
                RegistryCommitSha, RegistryObservedAtUtc, CreatedUtc, RevokedUtc
            FROM Exclusions
            WHERE ResourceType = $resourceType AND ResourceValue = $resourceValue
            ORDER BY Id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$resourceType", resourceType.ToString());
        command.Parameters.AddWithValue("$resourceValue", normalized);
        return (await ReadRecordsAsync(command, cancellationToken)).SingleOrDefault();
    }

    public async Task<ExclusionRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenExisting();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id,
                ResourceType,
                ResourceValue,
                Subject,
                Maintainer,
                Asn,
                RegistryCommitSha,
                RegistryObservedAtUtc,
                CreatedUtc,
                RevokedUtc
            FROM Exclusions
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        return (await ReadRecordsAsync(command, cancellationToken))
            .SingleOrDefault();
    }

    public async Task<bool> RevokeAsync(
        long id,
        DateTimeOffset revokedUtc,
        CancellationToken cancellationToken = default)
    {
        if (revokedUtc == default)
            throw new ArgumentOutOfRangeException(nameof(revokedUtc));

        await using var connection = OpenExisting();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Exclusions
            SET RevokedUtc = $revokedUtc
            WHERE Id = $id AND RevokedUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue(
            "$revokedUtc",
            FormatTimestamp(revokedUtc));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // Recovery is limited to the revocation timestamp written by this operation.
    public async Task<bool> ReactivateAsync(long id, DateTimeOffset expectedRevokedUtc,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevokedUtc == default)
            throw new ArgumentOutOfRangeException(nameof(expectedRevokedUtc));
        await using var connection = OpenExisting();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Exclusions SET RevokedUtc = NULL WHERE Id = $id AND RevokedUtc = $expected;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$expected", FormatTimestamp(expectedRevokedUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private SqliteConnection OpenExisting()
    {
        if (!File.Exists(databasePath) ||
            new FileInfo(databasePath).Length == 0)
        {
            throw new FileNotFoundException(
                "The initialized exclusion database was not found.",
                databasePath);
        }

        var connection = CreateConnection(
            databasePath,
            SqliteOpenMode.ReadWrite);

        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";

            if (Convert.ToInt32(
                    version.ExecuteScalar(),
                    CultureInfo.InvariantCulture) != SchemaVersion)
            {
                throw new InvalidDataException(
                    "The exclusion database schema version is unsupported.");
            }

            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT 1 FROM Exclusions LIMIT 1;";
            schema.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection CreateConnection(
        string path,
        SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());

    private static NewExclusionRecord Normalize(NewExclusionRecord input)
    {
        var resourceValue = input.ResourceType switch
        {
            ExclusionResourceType.Domain =>
                ExclusionResourceNormalizer.NormalizeDomain(
                    input.ResourceValue),
            ExclusionResourceType.IPv4Prefix =>
                ExclusionResourceNormalizer.NormalizePrefix(
                    input.ResourceValue,
                    AddressFamily.InterNetwork),
            ExclusionResourceType.IPv6Prefix =>
                ExclusionResourceNormalizer.NormalizePrefix(
                    input.ResourceValue,
                    AddressFamily.InterNetworkV6),
            _ => throw new ArgumentOutOfRangeException(
                nameof(input.ResourceType))
        };

        if (string.IsNullOrWhiteSpace(input.Subject) ||
            string.IsNullOrWhiteSpace(input.Maintainer) ||
            input.Asn == 0 ||
            string.IsNullOrWhiteSpace(input.RegistryCommitSha) ||
            input.RegistryObservedAtUtc == default ||
            input.CreatedUtc == default)
        {
            throw new ArgumentException(
                "Complete exclusion audit evidence is required.",
                nameof(input));
        }

        return input with
        {
            ResourceValue = resourceValue,
            Subject = input.Subject.Trim(),
            Maintainer = input.Maintainer.Trim(),
            RegistryCommitSha = input.RegistryCommitSha.Trim(),
            RegistryObservedAtUtc =
                input.RegistryObservedAtUtc.ToUniversalTime(),
            CreatedUtc = input.CreatedUtc.ToUniversalTime()
        };
    }

    private static void AddParameters(
        SqliteCommand command,
        NewExclusionRecord input)
    {
        command.Parameters.AddWithValue(
            "$resourceType",
            input.ResourceType.ToString());
        command.Parameters.AddWithValue(
            "$resourceValue",
            input.ResourceValue);
        command.Parameters.AddWithValue("$subject", input.Subject);
        command.Parameters.AddWithValue("$maintainer", input.Maintainer);
        command.Parameters.AddWithValue("$asn", (long)input.Asn);
        command.Parameters.AddWithValue(
            "$registryCommitSha",
            input.RegistryCommitSha);
        command.Parameters.AddWithValue(
            "$registryObservedAtUtc",
            FormatTimestamp(input.RegistryObservedAtUtc));
        command.Parameters.AddWithValue(
            "$createdUtc",
            FormatTimestamp(input.CreatedUtc));
    }

    private static async Task<ExclusionRecord?> FindActiveAsync(
        SqliteConnection connection,
        ExclusionResourceType resourceType,
        string resourceValue,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                Id,
                ResourceType,
                ResourceValue,
                Subject,
                Maintainer,
                Asn,
                RegistryCommitSha,
                RegistryObservedAtUtc,
                CreatedUtc,
                RevokedUtc
            FROM Exclusions
            WHERE
                ResourceType = $resourceType AND
                ResourceValue = $resourceValue AND
                RevokedUtc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "$resourceType",
            resourceType.ToString());
        command.Parameters.AddWithValue("$resourceValue", resourceValue);

        return (await ReadRecordsAsync(command, cancellationToken))
            .SingleOrDefault();
    }

    private static async Task<IReadOnlyList<ExclusionRecord>> ReadRecordsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<ExclusionRecord>();
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new ExclusionRecord(
                reader.GetInt64(0),
                Enum.Parse<ExclusionResourceType>(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                checked((uint)reader.GetInt64(5)),
                reader.GetString(6),
                ParseTimestamp(reader.GetString(7)),
                ParseTimestamp(reader.GetString(8)),
                reader.IsDBNull(9)
                    ? null
                    : ParseTimestamp(reader.GetString(9))));
        }

        return records;
    }

    private static ExclusionRecord ToRecord(
        long id,
        NewExclusionRecord input,
        DateTimeOffset? revokedUtc) =>
        new(
            id,
            input.ResourceType,
            input.ResourceValue,
            input.Subject,
            input.Maintainer,
            input.Asn,
            input.RegistryCommitSha,
            input.RegistryObservedAtUtc,
            input.CreatedUtc,
            revokedUtc);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "O",
            CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
