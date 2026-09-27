using System.Text.RegularExpressions;
using System.Windows;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class WindowHandler
    {
        public static Rect SaveState(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;
            string windowName = window.GetType().Name;
            setting.WindowBounds[windowName] = Regex.Replace(
                window.RestoreBounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                @"(\d+\.\d{1})\d+", "$1");
            setting.ScheduleSave();
            return window.RestoreBounds;
        }

        public static Rect LoadState(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;
            string windowName = window.GetType().Name;
            if (!setting.WindowBounds.TryGetValue(windowName, out string? serialized))
                return Rect.Empty;
            try
            {
                Rect bound = Rect.Parse(serialized);
                if (bound.IsEmpty || !double.IsFinite(bound.Left) || !double.IsFinite(bound.Top) ||
                    bound.Width < window.MinWidth || bound.Height < window.MinHeight)
                    return Rect.Empty;
                return bound;
            }
            catch (FormatException)
            {
                return Rect.Empty;
            }
        }

        public static bool IsVisibleOnVirtualDesktop(Rect bounds)
        {
            var virtualDesktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            return IsVisibleOnVirtualDesktop(bounds, virtualDesktop);
        }

        internal static bool IsVisibleOnVirtualDesktop(Rect bounds, Rect virtualDesktop)
        {
            if (bounds.IsEmpty || virtualDesktop.IsEmpty)
                return false;
            Rect visibleArea = Rect.Intersect(bounds, virtualDesktop);
            return !visibleArea.IsEmpty && visibleArea.Width >= 80 && visibleArea.Height >= 40;
        }

        public static void RestoreState(Window? window, Rect bound)
        {
            if (window == null || bound.IsEmpty)
                return;
            window.Top = bound.Top;
            window.Left = bound.Left;

            // Restore the size only for a manually sized
            if (window.SizeToContent == SizeToContent.Manual)
            {
                window.Width = bound.Width;
                window.Height = bound.Height;
            }
        }
    }
}
