using ToDooo.Models;
using ToDooo.Services;
using ToDooo.ViewModels;
using Xunit;

namespace ToDooo.Tests;

public sealed class TodoViewModelTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"todooo-test-{Guid.NewGuid():N}.db");

    private readonly TodoRepository _repository;
    private readonly MainViewModel _viewModel;

    public TodoViewModelTests()
    {
        _repository = new TodoRepository(_dbPath);
        _repository.Initialize();
        _viewModel = new MainViewModel(_repository);
        _viewModel.Load();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 测试清理失败不影响结果
            }
        }
    }

    [Fact]
    public void Add_PutsNewItemOnTop()
    {
        _viewModel.Add("第一条");
        _viewModel.Add("第二条");

        Assert.Equal(new[] { "第二条", "第一条" }, _viewModel.Items.Select(x => x.Title));
        Assert.Equal(2, _viewModel.AllCount);
    }

    [Fact]
    public void Search_FiltersByTitle()
    {
        _viewModel.Add("买牛奶");
        _viewModel.Add("交周报");

        _viewModel.SetSearch("牛奶");

        Assert.Single(_viewModel.Items);
        Assert.Equal("买牛奶", _viewModel.Items[0].Title);
        Assert.Equal("没有找到匹配的待办", _viewModel.EmptyTitle);
    }

    [Fact]
    public void TagAndFilter_Combine()
    {
        var a = new TodoItem { Title = "重要红色", IsImportant = true, TagIndex = 0 };
        var b = new TodoItem { Title = "普通红色", TagIndex = 0 };
        var c = new TodoItem { Title = "重要蓝色", IsImportant = true, TagIndex = 1 };
        _viewModel.AddItem(a);
        _viewModel.AddItem(b);
        _viewModel.AddItem(c);

        _viewModel.ToggleTag(0);
        _viewModel.SetFilter(TodoFilter.Important);

        Assert.Single(_viewModel.Items);
        Assert.Equal("重要红色", _viewModel.Items[0].Title);
    }

    [Fact]
    public void CompletingRecurring_CreatesNextOccurrence()
    {
        var item = new TodoItem
        {
            Title = "每天喝水",
            Recurrence = TodoRecurrence.Daily,
            DueAt = DateTime.Now.AddHours(-2),
        };
        _viewModel.AddItem(item);

        _viewModel.SetDone(item, true);

        Assert.Equal(2, _viewModel.AllCount);
        var next = _viewModel.AllItems.Single(x => x.Id != item.Id);
        Assert.False(next.IsDone);
        Assert.True(next.DueAt > DateTime.Now);
    }

    [Fact]
    public void Reorder_PersistsAcrossReload()
    {
        _viewModel.Add("A");
        _viewModel.Add("B");
        _viewModel.Add("C");

        var reordered = _viewModel.Items.ToList();
        reordered.Reverse();
        _viewModel.PersistReorder(reordered);

        var reloaded = new MainViewModel(_repository);
        reloaded.Load();
        Assert.Equal(new[] { "A", "B", "C" }, reloaded.Items.Select(x => x.Title));
    }

    [Fact]
    public void Delete_ThenRestore_BringsItemBack()
    {
        _viewModel.Add("临时任务");
        var item = _viewModel.Items[0];

        var removed = _viewModel.Delete(item);
        Assert.Empty(_viewModel.Items);

        _viewModel.Restore(new[] { removed });
        Assert.Single(_viewModel.Items);
        Assert.Equal("临时任务", _viewModel.Items[0].Title);
    }

    [Fact]
    public void ClearCompleted_ReturnsRemovedItems()
    {
        _viewModel.Add("一");
        _viewModel.Add("二");
        var first = _viewModel.AllItems.Single(x => x.Title == "一");
        _viewModel.SetDone(first, true);

        var removed = _viewModel.ClearCompleted();

        Assert.Single(removed);
        Assert.Equal("一", removed[0].Title);
        Assert.Single(_viewModel.AllItems);
        Assert.Equal(0, _viewModel.DoneCount);
    }

    [Fact]
    public void DueText_MarksOverdue()
    {
        var item = new TodoItem { Title = "逾期任务", DueAt = DateTime.Now.AddHours(-1) };

        Assert.True(item.IsOverdue);
        Assert.Contains("已逾期", item.DueText);
    }

    [Fact]
    public void Repository_RoundTripsAllFields()
    {
        var item = new TodoItem
        {
            Title = "完整字段",
            IsImportant = true,
            TagIndex = 3,
            Recurrence = TodoRecurrence.Weekly,
            DueAt = new DateTime(2026, 12, 1, 9, 30, 0),
        };
        _repository.Add(item);

        var loaded = _repository.GetAll().Single();

        Assert.Equal(item.Title, loaded.Title);
        Assert.True(loaded.IsImportant);
        Assert.Equal(3, loaded.TagIndex);
        Assert.Equal(TodoRecurrence.Weekly, loaded.Recurrence);
        Assert.Equal(item.DueAt, loaded.DueAt);
    }
}
