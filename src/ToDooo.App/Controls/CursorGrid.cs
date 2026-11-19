using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ToDooo.Controls;

/// <summary>Root 容器：托管时可以换光标（ProtectedCursor 只有子类能访问）。</summary>
public sealed class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape? shape)
    {
        ProtectedCursor = shape is { } value ? InputSystemCursor.Create(value) : null;
    }
}