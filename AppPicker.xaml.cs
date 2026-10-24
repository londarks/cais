using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Cais
{
    /// <summary>Um app instalado (desktop ou da Microsoft Store), como aparece em "Todos os apps".</summary>
    public class InstalledApp : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string ParsingPath { get; set; }
        public string Exe { get; set; }

        ImageSource icon;
        public ImageSource Icon => icon ?? (icon = ShellIcons.Get(ParsingPath, 64) ?? ShellIcons.Fallback(Name));

        bool pinned;
        public bool Pinned
        {
            get => pinned;
            set { pinned = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
        }
        public string Status => pinned ? "✓ No Dock" : "";

        public event PropertyChangedEventHandler PropertyChanged;

        static readonly string[] skipWords = { "uninstall", "desinstalar", "readme", "leia-me", "release notes", "documentation", "help", "ajuda", "website", "site" };
        static readonly string[] skipExt = { ".url", ".chm", ".html", ".htm", ".txt", ".pdf", ".msc", ".ini" };

        /// <summary>Lê a pasta shell:AppsFolder (a mesma lista de "Todos os apps" do Iniciar).</summary>
        public static List<InstalledApp> Load()
        {
            var list = new List<InstalledApp>();
            var type = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(type);
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name, path;
                try { name = item.Name; path = item.Path; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                string lower = name.ToLowerInvariant();
                if (skipWords.Any(w => lower.Contains(w))) continue;
                if (skipExt.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;

                string exe = null;
                try
                {
                    string target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                    if (!string.IsNullOrEmpty(target)) exe = Native.ExpandKnownFolderPath(target);
                }
                catch { }
                if (exe == null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = Native.ExpandKnownFolderPath(path);
                if (exe != null && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = null;

                list.Add(new InstalledApp { Name = name, ParsingPath = DockController.AppsFolderPrefix + path, Exe = exe });
            }
            return list
                .GroupBy(a => a.ParsingPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.Create(new CultureInfo("pt-BR"), true))
                .ToList();
        }
    }

    public partial class AppPicker : Window
    {
        readonly DockController ctl;
        static readonly CompareInfo Compare = new CultureInfo("pt-BR").CompareInfo;
        ICollectionView view;

        internal AppPicker(DockController controller)
        {
            ctl = controller;
            InitializeComponent();
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(LoadApps), DispatcherPriority.Background);
        }

        /// <summary>Carrega a lista na hora (usado pelas capturas de tela).</summary>
        internal void LoadNow() => LoadApps();

        void LoadApps()
        {
            if (view != null) return;
            List<InstalledApp> apps;
            try { apps = InstalledApp.Load(); }
            catch (Exception ex)
            {
                App.Log(ex);
                Loading.Text = "Não foi possível ler a lista de apps. Use “Procurar arquivo ou atalho…”.";
                return;
            }
            foreach (var a in apps) a.Pinned = ctl.IsPinned(a.ParsingPath);
            view = CollectionViewSource.GetDefaultView(apps);
            view.Filter = o => Matches((InstalledApp)o);
            List.ItemsSource = view;
            Loading.Visibility = Visibility.Collapsed;
            List.Visibility = Visibility.Visible;
            Search.Focus();
        }

        bool Matches(InstalledApp a)
        {
            string q = Search.Text.Trim();
            return q.Length == 0 || Compare.IndexOf(a.Name, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            view?.Refresh();
        }

        void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(List.SelectedItem is InstalledApp a)) return;
            List.SelectedItem = null;
            if (a.Pinned) ctl.RemovePin(a.ParsingPath);
            else ctl.AddPin(a.ParsingPath, a.Name, a.Exe);
            a.Pinned = ctl.IsPinned(a.ParsingPath);
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Escolha apps, atalhos ou arquivos para fixar no Dock",
                Filter = "Apps e atalhos (*.exe;*.lnk)|*.exe;*.lnk|Todos os arquivos (*.*)|*.*",
                DereferenceLinks = false,
                Multiselect = true,
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames) ctl.AddPin(f, null);
        }

        void Done_Click(object sender, RoutedEventArgs e) => Close();
    }
}
