using System.IO;
using System.Text.Json;
using ToDooo.Models;

namespace ToDooo.Services;

/// <summary>
/// 便签本地存储：正文存独立 .md 文件，窗口位置/颜色/置顶等元数据存 meta.json，
/// 与 Sticky++ 保持同样的干净文件结构。
/// </summary>
public sealed class StickyNoteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _notesDir;
    private readonly string _metaPath;
    private readonly List<StickyNote> _notes = new();

    public event Action? NotesChanged;

    public StickyNoteStore()
    {
        _notesDir = Path.Combine(AppPaths.Root, "notes");
        _metaPath = Path.Combine(_notesDir, "meta.json");
        Load();
    }

    public IReadOnlyList<StickyNote> Notes => _notes;

    public StickyNote? Find(string id) => _notes.FirstOrDefault(n => n.Id == id);

    public StickyNote Create(string color = "yellow")
    {
        Directory.CreateDirectory(_notesDir);
        var note = new StickyNote
        {
            Color = StickyColorTheme.Resolve(color).Key,
            Visible = true,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };
        note.FileName = $"note-{note.Id}.md";

        _notes.Insert(0, note);
        WriteNoteFile(note);
        SaveMeta();
        NotesChanged?.Invoke();
        return note;
    }

    public void SaveContent(StickyNote note)
    {
        note.UpdatedAt = DateTimeOffset.Now;
        WriteNoteFile(note);
        SaveMeta();
        NotesChanged?.Invoke();
    }

    public void SaveMeta(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(_notesDir);
            var json = JsonSerializer.Serialize(_notes, JsonOptions);
            File.WriteAllText(_metaPath, json);
        }
        catch
        {
            // 写盘失败不影响前台编辑
        }

        if (notify)
        {
            NotesChanged?.Invoke();
        }
    }

    public void Delete(string id)
    {
        var note = Find(id);
        if (note is null)
        {
            return;
        }

        _notes.Remove(note);
        try
        {
            var path = Path.Combine(_notesDir, note.FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 文件已不存在或被占用时忽略
        }

        SaveMeta();
        NotesChanged?.Invoke();
    }

    private void Load()
    {
        _notes.Clear();
        try
        {
            if (!File.Exists(_metaPath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<StickyNote>>(File.ReadAllText(_metaPath));
            if (loaded is null)
            {
                return;
            }

            foreach (var note in loaded)
            {
                if (string.IsNullOrWhiteSpace(note.FileName))
                {
                    note.FileName = $"note-{note.Id}.md";
                }

                var path = Path.Combine(_notesDir, note.FileName);
                note.Content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                _notes.Add(note);
            }

            _notes.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        }
        catch
        {
            // 元数据损坏时退回空列表
        }
    }

    private void WriteNoteFile(StickyNote note)
    {
        try
        {
            Directory.CreateDirectory(_notesDir);
            var path = Path.Combine(_notesDir, note.FileName);
            File.WriteAllText(path, note.Content ?? string.Empty);
        }
        catch
        {
            // 忽略临时 IO 错误
        }
    }
}
