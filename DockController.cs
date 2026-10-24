using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Cais
{
    internal enum EntryKind { Start, App, Separator, Downloads, Trash, Status }

    internal class DockEntry
    {
        public EntryKind Kind;
        public string Id;
        public PinnedApp Pin;
        public string ExePath;
        public string Name;
        public readonly List<IntPtr> Windows = new List<IntPtr>();
        public bool IsRunning => Windows.Count > 0;
        public FrameworkElement Visual;
        public Ellipse Dot;
        public double Size, BaseLen;
        public double BounceT = -1;
        public Rect Bounds = Rect.Empty, HitRect = Rect.Empty;
        public bool IsIcon => Kind != EntryKind.Separator && Kind != EntryKind.Status;
    }

    internal class DockController
    {
        static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static readonly CultureInfo PtBr = new CultureInfo("pt-BR");

        public DockConfig Config { get; private set; }
        public bool IsDark { get; private set; }

        DockWindow win;
        BackdropWindow back;
        SettingsWindow settings;
        readonly Border bg = new Border { BorderThickness = new Thickness(1) };
        readonly Border rim = new Border();
        bool rimDirty = true;

        // Sombra: uma imagem desfocada renderizada uma vez e só esticada a cada quadro.
        // (Um DropShadowEffect ao vivo derrubava a animação de ~140 para ~75 quadros por segundo.)
        readonly Image shadow = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill, RenderTransform = new MatrixTransform() };
        double shadowW = -1, shadowH, shadowCorner;
        const double ShadowBlur = 16, ShadowDrop = 5;

        void EnsureShadow(double w, double h, double corner)
        {
            if (Math.Abs(w - shadowW) < 0.5 && Math.Abs(h - shadowH) < 0.5 && corner == shadowCorner) return;
            shadowW = w; shadowH = h; shadowCorner = corner;
            int pw = (int)Math.Ceiling(w + 2 * ShadowBlur), ph = (int)Math.Ceiling(h + 2 * ShadowBlur + ShadowDrop);
            var drawing = new DrawingVisual { Effect = new BlurEffect { Radius = ShadowBlur, KernelType = KernelType.Gaussian } };
            using (var dc = drawing.RenderOpen())
            {
                var brush = new SolidColorBrush(Color.FromArgb((byte)(IsDark ? 130 : 70), 0, 0, 0));
                dc.DrawRoundedRectangle(brush, null, new Rect(ShadowBlur, ShadowBlur + ShadowDrop, w, h), corner, corner);
            }
            var host = new ContainerVisual();
            host.Children.Add(drawing);
            var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            bmp.Freeze();
            shadow.Source = bmp;
            shadow.Width = pw;
            shadow.Height = ph;
        }

        // Não desenha mais quadros do que o monitor do Dock consegue mostrar
        int monitorHz = 60;
        double minFrame;
        readonly Border tip = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 6), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        readonly TextBlock tipText = new TextBlock { FontSize = 12.5, FontFamily = UiFont };

        readonly List<DockEntry> entries = new List<DockEntry>();
        readonly Dictionary<string, DockEntry> pool = new Dictionary<string, DockEntry>();
        Dictionary<string, TrackedApp> tracked = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> runningOrder = new List<string>();
        readonly Dictionary<string, string> resolveCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        DispatcherTimer pollTimer, slowTimer, saveTimer, refreshTimer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        bool looping, menuOpen, fullscreen, windowsHidden, cleaned;
        double lastT, hover, hideAmt, lastInsideT = -10, mouseU;
        DockEntry pressed;

        MonitorInfo mon;
        double scale = 1, winW, winH;
        Native.RECT strip;
        double bgU0, bgU1, bgV0;
        Rect tipRect;

        uint appBarMsg, taskbarCreatedMsg;
        bool appBarOn;
        string appliedTaskbar, appliedGeometry, appliedStartup;
        Native.WinEventDelegate hookProc;
        readonly List<IntPtr> hooks = new List<IntPtr>();
        int slowTicks;

        Brush indicatorBrush, sepBrush, fgBrush, mutedBrush;
        FrameworkElement statusIcons;
        TextBlock statusTime, statusDate, glyphNet, glyphVol, glyphBat;

        // Tamanho efetivo: encolhe sozinho quando há apps demais para caber na tela.
        double iconS = 52;

        bool Horizontal => Config.Position != "Esquerda" && Config.Position != "Direita";
        double Pad => Math.Max(5, Math.Round(iconS * 0.18));
        double Gap => Math.Max(4, Math.Round(iconS * 0.12));
        double Cross => iconS + 2 * Pad;

        public DockController()
        {
            tip.Child = tipText;
            tip.RenderTransform = new MatrixTransform();
            bg.Child = rim;
        }

        // ───────────────────────── medição de desempenho (--bench) ─────────────────────────
        // Simula o mouse passando pelo Dock por alguns segundos e registra quadros e CPU em perf.log.

        const double BenchSeconds = 6;
        double benchFrom, benchLastT, benchWorst, benchTickMs, benchBackMs, baseCenter, baseHalf;
        int benchFrames;
        TimeSpan benchCpu;
        bool benchRunning;

        void BenchFrame(double t, double tickMs)
        {
            if (!benchRunning)
            {
                benchRunning = true;
                benchCpu = Process.GetCurrentProcess().TotalProcessorTime;
                benchLastT = t;
                return;
            }
            benchFrames++;
            benchWorst = Math.Max(benchWorst, t - benchLastT);
            benchLastT = t;
            benchTickMs += tickMs;
        }

        void BenchFinish()
        {
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - benchCpu).TotalSeconds / BenchSeconds * 100;
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:s}  monitor={1}Hz  fps={2:0.0}  pior_quadro={3:0}ms  tick_medio={4:0.00}ms  vidro_medio={7:0.00}ms  cpu={5:0.0}% de um núcleo{6}",
                DateTime.Now, monitorHz, benchFrames / BenchSeconds,
                benchWorst * 1000, benchTickMs / Math.Max(1, benchFrames), cpu, Environment.NewLine,
                benchBackMs / Math.Max(1, benchFrames));
            try { File.AppendAllText(Path.Combine(DockConfig.Folder, "perf.log"), line); } catch { }
            benchFrom = 0;
            Exit();
        }

        // ───────────────────────── início e fim ─────────────────────────

        public void Start(string[] args)
        {
            Config = DockConfig.Load();
            weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
            weatherTimer.Tick += (s, e) => RefreshWeather();
            weatherTimer.Start();
            appBarMsg = Native.RegisterWindowMessage("Cais.AppBar");
            taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");

            back = new BackdropWindow();
            back.Create();
            win = new DockWindow();
            win.EnsureHandle();
            win.OnMessage = HandleMessage;
            win.PreviewMouseDown += OnMouseDown;
            win.PreviewMouseUp += OnMouseUp;
            win.DragEnter += OnDragOver;
            win.DragOver += OnDragOver;
            win.Drop += OnDrop;

            ApplyConfigCore();
            win.Show();
            PlaceWindows();
            back.Above = win.Handle;
            back.PlaceBelow(win.Handle);

            if (args.Contains("--bench"))
            {
                benchFrom = clock.Elapsed.TotalSeconds + 2;
                var kick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                kick.Tick += (s, e) => { kick.Stop(); StartLoop(); };
                kick.Start();
            }

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            pollTimer.Tick += Poll;
            pollTimer.Start();
            slowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            slowTimer.Tick += SlowTick;
            slowTimer.Start();
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Config.Save(); };
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); RefreshWindows(); UpdateFullscreen(); };

            hookProc = OnWinEvent;
            hooks.Add(Native.SetWinEventHook(3, 3, IntPtr.Zero, hookProc, 0, 0, 2));                 // primeiro plano
            hooks.Add(Native.SetWinEventHook(0x8001, 0x8003, IntPtr.Zero, hookProc, 0, 0, 2));       // criar/mostrar/esconder
            hooks.Add(Native.SetWinEventHook(0x16, 0x17, IntPtr.Zero, hookProc, 0, 0, 2));           // minimizar/restaurar

            RefreshWindows();
            if (Config.FirstRun || args.Contains("--config"))
            {
                Config.FirstRun = false;
                ScheduleSave();
                ShowSettings();
            }
            StartLoop();
        }

        public void Exit()
        {
            Cleanup();
            Application.Current.Shutdown();
        }

        // ───────────────────────── capturas (--snapshot) ─────────────────────────
        // Renderiza o Dock e as configurações em PNG sem mostrar nada na tela.
        // Usado para conferir o visual e gerar as imagens do README.

        bool snapshot;

        bool demo;

        public void Snapshot(string dir, bool demoApps)
        {
            snapshot = true;
            demo = demoApps;
            Directory.CreateDirectory(dir);
            back = new BackdropWindow();
            win = new DockWindow();
            win.EnsureHandle();
            if (!demo)
            {
                tracked = WindowTracker.Snapshot();
                runningOrder.AddRange(tracked.Keys);
            }

            ResetSnapshotConfig();
            RenderDock(Path.Combine(dir, "dock-escuro.png"), -1);
            RenderDock(Path.Combine(dir, "dock-escuro-hover.png"), 4);
            Config.Theme = "Claro";
            ApplySnapshotConfig();
            RenderDock(Path.Combine(dir, "dock-claro-hover.png"), 7);
            Config.Theme = "Escuro";
            Config.Position = "Esquerda";
            ApplySnapshotConfig();
            RenderDock(Path.Combine(dir, "dock-esquerda-hover.png"), 3);

            ResetSnapshotConfig();
            RenderSettings(dir);
            RenderPicker(Path.Combine(dir, "adicionar-apps.png"));
        }

        void ResetSnapshotConfig()
        {
            Config = DockConfig.Load();
            if (demo)
            {
                Config.Apps = DemoApps();
                Config.ShowRunning = false;
                Config.IndicatorColor = "#4CC2FF";
                Config.ShowWeather = true;
                weather = new WeatherInfo { Temperature = 24, Sky = Sky.PartlyCloudy, IsDay = true, Description = "Parcialmente nublado", Place = "São Paulo" };
            }
            ApplySnapshotConfig();
        }

        void ApplySnapshotConfig()
        {
            ApplyConfigCore();
            if (!demo) return;
            // No modo demonstração, os três primeiros apps aparecem como abertos
            foreach (var e in entries.Where(x => x.Kind == EntryKind.App).Take(3)) e.Windows.Add(new IntPtr(1));
        }

        static List<PinnedApp> DemoApps()
        {
            var list = new List<PinnedApp>();
            void Add(string name, string path)
            {
                if (ShellIcons.Get(path) != null) list.Add(new PinnedApp { Name = name, Path = path });
            }
            Add("Explorador de Arquivos", DockConfig.ExplorerPath);
            Add("Microsoft Edge", AppsFolderPrefix + "MSEdge");
            Add("Terminal", AppsFolderPrefix + "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");
            Add("Bloco de Notas", AppsFolderPrefix + "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
            Add("Calculadora", AppsFolderPrefix + "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
            Add("Fotos", AppsFolderPrefix + "Microsoft.Windows.Photos_8wekyb3d8bbwe!App");
            Add("Paint", AppsFolderPrefix + "Microsoft.Paint_8wekyb3d8bbwe!App");
            Add("Relógio", AppsFolderPrefix + "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App");
            Add("Microsoft Store", AppsFolderPrefix + "Microsoft.WindowsStore_8wekyb3d8bbwe!App");
            Add("Configurações", AppsFolderPrefix + "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel");
            return list;
        }

        void RenderPicker(string file)
        {
            var picker = new AppPicker(this);
            picker.LoadNow();
            var content = (FrameworkElement)picker.Content;
            picker.Content = null;
            RenderPage(content, 560, 680, file);
            picker.Close();
        }

        static void RenderPage(FrameworkElement content, double w, double h, string file)
        {
            var host = new Border { Child = content, Width = w, Height = h, Background = (Brush)Application.Current.Resources["WinBg"] };
            TextElement.SetFontFamily(host, UiFont);
            TextElement.SetForeground(host, (Brush)Application.Current.Resources["TextPrimary"]);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            for (int i = 0; i < 3; i++)
            {
                host.Measure(new Size(w, h));
                host.Arrange(new Rect(0, 0, w, h));
                host.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
            }
            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            SavePng(rtb, file);
            host.Child = null;
        }

        void RenderDock(string file, int hoverIndex)
        {
            hover = 0;
            hideAmt = 0;
            Layout(0);
            if (hoverIndex >= 0)
            {
                var icons = entries.Where(e => e.IsIcon).ToList();
                var b = icons[Math.Min(hoverIndex, icons.Count - 1)].Bounds;
                mouseU = (Horizontal ? b.X + b.Width / 2 : b.Y + b.Height / 2) + 6;
                hover = 1;
            }
            var root = win.Root;
            for (int i = 0; i < 2; i++)
            {
                Layout(0);
                root.Measure(new Size(winW, winH));
                root.Arrange(new Rect(0, 0, winW, winH));
                root.UpdateLayout();
            }

            int w = (int)Math.Ceiling(winW), h = (int)Math.Ceiling(winH);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) DrawWallpaper(dc, w, h);
            rtb.Render(dv);
            rtb.Render(root);

            var area = new Rect(Canvas.GetLeft(bg), Canvas.GetTop(bg), bg.Width, bg.Height);
            if (tip.Visibility == Visibility.Visible)
                area.Union(tipRect);
            foreach (var e in entries) if (e.IsIcon) area.Union(e.Bounds);
            area.Inflate(48, 40);
            area.Intersect(new Rect(0, 0, w, h));
            var crop = new CroppedBitmap(rtb, new Int32Rect((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height));
            SavePng(crop, file);
        }

        static void DrawWallpaper(DrawingContext dc, double w, double h)
        {
            var sky = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x2A, 0x2E, 0x6B), 0),
                    new GradientStop(Color.FromRgb(0x8B, 0x5A, 0x8C), 0.55),
                    new GradientStop(Color.FromRgb(0xE2, 0x9A, 0x74), 1),
                }, new Point(0, 0), new Point(1, 0.4));
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new Point(0, h * 0.55), true, true);
                g.BezierTo(new Point(w * 0.2, h * 0.3), new Point(w * 0.35, h * 0.7), new Point(w * 0.55, h * 0.45), true, true);
                g.BezierTo(new Point(w * 0.75, h * 0.2), new Point(w * 0.85, h * 0.6), new Point(w, h * 0.4), true, true);
                g.LineTo(new Point(w, h), true, false);
                g.LineTo(new Point(0, h), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x25, 0x1F, 0x47)), null, hills);
        }

        void RenderSettings(string dir)
        {
            var sw = new SettingsWindow(this);
            var content = (FrameworkElement)sw.Content;
            sw.Content = null;
            var host = new Border { Child = content, Width = 1000, Background = (Brush)Application.Current.Resources["WinBg"] };
            TextElement.SetFontFamily(host, UiFont);
            TextElement.SetForeground(host, (Brush)Application.Current.Resources["TextPrimary"]);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            string[] names = { "config-aparencia.png", "config-comportamento.png", "config-apps.png" };
            for (int i = 0; i < names.Length; i++)
            {
                sw.SelectPage(i);
                host.Height = double.NaN;
                host.Measure(new Size(1000, double.PositiveInfinity));
                double hgt = Math.Max(720, host.DesiredSize.Height);
                host.Height = hgt;
                host.Measure(new Size(1000, hgt));
                host.Arrange(new Rect(0, 0, 1000, hgt));
                host.UpdateLayout();
                var rtb = new RenderTargetBitmap(1000, (int)Math.Ceiling(hgt), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);
                SavePng(rtb, Path.Combine(dir, names[i]));
            }
            sw.Close();
        }

        static void SavePng(BitmapSource bmp, string file)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        public void Cleanup()
        {
            if (cleaned) return;
            cleaned = true;
            try { saveTimer?.Stop(); Config?.Save(); } catch { }
            try { RemoveAppBar(); } catch { }
            try { Taskbar.SetVisible(true); } catch { }
            foreach (var h in hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            hooks.Clear();
        }

        // ───────────────────────── configuração ─────────────────────────

        public void ApplyConfig()
        {
            ApplyConfigCore();
            ScheduleSave();
        }

        void ScheduleSave()
        {
            if (saveTimer == null) return;
            saveTimer.Stop();
            saveTimer.Start();
        }

        void ApplyConfigCore()
        {
            Config.Normalize();
            IsDark = Palette.IsDark(Config);
            Palette.ApplyBrushes(Application.Current.Resources, IsDark);
            settings?.ApplyTitleBar();

            var tint = IsDark ? Color.FromRgb(24, 26, 34) : Color.FromRgb(246, 247, 250);
            ApplyTint();
            rimDirty = true;   // recalcula a borda interna no próximo quadro
            bg.BorderBrush = Frozen(new SolidColorBrush(IsDark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0xB4, 255, 255, 255)));
            shadowW = -1;   // a sombra (imagem pré-renderizada) é refeita no próximo quadro
            tip.Background = Frozen(new SolidColorBrush(IsDark ? Color.FromArgb(0xEB, 28, 30, 38) : Color.FromArgb(0xF5, 255, 255, 255)));
            tip.BorderBrush = Frozen(new SolidColorBrush(IsDark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0x1A, 20, 24, 40)));
            tip.BorderThickness = new Thickness(1);
            fgBrush = Frozen(new SolidColorBrush(IsDark ? Color.FromRgb(0xF4, 0xF5, 0xF8) : Color.FromRgb(0x1F, 0x23, 0x30)));
            mutedBrush = Frozen(new SolidColorBrush(IsDark ? Color.FromRgb(0xC9, 0xCC, 0xD6) : Color.FromRgb(0x4A, 0x50, 0x60)));
            tipText.Foreground = fgBrush;
            sepBrush = Frozen(new SolidColorBrush(IsDark ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromArgb(0x30, 20, 24, 40)));
            var ind = Palette.Parse(Config.IndicatorColor, Color.FromRgb(0x4C, 0xC2, 0xFF));
            indicatorBrush = Frozen(new SolidColorBrush(ind));

            back.SetGlass(Config.Glass, tint);

            string geometry = string.Join("|", Config.IconSize, Config.Magnification, Config.Position, Config.Monitor, Config.EdgeMargin, Config.ReserveSpace, Config.AutoHide);
            bool moved = geometry != appliedGeometry;
            if (moved)
            {
                appliedGeometry = geometry;
                PlaceWindows();
                UpdateAppBar();
            }

            foreach (var e in pool.Values) RemoveVisual(e);
            RebuildEntries(true);

            ApplyTaskbarMode(moved);   // o Dock pode ter mudado de monitor
            string weatherKey = Config.ShowWeather + "|" + Config.WeatherCity;
            if (weatherKey != appliedWeather) { appliedWeather = weatherKey; RefreshWeather(); }
            string startup = Config.StartWithWindows ? "on" : "off";
            if (!snapshot && startup != appliedStartup) { appliedStartup = startup; SetStartup(Config.StartWithWindows); }
            StartLoop();
        }

        static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

        void ApplyTaskbarMode(bool force)
        {
            if (snapshot || (!force && appliedTaskbar == Config.TaskbarMode)) return;
            appliedTaskbar = Config.TaskbarMode;
            try
            {
                switch (Config.TaskbarMode)
                {
                    case "Esconder": Taskbar.SetAutoHide(true); Taskbar.HideOnly(mon?.Handle ?? IntPtr.Zero); break;
                    case "AutoOcultar": Taskbar.SetVisible(true); Taskbar.SetAutoHide(true); break;
                    default: Taskbar.SetVisible(true); Taskbar.SetAutoHide(false); break;
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("Cais", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue("Cais", false);
                }
            }
            catch (Exception ex) { App.Log(ex); }
        }

        public List<MonitorInfo> Monitors() => MonitorInfo.All();

        // ───────────────────────── janelas e posição ─────────────────────────

        void PlaceWindows()
        {
            var mons = MonitorInfo.All();
            if (mons.Count == 0) return;
            mon = Config.Monitor < mons.Count ? mons[Config.Monitor] : mons[0];
            scale = mon.Scale > 0 ? mon.Scale : 1;
            iconS = Config.IconSize;
            monitorHz = Math.Max(30, mon.Hz);
            minFrame = 0.7 / monitorHz;   // folga: os quadros do WPF chegam no ritmo do monitor principal
            double mag = Math.Max(1, Config.Magnification);
            double crossDip = Config.EdgeMargin + Cross + Config.IconSize * (mag - 1) + (Horizontal ? 54 : 250) + 12;
            int c = (int)Math.Ceiling(crossDip * scale);
            var b = mon.Bounds;
            Native.RECT r;
            switch (Config.Position)
            {
                case "Esquerda": r = new Native.RECT { Left = b.Left, Top = b.Top, Right = b.Left + c, Bottom = b.Bottom }; break;
                case "Direita": r = new Native.RECT { Left = b.Right - c, Top = b.Top, Right = b.Right, Bottom = b.Bottom }; break;
                default: r = new Native.RECT { Left = b.Left, Top = b.Bottom - c, Right = b.Right, Bottom = b.Bottom }; break;
            }
            strip = r;
            winW = r.Width / scale;
            winH = r.Height / scale;
            win.Root.Width = winW;
            win.Root.Height = winH;
            Native.SetWindowPos(win.Handle, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
        }

        void ShowWindows(bool show)
        {
            if (show == !windowsHidden) return;
            windowsHidden = !show;
            Native.ShowWindow(win.Handle, show ? Native.SW_SHOWNA : Native.SW_HIDE);
            if (!show) back.SetShown(false);
            else
            {
                // volta para cima de tudo (alguns jogos se marcam como "sempre no topo")
                Native.SetWindowPos(win.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                back.PlaceBelow(win.Handle);
            }
        }

        void ReassertTopmost()
        {
            if (menuOpen || windowsHidden) return;
            const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            Native.SetWindowPos(win.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, f);
            back.PlaceBelow(win.Handle);
        }

        void HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == appBarMsg)
            {
                int code = wParam.ToInt32();
                if (code == 1) SetAppBarPos();                              // ABN_POSCHANGED
                else if (code == 2) UpdateFullscreen();                     // ABN_FULLSCREENAPP
            }
            else if (msg == taskbarCreatedMsg)
            {
                win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyTaskbarMode(true);
                    if (appBarOn) { appBarOn = false; UpdateAppBar(); }
                }), DispatcherPriority.Background);
            }
            else if (msg == Native.WM_DISPLAYCHANGE || msg == Native.WM_DPICHANGED)
            {
                // Jogos em tela cheia exclusiva trocam a resolução ao entrar/sair; só reposiciona se o monitor do Dock mudou mesmo
                win.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var before = mon?.Bounds;
                    double beforeScale = scale;
                    var mons = MonitorInfo.All();
                    var now = Config.Monitor < mons.Count ? mons[Config.Monitor] : mons.FirstOrDefault();
                    if (now == null || (before.HasValue && SameRect(before.Value, now.Bounds) && Math.Abs(beforeScale - now.Scale) < 0.01)) return;
                    PlaceWindows();
                    SetAppBarPos();
                    StartLoop();
                }), DispatcherPriority.Background);
            }
            else if (msg == Native.WM_SETTINGCHANGE && lParam != IntPtr.Zero && Config.Theme == "Sistema")
            {
                string area = Marshal.PtrToStringUni(lParam);
                if (area == "ImmersiveColorSet") win.Dispatcher.BeginInvoke(new Action(ApplyConfigCore), DispatcherPriority.Background);
            }
        }

        // Reserva a faixa do Dock para as janelas maximizadas não ficarem por baixo.
        void UpdateAppBar()
        {
            if (snapshot) return;
            bool want = Config.ReserveSpace && !Config.AutoHide;
            if (!want) { RemoveAppBar(); return; }
            if (!appBarOn)
            {
                var d = AppBarData();
                d.uCallbackMessage = appBarMsg;
                Native.SHAppBarMessage(0, ref d);   // ABM_NEW
                appBarOn = true;
            }
            SetAppBarPos();
        }

        void RemoveAppBar()
        {
            if (!appBarOn) return;
            var d = AppBarData();
            Native.SHAppBarMessage(1, ref d);       // ABM_REMOVE
            appBarOn = false;
        }

        void SetAppBarPos()
        {
            if (!appBarOn || mon == null) return;
            int thick = (int)Math.Ceiling((Config.EdgeMargin + Cross + 4) * scale);
            var d = AppBarData();
            d.uEdge = Config.Position == "Esquerda" ? 0u : Config.Position == "Direita" ? 2u : 3u;
            d.rc = mon.Bounds;
            Thicken(ref d.rc, d.uEdge, thick);
            Native.SHAppBarMessage(2, ref d);       // ABM_QUERYPOS
            Thicken(ref d.rc, d.uEdge, thick);
            Native.SHAppBarMessage(3, ref d);       // ABM_SETPOS
        }

        static bool SameRect(Native.RECT a, Native.RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        static void Thicken(ref Native.RECT rc, uint edge, int thick)
        {
            if (edge == 0) rc.Right = rc.Left + thick;
            else if (edge == 2) rc.Left = rc.Right - thick;
            else rc.Top = rc.Bottom - thick;
        }

        Native.APPBARDATA AppBarData() => new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = win.Handle };

        /// <summary>
        /// O monitor do Dock está coberto por uma janela em tela cheia (jogo, vídeo)?
        /// Olha a janela de app mais à frente NAQUELE monitor, e não a que tem o foco: assim, dar Alt+Tab
        /// do jogo para outro monitor não faz o Dock aparecer e sumir por cima do jogo.
        /// </summary>
        bool MonitorCoveredByFullscreen(out bool fullscreenBehind)
        {
            fullscreenBehind = false;
            if (mon == null) return false;
            var b = mon.Bounds;
            bool covered = false, first = true, behind = false;
            Native.EnumWindows((h, l) =>
            {
                if (h == win.Handle || h == back.Handle || Native.IsIconic(h) || !WindowTracker.IsAppWindow(h)) return true;
                if (!Native.GetWindowRect(h, out var r)) return true;
                if (r.Right <= b.Left || r.Left >= b.Right || r.Bottom <= b.Top || r.Top >= b.Bottom) return true;   // outro monitor
                bool fs = !Native.IsZoomed(h) && r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
                if (first) { covered = fs; first = false; }
                if (fs) { behind = true; return false; }   // achou um jogo/vídeo em tela cheia neste monitor
                return true;
            }, IntPtr.Zero);
            fullscreenBehind = behind;
            // Menu Iniciar, pesquisa, central de notificações e Widgets mostram o Dock, como fazem com a barra do Windows
            if (covered && ShellFlyoutOpen()) covered = false;
            return covered;
        }

        DispatcherTimer fullscreenTimer;
        bool fullscreenBehind;

        /// <summary>
        /// Esconde o Dock quando um jogo/vídeo em tela cheia cobre o monitor dele. A troca só acontece se o estado
        /// se mantiver por um instante (evita piscar no Alt+Tab): 0,15 s para aparecer e 0,35 s para sumir.
        /// </summary>
        void UpdateFullscreen()
        {
            if (snapshot) return;
            bool fs = MonitorCoveredByFullscreen(out bool behind);
            if (behind != fullscreenBehind)
            {
                fullscreenBehind = behind;
                ApplyTint();
                StartLoop();
            }
            if (fs == fullscreen) { fullscreenTimer?.Stop(); return; }
            if (fullscreenTimer == null)
            {
                fullscreenTimer = new DispatcherTimer();
                fullscreenTimer.Tick += (s, e) =>
                {
                    fullscreenTimer.Stop();
                    bool now = MonitorCoveredByFullscreen(out bool b2);
                    if (b2 != fullscreenBehind) { fullscreenBehind = b2; ApplyTint(); }
                    if (now == fullscreen) return;
                    fullscreen = now;
                    StartLoop();
                };
            }
            if (fullscreenTimer.IsEnabled) return;
            fullscreenTimer.Interval = TimeSpan.FromMilliseconds(fs ? 350 : 150);
            fullscreenTimer.Start();
        }

        /// <summary>
        /// Fundo do Dock. Com um jogo em tela cheia por trás, o desfoque do Windows não tem o que desfocar e fica preto,
        /// então o vidro sai e o fundo fica mais opaco.
        /// </summary>
        void ApplyTint()
        {
            var tint = IsDark ? Color.FromRgb(24, 26, 34) : Color.FromRgb(246, 247, 250);
            double opacity = Config.Opacity / 100.0;
            if (fullscreenBehind || Config.Glass == "Nenhum") opacity = Math.Max(opacity, 0.82);
            byte alpha = (byte)Math.Round(opacity * 255);
            bg.Background = Frozen(new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B)));
            rim.BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, alpha + 70), tint.R, tint.G, tint.B)));
        }

        // ───────────────────────── janelas abertas ─────────────────────────

        void OnWinEvent(IntPtr hook, uint ev, IntPtr hWnd, int idObject, int idChild, uint thread, uint time)
        {
            if (ev != 3 && (idObject != 0 || idChild != 0)) return;
            if (hWnd == win.Handle || hWnd == back.Handle) return;

            // O Windows reexibe a barra em algumas trocas de janela (ex.: Alt+Tab saindo de um jogo).
            // Esconde na hora em vez de esperar o próximo segundo, para ela não "piscar".
            if (ev == 0x8002 && Config.TaskbarMode == "Esconder" && mon != null)
            {
                string cls = Native.ClassOf(hWnd);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    Taskbar.HideOnly(mon.Handle);
            }

            if (ev == 3) UpdateFullscreen();   // troca de foco: reage na hora (ex.: tecla Windows sobre um jogo)
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        void ScheduleRefresh()
        {
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        void RefreshWindows()
        {
            try
            {
                tracked = WindowTracker.Snapshot();
                foreach (var k in tracked.Keys)
                    if (!runningOrder.Contains(k, StringComparer.OrdinalIgnoreCase)) runningOrder.Add(k);
                runningOrder.RemoveAll(k => !tracked.ContainsKey(k));
                RebuildEntries(false);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        void SlowTick(object s, EventArgs e)
        {
            slowTicks++;
            RefreshWindows();
            UpdateFullscreen();
            UpdateStatus(slowTicks % 5 == 1);
            // O Windows às vezes mostra a barra de novo (ex.: ao reservar espaço); esconde outra vez.
            if (Config.TaskbarMode == "Esconder" && mon != null && Taskbar.VisibleOn(mon.Handle))
                Taskbar.HideOnly(mon.Handle);
            if (slowTicks % 5 == 0) ReassertTopmost();
            if (slowTicks % 10 == 0 && pool.TryGetValue("trash", out var trash) && trash.Visual is Image img)
            {
                ShellIcons.Forget(ShellIcons.RecycleBin);
                img.Source = ShellIcons.Get(ShellIcons.RecycleBin) ?? img.Source;
            }
            if (!looping) StartLoop();
        }

        // Não esconde a barra enquanto o Iniciar, a pesquisa ou a central estão abertos.
        static bool ShellFlyoutOpen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            string p = Native.GetProcessPath(pid) ?? "";
            string n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
            return n == "startmenuexperiencehost" || n == "searchhost" || n == "shellexperiencehost" || n == "shellhost"
                || n == "widgets" || n == "widgetboard";
        }

        string ResolveTarget(PinnedApp pin)
        {
            string p = pin.Path ?? "";
            if (resolveCache.TryGetValue(p, out var r)) return r;
            r = null;
            try
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (!string.IsNullOrEmpty(pin.Exe)) r = Native.ExpandKnownFolderPath(pin.Exe);
                else if (p.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
                    r = Native.ExpandKnownFolderPath(p.Substring(AppsFolderPrefix.Length));
                else if (ext == ".lnk")
                {
                    r = ShellIcons.ResolveLink(p);
                    if (string.IsNullOrEmpty(r) && (pin.Name ?? p).IndexOf("explor", StringComparison.OrdinalIgnoreCase) >= 0)
                        r = DockConfig.ExplorerPath;
                }
                else if (ext == ".exe") r = p;
                if (r != null && !string.Equals(Path.GetExtension(r), ".exe", StringComparison.OrdinalIgnoreCase)) r = null;
            }
            catch { r = null; }
            resolveCache[p] = r;
            return r;
        }

        public const string AppsFolderPrefix = @"shell:AppsFolder\";

        /// <summary>Apps da Microsoft Store: compara o pacote do atalho com a pasta do processo em WindowsApps.</summary>
        TrackedApp MatchPackage(PinnedApp pin, HashSet<string> used)
        {
            string p = pin.Path ?? "";
            int bang = p.IndexOf('!');
            if (!p.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) || bang < 0) return null;
            string family = p.Substring(AppsFolderPrefix.Length, bang - AppsFolderPrefix.Length);
            int us = family.LastIndexOf('_');
            if (us <= 0) return null;
            string name = family.Substring(0, us), publisher = family.Substring(us + 1);
            foreach (var t in tracked.Values)
            {
                if (used.Contains(t.ExePath)) continue;
                int i = t.ExePath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                string rest = t.ExePath.Substring(i + 13);
                int slash = rest.IndexOf('\\');
                if (slash < 0) continue;
                string pkg = rest.Substring(0, slash);
                if (pkg.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) && pkg.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }

        TrackedApp Match(string exe, HashSet<string> used)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            if (tracked.TryGetValue(exe, out var a) && !used.Contains(a.ExePath)) return a;
            string name = Path.GetFileNameWithoutExtension(exe);
            foreach (var t in tracked.Values)
                if (!used.Contains(t.ExePath) && string.Equals(Path.GetFileNameWithoutExtension(t.ExePath), name, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        string DisplayName(string exe)
        {
            if (nameCache.TryGetValue(exe, out var n)) return n;
            n = exe.Equals(DockConfig.ExplorerPath, StringComparison.OrdinalIgnoreCase) ? "Explorador de Arquivos" : DockConfig.NiceName(exe);
            nameCache[exe] = n;
            return n;
        }

        // ───────────────────────── itens do Dock ─────────────────────────

        DockEntry Entry(string id, EntryKind kind)
        {
            if (!pool.TryGetValue(id, out var e))
            {
                e = new DockEntry { Id = id, Kind = kind };
                pool[id] = e;
            }
            return e;
        }

        void RebuildEntries(bool force)
        {
            var list = new List<DockEntry>();
            var start = Entry("start", EntryKind.Start);
            start.Name = "Iniciar";
            list.Add(start);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in Config.Apps)
            {
                var e = Entry("pin:" + pin.Path + "|" + (pin.Args ?? ""), EntryKind.App);
                e.Pin = pin;
                e.Name = string.IsNullOrWhiteSpace(pin.Name) ? DockConfig.NiceName(pin.Path) : pin.Name;
                e.ExePath = ResolveTarget(pin);
                e.Windows.Clear();
                var app = Match(e.ExePath, used) ?? MatchPackage(pin, used);
                if (app != null) { e.Windows.AddRange(app.Windows); used.Add(app.ExePath); }
                list.Add(e);
            }

            if (Config.ShowRunning)
            {
                foreach (var key in runningOrder)
                {
                    if (used.Contains(key) || !tracked.TryGetValue(key, out var app)) continue;
                    var e = Entry("run:" + key, EntryKind.App);
                    e.Pin = null;
                    e.ExePath = app.ExePath;
                    e.Name = DisplayName(app.ExePath);
                    e.Windows.Clear();
                    e.Windows.AddRange(app.Windows);
                    list.Add(e);
                }
            }

            list.Add(Entry("sep1", EntryKind.Separator));
            var dl = Entry("downloads", EntryKind.Downloads);
            dl.Name = "Downloads";
            dl.ExePath = Native.DownloadsFolder();
            list.Add(dl);
            var trash = Entry("trash", EntryKind.Trash);
            trash.Name = "Lixeira";
            list.Add(trash);
            if (Config.ShowClock)
            {
                list.Add(Entry("sep2", EntryKind.Separator));
                list.Add(Entry("status", EntryKind.Status));
            }

            bool same = !force && list.Count == entries.Count && list.SequenceEqual(entries);
            if (!same)
            {
                foreach (var old in entries) if (!list.Contains(old)) RemoveVisual(old);
                entries.Clear();
                entries.AddRange(list);
                foreach (var e in entries) if (e.Visual == null) CreateVisual(e);
                win.Root.Children.Clear();
                win.Root.Children.Add(shadow);
                win.Root.Children.Add(bg);
                foreach (var e in entries) win.Root.Children.Add(e.Visual);
                foreach (var e in entries) if (e.Dot != null) win.Root.Children.Add(e.Dot);
                win.Root.Children.Add(tip);
            }
            StartLoop();
        }

        void RemoveVisual(DockEntry e)
        {
            if (e.Visual != null) win.Root.Children.Remove(e.Visual);
            if (e.Dot != null) win.Root.Children.Remove(e.Dot);
            e.Visual = null;
            e.Dot = null;
            if (e.Kind == EntryKind.Status) statusTime = null;
        }

        void CreateVisual(DockEntry e)
        {
            switch (e.Kind)
            {
                case EntryKind.Separator:
                    e.Visual = new Rectangle { Fill = sepBrush, Width = 1, Height = 100 };
                    break;
                case EntryKind.Status:
                    e.Visual = BuildStatus();
                    break;
                default:
                    // Tamanho-base fixo de 100; o tamanho real (com a ampliação) vem só da transformação,
                    // então mexer nos ícones não dispara o recálculo de layout do WPF a cada quadro.
                    var img = new Image { Source = IconFor(e), Stretch = Stretch.Uniform, Width = 100, Height = 100 };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    e.Visual = img;
                    if (e.Kind == EntryKind.App)
                        e.Dot = new Ellipse { Fill = indicatorBrush, IsHitTestVisible = false, Visibility = Visibility.Collapsed, Width = 10, Height = 10, RenderTransform = new MatrixTransform() };
                    break;
            }
            e.Visual.RenderTransform = new MatrixTransform();
        }

        static void Xform(FrameworkElement el, Rect r, double baseW, double baseH) =>
            ((MatrixTransform)el.RenderTransform).Matrix = new Matrix(r.Width / baseW, 0, 0, r.Height / baseH, r.X, r.Y);

        static void Move(FrameworkElement el, double x, double y) =>
            ((MatrixTransform)el.RenderTransform).Matrix = new Matrix(1, 0, 0, 1, x, y);

        ImageSource IconFor(DockEntry e)
        {
            switch (e.Kind)
            {
                case EntryKind.Start: return ShellIcons.StartIcon();
                case EntryKind.Downloads: return ShellIcons.Get(e.ExePath) ?? ShellIcons.Fallback("D");
                case EntryKind.Trash: return ShellIcons.Get(ShellIcons.RecycleBin) ?? ShellIcons.Fallback("L");
                default: return e.Pin != null ? IconForPin(e.Pin) : (ShellIcons.Get(e.ExePath) ?? ShellIcons.Fallback(e.Name));
            }
        }

        public ImageSource IconForPin(PinnedApp pin) =>
            ShellIcons.Get(pin.Path) ?? ShellIcons.Get(ResolveTarget(pin)) ?? ShellIcons.Fallback(pin.Name);

        FrameworkElement BuildStatus()
        {
            bool h = Horizontal;
            var orient = h ? Orientation.Horizontal : Orientation.Vertical;
            var icons = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            glyphNet = Glyph("");
            glyphVol = Glyph("");
            glyphBat = Glyph("");
            foreach (var g in new[] { glyphNet, glyphVol, glyphBat })
            {
                g.Margin = h ? new Thickness(0, 0, 11, 0) : new Thickness(0, 0, 0, 9);
                icons.Children.Add(g);
            }
            statusTime = new TextBlock { FontFamily = UiFont, FontSize = h ? 14 : 12.5, FontWeight = FontWeights.SemiBold, Foreground = fgBrush, HorizontalAlignment = h ? HorizontalAlignment.Left : HorizontalAlignment.Center };
            statusDate = new TextBlock { FontFamily = UiFont, FontSize = 11.5, Foreground = mutedBrush, Visibility = h ? Visibility.Visible : Visibility.Collapsed };
            var time = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            time.Children.Add(statusTime);
            time.Children.Add(statusDate);

            // clima, como o widget da barra do Windows: ícone colorido + temperatura
            weatherIcon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(weatherIcon, BitmapScalingMode.HighQuality);
            weatherTemp = new TextBlock { FontFamily = UiFont, FontSize = h ? 14 : 12.5, FontWeight = FontWeights.SemiBold, Foreground = fgBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            weatherTemp.Margin = h ? new Thickness(5, 0, 0, 0) : new Thickness(0, 3, 0, 0);
            var weatherStack = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = h ? new Thickness(0, 0, 14, 0) : new Thickness(0, 0, 0, 12) };
            weatherStack.Children.Add(weatherIcon);
            weatherStack.Children.Add(weatherTemp);
            weatherPanel = weatherStack;

            var root = new StackPanel { Orientation = orient };
            root.Children.Add(weatherPanel);
            root.Children.Add(icons);
            root.Children.Add(time);
            statusIcons = icons;
            statusTimePanel = time;
            UpdateStatus(true);
            UpdateWeatherVisual();
            statusBorder = new Border { Child = root, Background = Brushes.Transparent, Padding = h ? new Thickness(2, 0, 6, 0) : new Thickness(0, 2, 0, 6) };
            return statusBorder;
        }

        // ───────────────────────── clima ─────────────────────────

        WeatherInfo weather;
        Image weatherIcon;
        TextBlock weatherTemp;
        FrameworkElement weatherPanel, statusTimePanel;
        Border statusBorder;
        DispatcherTimer weatherTimer;
        string appliedWeather;
        int weatherRequest;

        /// <summary>Texto mostrado nas configurações (resultado da última consulta).</summary>
        public string WeatherStatus { get; private set; } = "Ainda não consultado";

        async void RefreshWeather()
        {
            if (snapshot) return;
            int id = ++weatherRequest;
            if (!Config.ShowWeather)
            {
                weather = null;
                WeatherStatus = "Desligado";
                UpdateWeatherVisual();
                settings?.UpdateWeatherStatus();
                return;
            }
            WeatherStatus = "Consultando…";
            settings?.UpdateWeatherStatus();
            try
            {
                var w = await Weather.FetchAsync(Config.WeatherCity, Config.LastLatitude, Config.LastLongitude);
                if (id != weatherRequest) return;
                weather = w;
                if (w.FromWindowsLocation && (Config.LastLatitude != w.Latitude || Config.LastLongitude != w.Longitude))
                {
                    Config.LastLatitude = w.Latitude;
                    Config.LastLongitude = w.Longitude;
                    ScheduleSave();
                }
                WeatherStatus = string.Format("{0} · {1:0} °C, {2} · atualizado às {3:HH:mm}", w.Place, w.Temperature, w.Description.ToLowerInvariant(), DateTime.Now);
                weatherTimer.Interval = TimeSpan.FromMinutes(20);
            }
            catch (Exception ex)
            {
                if (id != weatherRequest) return;
                WeatherStatus = ex is InvalidOperationException ? ex.Message : "Sem conexão com o serviço de clima. Tentando de novo em alguns minutos.";
                weatherTimer.Interval = TimeSpan.FromMinutes(5);
            }
            UpdateWeatherVisual();
            settings?.UpdateWeatherStatus();
            StartLoop();
        }

        void UpdateWeatherVisual()
        {
            if (weatherPanel == null) return;
            bool show = Config.ShowWeather && weather != null;
            weatherPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            weatherIcon.Source = Weather.Icon(weather.Sky, weather.IsDay);
            weatherTemp.Text = Math.Round(weather.Temperature).ToString("0", PtBr) + "°";
        }

        /// <summary>Qual parte do relógio/bandeja está sob o mouse: 0 = clima, 1 = rede/som/bateria, 2 = hora.</summary>
        int StatusPart(double offset, out double start, out double length)
        {
            var parts = new[] { weatherPanel, statusIcons, statusTimePanel };
            start = 0; length = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part == null || part.Visibility != Visibility.Visible || statusBorder == null) continue;
                var at = part.TranslatePoint(new Point(0, 0), statusBorder);
                start = Horizontal ? at.X : at.Y;
                length = Horizontal ? part.ActualWidth : part.ActualHeight;
                if (offset < start + length + 6) return i;
            }
            return 2;
        }

        string StatusTip(int part)
        {
            if (part == 0 && weather != null)
                return string.Format("{0} · {1:0} °C{2}", weather.Description, weather.Temperature, weather.Place == null ? "" : " · " + weather.Place);
            if (part == 1) return "Configurações rápidas";
            string date = DateTime.Now.ToString("dddd, d 'de' MMMM", PtBr);
            return char.ToUpper(date[0]) + date.Substring(1);
        }

        TextBlock Glyph(string g) => new TextBlock
        {
            Text = g, FontFamily = IconFont, FontSize = 15, Foreground = fgBrush,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };

        void UpdateStatus(bool network)
        {
            if (statusTime == null) return;
            var now = DateTime.Now;
            statusTime.Text = now.ToString("HH:mm");
            statusDate.Text = now.ToString("ddd, d MMM", PtBr).Replace(".", "");

            if (network)
            {
                string glyph = "";
                double op = 1;
                try
                {
                    var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();
                    if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)) glyph = "";
                    else if (up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)) glyph = "";
                    else op = 0.45;
                }
                catch { }
                glyphNet.Text = glyph;
                glyphNet.Opacity = op;
            }

            if (Native.GetSystemPowerStatus(out var ps) && (ps.BatteryFlag & 128) == 0 && ps.BatteryFlag != 255 && ps.BatteryLifePercent <= 100)
            {
                int lvl = Math.Min(10, ps.BatteryLifePercent / 10);
                bool charging = ps.ACLineStatus == 1;
                int code = charging ? (lvl == 10 ? 0xE83E : 0xE85A + lvl) : (lvl == 10 ? 0xE83F : 0xE850 + lvl);
                glyphBat.Text = ((char)code).ToString();
                glyphBat.Visibility = Visibility.Visible;
            }
            else glyphBat.Visibility = Visibility.Collapsed;
        }

        // ───────────────────────── animação e layout ─────────────────────────

        void StartLoop()
        {
            if (looping || win == null || snapshot) return;
            looping = true;
            lastT = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRender;
        }

        void StopLoop()
        {
            if (!looping) return;
            looping = false;
            CompositionTarget.Rendering -= OnRender;
        }

        void OnRender(object s, EventArgs e)
        {
            try { Tick(); }
            catch (Exception ex) { App.Log(ex); StopLoop(); }
        }

        void Poll(object s, EventArgs e)
        {
            if (looping) return;
            bool inside = CursorInside(out bool atEdge, out _);
            if (inside || (atEdge && Config.AutoHide)) StartLoop();
            else win.SetClickThrough(true);
        }

        bool CursorInside(out bool atEdge, out double u)
        {
            atEdge = false;
            u = 0;
            if (windowsHidden) return false;
            Native.GetCursorPos(out var pt);
            if (pt.X < strip.Left || pt.X >= strip.Right || pt.Y < strip.Top || pt.Y >= strip.Bottom) return false;
            var p = new Point((pt.X - strip.Left) / scale, (pt.Y - strip.Top) / scale);
            u = Horizontal ? p.X : p.Y;
            double v = Config.Position == "Esquerda" ? p.X : Config.Position == "Direita" ? winW - p.X : winH - p.Y;
            atEdge = v <= 2.5 && u >= bgU0 - 60 && u <= bgU1 + 60;
            if (hideAmt > 0.9) return false;
            double reach = bgV0 + Cross + iconS * (Math.Max(1, Config.Magnification) - 1) * hover + 6;
            return u >= bgU0 - 4 && u <= bgU1 + 4 && v <= reach;
        }

        static double Approach(double cur, double target, double dt, double speed)
        {
            double v = cur + (target - cur) * (1 - Math.Exp(-speed * dt));
            return Math.Abs(v - target) < 0.002 ? target : v;
        }

        void Tick()
        {
            long started = Stopwatch.GetTimestamp();
            double t = clock.Elapsed.TotalSeconds;
            if (t - lastT < minFrame) return;   // ex.: WPF a 144 Hz com o Dock num monitor de 60 Hz
            double dt = Math.Max(0.001, Math.Min(0.05, t - lastT));
            lastT = t;

            if (windowsHidden && !fullscreen) ShowWindows(true);

            bool benching = benchFrom > 0 && t >= benchFrom;
            if (benching && t >= benchFrom + BenchSeconds) { BenchFinish(); return; }

            bool inside = CursorInside(out bool atEdge, out double cu);
            if (benching)
            {
                inside = true;
                cu = baseCenter + baseHalf * 0.9 * Math.Sin(2 * Math.PI * (t - benchFrom) / 2.4);
            }
            if (menuOpen) inside = true;
            else
            {
                if (inside) mouseU = cu;
                hover = Approach(hover, inside ? 1 : 0, dt, 11);
            }
            if (inside) lastInsideT = t;
            if (!benching) win.SetClickThrough(!inside);

            double hideTarget = 0;
            if (fullscreen) hideTarget = 1;
            else if (Config.AutoHide) hideTarget = (inside || atEdge || t - lastInsideT < 0.6) ? 0 : 1;
            hideAmt = Approach(hideAmt, hideTarget, dt, hideTarget > hideAmt ? 8 : 14);

            bool bouncing = Layout(t);
            if (fullscreen && hideAmt >= 1) ShowWindows(false);
            if (benching) BenchFrame(t, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);

            bool settled = !bouncing && hideAmt == hideTarget && (hover == 0 || hover == 1);
            if (settled && !inside) StopLoop();
        }

        Rect MakeRect(double u, double v, double lu, double lv)
        {
            switch (Config.Position)
            {
                case "Esquerda": return new Rect(v, u, lv, lu);
                case "Direita": return new Rect(winW - v - lv, u, lv, lu);
                default: return new Rect(u, winH - v - lv, lu, lv);
            }
        }

        static void Place(FrameworkElement el, Rect r, bool size = true)
        {
            Canvas.SetLeft(el, r.X);
            Canvas.SetTop(el, r.Y);
            if (!size) return;
            el.Width = Math.Max(0, r.Width);
            el.Height = Math.Max(0, r.Height);
        }

        double BaseLength(DockEntry e)
        {
            if (e.Kind == EntryKind.Separator) return 7;
            if (e.Kind == EntryKind.Status)
            {
                // O tamanho vem do último layout (um quadro de atraso quando a hora muda, imperceptível)
                if (e.Visual.DesiredSize.Width == 0) e.Visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return Horizontal ? e.Visual.DesiredSize.Width : e.Visual.DesiredSize.Height;
            }
            return iconS;
        }

        void FitIconSize()
        {
            double available = (Horizontal ? winW : winH) - 32;
            int n = entries.Count, icons = entries.Count(e => e.IsIcon);
            if (icons == 0 || available <= 0) { iconS = Config.IconSize; return; }
            double fixedLen = entries.Where(e => !e.IsIcon).Sum(e => e.Kind == EntryKind.Separator ? 7 : BaseLength(e));
            // total ≈ S·(ícones + 0,36 de padding + 0,12 por espaço) + itens fixos
            double fit = (available - fixedLen) / (icons + 0.36 + 0.12 * (n - 1));
            iconS = Math.Max(24, Math.Min(Config.IconSize, Math.Floor(fit)));
        }

        /// <summary>Posiciona tudo. Devolve true enquanto algum ícone estiver pulando.</summary>
        bool Layout(double t)
        {
            if (entries.Count == 0) return false;
            FitIconSize();
            double S = iconS, mag = Math.Max(1, Config.Magnification), pad = Pad, gap = Gap, cross = Cross;
            double center = (Horizontal ? winW : winH) / 2;
            int n = entries.Count;

            double baseTotal = 2 * pad + gap * (n - 1);
            foreach (var e in entries) { e.BaseLen = BaseLength(e); baseTotal += e.BaseLen; }
            baseCenter = center;
            baseHalf = baseTotal / 2;

            // ampliação: curva suave em volta do mouse, calculada sobre as posições sem ampliação
            double bu = center - baseTotal / 2 + pad;
            double radius = 2.6 * (S + gap);
            double total = 2 * pad + gap * (n - 1);
            foreach (var e in entries)
            {
                double c = bu + e.BaseLen / 2;
                bu += e.BaseLen + gap;
                if (e.IsIcon && hover > 0 && mag > 1.001)
                {
                    double d = Math.Abs(mouseU - c);
                    double f = d < radius ? (Math.Cos(Math.PI * d / radius) + 1) / 2 : 0;
                    e.Size = S * (1 + (mag - 1) * f * hover);
                }
                else e.Size = e.BaseLen;
                total += e.Size;
            }

            double u0 = center - total / 2;
            double v0 = Config.EdgeMargin - hideAmt * (Config.EdgeMargin + cross + 16);
            bgU0 = u0; bgU1 = u0 + total; bgV0 = v0;
            var bgRect = MakeRect(u0, v0, total, cross);
            Place(bg, bgRect);
            double corner = Math.Min(Config.CornerRadius, cross / 2);

            // a sombra é feita no tamanho de repouso e só esticada durante a ampliação
            if (Horizontal) EnsureShadow(baseTotal, cross, corner); else EnsureShadow(cross, baseTotal, corner);
            Xform(shadow, new Rect(bgRect.X - ShadowBlur, bgRect.Y - ShadowBlur, bgRect.Width + 2 * ShadowBlur, bgRect.Height + 2 * ShadowBlur + ShadowDrop),
                  shadow.Width, shadow.Height);
            if (rimDirty || bg.CornerRadius.TopLeft != corner)
            {
                rimDirty = false;
                bg.CornerRadius = new CornerRadius(corner);
                // Borda interna um pouco mais opaca cobrindo a faixa onde o vidro fica recuado
                double rimDip = Config.Glass == "Nenhum" ? 0 : BackdropWindow.InsetFor((int)Math.Round(corner * scale), scale) / scale;
                rim.BorderThickness = new Thickness(rimDip);
                rim.CornerRadius = new CornerRadius(Math.Max(0, corner - 1));
            }
            UpdateBackdrop(bgRect, corner);

            bool bouncing = false;
            DockEntry tipEntry = null;
            string tipLabel = null;
            double tipCenterU = 0, tipFar = 0;
            double cu = u0 + pad;
            foreach (var e in entries)
            {
                if (e.IsIcon)
                {
                    double jump = 0;
                    if (e.BounceT >= 0)
                    {
                        double bt = t - e.BounceT;
                        if (bt >= 1.05) e.BounceT = -1;
                        else { jump = Math.Abs(Math.Sin(Math.PI * bt / 0.35)) * S * 0.42 * (1 - bt / 1.05); bouncing = true; }
                    }
                    e.Bounds = MakeRect(cu, v0 + pad + jump, e.Size, e.Size);
                    e.HitRect = MakeRect(cu - gap / 2, v0, e.Size + gap, pad + e.Size + 2);
                    Xform(e.Visual, e.Bounds, 100, 100);
                    if (e.Dot != null)
                    {
                        var vis = e.IsRunning ? Visibility.Visible : Visibility.Collapsed;
                        if (e.Dot.Visibility != vis) e.Dot.Visibility = vis;
                        double ds = Math.Max(4, Math.Round(S * 0.085));
                        Xform(e.Dot, MakeRect(cu + e.Size / 2 - ds / 2, v0 + pad * 0.45 - ds / 2, ds, ds), 10, 10);
                    }
                    if (hover > 0.5 && !menuOpen && mouseU >= cu - gap / 2 && mouseU < cu + e.Size + gap / 2) tipEntry = e;
                }
                else if (e.Kind == EntryKind.Separator)
                {
                    double len = Math.Round(S * 0.7);
                    e.Bounds = MakeRect(cu, v0, e.Size, cross);
                    e.HitRect = e.Bounds;
                    Xform(e.Visual, MakeRect(cu + e.Size / 2 - 0.5, v0 + (cross - len) / 2, 1, len), 1, 100);
                }
                else
                {
                    var ds = e.Visual.DesiredSize;
                    double lv = Horizontal ? ds.Height : ds.Width;
                    e.Bounds = MakeRect(cu, v0 + (cross - lv) / 2, e.Size, lv);
                    e.HitRect = MakeRect(cu, v0, e.Size, cross);
                    Move(e.Visual, e.Bounds.X, e.Bounds.Y);
                    if (hover > 0.5 && !menuOpen && mouseU >= cu && mouseU < cu + e.Size)
                    {
                        // etiqueta também para o clima, a bandeja e a hora
                        int part = StatusPart(mouseU - cu, out double ps, out double pl);
                        tipLabel = StatusTip(part);
                        tipCenterU = cu + ps + pl / 2;
                        tipFar = v0 + cross;
                    }
                }
                cu += e.Size + gap;
            }

            if (tipEntry != null)
            {
                var b = tipEntry.Bounds;
                tipLabel = tipEntry.Name;
                tipCenterU = (Horizontal ? b.X : b.Y) + tipEntry.Size / 2;
                tipFar = Config.Position == "Esquerda" ? b.Right : Config.Position == "Direita" ? winW - b.Left : winH - b.Top;
            }
            if (tipLabel != null && Config.ShowLabels && hideAmt < 0.5)
            {
                if (tipText.Text != tipLabel || tip.Visibility != Visibility.Visible)
                {
                    tipText.Text = tipLabel;
                    tip.Visibility = Visibility.Visible;
                    tip.UpdateLayout();
                }
                var ts = tip.DesiredSize;
                double tlu = Horizontal ? ts.Width : ts.Height, tlv = Horizontal ? ts.Height : ts.Width;
                tipRect = MakeRect(tipCenterU - tlu / 2, tipFar + 10, tlu, tlv);
                Move(tip, tipRect.X, tipRect.Y);
            }
            else if (tip.Visibility != Visibility.Collapsed) tip.Visibility = Visibility.Collapsed;

            return bouncing;
        }

        void UpdateBackdrop(Rect r, double corner)
        {
            if (snapshot) return;
            bool show = Config.Glass != "Nenhum" && !fullscreenBehind && hideAmt < 0.98 && !windowsHidden;
            back.SetShown(show);
            if (!show) return;
            int x = strip.Left + (int)Math.Round(r.X * scale), y = strip.Top + (int)Math.Round(r.Y * scale);
            int w = (int)Math.Round(r.Width * scale), h = (int)Math.Round(r.Height * scale);
            int rad = (int)Math.Round(corner * scale);
            int inset = BackdropWindow.InsetFor(rad, scale);
            long t0 = Stopwatch.GetTimestamp();
            back.SetBounds(x + inset, y + inset, w - 2 * inset, h - 2 * inset, rad);
            if (benchRunning) benchBackMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }

        // ───────────────────────── mouse ─────────────────────────

        DockEntry HitTest(Point p)
        {
            foreach (var e in entries)
                if (!e.HitRect.IsEmpty && e.HitRect.Contains(p)) return e;
            return null;
        }

        void OnMouseDown(object s, MouseButtonEventArgs a)
        {
            pressed = HitTest(a.GetPosition(win.Root));
            a.Handled = true;
        }

        void OnMouseUp(object s, MouseButtonEventArgs a)
        {
            var e = HitTest(a.GetPosition(win.Root));
            var p = pressed;
            pressed = null;
            a.Handled = true;
            if (a.ChangedButton == MouseButton.Right)
            {
                if (e == p) ShowMenu(e);
                return;
            }
            if (e == null || e != p) return;
            if (a.ChangedButton == MouseButton.Left) Click(e, a.GetPosition(win.Root), (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            else if (a.ChangedButton == MouseButton.Middle && e.Kind == EntryKind.App) Launch(e);
        }

        void Click(DockEntry e, Point p, bool shift)
        {
            switch (e.Kind)
            {
                case EntryKind.Start:
                    Native.SendWinCombo(0);
                    break;
                case EntryKind.Downloads:
                    OpenShell(e.ExePath);
                    Bounce(e);
                    break;
                case EntryKind.Trash:
                    OpenShell("shell:RecycleBinFolder");
                    Bounce(e);
                    break;
                case EntryKind.Status:
                    int part = StatusPart(Horizontal ? p.X - e.Bounds.X : p.Y - e.Bounds.Y, out _, out _);
                    Native.SendWinCombo(part == 0 ? (byte)0x57 : part == 1 ? (byte)0x41 : (byte)0x4E);   // Win+W (Widgets), Win+A, Win+N
                    break;
                case EntryKind.App:
                    var wins = e.Windows.Where(Native.IsWindow).ToList();
                    if (shift || wins.Count == 0) { Launch(e); return; }
                    IntPtr fg = Native.GetForegroundWindow();
                    if (wins.Contains(fg))
                    {
                        if (wins.Count == 1) Native.ShowWindow(fg, Native.SW_MINIMIZE);
                        else Activate(wins[wins.Count - 1]);
                    }
                    else Activate(wins[0]);
                    ScheduleRefresh();
                    break;
            }
        }

        void Bounce(DockEntry e)
        {
            e.BounceT = clock.Elapsed.TotalSeconds;
            StartLoop();
        }

        void Launch(DockEntry e)
        {
            string path = e.Pin != null ? e.Pin.Path : e.ExePath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var psi = new ProcessStartInfo { FileName = path, Arguments = e.Pin?.Args ?? "", UseShellExecute = true };
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "explorer.exe";
                    psi.Arguments = path;
                }
                else if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    psi.WorkingDirectory = Path.GetDirectoryName(path);
                Process.Start(psi);
                Bounce(e);
                ScheduleRefresh();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        static void OpenShell(string target)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
        }

        static void Run(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
        }

        static void Activate(IntPtr h)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            IntPtr fg = Native.GetForegroundWindow();
            uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
            uint me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            Native.SetForegroundWindow(h);
            Native.BringWindowToTop(h);
            if (attached) Native.AttachThreadInput(me, fgThread, false);
        }

        // ───────────────────────── menus ─────────────────────────

        void ShowMenu(DockEntry e)
        {
            var m = new ContextMenu { FontFamily = UiFont };
            if (e != null && e.Kind == EntryKind.App)
            {
                m.Items.Add(new MenuItem { Header = e.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold });
                var wins = e.Windows.Where(Native.IsWindow).ToList();
                foreach (var h in wins.Take(12))
                {
                    var hh = h;
                    m.Items.Add(Item(Trim(Native.TitleOf(h), 52), () => Activate(hh)));
                }
                if (wins.Count > 0) m.Items.Add(Sep());
                m.Items.Add(Item(wins.Count > 0 ? "Nova janela" : "Abrir", () => Launch(e)));
                if (e.Pin != null) m.Items.Add(Item("Remover do Dock", () => RemovePin(e.Pin)));
                else m.Items.Add(Item("Fixar no Dock", () => AddPin(e.ExePath, e.Name)));
                if (wins.Count > 0)
                    m.Items.Add(Item(wins.Count > 1 ? "Fechar todas as janelas" : "Fechar janela", () =>
                    {
                        foreach (var h in wins) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        ScheduleRefresh();
                    }));
                m.Items.Add(Sep());
            }
            else if (e != null && e.Kind == EntryKind.Trash)
            {
                m.Items.Add(Item("Abrir Lixeira", () => OpenShell("shell:RecycleBinFolder")));
                m.Items.Add(Item("Esvaziar Lixeira…", () => Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0)));
                m.Items.Add(Sep());
            }
            else if (e != null && e.Kind == EntryKind.Downloads)
            {
                m.Items.Add(Item("Abrir Downloads", () => OpenShell(e.ExePath)));
                m.Items.Add(Sep());
            }
            else if (e != null && e.Kind == EntryKind.Start)
            {
                m.Items.Add(Item("Gerenciador de Tarefas", () => Run("taskmgr.exe")));
                m.Items.Add(Item("Configurações do Windows", () => Run("ms-settings:")));
                m.Items.Add(Sep());
            }

            m.Items.Add(Check("Ocultar o Dock automaticamente", Config.AutoHide, () => { Config.AutoHide = !Config.AutoHide; ApplyConfig(); settings?.LoadValues(); }));
            m.Items.Add(Check("Mostrar a barra do Windows", Config.TaskbarMode != "Esconder", () =>
            {
                Config.TaskbarMode = Config.TaskbarMode == "Esconder" ? "AutoOcultar" : "Esconder";
                ApplyConfig();
                settings?.LoadValues();
            }));
            m.Items.Add(Item("Configurações do Cais…", ShowSettings));
            m.Items.Add(Sep());
            m.Items.Add(Item("Sair do Cais", Exit));

            menuOpen = true;
            StartLoop();
            m.Closed += (s, a) => { menuOpen = false; StartLoop(); };
            m.Placement = PlacementMode.MousePoint;
            m.PlacementTarget = win;
            m.IsOpen = true;
        }

        static MenuItem Item(string text, Action action)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (s, a) => action();
            return mi;
        }

        static MenuItem Check(string text, bool on, Action action)
        {
            var mi = Item(text, action);
            mi.IsChecked = on;
            return mi;
        }

        static Separator Sep() => new Separator { Style = (Style)Application.Current.Resources["MenuSep"] };

        static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "(sem título)" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        // ───────────────────────── apps fixados ─────────────────────────

        void OnDragOver(object s, DragEventArgs a)
        {
            a.Effects = a.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            a.Handled = true;
        }

        void OnDrop(object s, DragEventArgs a)
        {
            if (a.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) AddPin(f, null);
            a.Handled = true;
        }

        public void AddPin(string path, string name, string exe = null)
        {
            if (string.IsNullOrWhiteSpace(path) || IsPinned(path)) return;
            Config.Apps.Add(new PinnedApp { Name = name ?? DockConfig.NiceName(path), Path = path, Exe = exe });
            PinsChanged();
        }

        public bool IsPinned(string path) => Config.Apps.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));

        public void RemovePin(string path)
        {
            var pin = Config.Apps.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (pin != null) RemovePin(pin);
        }

        public void RemovePin(PinnedApp pin)
        {
            Config.Apps.Remove(pin);
            PinsChanged();
        }

        public void MovePin(int index, int delta)
        {
            int to = index + delta;
            if (index < 0 || index >= Config.Apps.Count || to < 0 || to >= Config.Apps.Count) return;
            var a = Config.Apps[index];
            Config.Apps.RemoveAt(index);
            Config.Apps.Insert(to, a);
            PinsChanged();
        }

        public void ImportTaskbarPins()
        {
            foreach (var p in DockConfig.SeedFromTaskbar())
                if (!Config.Apps.Any(x => string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase)))
                    Config.Apps.Add(p);
            PinsChanged();
        }

        void PinsChanged()
        {
            RebuildEntries(false);
            ScheduleSave();
            settings?.BuildAppsList();
        }

        public void ResetLook()
        {
            Config.ResetLook();
            ApplyConfig();
        }

        // ───────────────────────── configurações ─────────────────────────

        public void ShowSettings()
        {
            if (settings == null)
            {
                settings = new SettingsWindow(this);
                settings.Closed += (s, a) => settings = null;
                settings.Show();
            }
            if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal;
            settings.Activate();
        }
    }
}

