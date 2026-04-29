#pragma warning disable CA1416
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrowserTesting.Desktop.Models;
using Microsoft.Data.Sqlite;

namespace BrowserTesting.Desktop.Classes;

public sealed class SqliteChatRepository(AppSettings settings)
{
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim databaseGate = new(1, 1);
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = settings.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath) ?? AppContext.BaseDirectory);
        return UseConnectionAsync(async (connection, token) =>
        {
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS chats (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS runs (
                    id TEXT PRIMARY KEY,
                    chat_id TEXT NOT NULL,
                    user_prompt TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    failure_reason TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT NULL,
                    browser_snapshot_json TEXT NOT NULL,
                    FOREIGN KEY(chat_id) REFERENCES chats(id)
                );

                CREATE TABLE IF NOT EXISTS goals (
                    id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    success_criteria TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    note TEXT NULL,
                    evidence TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    completed_at_utc TEXT NULL,
                    FOREIGN KEY(run_id) REFERENCES runs(id)
                );

                CREATE TABLE IF NOT EXISTS timeline_entries (
                    id TEXT PRIMARY KEY,
                    chat_id TEXT NOT NULL,
                    run_id TEXT NULL,
                    sequence_no INTEGER NOT NULL,
                    kind INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    tool_call_id TEXT NULL,
                    tool_name TEXT NULL,
                    metadata_json TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    FOREIGN KEY(chat_id) REFERENCES chats(id)
                );

                CREATE TABLE IF NOT EXISTS secrets (
                    chat_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    encrypted_value TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY(chat_id, name),
                    FOREIGN KEY(chat_id) REFERENCES chats(id)
                );

                CREATE INDEX IF NOT EXISTS ix_runs_chat_id ON runs(chat_id);
                CREATE INDEX IF NOT EXISTS ix_goals_run_id ON goals(run_id);
                CREATE INDEX IF NOT EXISTS ix_timeline_chat_sequence ON timeline_entries(chat_id, sequence_no);
                CREATE INDEX IF NOT EXISTS ix_timeline_run_id ON timeline_entries(run_id);
                CREATE INDEX IF NOT EXISTS ix_secrets_chat_id ON secrets(chat_id);
                """;

            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(CancellationToken cancellationToken) =>
        UseConnectionAsync<IReadOnlyList<ChatSessionSummary>>(async (connection, token) =>
        {
            var results = new List<ChatSessionSummary>();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.id,
                       c.title,
                       c.updated_at_utc,
                       COALESCE(SUM(CASE WHEN r.status IN (0, 1, 2) THEN 1 ELSE 0 END), 0) AS active_runs
                FROM chats c
                LEFT JOIN runs r ON r.chat_id = c.id
                GROUP BY c.id, c.title, c.updated_at_utc
                ORDER BY c.updated_at_utc DESC;
                """;

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                results.Add(new ChatSessionSummary
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Title = reader.GetString(1),
                    UpdatedAtUtc = ParseDate(reader.GetString(2)),
                    ActiveRuns = reader.GetInt32(3),
                });
            }

            return results;
        }, cancellationToken);

    public Task<ChatSession> CreateChatAsync(string? title, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var chat = new ChatSession
        {
            Id = Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(title) ? "New Chat" : title.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        return UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                INSERT INTO chats (id, title, created_at_utc, updated_at_utc)
                VALUES (@id, @title, @created_at_utc, @updated_at_utc);
                """;
            Add(command, "@id", chat.Id);
            Add(command, "@title", chat.Title);
            Add(command, "@created_at_utc", chat.CreatedAtUtc);
            Add(command, "@updated_at_utc", chat.UpdatedAtUtc);
            await command.ExecuteNonQueryAsync(token);
            return chat;
        }, cancellationToken);
    }

    public Task<ChatSession?> GetChatAsync(Guid chatId, CancellationToken cancellationToken) =>
        UseConnectionAsync<ChatSession?>(async (connection, token) =>
        {
            var chatCommand = connection.CreateCommand();
            chatCommand.CommandText =
                """
                SELECT id, title, created_at_utc, updated_at_utc
                FROM chats
                WHERE id = @id;
                """;
            Add(chatCommand, "@id", chatId);

            Guid loadedChatId;
            string loadedTitle;
            DateTime loadedCreatedAtUtc;
            DateTime loadedUpdatedAtUtc;

            await using (var chatReader = await chatCommand.ExecuteReaderAsync(token))
            {
                if (!await chatReader.ReadAsync(token))
                {
                    return null;
                }

                loadedChatId = Guid.Parse(chatReader.GetString(0));
                loadedTitle = chatReader.GetString(1);
                loadedCreatedAtUtc = ParseDate(chatReader.GetString(2));
                loadedUpdatedAtUtc = ParseDate(chatReader.GetString(3));
            }

            var chat = new ChatSession
            {
                Id = loadedChatId,
                Title = loadedTitle,
                CreatedAtUtc = loadedCreatedAtUtc,
                UpdatedAtUtc = loadedUpdatedAtUtc,
            };

            chat.Runs = await LoadRunsAsync(connection, chatId, token);
            chat.Timeline = await LoadTimelineAsync(connection, chatId, token);
            return chat;
        }, cancellationToken);

    public Task UpdateChatAsync(ChatSession chat, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                UPDATE chats
                SET title = @title,
                    updated_at_utc = @updated_at_utc
                WHERE id = @id;
                """;
            Add(command, "@id", chat.Id);
            Add(command, "@title", chat.Title);
            Add(command, "@updated_at_utc", chat.UpdatedAtUtc);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task<TestRun> CreateRunAsync(Guid chatId, string userPrompt, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var run = new TestRun
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatId,
            UserPrompt = userPrompt.Trim(),
            Status = TestRunStatus.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            BrowserSnapshot = new BrowserSessionSnapshot
            {
                TestRunId = Guid.Empty,
            },
        };
        run.BrowserSnapshot.TestRunId = run.Id;

        return UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                INSERT INTO runs (
                    id,
                    chat_id,
                    user_prompt,
                    status,
                    failure_reason,
                    created_at_utc,
                    updated_at_utc,
                    completed_at_utc,
                    browser_snapshot_json)
                VALUES (
                    @id,
                    @chat_id,
                    @user_prompt,
                    @status,
                    @failure_reason,
                    @created_at_utc,
                    @updated_at_utc,
                    @completed_at_utc,
                    @browser_snapshot_json);
                """;
            Add(command, "@id", run.Id);
            Add(command, "@chat_id", chatId);
            Add(command, "@user_prompt", run.UserPrompt);
            Add(command, "@status", (int)run.Status);
            Add(command, "@failure_reason", run.FailureReason);
            Add(command, "@created_at_utc", run.CreatedAtUtc);
            Add(command, "@updated_at_utc", run.UpdatedAtUtc);
            Add(command, "@completed_at_utc", run.CompletedAtUtc);
            Add(command, "@browser_snapshot_json", Serialize(run.BrowserSnapshot));
            await command.ExecuteNonQueryAsync(token);

            var chatUpdate = CreateCommand(connection, transaction);
            chatUpdate.CommandText = "UPDATE chats SET updated_at_utc = @updated WHERE id = @chat_id;";
            Add(chatUpdate, "@updated", now);
            Add(chatUpdate, "@chat_id", chatId);
            await chatUpdate.ExecuteNonQueryAsync(token);

            return run;
        }, cancellationToken);
    }

    public Task UpdateRunAsync(TestRun run, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                UPDATE runs
                SET status = @status,
                    failure_reason = @failure_reason,
                    updated_at_utc = @updated_at_utc,
                    completed_at_utc = @completed_at_utc,
                    browser_snapshot_json = @browser_snapshot_json
                WHERE id = @id;
                """;
            Add(command, "@id", run.Id);
            Add(command, "@status", (int)run.Status);
            Add(command, "@failure_reason", run.FailureReason);
            Add(command, "@updated_at_utc", run.UpdatedAtUtc);
            Add(command, "@completed_at_utc", run.CompletedAtUtc);
            Add(command, "@browser_snapshot_json", Serialize(run.BrowserSnapshot));
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task<IReadOnlyList<GoalItem>> ListGoalsAsync(Guid runId, CancellationToken cancellationToken) =>
        UseConnectionAsync<IReadOnlyList<GoalItem>>(async (connection, token) =>
            await LoadGoalsAsync(connection, runId, token), cancellationToken);

    public Task<GoalItem> AddGoalAsync(GoalItem goal, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                INSERT INTO goals (
                    id,
                    run_id,
                    title,
                    success_criteria,
                    status,
                    note,
                    evidence,
                    created_at_utc,
                    updated_at_utc,
                    completed_at_utc)
                VALUES (
                    @id,
                    @run_id,
                    @title,
                    @success_criteria,
                    @status,
                    @note,
                    @evidence,
                    @created_at_utc,
                    @updated_at_utc,
                    @completed_at_utc);
                """;
            PopulateGoalParameters(command, goal);
            await command.ExecuteNonQueryAsync(token);
            return goal;
        }, cancellationToken);

    public Task UpdateGoalAsync(GoalItem goal, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                UPDATE goals
                SET title = @title,
                    success_criteria = @success_criteria,
                    status = @status,
                    note = @note,
                    evidence = @evidence,
                    updated_at_utc = @updated_at_utc,
                    completed_at_utc = @completed_at_utc
                WHERE id = @id;
                """;
            PopulateGoalParameters(command, goal);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task<TimelineEntry> AddTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            entry.Sequence = await GetNextSequenceCoreAsync(connection, transaction, entry.ChatSessionId, token);

            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                INSERT INTO timeline_entries (
                    id,
                    chat_id,
                    run_id,
                    sequence_no,
                    kind,
                    role,
                    content,
                    tool_call_id,
                    tool_name,
                    metadata_json,
                    created_at_utc)
                VALUES (
                    @id,
                    @chat_id,
                    @run_id,
                    @sequence_no,
                    @kind,
                    @role,
                    @content,
                    @tool_call_id,
                    @tool_name,
                    @metadata_json,
                    @created_at_utc);
                """;
            PopulateTimelineParameters(command, entry);
            await command.ExecuteNonQueryAsync(token);
            return entry;
        }, cancellationToken);

    public Task UpdateTimelineEntryAsync(TimelineEntry entry, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                UPDATE timeline_entries
                SET content = @content,
                    metadata_json = @metadata_json
                WHERE id = @id;
                """;
            Add(command, "@id", entry.Id);
            Add(command, "@content", entry.Content);
            Add(command, "@metadata_json", entry.MetadataJson);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task<long> GetNextSequenceAsync(Guid chatId, CancellationToken cancellationToken) =>
        UseConnectionAsync<long>((connection, token) => GetNextSequenceCoreAsync(connection, transaction: null, chatId, token), cancellationToken);

    public Task SaveBrowserSnapshotAsync(Guid runId, BrowserSessionSnapshot snapshot, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText = "UPDATE runs SET browser_snapshot_json = @snapshot, updated_at_utc = @updated WHERE id = @run_id;";
            Add(command, "@snapshot", Serialize(snapshot));
            Add(command, "@updated", DateTime.UtcNow);
            Add(command, "@run_id", runId);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task SaveSecretAsync(Guid chatId, string name, string value, CancellationToken cancellationToken) =>
        UseTransactionAsync(async (connection, transaction, token) =>
        {
            var command = CreateCommand(connection, transaction);
            command.CommandText =
                """
                INSERT INTO secrets (chat_id, name, encrypted_value, updated_at_utc)
                VALUES (@chat_id, @name, @encrypted_value, @updated_at_utc)
                ON CONFLICT(chat_id, name)
                DO UPDATE SET encrypted_value = excluded.encrypted_value,
                              updated_at_utc = excluded.updated_at_utc;
                """;
            Add(command, "@chat_id", chatId);
            Add(command, "@name", name.Trim());
            Add(command, "@encrypted_value", EncryptSecret(value));
            Add(command, "@updated_at_utc", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);

    public Task<string?> GetSecretAsync(Guid chatId, string name, CancellationToken cancellationToken) =>
        UseConnectionAsync<string?>(async (connection, token) =>
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT encrypted_value FROM secrets WHERE chat_id = @chat_id AND name = @name;";
            Add(command, "@chat_id", chatId);
            Add(command, "@name", name.Trim());
            var result = await command.ExecuteScalarAsync(token);
            return result is string encryptedValue
                ? DecryptSecret(encryptedValue)
                : null;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ListSecretNamesAsync(Guid chatId, CancellationToken cancellationToken) =>
        UseConnectionAsync<IReadOnlyList<string>>(async (connection, token) =>
        {
            var results = new List<string>();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM secrets WHERE chat_id = @chat_id ORDER BY name;";
            Add(command, "@chat_id", chatId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }, cancellationToken);

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UseConnectionAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await action(connection, cancellationToken);
        }
        finally
        {
            databaseGate.Release();
        }
    }

    private async Task<T> UseConnectionAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            return await action(connection, cancellationToken);
        }
        finally
        {
            databaseGate.Release();
        }
    }

    private async Task UseTransactionAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var dbTransaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var transaction = (SqliteTransaction)dbTransaction;
            try
            {
                await action(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            databaseGate.Release();
        }
    }

    private async Task<T> UseTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var dbTransaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var transaction = (SqliteTransaction)dbTransaction;
            try
            {
                var result = await action(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            databaseGate.Release();
        }
    }

    private async Task<List<TestRun>> LoadRunsAsync(SqliteConnection connection, Guid chatId, CancellationToken cancellationToken)
    {
        var runs = new List<TestRun>();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   chat_id,
                   user_prompt,
                   status,
                   failure_reason,
                   created_at_utc,
                   updated_at_utc,
                   completed_at_utc,
                   browser_snapshot_json
            FROM runs
            WHERE chat_id = @chat_id
            ORDER BY created_at_utc;
            """;
        Add(command, "@chat_id", chatId);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var runId = Guid.Parse(reader.GetString(0));
                runs.Add(new TestRun
                {
                    Id = runId,
                    ChatSessionId = Guid.Parse(reader.GetString(1)),
                    UserPrompt = reader.GetString(2),
                    Status = (TestRunStatus)reader.GetInt32(3),
                    FailureReason = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAtUtc = ParseDate(reader.GetString(5)),
                    UpdatedAtUtc = ParseDate(reader.GetString(6)),
                    CompletedAtUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
                    BrowserSnapshot = DeserializeSnapshot(reader.IsDBNull(8) ? null : reader.GetString(8), runId),
                });
            }
        }

        foreach (var run in runs)
        {
            run.Goals = await LoadGoalsAsync(connection, run.Id, cancellationToken);
        }

        return runs;
    }

    private async Task<List<GoalItem>> LoadGoalsAsync(SqliteConnection connection, Guid runId, CancellationToken cancellationToken)
    {
        var goals = new List<GoalItem>();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   run_id,
                   title,
                   success_criteria,
                   status,
                   note,
                   evidence,
                   created_at_utc,
                   updated_at_utc,
                   completed_at_utc
            FROM goals
            WHERE run_id = @run_id
            ORDER BY created_at_utc;
            """;
        Add(command, "@run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            goals.Add(new GoalItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                TestRunId = Guid.Parse(reader.GetString(1)),
                Title = reader.GetString(2),
                SuccessCriteria = reader.GetString(3),
                Status = (GoalStatus)reader.GetInt32(4),
                Note = reader.IsDBNull(5) ? null : reader.GetString(5),
                Evidence = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAtUtc = ParseDate(reader.GetString(7)),
                UpdatedAtUtc = ParseDate(reader.GetString(8)),
                CompletedAtUtc = reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
            });
        }

        return goals;
    }

    private async Task<List<TimelineEntry>> LoadTimelineAsync(SqliteConnection connection, Guid chatId, CancellationToken cancellationToken)
    {
        var timeline = new List<TimelineEntry>();
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   chat_id,
                   run_id,
                   sequence_no,
                   kind,
                   role,
                   content,
                   tool_call_id,
                   tool_name,
                   metadata_json,
                   created_at_utc
            FROM timeline_entries
            WHERE chat_id = @chat_id
            ORDER BY sequence_no;
            """;
        Add(command, "@chat_id", chatId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            timeline.Add(new TimelineEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                ChatSessionId = Guid.Parse(reader.GetString(1)),
                TestRunId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Sequence = reader.GetInt64(3),
                Kind = (TimelineItemKind)reader.GetInt32(4),
                Role = reader.GetString(5),
                Content = reader.GetString(6),
                ToolCallId = reader.IsDBNull(7) ? null : reader.GetString(7),
                ToolName = reader.IsDBNull(8) ? null : reader.GetString(8),
                MetadataJson = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedAtUtc = ParseDate(reader.GetString(10)),
            });
        }

        return timeline;
    }

    private static async Task<long> GetNextSequenceCoreAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid chatId, CancellationToken cancellationToken)
    {
        var command = CreateCommand(connection, transaction);
        command.CommandText = "SELECT COALESCE(MAX(sequence_no), 0) + 1 FROM timeline_entries WHERE chat_id = @chat_id;";
        Add(command, "@chat_id", chatId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private void PopulateGoalParameters(SqliteCommand command, GoalItem goal)
    {
        Add(command, "@id", goal.Id);
        Add(command, "@run_id", goal.TestRunId);
        Add(command, "@title", goal.Title);
        Add(command, "@success_criteria", goal.SuccessCriteria);
        Add(command, "@status", (int)goal.Status);
        Add(command, "@note", goal.Note);
        Add(command, "@evidence", goal.Evidence);
        Add(command, "@created_at_utc", goal.CreatedAtUtc);
        Add(command, "@updated_at_utc", goal.UpdatedAtUtc);
        Add(command, "@completed_at_utc", goal.CompletedAtUtc);
    }

    private void PopulateTimelineParameters(SqliteCommand command, TimelineEntry entry)
    {
        Add(command, "@id", entry.Id);
        Add(command, "@chat_id", entry.ChatSessionId);
        Add(command, "@run_id", entry.TestRunId);
        Add(command, "@sequence_no", entry.Sequence);
        Add(command, "@kind", (int)entry.Kind);
        Add(command, "@role", entry.Role);
        Add(command, "@content", entry.Content);
        Add(command, "@tool_call_id", entry.ToolCallId);
        Add(command, "@tool_name", entry.ToolName);
        Add(command, "@metadata_json", entry.MetadataJson);
        Add(command, "@created_at_utc", entry.CreatedAtUtc);
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value switch
        {
            null => DBNull.Value,
            Guid guid => guid.ToString(),
            DateTime dateTime => dateTime.ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O"),
            Enum enumeration => Convert.ToInt32(enumeration, CultureInfo.InvariantCulture),
            _ => value,
        });

    private string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, jsonOptions);

    private static string EncryptSecret(string value)
    {
        var clearBytes = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string DecryptSecret(string encryptedValue)
    {
        var encryptedBytes = Convert.FromBase64String(encryptedValue);
        var clearBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(clearBytes);
    }

    private BrowserSessionSnapshot DeserializeSnapshot(string? json, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BrowserSessionSnapshot { TestRunId = runId };
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<BrowserSessionSnapshot>(json, jsonOptions) ?? new BrowserSessionSnapshot();
            snapshot.TestRunId = snapshot.TestRunId == Guid.Empty ? runId : snapshot.TestRunId;
            snapshot.Tabs ??= [];
            return snapshot;
        }
        catch
        {
            return new BrowserSessionSnapshot
            {
                TestRunId = runId,
                State = BrowserState.Failed,
                LastCapturedAtUtc = DateTime.UtcNow,
            };
        }
    }

    private static DateTime ParseDate(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTime.UtcNow;
    }
}
