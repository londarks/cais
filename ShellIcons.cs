using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cais
{
    /// <summary>Ícones em alta resolução direto do shell do Windows, e leitura de atalhos .lnk.</summary>
    internal static class ShellIcons
    {
        public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bmp);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Get(string parsingName, int size = 256)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            string key = parsingName + "|" + size;
            if (cache.TryGetValue(key, out var img)) return img;
            img = Load(parsingName, size);
            cache[key] = img;
            return img;
        }

        public static void Forget(string parsingName, int size = 256) => cache.Remove(parsingName + "|" + size);

        static ImageSource Load(string name, int size)
        {
            var img = LoadRaw(name, size, out double fill);
            // Apps sem ícone grande vêm como um ícone de 32/48 px no meio de um quadro de 256 px.
            // Nesse caso, pega o maior tamanho que realmente preenche o quadro.
            if (img != null && size > 64 && fill < 0.6)
            {
                foreach (int s in new[] { 128, 96, 64, 48, 32 })
                {
                    if (s >= size) continue;
                    var alt = LoadRaw(name, s, out double f);
                    if (alt != null && f >= 0.7) return alt;
                }
            }
            return img;
        }

        static BitmapSource LoadRaw(string name, int size, out double fill)
        {
            fill = 0;
            IShellItemImageFactory factory = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                // 0x4 = SIIGBF_ICONONLY: sempre o ícone, nunca miniatura
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                return FromHBitmap(hbmp, out fill);
            }
            catch { return null; }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        static BitmapSource FromHBitmap(IntPtr hbmp, out double fill)
        {
            fill = 0;
            var bm = new BITMAP();
            GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), ref bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0) return null;

            var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbmp, 0, (uint)h, px, ref info, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }

            bool anyAlpha = false, premultiplied = true;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) anyAlpha = true;
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a) premultiplied = false;
                if (a > 24)
                {
                    int p = i / 4, x = p % w, y = p / w;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (!anyAlpha)
            {
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                premultiplied = true;
                fill = 1;
            }
            else if (maxX >= 0)
                fill = Math.Max(maxX - minX + 1, maxY - minY + 1) / (double)Math.Max(w, h);

            var bs = BitmapSource.Create(w, h, 96, 96, premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }

        // Atalhos .lnk
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        }

        public static string ResolveLink(string lnk) => ResolveLink(lnk, out _);

        public static string ResolveLink(string lnk, out string args)
        {
            args = "";
            object obj = null;
            try
            {
                obj = new CShellLink();
                ((IPersistFile)obj).Load(lnk, 0);
                var link = (IShellLinkW)obj;
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                var ab = new StringBuilder(2048);
                link.GetArguments(ab, ab.Capacity);
                args = ab.ToString();
                return sb.ToString();
            }
            catch { return null; }
            finally { if (obj != null) Marshal.ReleaseComObject(obj); }
        }

        // Ícones desenhados
        public static ImageSource StartIcon()
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
            var brush = new LinearGradientBrush(Color.FromRgb(0x7A, 0xD4, 0xFF), Color.FromRgb(0x00, 0x6C, 0xD6), new Point(0, 0), new Point(100, 100))
            {
                MappingMode = BrushMappingMode.Absolute
            };
            double pad = 15, gap = 6, cell = (100 - 2 * pad - gap) / 2;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    g.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(pad + i * (cell + gap), pad + j * (cell + gap), cell, cell), 6, 6)));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        public static ImageSource Fallback(string name)
        {
            string letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x70)), null, new RectangleGeometry(new Rect(8, 8, 84, 84), 20, 20)));
            var ft = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 46, Brushes.White, 1.0);
            g.Children.Add(new GeometryDrawing(Brushes.White, null, ft.BuildGeometry(new Point(50 - ft.Width / 2, 50 - ft.Height / 2))));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }
    }
}
