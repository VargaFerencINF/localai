using Microsoft.Data.Sqlite;

namespace EfMailAgent.Jobs;

/// <summary>
/// SQLite database (data/queue.db) with a minimal migration mechanism: the schema_version table stores the number of
/// the last applied migration; migrations are applied in order inside a transaction.
/// </summary>
public sealed class JobDatabase
{
    public const string FileName = "queue.db";

    // Append-only: never edit an existing migration, add a new one instead.
    private static readonly string[] Migrations =
    [
        // 1: the jobs table from the Step 2 specification
        """
        CREATE TABLE jobs (
            id               TEXT PRIMARY KEY,
            message_id       TEXT NOT NULL UNIQUE,
            account          TEXT,
            folder           TEXT,
            received_at      TEXT,
            sender           TEXT,
            recipients       TEXT,
            subject          TEXT,
            body_text        TEXT,
            body_html        TEXT,
            attachment_names TEXT,
            source           TEXT NOT NULL,
            rule_id          TEXT NOT NULL,
            status           TEXT NOT NULL,
            attempts         INTEGER NOT NULL DEFAULT 0,
            next_attempt_at  TEXT,
            last_error       TEXT,
            raw_response     TEXT,
            output_file      TEXT,
            row_count        INTEGER,
            created_at       TEXT NOT NULL,
            started_at       TEXT,
            finished_at      TEXT
        );
        CREATE INDEX ix_jobs_status_created ON jobs (status, created_at);
        """,
        // 2: diagnostics for the admin UI (assembled prompt, repair answer, parsed rows, timing)
        """
        ALTER TABLE jobs ADD COLUMN original_sender TEXT;
        ALTER TABLE jobs ADD COLUMN prompt_system TEXT;
        ALTER TABLE jobs ADD COLUMN prompt_user TEXT;
        ALTER TABLE jobs ADD COLUMN repair_response TEXT;
        ALTER TABLE jobs ADD COLUMN parsed_rows TEXT;
        ALTER TABLE jobs ADD COLUMN duration_ms INTEGER;
        CREATE INDEX ix_jobs_created ON jobs (created_at);
        """,
        // 3: LLM usage per job (Teljesítmény page): tokens, generation speed, model
        """
        ALTER TABLE jobs ADD COLUMN prompt_tokens INTEGER;
        ALTER TABLE jobs ADD COLUMN output_tokens INTEGER;
        ALTER TABLE jobs ADD COLUMN tokens_per_sec REAL;
        ALTER TABLE jobs ADD COLUMN llm_model TEXT;
        """,
    ];

    private readonly string _connectionString;

    public JobDatabase(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        FilePath = Path.Combine(dataDir, FileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30, // also the busy timeout when another process (CLI) holds a lock
        }.ToString();
        Migrate();
    }

    public string FilePath { get; }

    public static int LatestVersion => Migrations.Length;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public int CurrentVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void Migrate()
    {
        using var connection = Open();
        Execute(connection, null, "PRAGMA journal_mode=WAL;");
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL, applied_at TEXT NOT NULL);");

        using var transaction = connection.BeginTransaction();
        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        var current = Convert.ToInt32(versionCommand.ExecuteScalar());

        if (current > Migrations.Length)
            throw new InvalidOperationException(
                $"Az adatbázis ({FilePath}) újabb verziójú ({current}), mint amit ez a program ismer ({Migrations.Length}). Frissítsd a programot.");

        for (var version = current + 1; version <= Migrations.Length; version++)
        {
            Execute(connection, transaction, Migrations[version - 1]);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES ($v, $at)";
            insert.Parameters.AddWithValue("$v", version);
            insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
