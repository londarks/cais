using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Cais
{
    public class PinnedApp
    {
        public string Name { get; set; }
        /// <summary>.exe, .lnk, pasta ou endereço shell: (ex.: shell:AppsFolder\...)</summary>
        public string Path { get; set; }
        public string Args { get; set; }
        /// <summary>Executável real, quando o Path é um atalho/endereço shell (para saber se o app está aberto).</summary>
        public string Exe { get; set; }
    }

    public class DockConfig
    {
        public string Theme { get; set; } = "Escuro";          // Escuro | Claro | Sistema
        public int IconSize { get; set; } = 52;
        public double Magnification { get; set; } = 1.6;
        public int CornerRadius { get; set; } = 22;
        public int Opacity { get; set; } = 38;
        public string Glass { get; set; } = "Acrilico";        // Acrilico | Desfoque | Nenhum
        public string IndicatorColor { get; set; } = "#4CC2FF";
        public string Position { get; set; } = "Embaixo";      // Embaixo | Esquerda | Direita
        public int Monitor { get; set; } = 0;
        public int EdgeMargin { get; set; } = 10;
        public bool ShowLabels { get; set; } = true;
        public bool ShowClock { get; set; } = true;
        public bool ShowWeather { get; set; } = true;
        /// <summary>Vazio = localização do Windows.</summary>
        public string WeatherCity { get; set; } = "";
        public bool ShowRunning { get; set; } = true;
        public bool AutoHide { get; set; } = false;
        public bool ReserveSpace { get; set; } = true;
        public string TaskbarMode { get; set; } = "Esconder";  // Esconder | AutoOcultar | Normal
        public bool StartWithWindows { get; set; } = true;
        public bool FirstRun { get; set; } = true;
        public List<PinnedApp> Apps { get; set; } = new List<PinnedApp>();

        public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cais");
        public static string FilePath => System.IO.Path.Combine(Folder, "config.json");
        public static readonly string ExplorerPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        public static DockConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var c = new JavaScriptSerializer().Deserialize<DockConfig>(File.ReadAllText(FilePath));
                    if (c != null) { c.Normalize(); return c; }
                }
            }
            catch (Exception ex) { App.Log(ex); }
            var d = new DockConfig { Apps = SeedFromTaskbar() };
            d.Normalize();
            return d;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { App.Log(ex); }
        }

        /// <summary>Volta a aparência e o comportamento ao padrão, mantendo os apps fixados.</summary>
        public void ResetLook()
        {
            var d = new DockConfig();
            Theme = d.Theme; IconSize = d.IconSize; Magnification = d.Magnification; CornerRadius = d.CornerRadius;
            Opacity = d.Opacity; Glass = d.Glass; IndicatorColor = d.IndicatorColor; Position = d.Position;
            EdgeMargin = d.EdgeMargin; ShowLabels = d.ShowLabels; ShowClock = d.ShowClock; ShowRunning = d.ShowRunning;
            AutoHide = d.AutoHide; ReserveSpace = d.ReserveSpace;
        }

        public void Normalize()
        {
            IconSize = Clamp(IconSize, 28, 96);
            Magnification = Math.Max(1, Math.Min(2.5, Magnification));
            CornerRadius = Clamp(CornerRadius, 0, 48);
            Opacity = Clamp(Opacity, 0, 100);
            EdgeMargin = Clamp(EdgeMargin, 0, 60);
            if (Monitor < 0) Monitor = 0;
            if (!new[] { "Escuro", "Claro", "Sistema" }.Contains(Theme)) Theme = "Escuro";
            if (!new[] { "Acrilico", "Desfoque", "Nenhum" }.Contains(Glass)) Glass = "Acrilico";
            if (!new[] { "Embaixo", "Esquerda", "Direita" }.Contains(Position)) Position = "Embaixo";
            if (!new[] { "Esconder", "AutoOcultar", "Normal" }.Contains(TaskbarMode)) TaskbarMode = "Esconder";
            if (string.IsNullOrWhiteSpace(IndicatorColor)) IndicatorColor = "#4CC2FF";
            WeatherCity = (WeatherCity ?? "").Trim();
            if (Apps == null) Apps = new List<PinnedApp>();
            Apps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Path));
        }

        static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

        /// <summary>Começa com o Explorador e os apps que já estavam fixados na barra do Windows.</summary>
        public static List<PinnedApp> SeedFromTaskbar()
        {
            var list = new List<PinnedApp> { new PinnedApp { Name = "Explorador de Arquivos", Path = ExplorerPath } };
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(dir)) return list;
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                    string target = ShellIcons.ResolveLink(lnk, out string args);
                    if (string.IsNullOrEmpty(target))
                    {
                        if (name.IndexOf("explor", StringComparison.OrdinalIgnoreCase) >= 0) continue; // já está na lista
                        list.Add(new PinnedApp { Name = name, Path = lnk });
                        continue;
                    }
                    bool exe = string.Equals(System.IO.Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase);
                    list.Add(exe && File.Exists(target)
                        ? new PinnedApp { Name = name, Path = target, Args = args }
                        : new PinnedApp { Name = name, Path = lnk });
                }
            }
            catch (Exception ex) { App.Log(ex); }
            return list;
        }

        public static string NiceName(string path)
        {
            try
            {
                if (Directory.Exists(path)) return new DirectoryInfo(path).Name;
                if (string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    string d = (vi.FileDescription ?? "").Trim();
                    if (d.Length > 1 && d.Length < 40) return d;
                    string p = (vi.ProductName ?? "").Trim();
                    if (p.Length > 1 && p.Length < 40) return p;
                }
            }
            catch { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }
    }
}
