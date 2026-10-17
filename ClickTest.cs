using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// --click-test: нажимаются ли ✕ и ↻ на окнах поверх игры. Показывает окно рейтинга и всплывашку, ведёт курсор на ✕ / ↻
/// и в середину окна, смотрит WS_EX_TRANSPARENT (над кнопкой снят — окно ловит клик, над телом стоит — клик уходит в игру)
/// и жмёт кнопку, только если под курсором это самое окно. Курсор занят около трёх секунд и возвращается на место.
/// Итог — в click_test.txt.
/// </summary>
static class ClickTest
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    const uint LeftDown = 0x02, LeftUp = 0x04, GaRoot = 2;

    public static void Run(string file)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var log = new List<string>();
        Native.GetCursorPos(out var home);

        var rating = new RatingWindow();
        bool ratingClosed = false, popupRefreshed = false;
        rating.CloseButtonPressed = () => ratingClosed = true;
        rating.RenderRating(new RatingView(0, "Легенда II", null, null, (4, 3, 0, 0), null, null, Array.Empty<int>(), null, null, null, "тест"),
            () => log.Add("↻ рейтинга нажат"));
        var popup = new OverlayWindow();
        popup.ShowPopup("Проверка кнопок", OverlayWindow.White, new[] { ("↻ и ✕ должны нажиматься", OverlayWindow.Gray) },
            TimeSpan.Zero, refresh: () => popupRefreshed = true);
        rating.Toggle();

        var steps = new Queue<Func<bool>>();
        (int X, int Y) Center(Window w, string glyph)
        {
            var b = Find(w, glyph) ?? throw new InvalidOperationException($"нет кнопки {glyph}");
            var a = b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
            return ((int)a.X, (int)a.Y);
        }
        (int X, int Y) Body(Window w)
        {
            var a = w.PointToScreen(new Point(w.ActualWidth / 2, w.ActualHeight * 0.6));
            return ((int)a.X, (int)a.Y);
        }
        bool Transparent(Window w) =>
            (Native.GetWindowLong(new WindowInteropHelper(w).Handle, Native.GWL_EXSTYLE) & Native.WS_EX_TRANSPARENT) != 0;
        bool Ours(Window w, (int X, int Y) p) =>
            GetAncestor(WindowFromPoint(new Native.POINT { X = p.X, Y = p.Y }), GaRoot) == new WindowInteropHelper(w).Handle;

        // Курсор там, куда поставили? Нет (двигали мышь) — поставить снова, шаг повторится на следующем такте.
        bool At((int X, int Y) target)
        {
            Native.GetCursorPos(out var now);
            if (now.X == target.X && now.Y == target.Y) return true;
            SetCursorPos(target.X, target.Y);
            return false;
        }

        // Шаг возвращает true, когда сделан; false — повторить на следующем такте (не больше четырёх раз).
        void Check(string name, Window w, string glyph, Func<bool> done)
        {
            (int X, int Y) button = default, body = default;
            steps.Enqueue(() => { button = Center(w, glyph); body = Body(w); SetCursorPos(button.X, button.Y); return true; });
            steps.Enqueue(() =>
            {
                if (!At(button)) return false;
                log.Add($"{name}: над {glyph} окно ловит клики — {(!Transparent(w) ? "да" : "НЕТ")}, под курсором оно — {(Ours(w, button) ? "да" : "НЕТ")}");
                if (Transparent(w) && w is GlassWindow g) log.Add($"    {g.Diagnose()}; поставили курсор в {button.X},{button.Y}");
                return true;
            });
            steps.Enqueue(() => { SetCursorPos(body.X, body.Y); return true; });
            steps.Enqueue(() =>
            {
                if (!At(body)) return false;
                log.Add($"{name}: над телом окна клики проходят в игру — {(Transparent(w) ? "да" : "НЕТ")}");
                return true;
            });
            steps.Enqueue(() => { SetCursorPos(button.X, button.Y); return true; });
            steps.Enqueue(() =>
            {
                if (!At(button) || Transparent(w) || !Ours(w, button)) return false; // нажимаем только в своё окно
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                return true;
            });
            steps.Enqueue(() =>
            {
                log.Add($"{name}: нажатие {glyph} сработало — {(done() ? "да" : "НЕТ")}");
                return true;
            });
        }

        Check("всплывашка", popup, "↻", () => popupRefreshed);
        Check("всплывашка", popup, "✕", () => !popup.IsVisible);
        Check("рейтинг", rating, "✕", () => ratingClosed && !rating.IsVisible);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        int tries = 0;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (steps.Count > 0)
                {
                    if (steps.Peek()() || ++tries > 4)
                    {
                        if (tries > 4) log.Add("шаг не удался за 5 попыток (курсор сдвигали или окно не ловит клик)");
                        steps.Dequeue();
                        tries = 0;
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                log.Add("ошибка: " + ex.Message);
                steps.Clear();
            }
            timer.Stop();
            SetCursorPos(home.X, home.Y);
            File.WriteAllLines(file, log);
            app.Shutdown();
        };
        // окна должны успеть показаться и разложиться
        app.Dispatcher.BeginInvoke(() => timer.Start(), DispatcherPriority.ApplicationIdle);
        app.Run();
    }

    static Border? Find(DependencyObject node, string glyph)
    {
        if (node is Border { Child: TextBlock t } b && t.Text == glyph) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find(VisualTreeHelper.GetChild(node, i), glyph) is { } found) return found;
        return null;
    }
}
