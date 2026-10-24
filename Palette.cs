using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Cais
{
    /// <summary>Cores do Dock e da janela de configurações, no escuro e no claro.</summary>
    internal static class Palette
    {
        public static bool IsDark(DockConfig c)
        {
            if (c.Theme == "Claro") return false;
            if (c.Theme == "Escuro") return true;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0;
            }
            catch { }
            return true;
        }

        public static Color Parse(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }

        /// <summary>Cor de destaque escolhida no Windows (Personalização › Cores).</summary>
        public static string WindowsAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    if (k?.GetValue("AccentColor") is int abgr)
                        return string.Format("#{0:X2}{1:X2}{2:X2}", abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
            catch { }
            return "#4CC2FF";
        }

        public static void ApplyBrushes(ResourceDictionary res, bool dark)
        {
            void Set(string key, string hex)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                res[key] = b;
            }

            if (dark)
            {
                Set("WinBg", "#202128"); Set("CardBg", "#2B2C34"); Set("CardHover", "#34353E"); Set("CardBorder", "#12FFFFFF");
                Set("TextPrimary", "#EEF0F4"); Set("TextMuted", "#A7ACB9"); Set("NavActive", "#16FFFFFF"); Set("NavHover", "#0BFFFFFF");
                Set("Accent", "#4CC2FF"); Set("OnAccent", "#05202E"); Set("SegBg", "#1C1D23"); Set("TrackOff", "#6B7080");
                Set("ThumbOuter", "#45464F"); Set("MenuBg", "#F5282A31"); Set("MenuBorder", "#1FFFFFFF"); Set("Danger", "#FF99A4");
            }
            else
            {
                Set("WinBg", "#F3F4F7"); Set("CardBg", "#FFFFFF"); Set("CardHover", "#F4F5F8"); Set("CardBorder", "#16141828");
                Set("TextPrimary", "#1B1D25"); Set("TextMuted", "#5B6172"); Set("NavActive", "#10141828"); Set("NavHover", "#08141828");
                Set("Accent", "#0067C0"); Set("OnAccent", "#FFFFFF"); Set("SegBg", "#ECEEF2"); Set("TrackOff", "#8A90A0");
                Set("ThumbOuter", "#FFFFFF"); Set("MenuBg", "#FAFCFCFD"); Set("MenuBorder", "#1A141828"); Set("Danger", "#C42B1C");
            }
        }
    }
}
