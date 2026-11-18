using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PDFStudio.Controls;

/// <summary>
/// Camada transparente por cima de cada página, onde o usuário desenha os comentários.
/// Existe como classe própria só para poder trocar o cursor do mouse (ProtectedCursor).
/// </summary>
public sealed partial class DrawingSurface : Canvas
{
    /// <summary>null = cursor normal (seta).</summary>
    public void SetCursor(InputSystemCursorShape? shape) =>
        ProtectedCursor = shape is { } s ? InputSystemCursor.Create(s) : null;
}
