using System.Diagnostics;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.utils
{
    public sealed record LiveCaptionsSession(
        AutomationElement Window,
        int ProcessId,
        DateTime ProcessStartTimeUtc,
        bool OwnsProcess,
        bool WasHidden);

    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";
        private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(2);

        private static AutomationElement? captionsTextBlock = null;

        public static async Task<LiveCaptionsSession> ConnectAsync(CancellationToken token = default)
        {
            Process? existing = SelectExistingProcess();
            bool ownsProcess = existing is null;
            Process process = existing ?? Process.Start(PROCESS_NAME) ??
                throw new InvalidOperationException("Windows Live Captions could not be started.");

            try
            {
                AutomationElement? window = null;
                for (int attemptCount = 0; attemptCount < 100; attemptCount++)
                {
                    token.ThrowIfCancellationRequested();
                    window = FindWindowByPId(process.Id);
                    if (window != null &&
                        string.Equals(window.Current.ClassName, "LiveCaptionsDesktopWindow", StringComparison.Ordinal))
                        break;
                    window = null;
                    await Task.Delay(50, token);
                }

                if (window == null)
                    throw new TimeoutException("Timed out while waiting for Windows Live Captions.");

                bool wasHidden = window.Current.BoundingRectangle == System.Windows.Rect.Empty;
                return new LiveCaptionsSession(window, process.Id, process.StartTime.ToUniversalTime(),
                    ownsProcess, wasHidden);
            }
            catch
            {
                if (ownsProcess)
                    StopOwnedProcess(process);
                throw;
            }
            finally { process.Dispose(); }
        }

        private static Process? SelectExistingProcess()
        {
            Process? selected = null;
            DateTime selectedStartTime = DateTime.MinValue;
            foreach (Process candidate in Process.GetProcessesByName(PROCESS_NAME))
            {
                try
                {
                    if (candidate.HasExited)
                    {
                        candidate.Dispose();
                        continue;
                    }

                    DateTime startTime = candidate.StartTime;
                    if (selected is null || startTime > selectedStartTime)
                    {
                        selected?.Dispose();
                        selected = candidate;
                        selectedStartTime = startTime;
                    }
                    else
                        candidate.Dispose();
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                                           System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    candidate.Dispose();
                }
            }
            return selected;
        }

        private static void StopOwnedProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;
                process.Kill();
                if (!process.WaitForExit((int)ProcessExitTimeout.TotalMilliseconds))
                    Debug.WriteLine("Windows Live Captions did not exit within the startup cleanup timeout.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not clean up the owned Windows Live Captions process: {ex.GetType().Name}.");
            }
        }

        public static bool KillLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
            return KillLiveCaptions(processId, expectedStartTimeUtc: null);
        }

        public static bool KillLiveCaptions(LiveCaptionsSession session)
        {
            return KillLiveCaptions(session.ProcessId, session.ProcessStartTimeUtc);
        }

        private static bool KillLiveCaptions(int processId, DateTime? expectedStartTimeUtc)
        {
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return true; }

            using (process)
            {
                if (process.HasExited)
                    return true;

                if (expectedStartTimeUtc.HasValue &&
                    !IsSameProcessInstance(expectedStartTimeUtc.Value, process.StartTime.ToUniversalTime()))
                    return true;

                process.Kill();
                return process.WaitForExit((int)ProcessExitTimeout.TotalMilliseconds);
            }
        }

        internal static bool ReleaseSession(
            LiveCaptionsSession session,
            Func<LiveCaptionsSession, bool> killOwnedProcess,
            Action<AutomationElement> restoreExistingWindow)
        {
            if (session.OwnsProcess)
                return killOwnedProcess(session);
            if (!session.WasHidden)
                restoreExistingWindow(session.Window);
            return true;
        }

        internal static void InitializeSessionOrRelease(
            LiveCaptionsSession session,
            Action<AutomationElement> initializeWindow,
            Action<LiveCaptionsSession> releaseSession)
        {
            try
            {
                initializeWindow(session.Window);
            }
            catch
            {
                try { releaseSession(session); }
                catch (Exception cleanupException)
                {
                    Debug.WriteLine($"Could not release the caption session after setup failure: {cleanupException.GetType().Name}.");
                }
                throw;
            }
        }

        internal static bool IsSameProcessInstance(DateTime expectedStartTimeUtc, DateTime actualStartTimeUtc) =>
            expectedStartTimeUtc.ToUniversalTime() == actualStartTimeUtc.ToUniversalTime();

        public static void HideLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_MINIMIZE);
            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);

            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle & ~WindowsAPI.WS_EX_TOOLWINDOW);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetForegroundWindow(hWnd);
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);

            RECT rect;
            if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                throw new InvalidOperationException("Unable to get the window rectangle of Live Captions.");

            int virtualLeft = WindowsAPI.GetSystemMetrics(WindowsAPI.SM_XVIRTUALSCREEN);
            int virtualTop = WindowsAPI.GetSystemMetrics(WindowsAPI.SM_YVIRTUALSCREEN);
            var virtualDesktop = new RECT
            {
                Left = virtualLeft,
                Top = virtualTop,
                Right = virtualLeft + WindowsAPI.GetSystemMetrics(WindowsAPI.SM_CXVIRTUALSCREEN),
                Bottom = virtualTop + WindowsAPI.GetSystemMetrics(WindowsAPI.SM_CYVIRTUALSCREEN)
            };

            if (!NeedsCaptionWindowCorrection(rect, virtualDesktop))
                return;

            int screenWidth = WindowsAPI.GetSystemMetrics(WindowsAPI.SM_CXSCREEN);
            int screenHeight = WindowsAPI.GetSystemMetrics(WindowsAPI.SM_CYSCREEN);
            int width = Math.Min(600, screenWidth);
            int height = Math.Min(200, screenHeight);
            int x = Math.Max(0, (screenWidth - width) / 2);
            int y = Math.Max(0, (screenHeight - height) / 2);
            if (width <= 0 || height <= 0 || !WindowsAPI.MoveWindow(hWnd, x, y, width, height, true))
                throw new Exception("Failed to fix LiveCaptions!");
        }

        internal static bool NeedsCaptionWindowCorrection(RECT windowBounds, RECT virtualDesktop)
        {
            int width = windowBounds.Right - windowBounds.Left;
            int height = windowBounds.Bottom - windowBounds.Top;
            int desktopWidth = virtualDesktop.Right - virtualDesktop.Left;
            int desktopHeight = virtualDesktop.Bottom - virtualDesktop.Top;
            if (width <= 0 || height <= 0 || desktopWidth <= 0 || desktopHeight <= 0)
                return true;

            return !WindowHandler.IsVisibleOnVirtualDesktop(
                new System.Windows.Rect(windowBounds.Left, windowBounds.Top, width, height),
                new System.Windows.Rect(virtualDesktop.Left, virtualDesktop.Top, desktopWidth, desktopHeight));
        }

        public static string GetCaptions(AutomationElement window)
        {
            if (captionsTextBlock == null)
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
            try
            {
                return captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
        }

        private static AutomationElement? FindWindowByPId(int processId)
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
        }

        public static AutomationElement? FindElementByAId(
            AutomationElement window, string automationId, CancellationToken token = default)
        {
            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        public static void PrintAllElementsAId(AutomationElement window)
        {
            var treeWalker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(window);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (!string.IsNullOrEmpty(element.Current.AutomationId))
                    Console.WriteLine(element.Current.AutomationId);

                var child = treeWalker.GetFirstChild(element);
                while (child != null)
                {
                    stack.Push(child);
                    child = treeWalker.GetNextSibling(child);
                }
            }
        }

        public static bool ClickSettingsButton(AutomationElement window)
        {
            var settingsButton = FindElementByAId(window, "SettingsButton");
            if (settingsButton != null)
            {
                var invokePattern = settingsButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (invokePattern != null)
                {
                    invokePattern.Invoke();
                    return true;
                }
            }
            return false;
        }

    }
}
