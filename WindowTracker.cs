using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cais
{
    internal class TrackedApp
    {
        public string ExePath;
        public readonly List<IntPtr> Windows = new List<IntPtr>();
    }

    /// <summary>Descobre quais apps têm janela aberta (a mesma lógica do Alt+Tab).</summary>
    internal static class WindowTracker
    {
        static readonly Dictionary<uint, string> pidPath = new Dictionary<uint, string>();
        static readonly HashSet<string> ignoredClasses = new HashSet<string>
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "Xaml_WindowedPopupClass", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
            "MultitaskingViewFrame", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd", "ForegroundStaging"
        };
        static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

        /// <summary>Apps com janela, na ordem Z (a janela mais à frente primeiro).</summary>
        public static Dictionary<string, TrackedApp> Snapshot()
        {
            var result = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase);
            var alive = new HashSet<uint>();
            Native.EnumWindows((h, l) =>
            {
                if (!IsAppWindow(h)) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == ownPid) return true;
                if (Native.ClassOf(h) == "ApplicationFrameWindow")
                {
                    pid = FrameChildPid(h, pid);
                    if (pid == 0) return true;
                }
                string path = PathOf(pid);
                if (string.IsNullOrEmpty(path)) return true;
                alive.Add(pid);
                if (!result.TryGetValue(path, out var app))
                {
                    app = new TrackedApp { ExePath = path };
                    result[path] = app;
                }
                app.Windows.Add(h);
                return true;
            }, IntPtr.Zero);

            var dead = new List<uint>();
            foreach (var pid in pidPath.Keys) if (!alive.Contains(pid)) dead.Add(pid);
            foreach (var pid in dead) pidPath.Remove(pid);
            return result;
        }

        public static bool IsAppWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_APPWINDOW) == 0)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
                if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
            }
            if (Native.DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return false;
            if (Native.GetWindowTextLength(h) == 0) return false;
            return !ignoredClasses.Contains(Native.ClassOf(h));
        }

        static string PathOf(uint pid)
        {
            if (pidPath.TryGetValue(pid, out var p)) return p;
            p = Native.GetProcessPath(pid);
            pidPath[pid] = p;
            return p;
        }

        // Apps da loja ficam dentro do ApplicationFrameHost; o processo real está na janela filha.
        static uint FrameChildPid(IntPtr frame, uint framePid)
        {
            uint found = 0;
            Native.EnumChildWindows(frame, (c, l) =>
            {
                Native.GetWindowThreadProcessId(c, out uint pid);
                if (pid != framePid && Native.ClassOf(c) == "Windows.UI.Core.CoreWindow") { found = pid; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
