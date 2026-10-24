using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Cais
{
    internal enum Sky { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Storm }

    internal class WeatherInfo
    {
        public double Temperature;
        public Sky Sky;
        public bool IsDay = true;
        public string Description;
        public string Place;
    }

    /// <summary>
    /// Clima atual pelo Open-Meteo (gratuito, sem conta nem chave). A localização vem do Windows
    /// ou da cidade digitada nas configurações.
    /// </summary>
    internal static class Weather
    {
        static readonly Dictionary<string, Tuple<double, double, string>> cityCache = new Dictionary<string, Tuple<double, double, string>>(StringComparer.OrdinalIgnoreCase);

        public static Task<WeatherInfo> FetchAsync(string city) => Task.Run(() => Fetch(city));

        static WeatherInfo Fetch(string city)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var place = Locate(city);
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}&current=temperature_2m,weather_code,is_day&timezone=auto",
                place.Item1, place.Item2);
            var json = Get(url);
            var current = (Dictionary<string, object>)json["current"];
            int code = Convert.ToInt32(current["weather_code"], CultureInfo.InvariantCulture);
            var sky = SkyOf(code);
            return new WeatherInfo
            {
                Temperature = Convert.ToDouble(current["temperature_2m"], CultureInfo.InvariantCulture),
                IsDay = Convert.ToInt32(current["is_day"], CultureInfo.InvariantCulture) == 1,
                Sky = sky,
                Description = Describe(code),
                Place = place.Item3,
            };
        }

        static Tuple<double, double, string> Locate(string city)
        {
            if (!string.IsNullOrWhiteSpace(city))
            {
                city = city.Trim();
                if (cityCache.TryGetValue(city, out var cached)) return cached;
                var json = Get("https://geocoding-api.open-meteo.com/v1/search?count=1&language=pt&format=json&name=" + Uri.EscapeDataString(city));
                if (!json.TryGetValue("results", out var r) || !(r is object[] results) || results.Length == 0)
                    throw new InvalidOperationException("Cidade não encontrada: " + city);
                var first = (Dictionary<string, object>)results[0];
                string name = first["name"] as string;
                if (first.TryGetValue("admin1", out var state) && state is string s && s != name) name += ", " + s;
                var found = Tuple.Create(Convert.ToDouble(first["latitude"], CultureInfo.InvariantCulture), Convert.ToDouble(first["longitude"], CultureInfo.InvariantCulture), name);
                cityCache[city] = found;
                return found;
            }

            using (var watcher = new GeoCoordinateWatcher(GeoPositionAccuracy.Default))
            {
                if (watcher.TryStart(false, TimeSpan.FromSeconds(4)))
                {
                    var loc = watcher.Position.Location;
                    if (!loc.IsUnknown) return Tuple.Create(loc.Latitude, loc.Longitude, "Sua localização");
                }
            }
            throw new InvalidOperationException("Localização do Windows indisponível. Digite sua cidade nas configurações.");
        }

        static Dictionary<string, object> Get(string url)
        {
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Cais/1.0 (+https://github.com/londarks/cais)";
                return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(wc.DownloadString(url));
            }
        }

        // Códigos WMO usados pelo Open-Meteo
        static Sky SkyOf(int code)
        {
            if (code == 0) return Sky.Clear;
            if (code <= 2) return Sky.PartlyCloudy;
            if (code == 3) return Sky.Cloudy;
            if (code == 45 || code == 48) return Sky.Fog;
            if (code >= 51 && code <= 57) return Sky.Drizzle;
            if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return Sky.Rain;
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return Sky.Snow;
            if (code >= 95) return Sky.Storm;
            return Sky.Cloudy;
        }

        static string Describe(int code)
        {
            switch (code)
            {
                case 0: return "Céu limpo";
                case 1: return "Predominantemente limpo";
                case 2: return "Parcialmente nublado";
                case 3: return "Nublado";
                case 45: case 48: return "Neblina";
                case 51: case 53: case 55: return "Garoa";
                case 56: case 57: return "Garoa congelante";
                case 61: return "Chuva fraca";
                case 63: return "Chuva";
                case 65: return "Chuva forte";
                case 66: case 67: return "Chuva congelante";
                case 71: case 73: case 75: case 77: return "Neve";
                case 80: return "Pancadas de chuva fracas";
                case 81: return "Pancadas de chuva";
                case 82: return "Pancadas de chuva fortes";
                case 85: case 86: return "Pancadas de neve";
                case 95: return "Trovoadas";
                case 96: case 99: return "Trovoadas com granizo";
                default: return "Tempo instável";
            }
        }

        // ───────── ícones coloridos (desenhados, como os do widget do Windows) ─────────

        static readonly Brush SunBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x3D)));
        static readonly Brush MoonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE9, 0xD9, 0xA6)));
        static readonly Brush CloudBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF1)));
        static readonly Brush CloudEdge = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
        static readonly Brush RainBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)));
        static readonly Brush FogBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xBF, 0xCC)));

        static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        const string CloudPath = "M7 18.5h10.2a4.1 4.1 0 0 0 .5-8.17A5.6 5.6 0 0 0 6.9 9.3a4.6 4.6 0 0 0 .1 9.2z";

        public static ImageSource Icon(Sky sky, bool day)
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
            switch (sky)
            {
                case Sky.Clear:
                    if (day) AddSun(g, 12, 12, 1); else AddMoon(g, 0, 0, 1);
                    break;
                case Sky.PartlyCloudy:
                    if (day) AddSun(g, 8.5, 8.5, 0.72); else AddMoon(g, -3, -3.5, 0.8);
                    AddCloud(g, 1.5, 1.5, 0.95);
                    break;
                case Sky.Cloudy:
                    AddCloud(g, -3.5, -2.5, 0.75, true);
                    AddCloud(g, 1, 1.5, 0.95);
                    break;
                case Sky.Fog:
                    AddCloud(g, 0, -3, 0.9);
                    AddLines(g, FogBrush, "M5 19.5h14M7 22h10");
                    break;
                case Sky.Drizzle:
                    AddCloud(g, 0, -3.5, 1);
                    AddLines(g, RainBrush, "M9 19v.6M13 19v.6M17 19v.6M11 22v.6M15 22v.6");
                    break;
                case Sky.Rain:
                    AddCloud(g, 0, -3.5, 1);
                    AddLines(g, RainBrush, "M9 18.5l-1 3M13 18.5l-1 3M17 18.5l-1 3");
                    break;
                case Sky.Snow:
                    AddCloud(g, 0, -3.5, 1);
                    AddLines(g, Brushes.White, "M9 19.5v.4M13 19.5v.4M17 19.5v.4M11 22.3v.4M15 22.3v.4");
                    break;
                case Sky.Storm:
                    AddCloud(g, 0, -3.5, 1);
                    g.Children.Add(new GeometryDrawing(SunBrush, null, Geometry.Parse("M13 15.5l-3 4.5h2.6l-1.6 4 4.5-5.6h-2.7l1.7-2.9z")));
                    break;
            }
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        static void AddSun(DrawingGroup g, double cx, double cy, double k)
        {
            var sun = new DrawingGroup { Transform = new MatrixTransform(k, 0, 0, k, cx - 12 * k, cy - 12 * k) };
            sun.Children.Add(new GeometryDrawing(SunBrush, null, new EllipseGeometry(new Point(12, 12), 4.6, 4.6)));
            var rays = new StreamGeometry();
            using (var c = rays.Open())
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    c.BeginFigure(new Point(12 + Math.Cos(a) * 7.2, 12 + Math.Sin(a) * 7.2), false, false);
                    c.LineTo(new Point(12 + Math.Cos(a) * 9.6, 12 + Math.Sin(a) * 9.6), true, false);
                }
            sun.Children.Add(new GeometryDrawing(null, new Pen(SunBrush, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, rays));
            g.Children.Add(sun);
        }

        static void AddMoon(DrawingGroup g, double dx, double dy, double k)
        {
            var moon = new DrawingGroup { Transform = new MatrixTransform(k, 0, 0, k, dx, dy) };
            var shape = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(11.5, 12.5), 7.5, 7.5), new EllipseGeometry(new Point(16, 8.5), 6, 6));
            moon.Children.Add(new GeometryDrawing(MoonBrush, null, shape));
            g.Children.Add(moon);
        }

        static void AddCloud(DrawingGroup g, double dx, double dy, double k, bool back = false)
        {
            var cloud = new DrawingGroup { Transform = new MatrixTransform(k, 0, 0, k, dx + 12 * (1 - k), dy + 12 * (1 - k)), Opacity = back ? 0.7 : 1 };
            cloud.Children.Add(new GeometryDrawing(CloudBrush, new Pen(CloudEdge, 0.8), Geometry.Parse(CloudPath)));
            g.Children.Add(cloud);
        }

        static void AddLines(DrawingGroup g, Brush brush, string path) =>
            g.Children.Add(new GeometryDrawing(null, new Pen(brush, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse(path)));
    }
}
