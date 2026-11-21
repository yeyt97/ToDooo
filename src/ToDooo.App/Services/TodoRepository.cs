using Microsoft.Data.Sqlite;
using ToDooo.Models;

namespace ToDooo.Services;

public sealed class TodoRepository
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly string? _backupDir;
    private DateTime _lastBackupDate;

    public TodoRepository(string databasePath, string? backupDir = null)
    {
        _databasePath = databasePath;
        _backupDir = backupDir;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS todos (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                title         TEXT    NOT NULL,
                is_done       INTEGER NOT NULL DEFAULT 0,
                is_important  INTEGER NOT NULL DEFAULT 0,
                due_at        TEXT    NULL,
                recurrence    INTEGER NOT NULL DEFAULT 0,
                tag_index     INTEGER NOT NULL DEFAULT -1,
                sort_order    REAL    NOT NULL DEFAULT 0,
                created_at    TEXT    NOT NULL,
                completed_at  TEXT    NULL,
                due_notified  INTEGER NOT NULL DEFAULT 0,
                snooze_until  TEXT    NULL,
                note          TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_todos_order ON todos(sort_order);

            CREATE TABLE IF NOT EXISTS subtasks (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                todo_id       INTEGER NOT NULL,
                title         TEXT    NOT NULL,
                is_done       INTEGER NOT NULL DEFAULT 0,
                sort_order    REAL    NOT NULL DEFAULT 0,
                due_at        TEXT    NULL,
                due_notified  INTEGER NOT NULL DEFAULT 0,
                note          TEXT    NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS idx_subtasks_todo ON subtasks(todo_id);
            """;
        command.ExecuteNonQuery();

        // 旧库补列：SQLite 没有 ADD COLUMN IF NOT EXISTS，重复执行报错就忽略。
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE todos ADD COLUMN snooze_until TEXT NULL";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在
        }

        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE subtasks ADD COLUMN due_at TEXT NULL";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在
        }

        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE todos ADD COLUMN note TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在
        }

        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE subtasks ADD COLUMN due_notified INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在
        }

        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE subtasks ADD COLUMN note TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
        }
        catch
        {
            // 列已存在
        }

        EnsureDailyBackup();
    }

    public void EnsureDailyBackup(DateTime? nowOverride = null, int keepCount = 7)
    {
        if (string.IsNullOrWhiteSpace(_backupDir) ||
            string.IsNullOrWhiteSpace(_databasePath) ||
            _databasePath == ":memory:" ||
            !File.Exists(_databasePath))
        {
            return;
        }

        var today = (nowOverride ?? DateTime.Now).Date;
        if (_lastBackupDate == today)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_backupDir);
            var target = Path.Combine(_backupDir, $"todo-{today:yyyyMMdd}.db");
            if (!File.Exists(target))
            {
                var backupConnString = new SqliteConnectionStringBuilder
                {
                    DataSource = target,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                }.ToString();

                using var source = Open();
                using var dest = new SqliteConnection(backupConnString);
                dest.Open();
                source.BackupDatabase(dest);
            }

            _lastBackupDate = today;

            var oldBackups = Directory.GetFiles(_backupDir, "todo-*.db")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(1, keepCount));
            foreach (var old in oldBackups)
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                    // 清理旧备份失败不影响正常读写
                }
            }
        }
        catch
        {
            // 自动备份失败不阻塞主流程
        }
    }

    public List<TodoItem> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, is_done, is_important, due_at, recurrence, tag_index,
                   sort_order, created_at, completed_at, due_notified, snooze_until, note
            FROM todos
            ORDER BY sort_order ASC, id ASC
            """;

        var items = new List<TodoItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(Read(reader));
        }

        reader.Close();
        LoadSubtasks(connection, items);
        return items;
    }

    private static void LoadSubtasks(SqliteConnection connection, List<TodoItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var byId = items.ToDictionary(item => item.Id);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, todo_id, title, is_done, sort_order, due_at, due_notified, note
            FROM subtasks
            ORDER BY todo_id ASC, sort_order ASC, id ASC
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!byId.TryGetValue(reader.GetInt64(1), out var parent))
            {
                continue;
            }

            parent.Subtasks.Add(new SubtaskItem
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(2),
                IsDone = reader.GetInt64(3) != 0,
                SortOrder = reader.GetDouble(4),
                DueAt = reader.IsDBNull(5) ? null : FromDb(reader.GetString(5)),
                DueNotified = !reader.IsDBNull(6) && reader.GetInt64(6) != 0,
                Note = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            });
        }

        foreach (var item in items)
        {
            item.RaiseSubtasksChanged();
        }
    }

    public void Add(TodoItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO todos (title, is_done, is_important, due_at, recurrence, tag_index,
                               sort_order, created_at, completed_at, due_notified, snooze_until, note)
            VALUES ($title, $done, $important, $due, $recurrence, $tag,
                    $order, $created, $completed, $notified, $snooze, $note);
            SELECT last_insert_rowid();
            """;
        Bind(command, item);
        item.Id = (long)(command.ExecuteScalar() ?? 0L);
        InsertSubtasks(connection, item);
    }

    private static void InsertSubtasks(SqliteConnection connection, TodoItem item)
    {
        if (item.Subtasks.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO subtasks (todo_id, title, is_done, sort_order, due_at, due_notified, note)
            VALUES ($todo, $title, $done, $order, $due, $notified, $note);
            SELECT last_insert_rowid();
            """;
        var todoParameter = command.Parameters.Add("$todo", SqliteType.Integer);
        var titleParameter = command.Parameters.Add("$title", SqliteType.Text);
        var doneParameter = command.Parameters.Add("$done", SqliteType.Integer);
        var orderParameter = command.Parameters.Add("$order", SqliteType.Real);
        var dueParameter = command.Parameters.Add("$due", SqliteType.Text);
        var notifiedParameter = command.Parameters.Add("$notified", SqliteType.Integer);
        var noteParameter = command.Parameters.Add("$note", SqliteType.Text);

        foreach (var subtask in item.Subtasks)
        {
            todoParameter.Value = item.Id;
            titleParameter.Value = subtask.Title;
            doneParameter.Value = subtask.IsDone ? 1 : 0;
            orderParameter.Value = subtask.SortOrder;
            dueParameter.Value = (object?)ToDb(subtask.DueAt) ?? DBNull.Value;
            notifiedParameter.Value = subtask.DueNotified ? 1 : 0;
            noteParameter.Value = subtask.Note ?? string.Empty;
            subtask.Id = (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    public void AddSubtask(long todoId, SubtaskItem subtask)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO subtasks (todo_id, title, is_done, sort_order, due_at, due_notified, note)
            VALUES ($todo, $title, $done, $order, $due, $notified, $note);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$todo", todoId);
        command.Parameters.AddWithValue("$title", subtask.Title);
        command.Parameters.AddWithValue("$done", subtask.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$order", subtask.SortOrder);
        command.Parameters.AddWithValue("$due", (object?)ToDb(subtask.DueAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$notified", subtask.DueNotified ? 1 : 0);
        command.Parameters.AddWithValue("$note", subtask.Note ?? string.Empty);
        subtask.Id = (long)(command.ExecuteScalar() ?? 0L);
    }

    public void UpdateSubtask(SubtaskItem subtask)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE subtasks SET
                title = $title,
                is_done = $done,
                sort_order = $order,
                due_at = $due,
                due_notified = $notified,
                note = $note
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$title", subtask.Title);
        command.Parameters.AddWithValue("$done", subtask.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$order", subtask.SortOrder);
        command.Parameters.AddWithValue("$due", (object?)ToDb(subtask.DueAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$notified", subtask.DueNotified ? 1 : 0);
        command.Parameters.AddWithValue("$note", subtask.Note ?? string.Empty);
        command.Parameters.AddWithValue("$id", subtask.Id);
        command.ExecuteNonQuery();
    }

    public void DeleteSubtask(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM subtasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Update(TodoItem item)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todos SET
                title = $title,
                is_done = $done,
                is_important = $important,
                due_at = $due,
                recurrence = $recurrence,
                tag_index = $tag,
                sort_order = $order,
                completed_at = $completed,
                due_notified = $notified,
                snooze_until = $snooze,
                note = $note
            WHERE id = $id
            """;
        Bind(command, item);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM subtasks WHERE todo_id = $id;
            DELETE FROM todos WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>拖动排序后批量写回 sort_order：单连接 + 单事务，避免逐条开连接。</summary>
    public void UpdateOrders(IEnumerable<TodoItem> items)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE todos SET sort_order = $order WHERE id = $id";
        var orderParameter = command.Parameters.Add("$order", SqliteType.Real);
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);

        foreach (var item in items)
        {
            if (item.Id <= 0)
            {
                continue;
            }

            orderParameter.Value = item.SortOrder;
            idParameter.Value = item.Id;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void ClearCompleted()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM subtasks WHERE todo_id IN (SELECT id FROM todos WHERE is_done = 1);
            DELETE FROM todos WHERE is_done = 1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Bind(SqliteCommand command, TodoItem item)
    {
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$done", item.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$important", item.IsImportant ? 1 : 0);
        command.Parameters.AddWithValue("$due", (object?)ToDb(item.DueAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurrence", (int)item.Recurrence);
        command.Parameters.AddWithValue("$tag", item.TagIndex);
        command.Parameters.AddWithValue("$order", item.SortOrder);
        command.Parameters.AddWithValue("$created", ToDb(item.CreatedAt) ?? string.Empty);
        command.Parameters.AddWithValue("$completed", (object?)ToDb(item.CompletedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$notified", item.DueNotified ? 1 : 0);
        command.Parameters.AddWithValue("$snooze", (object?)ToDb(item.SnoozeUntil) ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", item.Note ?? string.Empty);
    }

    private static TodoItem Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Title = reader.GetString(1),
        IsDone = reader.GetInt64(2) != 0,
        IsImportant = reader.GetInt64(3) != 0,
        DueAt = FromDb(reader.IsDBNull(4) ? null : reader.GetString(4)),
        Recurrence = (TodoRecurrence)reader.GetInt64(5),
        TagIndex = (int)reader.GetInt64(6),
        SortOrder = reader.GetDouble(7),
        CreatedAt = FromDb(reader.GetString(8)) ?? DateTime.Now,
        CompletedAt = FromDb(reader.IsDBNull(9) ? null : reader.GetString(9)),
        DueNotified = reader.GetInt64(10) != 0,
        SnoozeUntil = FromDb(reader.IsDBNull(11) ? null : reader.GetString(11)),
        Note = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
    };

    private static string? ToDb(DateTime? value) => value?.ToString(DateFormat);

    private static DateTime? FromDb(string? value) =>
        value is null ? null : DateTime.TryParse(value, out var parsed) ? parsed : null;
}
