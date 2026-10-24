using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Cais
{
    /// <summary>Controla a barra de tarefas do Windows (ocultar automaticamente, esconder e mostrar).</summary>
    internal static class Taskbar
    {
        const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

        public static IEnumerable<IntPtr> Windows()
        {
            var main = Native.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) yield return main;
            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
        }

        static Native.APPBARDATA Data() => new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)),
            hWnd = Native.FindWindow("Shell_TrayWnd", null)
        };

        public static bool GetAutoHide()
        {
            var d = Data();
            return (Native.SHAppBarMessage(ABM_GETSTATE, ref d).ToInt64() & ABS_AUTOHIDE) != 0;
        }

        public static void SetAutoHide(bool on)
        {
            if (GetAutoHide() == on) return;
            var d = Data();
            d.lParam = new IntPtr(on ? ABS_AUTOHIDE : ABS_ALWAYSONTOP);
            Native.SHAppBarMessage(ABM_SETSTATE, ref d);
        }

        static bool IsOn(IntPtr taskbar, IntPtr monitor) => Native.MonitorFromWindow(taskbar, 2) == monitor;   // 2 = mais próximo

        /// <summary>A barra do monitor indicado está aparecendo?</summary>
        public static bool VisibleOn(IntPtr monitor)
        {
            foreach (var h in Windows()) if (IsOn(h, monitor) && Native.IsWindowVisible(h)) return true;
            return false;
        }

        /// <summary>Mostra todas as barras.</summary>
        public static void SetVisible(bool visible) => HideOnly(visible ? IntPtr.Zero : new IntPtr(-1));

        /// <summary>Esconde só a barra do monitor indicado e mostra as dos outros monitores.</summary>
        public static void HideOnly(IntPtr monitor)
        {
            foreach (var h in Windows())
            {
                bool show = monitor != new IntPtr(-1) && !IsOn(h, monitor);
                if (Native.IsWindowVisible(h) != show)
                    Native.ShowWindow(h, show ? Native.SW_SHOWNA : Native.SW_HIDE);
            }
        }
    }
}
