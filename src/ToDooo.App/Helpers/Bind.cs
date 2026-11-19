using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ToDooo.ViewModels;
using Windows.UI;
using Windows.UI.Text;

namespace ToDooo.Helpers;

public static class Bind
{
    private static readonly SolidColorBrush TransparentBrush = new(Color.FromArgb(0, 0, 0, 0));

    // 统一取应用级共享画刷：主题切换时 ThemePalette 直接改这些实例的颜色，列表会跟着变。
    private static Brush Palette(string key) => (Brush)Application.Current.Resources[key];
    private static readonly SolidColorBrush[] TagBrushes = BuildTagBrushes();

    private static SolidColorBrush[] BuildTagBrushes()
    {
        var brushes = new SolidColorBrush[MainViewModel.TagColors.Length];
        for (var i = 0; i < brushes.Length; i++)
        {
            var hex = MainViewModel.TagColors[i];
            brushes[i] = new SolidColorBrush(Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16)));
        }

        return brushes;
    }

    public static TextDecorations Deco(bool done) =>
        done ? TextDecorations.Strikethrough : TextDecorations.None;

    public static Brush TitleBrush(bool done) => done ? Palette("TextMutedBrush") : Palette("TextPrimaryBrush");

    public static Brush DueBrush(bool overdue) => overdue ? Palette("OverdueBrush") : Palette("TextMutedBrush");

    public static Visibility Visible(string? text) =>
        string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleWhen(bool condition) =>
        condition ? Visibility.Visible : Visibility.Collapsed;


    public static Brush TagBrush(int tagIndex) =>
        tagIndex >= 0 && tagIndex < TagBrushes.Length ? TagBrushes[tagIndex] : TransparentBrush;

    public static Visibility HasTag(int tagIndex) =>
        tagIndex >= 0 && tagIndex < TagBrushes.Length ? Visibility.Visible : Visibility.Collapsed;

    public static Brush CheckFill(bool done) => done ? Palette("NeutralCheckFillBrush") : TransparentBrush;

    public static Brush CheckBorder(bool done) => done ? TransparentBrush : Palette("CheckBorderBrush");

    public static double CheckOpacity(bool done) => done ? 1 : 0;

    public static double DoneOpacity(bool done) => done ? 0.55 : 1.0;

    public static double DueOpacity(bool done) => done ? 0.5 : 1.0;

    // DeskBox 同款：重要用实心星 E735，普通用空心星 E734，且只有重要的才高亮。
    public static string ImportantGlyph(bool important) => important ? "\uE735" : "\uE734";

    public static Brush ImportantForeground(bool important) =>
        important ? Palette("AccentBrush") : Palette("TextMutedBrush");

    // 行尾星标：标记了重要就常亮，没标记的默认藏起来、靠悬停浮现。
    public static double ImportantOpacity(bool important) => important ? 1.0 : 0.0;

    public static string ChevronGlyph(bool expanded) => expanded ? "\uE70E" : "\uE70D";

    public static Brush SubtaskBrush(bool done) => done ? Palette("TextMutedBrush") : Palette("TextSecondaryBrush");
}
