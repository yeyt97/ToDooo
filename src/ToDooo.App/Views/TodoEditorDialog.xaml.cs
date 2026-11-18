using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ToDooo.Models;
using ToDooo.ViewModels;
using Windows.UI;

namespace ToDooo.Views;

public sealed partial class TodoEditorDialog : ContentDialog
{
    private readonly List<Border> _tagButtons = new();
    private int _tagIndex = -1;

    public TodoEditorDialog()
    {
        InitializeComponent();
        RequestedTheme = ElementTheme.Dark;
        Background = new SolidColorBrush(Color.FromArgb(255, 0x2A, 0x30, 0x38));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        BuildTagButtons();
        UpdateTagVisuals();
    }

    public TodoEditorDialog(TodoItem item) : this()
    {
        TitleBox.Text = item.Title;
        if (item.DueAt is { } due)
        {
            DueDate.Date = new DateTimeOffset(due.Date);
            DueTime.Time = due.TimeOfDay;
        }

        RecurrenceBox.SelectedIndex = (int)item.Recurrence;
        ImportantSwitch.IsOn = item.IsImportant;
        _tagIndex = item.TagIndex;
        UpdateTagVisuals();
    }

    public string TodoTitle => TitleBox.Text.Trim();

    public DateTime? DueAt
    {
        get
        {
            if (DueDate.Date is not { } date)
            {
                return null;
            }

            return date.Date + DueTime.Time;
        }
    }

    public TodoRecurrence Recurrence => (TodoRecurrence)Math.Max(0, RecurrenceBox.SelectedIndex);

    public bool IsImportant => ImportantSwitch.IsOn;

    public int TagIndex => _tagIndex;

    private void BuildTagButtons()
    {
        for (var i = 0; i < MainViewModel.TagColors.Length; i++)
        {
            var color = Color.FromArgb(
                255,
                Convert.ToByte(MainViewModel.TagColors[i].Substring(1, 2), 16),
                Convert.ToByte(MainViewModel.TagColors[i].Substring(3, 2), 16),
                Convert.ToByte(MainViewModel.TagColors[i].Substring(5, 2), 16));

            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var border = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255)),
                Child = dot,
                Tag = i,
            };

            border.Tapped += (s, _) =>
            {
                var index = (int)((Border)s!).Tag;
                _tagIndex = _tagIndex == index ? -1 : index;
                UpdateTagVisuals();
            };

            _tagButtons.Add(border);
            TagPanel.Children.Add(border);
        }

        var none = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            Tag = -1,
            Child = new FontIcon { Glyph = "\uE711", FontSize = 9, Opacity = 0.6 },
        };
        none.Tapped += (s, _) =>
        {
            _tagIndex = -1;
            UpdateTagVisuals();
        };
        _tagButtons.Add(none);
        TagPanel.Children.Add(none);
    }

    private void UpdateTagVisuals()
    {
        for (var i = 0; i < _tagButtons.Count; i++)
        {
            var index = i == _tagButtons.Count - 1 ? -1 : i;
            ((SolidColorBrush)_tagButtons[i].BorderBrush).Color = _tagIndex == index
                ? Color.FromArgb(255, 255, 255, 255)
                : Color.FromArgb(0, 0, 0, 0);
        }
    }
}
