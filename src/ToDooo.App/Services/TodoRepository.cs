using Microsoft.Data.Sqlite;
using ToDooo.Models;

namespace ToDooo.Services;

public sealed class TodoRepository
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss";
    private readonly string _connectionString;

    public TodoRepository(string databasePath)
    {
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
                snooze_until  TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS idx_todos_order ON todos(sort_order);

            CREATE TABLE IF NOT EXISTS subtasks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                todo_id     INTEGER NOT NULL,
                title       TEXT    NOT NULL,
                is_done     INTEGER NOT NULL DEFAULT 0,
                sort_order  REAL    NOT NULL DEFAULT 0
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
    }

    public List<TodoItem> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, is_done, is_important, due_at, recurrence, tag_index,
                   sort_order, created_at, completed_at, due_notified, snooze_until
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
            SELECT id, todo_id, title, is_done, sort_order
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
                               sort_order, created_at, completed_at, due_notified, snooze_until)
            VALUES ($title, $done, $important, $due, $recurrence, $tag,
                    $order, $created, $completed, $notified, $snooze);
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
            INSERT INTO subtasks (todo_id, title, is_done, sort_order)
            VALUES ($todo, $title, $done, $order);
            SELECT last_insert_rowid();
            """;
        var todoParameter = command.Parameters.Add("$todo", SqliteType.Integer);
        var titleParameter = command.Parameters.Add("$title", SqliteType.Text);
        var doneParameter = command.Parameters.Add("$done", SqliteType.Integer);
        var orderParameter = command.Parameters.Add("$order", SqliteType.Real);

        foreach (var subtask in item.Subtasks)
        {
            todoParameter.Value = item.Id;
            titleParameter.Value = subtask.Title;
            doneParameter.Value = subtask.IsDone ? 1 : 0;
            orderParameter.Value = subtask.SortOrder;
            subtask.Id = (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    public void AddSubtask(long todoId, SubtaskItem subtask)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO subtasks (todo_id, title, is_done, sort_order)
            VALUES ($todo, $title, $done, $order);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$todo", todoId);
        command.Parameters.AddWithValue("$title", subtask.Title);
        command.Parameters.AddWithValue("$done", subtask.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$order", subtask.SortOrder);
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
                sort_order = $order
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$title", subtask.Title);
        command.Parameters.AddWithValue("$done", subtask.IsDone ? 1 : 0);
        command.Parameters.AddWithValue("$order", subtask.SortOrder);
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
                snooze_until = $snooze
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
    };

    private static string? ToDb(DateTime? value) => value?.ToString(DateFormat);

    private static DateTime? FromDb(string? value) =>
        value is null ? null : DateTime.TryParse(value, out var parsed) ? parsed : null;
}
