using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Cais
{
    /// <summary>
    /// A "aba" com as janelas de um app: um cartão por janela com título e miniatura ao vivo
    /// (as mesmas miniaturas do DWM que a barra de tarefas usa). Clique para ir à janela; X fecha.
    /// </summary>
    internal class PreviewWindow : Window
    {
        const double MaxThumbW = 210, MaxThumbH = 130, MinCardW = 170;

        class Card
        {
            public IntPtr Window, Thumb;
            public FrameworkElement Slot;
            public bool Iconic;
        }

        static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static readonly bool Acrylic = Environment.OSVersion.Version.Build >= 22621;   // fundo acrílico nativo do Windows 11

        readonly StackPanel panel = new StackPanel();
        readonly Border root;
        readonly List<Card> cards = new List<Card>();
        Brush fg, muted, hoverBg;
        double scale = 1;

        public IntPtr Handle { get; private set; }
        public DockEntry Entry { get; private set; }
        public bool IsShown { get; private set; }
        public Action<IntPtr> OnPick, OnCloseWindow;

        public PreviewWindow()
        {
            Title = "Cais (janelas)";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            SizeToContent = SizeToContent.Manual;
            Background = Brushes.Transparent;
            root = new Border { Padding = new Thickness(6), Child = panel };
            Content = root;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SourceInitialized += (s, e) =>
            {
                Handle = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(Handle);
                source.AddHook(Hook);
                long ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                int round = 2;
                Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
                if (Acrylic)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                    var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    Native.DwmExtendFrameIntoClientArea(Handle, ref m);
                    int transient = 3;   // DWMSBT_TRANSIENTWINDOW: acrílico dos menus/flyouts
                    Native.DwmSetWindowAttribute(Handle, 38, ref transient, 4);
                }
            };
            LayoutUpdated += (s, e) => UpdateThumbs();
        }

        public void EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
            return IntPtr.Zero;
        }

        public void ApplyTheme(bool dark, double dpiScale)
        {
            scale = dpiScale;
            int d = dark ? 1 : 0;
            if (Handle != IntPtr.Zero) Native.DwmSetWindowAttribute(Handle, 20, ref d, 4);
            fg = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF3, 0xF6) : Color.FromRgb(0x1B, 0x1D, 0x25));
            muted = new SolidColorBrush(dark ? Color.FromRgb(0xB4, 0xB8, 0xC4) : Color.FromRgb(0x5B, 0x61, 0x72));
            hoverBg = new SolidColorBrush(dark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0));
            if (!Acrylic) Background = new SolidColorBrush(dark ? Color.FromRgb(0x2B, 0x2C, 0x34) : Color.FromRgb(0xF6, 0xF7, 0xFA));
        }

        /// <summary>Monta os cartões e devolve o tamanho da aba (em DIP).</summary>
        public Size Build(DockEntry entry, IList<IntPtr> windows, ImageSource icon, bool horizontal)
        {
            ClearCards();
            Entry = entry;
            panel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
            foreach (var w in windows) AddCard(w, icon);
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return root.DesiredSize;
        }

        void AddCard(IntPtr window, ImageSource icon)
        {
            var card = new Card { Window = window };
            if (Native.DwmRegisterThumbnail(Handle, window, out card.Thumb) != 0) card.Thumb = IntPtr.Zero;

            // tamanho da miniatura mantendo a proporção da janela
            double tw = MaxThumbW, th = MaxThumbH * 0.85;
            card.Iconic = Native.IsIconic(window);
            if (card.Thumb != IntPtr.Zero && !card.Iconic && Native.DwmQueryThumbnailSourceSize(card.Thumb, out var src) == 0 && src.cx > 50 && src.cy > 50)
            {
                double k = Math.Min(MaxThumbW / src.cx, MaxThumbH / src.cy);
                tw = Math.Round(src.cx * k);
                th = Math.Round(src.cy * k);
            }
            else card.Iconic = true;

            var title = new TextBlock
            {
                Text = Native.TitleOf(window), FontFamily = UiFont, FontSize = 12, Foreground = fg,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0),
            };
            var appIcon = new Image { Source = icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(appIcon, BitmapScalingMode.HighQuality);
            var close = new Button
            {
                Content = "", FontFamily = IconFont, FontSize = 9, Width = 24, Height = 24, Foreground = fg,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Visibility = Visibility.Hidden,
                Focusable = false, ToolTip = "Fechar janela",
            };
            close.Template = CloseTemplate();
            close.Click += (s, e) => { e.Handled = true; OnCloseWindow?.Invoke(window); RemoveCard(card); };

            var header = new DockPanel { Height = 24, LastChildFill = true };
            DockPanel.SetDock(appIcon, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(appIcon);
            header.Children.Add(close);
            header.Children.Add(title);

            FrameworkElement slot;
            if (card.Iconic)
            {
                // janela minimizada: o Windows não tem imagem dela, então mostra o ícone
                var big = new Image { Source = icon, Width = 44, Height = 44, Opacity = 0.9 };
                RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.HighQuality);
                var label = new TextBlock { Text = "Minimizada", FontFamily = UiFont, FontSize = 11, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(big);
                stack.Children.Add(label);
                slot = new Border { Width = tw, Height = th, Child = stack };
                if (card.Thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(card.Thumb); card.Thumb = IntPtr.Zero; }
            }
            else slot = new Border { Width = tw, Height = th };
            slot.Margin = new Thickness(0, 6, 0, 0);
            card.Slot = slot;

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(slot);
            var box = new Border
            {
                Child = body, Padding = new Thickness(8), Margin = new Thickness(2), CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, MinWidth = MinCardW, Width = Math.Max(MinCardW, tw + 16),
            };
            box.MouseEnter += (s, e) => { box.Background = hoverBg; close.Visibility = Visibility.Visible; };
            box.MouseLeave += (s, e) => { box.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
            box.MouseUp += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) OnPick?.Invoke(window);
                else if (e.ChangedButton == MouseButton.Middle) { OnCloseWindow?.Invoke(window); RemoveCard(card); }
            };
            panel.Children.Add(box);
            cards.Add(card);
        }

        static ControlTemplate CloseTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var b = new FrameworkElementFactory(typeof(Border));
            b.Name = "b";
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var c = new FrameworkElementFactory(typeof(ContentPresenter));
            c.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            c.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(c);
            t.VisualTree = b;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)), "b"));
            hover.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
            t.Triggers.Add(hover);
            return t;
        }

        void RemoveCard(Card card)
        {
            if (card.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(card.Thumb);
            int i = cards.IndexOf(card);
            if (i >= 0) { cards.RemoveAt(i); panel.Children.RemoveAt(i); }
            if (cards.Count == 0) HidePreview();
            else Resize();
        }

        void Resize()
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = root.DesiredSize;
            Native.GetWindowRect(Handle, out var r);
            int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale);
            // mantém o centro (horizontal) e a base no lugar
            int x = r.Left + (r.Width - w) / 2, y = r.Bottom - h;
            Width = size.Width; Height = size.Height;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        }

        /// <summary>Mostra a aba já montada na posição indicada (pixels de tela).</summary>
        public void ShowAt(int x, int y, Size size)
        {
            Width = size.Width;
            Height = size.Height;
            int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale);
            Left = x / scale;   // já nasce no lugar certo (sem piscar no canto da tela)
            Top = y / scale;
            if (!IsVisible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
            Native.ShowWindow(Handle, Native.SW_SHOWNA);
            IsShown = true;
            UpdateLayout();
            UpdateThumbs();
        }

        public void HidePreview()
        {
            if (!IsShown && cards.Count == 0) return;
            ClearCards();
            Entry = null;
            IsShown = false;
            if (Handle != IntPtr.Zero) Native.ShowWindow(Handle, Native.SW_HIDE);
        }

        public bool ContainsCursor()
        {
            if (!IsShown || Handle == IntPtr.Zero) return false;
            Native.GetCursorPos(out var p);
            Native.GetWindowRect(Handle, out var r);
            return p.X >= r.Left - 2 && p.X < r.Right + 2 && p.Y >= r.Top - 2 && p.Y < r.Bottom + 2;
        }

        public IEnumerable<IntPtr> Windows => cards.Select(c => c.Window);

        void ClearCards()
        {
            foreach (var c in cards) if (c.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(c.Thumb);
            cards.Clear();
            panel.Children.Clear();
        }

        /// <summary>Posiciona cada miniatura do DWM em cima do espaço reservado no cartão.</summary>
        void UpdateThumbs()
        {
            if (!IsShown) return;
            foreach (var c in cards)
            {
                if (c.Thumb == IntPtr.Zero || !c.Slot.IsVisible) continue;
                Point p;
                try { p = c.Slot.TranslatePoint(new Point(0, 0), root); }
                catch (InvalidOperationException) { continue; }
                var props = new Native.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 | 0x4 | 0x8 | 0x10,   // destino, opacidade, visível, janela inteira
                    rcDestination = new Native.RECT
                    {
                        Left = (int)Math.Round(p.X * scale), Top = (int)Math.Round(p.Y * scale),
                        Right = (int)Math.Round((p.X + c.Slot.ActualWidth) * scale), Bottom = (int)Math.Round((p.Y + c.Slot.ActualHeight) * scale),
                    },
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false,
                };
                Native.DwmUpdateThumbnailProperties(c.Thumb, ref props);
            }
        }
    }
}
