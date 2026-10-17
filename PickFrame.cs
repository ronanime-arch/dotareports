using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Rectangle = System.Drawing.Rectangle;

namespace DotaReports;

/// <summary>
/// Рамка вокруг героя, которого советует «Бери», прямо на экране: клетка в сетке пика или картинка среди трёх в кастомке.
/// Рисуется снаружи клетки, в зазоре между портретами (в сетке он 8 px, у трёх картинок — 6 px), — распознавание
/// смотрит внутрь клеток, рамка ему не мешает. Клики проходят насквозь, фокус не забирает.
/// </summary>
sealed class PickFrame : Window
{
    const int Gap = 3;   // от края клетки до рамки и толщина рамки, px экрана

    public PickFrame()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Content = new System.Windows.Controls.Border
        {
            BorderBrush = new SolidColorBrush(OverlayWindow.Green),
            BorderThickness = new Thickness(Gap),
            CornerRadius = new CornerRadius(2),
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
    }

    /// <summary>Окно рамки вокруг клетки: отступ <see cref="Gap"/> и столько же толщина — в пикселях экрана.</summary>
    public static Rectangle Outer(Rectangle cell) => Rectangle.Inflate(cell, 2 * Gap, 2 * Gap);

    public static int Thickness => Gap;

    /// <summary>Обвести область экрана (в пикселях снимка 1920×1080); null — убрать рамку.</summary>
    public void ShowAround(Rectangle? cell)
    {
        if (cell is not { } c)
        {
            Hide();
            return;
        }
        new WindowInteropHelper(this).EnsureHandle();
        // при масштабе Windows не 100% окна WPF меряются не в пикселях
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var o = Outer(c);
        var topLeft = toDip.Transform(new Point(o.Left, o.Top));
        var size = toDip.Transform(new Vector(o.Width, o.Height));
        (Left, Top, Width, Height) = (topLeft.X, topLeft.Y, size.X, size.Y);
        if (!IsVisible) Show();
    }
}
