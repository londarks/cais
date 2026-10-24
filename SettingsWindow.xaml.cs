using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Cais
{
    public partial class SettingsWindow : Window
    {
        readonly DockController ctl;
        bool loading = true;

        static readonly (string Name, string Hex)[] Swatches =
        {
            ("Azul", "#4CC2FF"), ("Branco", "#FFFFFF"), ("Verde", "#6CCB5F"), ("Laranja", "#FF9F43"),
            ("Rosa", "#F472B6"), ("Roxo", "#A78BFA"), ("Cor de destaque do Windows", null),
        };

        internal SettingsWindow(DockController controller)
        {
            ctl = controller;
            InitializeComponent();
            SourceInitialized += (s, e) => ApplyTitleBar();
            BuildSwatches();
            LoadValues();
            BuildAppsList();
        }

        DockConfig C => ctl.Config;

        /// <summary>Barra de título escura/clara combinando com o tema.</summary>
        public void ApplyTitleBar() => TitleBar.Apply(this, ctl.IsDark);

        void BuildSwatches()
        {
            SwatchPanel.Children.Clear();
            foreach (var (name, hex) in Swatches)
            {
                string value = hex ?? Palette.WindowsAccent();
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Swatch"),
                    GroupName = "swatch",
                    Tag = hex ?? "accent",
                    ToolTip = name,
                    Background = new SolidColorBrush(Palette.Parse(value, Colors.DeepSkyBlue)),
                };
                rb.Checked += (s, e) =>
                {
                    if (loading) return;
                    C.IndicatorColor = hex ?? Palette.WindowsAccent();
                    ctl.ApplyConfig();
                };
                SwatchPanel.Children.Add(rb);
            }
        }

        public void LoadValues()
        {
            loading = true;
            SetChoice("theme", C.Theme);
            SetChoice("glass", C.Glass);
            SetChoice("pos", C.Position);
            SetChoice("taskbar", C.TaskbarMode);

            SizeSlider.Value = C.IconSize;
            RadiusSlider.Value = C.CornerRadius;
            OpacitySlider.Value = C.Opacity;
            MagSlider.Value = C.Magnification;
            MarginSlider.Value = C.EdgeMargin;

            LabelsSwitch.IsChecked = C.ShowLabels;
            RunningSwitch.IsChecked = C.ShowRunning;
            ClockSwitch.IsChecked = C.ShowClock;
            WeatherSwitch.IsChecked = C.ShowWeather;
            CityBox.Text = C.WeatherCity;
            CityBox.IsEnabled = C.ShowWeather;
            UpdateWeatherStatus();
            AutoHideSwitch.IsChecked = C.AutoHide;
            ReserveSwitch.IsChecked = C.ReserveSpace;
            ReserveSwitch.IsEnabled = !C.AutoHide;
            StartupSwitch.IsChecked = C.StartWithWindows;

            bool matched = false;
            foreach (RadioButton rb in SwatchPanel.Children)
            {
                bool on = rb.Tag as string != "accent" && string.Equals(rb.Tag as string, C.IndicatorColor, StringComparison.OrdinalIgnoreCase);
                rb.IsChecked = on;
                matched |= on;
            }
            if (!matched) ((RadioButton)SwatchPanel.Children[SwatchPanel.Children.Count - 1]).IsChecked = true;

            MonitorPanel.Children.Clear();
            var mons = ctl.Monitors();
            for (int i = 0; i < mons.Count; i++)
            {
                int idx = i;
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("Seg"),
                    GroupName = "monitor",
                    Content = mons[i].Primary ? "Principal" : "Monitor " + (i + 1),
                    IsChecked = C.Monitor == i || (C.Monitor >= mons.Count && i == 0),
                };
                rb.Checked += (s, e) => { if (loading) return; C.Monitor = idx; ctl.ApplyConfig(); };
                MonitorPanel.Children.Add(rb);
            }

            UpdateLabels();
            loading = false;
        }

        void SetChoice(string group, string value)
        {
            foreach (var rb in FindRadios(this).Where(r => r.GroupName == group))
                rb.IsChecked = string.Equals(rb.Tag as string, value, StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<RadioButton> FindRadios(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is RadioButton rb) yield return rb;
                foreach (var r in FindRadios(child)) yield return r;
            }
        }

        void UpdateLabels()
        {
            SizeValue.Text = (int)SizeSlider.Value + " px";
            RadiusValue.Text = (int)RadiusSlider.Value + " px";
            OpacityValue.Text = (int)OpacitySlider.Value + "%";
            MarginValue.Text = (int)MarginSlider.Value + " px";
            MagValue.Text = MagSlider.Value <= 1.001 ? "Desligada" : MagSlider.Value.ToString("0.0").Replace('.', ',') + "×";
        }

        public void SelectPage(int index)
        {
            (index == 0 ? NavLook : index == 1 ? NavBehavior : NavApps).IsChecked = true;
        }

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageLook == null) return;
            PageLook.Visibility = NavLook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageBehavior.Visibility = NavBehavior.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageApps.Visibility = NavApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageTitle.Text = (string)((RadioButton)sender).Content;
            ResetBtn.Visibility = NavApps.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }

        void Choice_Checked(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            var rb = (RadioButton)sender;
            string v = rb.Tag as string;
            switch (rb.GroupName)
            {
                case "theme": C.Theme = v; break;
                case "glass": C.Glass = v; break;
                case "pos": C.Position = v; break;
                case "taskbar": C.TaskbarMode = v; break;
            }
            ctl.ApplyConfig();
        }

        void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading) return;
            C.IconSize = (int)SizeSlider.Value;
            C.CornerRadius = (int)RadiusSlider.Value;
            C.Opacity = (int)OpacitySlider.Value;
            C.Magnification = Math.Round(MagSlider.Value, 1);
            C.EdgeMargin = (int)MarginSlider.Value;
            UpdateLabels();
            ctl.ApplyConfig();
        }

        void Switch_Click(object sender, RoutedEventArgs e)
        {
            C.ShowLabels = LabelsSwitch.IsChecked == true;
            C.ShowRunning = RunningSwitch.IsChecked == true;
            C.ShowClock = ClockSwitch.IsChecked == true;
            C.ShowWeather = WeatherSwitch.IsChecked == true;
            CityBox.IsEnabled = C.ShowWeather;
            C.AutoHide = AutoHideSwitch.IsChecked == true;
            C.ReserveSpace = ReserveSwitch.IsChecked == true;
            C.StartWithWindows = StartupSwitch.IsChecked == true;
            ReserveSwitch.IsEnabled = !C.AutoHide;
            ctl.ApplyConfig();
        }

        // ───────── clima ─────────

        public void UpdateWeatherStatus() => WeatherStatusText.Text = ctl.WeatherStatus;

        void CityBox_TextChanged(object sender, TextChangedEventArgs e) =>
            CityPlaceholder.Visibility = CityBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        void CityBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) CityBox_Commit(sender, e);
        }

        void CityBox_Commit(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            string city = CityBox.Text.Trim();
            if (city == C.WeatherCity) return;
            C.WeatherCity = city;
            ctl.ApplyConfig();
        }

        void Reset_Click(object sender, RoutedEventArgs e)
        {
            ctl.ResetLook();
            LoadValues();
        }

        void Exit_Click(object sender, RoutedEventArgs e) => ctl.Exit();

        // ───────── apps fixados ─────────

        public void BuildAppsList()
        {
            AppsList.Children.Clear();
            var apps = C.Apps;
            AppsCount.Text = apps.Count == 1 ? "1 app fixado" : apps.Count + " apps fixados";
            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                int idx = i;
                var row = new Border { Style = (Style)FindResource("Card"), MinHeight = 60, Padding = new Thickness(16, 8, 8, 8) };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new Image { Width = 32, Height = 32, Source = ctl.IconForPin(app), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                grid.Children.Add(icon);

                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = app.Name, Style = (Style)FindResource("RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis });
                texts.Children.Add(new TextBlock { Text = app.Path, Style = (Style)FindResource("RowDesc"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(texts, 1);
                grid.Children.Add(texts);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                buttons.Children.Add(IconButton("", "Mover para cima", idx > 0, () => ctl.MovePin(idx, -1)));
                buttons.Children.Add(IconButton("", "Mover para baixo", idx < apps.Count - 1, () => ctl.MovePin(idx, 1)));
                buttons.Children.Add(IconButton("", "Remover do Dock", true, () => ctl.RemovePin(app)));
                Grid.SetColumn(buttons, 2);
                grid.Children.Add(buttons);

                row.Child = grid;
                AppsList.Children.Add(row);
            }
        }

        Button IconButton(string glyph, string tip, bool enabled, Action action)
        {
            var b = new Button { Content = glyph, ToolTip = tip, IsEnabled = enabled, Style = (Style)FindResource("IconBtn") };
            b.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, tip);
            b.Click += (s, e) => action();
            return b;
        }

        void Add_Click(object sender, RoutedEventArgs e)
        {
            var picker = new AppPicker(ctl) { Owner = this };
            picker.SourceInitialized += (s, a) => TitleBar.Apply(picker, ctl.IsDark);
            picker.ShowDialog();
        }

        void Import_Click(object sender, RoutedEventArgs e) => ctl.ImportTaskbarPins();
    }

    /// <summary>Deixa a barra de título da janela com as cores do tema (Windows 11).</summary>
    internal static class TitleBar
    {
        public static void Apply(Window w, bool isDark)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int dark = isDark ? 1 : 0;
            Native.DwmSetWindowAttribute(h, 20, ref dark, 4);
            if (w.TryFindResource("WinBg") is SolidColorBrush bg)
            {
                int cap = bg.Color.R | (bg.Color.G << 8) | (bg.Color.B << 16);
                Native.DwmSetWindowAttribute(h, 35, ref cap, 4);
            }
            if (w.TryFindResource("TextPrimary") is SolidColorBrush fg)
            {
                int txt = fg.Color.R | (fg.Color.G << 8) | (fg.Color.B << 16);
                Native.DwmSetWindowAttribute(h, 36, ref txt, 4);
            }
        }
    }
}
