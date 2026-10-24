using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cais
{
    /// <summary>
    /// Janela transparente (em camadas) que desenha os ícones. Não rouba o foco, e o clique atravessa
    /// tanto as partes transparentes quanto a janela inteira quando o mouse está fora do Dock.
    /// </summary>
    internal class DockWindow : Window
    {
        public readonly Canvas Root = new Canvas();
        public IntPtr Handle { get; private set; }
        public Action<int, IntPtr, IntPtr> OnMessage;
        bool clickThrough;

        public DockWindow()
        {
            Title = "Cais";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            Focusable = false;
            AllowDrop = true;
            Left = 0; Top = 0; Width = 200; Height = 100;
            Content = Root;
            SourceInitialized += (s, e) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                long ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                HwndSource.FromHwnd(Handle).AddHook(Hook);
            };
        }

        public void EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            OnMessage?.Invoke(msg, wParam, lParam);
            return IntPtr.Zero;
        }

        public void SetClickThrough(bool on)
        {
            if (Handle == IntPtr.Zero || on == clickThrough) return;
            clickThrough = on;
            long ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
        }
    }

    /// <summary>
    /// Janela Win32 simples atrás do Dock que só aplica o vidro (desfoque/acrílico).
    /// Roda numa thread própria: redimensionar uma janela com vidro custa 1–3 ms, e assim isso não trava a animação.
    /// </summary>
    internal class BackdropWindow
    {
        const string ClassName = "CaisBackdrop";
        const uint WS_POPUP = 0x80000000;
        const uint WM_NCHITTEST = 0x84;
        const uint SWP_ASYNCWINDOWPOS = 0x4000;
        static Native.WndProc proc;

        public IntPtr Handle { get; private set; }
        /// <summary>Janela dos ícones: o vidro fica sempre logo atrás dela.</summary>
        public IntPtr Above;
        int lx = int.MinValue, ly, lw, lh, lr = -1, corner = -1;
        bool shown;

        public void Create()
        {
            using (var ready = new ManualResetEventSlim())
            {
                var thread = new Thread(() =>
                {
                    IntPtr instance = Native.GetModuleHandle(null);
                    if (proc == null)
                    {
                        proc = WndProc;
                        var wc = new Native.WNDCLASSEX
                        {
                            cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                            lpfnWndProc = proc,
                            hInstance = instance,
                            hbrBackground = Native.GetStockObject(4),   // preto = transparente sob o vidro
                            lpszClassName = ClassName,
                        };
                        Native.RegisterClassEx(ref wc);
                    }
                    uint ex = (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT);
                    Handle = Native.CreateWindowEx(ex, ClassName, "Cais (vidro)", WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                    ready.Set();
                    while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        Native.TranslateMessage(ref msg);
                        Native.DispatchMessage(ref msg);
                    }
                }) { IsBackground = true, Name = "Cais vidro" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait(3000);
            }
        }

        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_NCHITTEST) return new IntPtr(-1);     // HTTRANSPARENT
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// O vidro do sistema ignora o recorte por região, mas respeita o arredondamento nativo do Windows 11
        /// (fixo em 4 ou 8 px). Devolve quanto o vidro precisa ficar recuado para não passar da curva do Dock.
        /// </summary>
        public static int InsetFor(int radiusPx, double scale)
        {
            if (radiusPx <= 3) return 0;
            double native = radiusPx <= 6 ? 4 * scale : 8 * scale;
            return (int)Math.Ceiling(Math.Max(0, radiusPx - native) * (1 - 1 / Math.Sqrt(2))) + 1;
        }

        public void SetGlass(string mode, Color tint)
        {
            if (Handle == IntPtr.Zero) return;
            var state = mode == "Desfoque" ? Native.AccentState.BlurBehind
                      : mode == "Nenhum" ? Native.AccentState.Disabled
                      : Native.AccentState.AcrylicBlurBehind;
            uint abgr = (0x10u << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R;
            Native.SetAccent(Handle, state, abgr);
        }

        public void SetBounds(int x, int y, int w, int h, int radius)
        {
            if (Handle == IntPtr.Zero || w <= 0 || h <= 0) return;
            if (x == lx && y == ly && w == lw && h == lh && radius == lr) return;
            lx = x; ly = y; lw = w; lh = h; lr = radius;
            int pref = radius <= 3 ? 1 : radius <= 6 ? 3 : 2;    // sem arredondar / pequeno (4 px) / normal (8 px)
            if (pref != corner)
            {
                corner = pref;
                Native.DwmSetWindowAttribute(Handle, 33, ref pref, 4);
                int noBorder = unchecked((int)0xFFFFFFFE);
                Native.DwmSetWindowAttribute(Handle, 34, ref noBorder, 4);
            }
            // Reposiciona já garantindo a ordem: se o vidro (fundo preto por baixo do desfoque) passar
            // para a frente da janela dos ícones, o Dock aparece como um retângulo preto.
            uint flags = Native.SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | (Above == IntPtr.Zero ? Native.SWP_NOZORDER : 0);
            Native.SetWindowPos(Handle, Above, x, y, w, h, flags);
        }

        /// <summary>Coloca o vidro logo atrás da janela dos ícones.</summary>
        public void PlaceBelow(IntPtr above)
        {
            if (Handle == IntPtr.Zero) return;
            Native.SetWindowPos(Handle, above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }

        public void SetShown(bool show)
        {
            if (Handle == IntPtr.Zero || show == shown) return;
            shown = show;
            Native.ShowWindowAsync(Handle, show ? Native.SW_SHOWNA : Native.SW_HIDE);
            if (show && Above != IntPtr.Zero) PlaceBelow(Above);
        }
    }
}
