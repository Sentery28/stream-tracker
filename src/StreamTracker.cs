// Трекер стримов: живёт в трее, сам замечает эфир на Twitch и считает стримы за месяц,
// напоминает про отчёты и собирает скриншоты статистики. Канал, цель и дни отчётов — в настройках.
// Сборка: build.ps1 (компилятор C# из .NET Framework, язык C# 5).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Трекер стримов")]
[assembly: System.Reflection.AssemblyProduct("Трекер стримов")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]

namespace StreamTracker
{
    static class Config
    {
        // Значения из настроек (settings.json), задаются при первом запуске
        public static string Channel = "";
        public static bool ByLink;               // канал указан ссылкой — без входа, статистика Twitch недоступна
        public static int Goal = 20;
        public static int[] ReportDays = { 13, 29 };

        // Отчёт сдаётся до конца дня
        public const string Until = "23:59";

        public static void Apply(Settings s)
        {
            Channel = s.channel;
            ByLink = s.byLink;
            Goal = s.goal;
            ReportDays = s.reportDays.Distinct().OrderBy(x => x).ToArray();
        }
        public const int RemindFromHour = 12;    // в день отчёта напоминать начиная с этого часа
        public const int RemindEveryHours = 3;   // и повторять каждые N часов, пока не отмечено «сдан»
        public const int ReconnectMinutes = 60;  // эфир прервался и начался снова в течение часа — тот же стрим
        public const int CheckMinutes = 10;
        public const string Fmt = "yyyy-MM-dd HH:mm";
        public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    }

    public class StreamRec
    {
        public string start { get; set; }
        public string end { get; set; }
        public string id { get; set; }     // id трансляций Twitch через запятую; пусто — добавлен вручную
    }

    public class TrackerData
    {
        public List<StreamRec> streams { get; set; }
        public List<string> reports { get; set; }   // даты сданных отчётов (yyyy-MM-dd)
        public string lastReminder { get; set; }
        public TrackerData() { streams = new List<StreamRec>(); reports = new List<string>(); lastReminder = ""; }
    }

    static class Store
    {
        public static readonly string DataPath = Path.Combine(Paths.Data, "streams.json");

        // true, если файл со стримами был повреждён и трекер начал с чистого (копия сохранена рядом)
        public static bool Recovered;

        public static TrackerData Load()
        {
            TrackerData d = null;
            if (File.Exists(DataPath))
            {
                try { d = new JavaScriptSerializer().Deserialize<TrackerData>(File.ReadAllText(DataPath, Encoding.UTF8)); }
                catch (Exception ex)
                {
                    // Файл испорчен (например, выключили питание во время записи) — не падаем и не теряем его:
                    // откладываем копию, а стримы потом подтянутся из статистики Twitch
                    Log.Error("streams.json повреждён", ex);
                    try { File.Copy(DataPath, DataPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
                    Recovered = true;
                }
            }
            if (d == null) d = new TrackerData();
            if (d.streams == null) d.streams = new List<StreamRec>();
            DateTime tmp;
            d.streams = d.streams.Where(s => s != null && !string.IsNullOrEmpty(s.start) &&
                                             DateTime.TryParseExact(s.start, Config.Fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out tmp))
                                 .ToList();
            foreach (var s in d.streams)
            {
                if (s.end == null || (s.end != "" &&
                    !DateTime.TryParseExact(s.end, Config.Fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out tmp))) s.end = "";
                if (s.id == null) s.id = "";
            }
            if (d.reports == null) d.reports = new List<string>();
            if (d.lastReminder == null) d.lastReminder = "";
            return d;
        }

        public static void Save(TrackerData d)
        {
            d.streams = d.streams.OrderBy(s => s.start, StringComparer.Ordinal).ToList();
            d.reports = d.reports.Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
            Directory.CreateDirectory(Paths.Data);
            string tmp = DataPath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
            if (File.Exists(DataPath)) File.Replace(tmp, DataPath, null);
            else File.Move(tmp, DataPath);
        }

        public static DateTime Parse(string s) { return DateTime.ParseExact(s, Config.Fmt, CultureInfo.InvariantCulture); }
        public static string Format(DateTime t) { return t.ToString(Config.Fmt, CultureInfo.InvariantCulture); }
    }

    static class Logic
    {
        // Сроки отчётов в месяце. В феврале без 29-го числа срок переносится на последний день.
        public static List<DateTime> Deadlines(DateTime month)
        {
            int last = DateTime.DaysInMonth(month.Year, month.Month);
            return Config.ReportDays
                .Select(d => new DateTime(month.Year, month.Month, Math.Min(d, last), 23, 59, 0))
                .ToList();
        }

        // Отчёт, о котором сейчас речь: в день отчёта — сегодняшний до полуночи (даже если срок уже прошёл,
        // чтобы его можно было отметить сданным поздно вечером), в остальные дни — ближайший
        public static DateTime CurrentDeadline(DateTime now)
        {
            return TodayDeadline(now) ?? NextDeadline(now);
        }

        public static DateTime NextDeadline(DateTime now)
        {
            var first = new DateTime(now.Year, now.Month, 1);
            return Deadlines(first).Concat(Deadlines(first.AddMonths(1))).First(d => d > now);
        }

        public static DateTime? TodayDeadline(DateTime now)
        {
            foreach (var d in Deadlines(now))
                if (d.Date == now.Date) return d;
            return null;
        }

        public static string Key(DateTime deadline) { return deadline.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        public static List<StreamRec> MonthStreams(TrackerData d, DateTime now)
        {
            string prefix = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return d.streams.Where(s => s.start.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }

        // Записывает эфир. Возвращает true, если это новый стрим, а не продолжение уже засчитанного.
        public static bool RegisterLive(TrackerData d, string liveId, DateTime liveStart, DateTime now)
        {
            var existing = d.streams.FirstOrDefault(s => s.id != "" && s.id.Split(',').Contains(liveId));
            if (existing == null)
            {
                // Переподключение: у Twitch новый id, но прошлый стрим закончился совсем недавно
                existing = d.streams
                    .Where(s => s.id != "" && s.end != "")
                    .Where(s => (liveStart - Store.Parse(s.end)).TotalMinutes <= Config.ReconnectMinutes
                                && liveStart >= Store.Parse(s.start))
                    .OrderBy(s => s.start, StringComparer.Ordinal)
                    .LastOrDefault();
                if (existing != null) existing.id += "," + liveId;
            }
            if (existing == null)
            {
                // Стрим за этот день уже добавлен вручную — превращаем его в автоматический, не считая дважды
                existing = d.streams.FirstOrDefault(s => s.id == "" && Store.Parse(s.start).Date == liveStart.Date);
                if (existing != null)
                {
                    existing.id = liveId;
                    existing.start = Store.Format(liveStart);
                }
            }
            if (existing != null)
            {
                existing.end = Store.Format(now);
                return false;
            }
            d.streams.Add(new StreamRec { start = Store.Format(liveStart), end = Store.Format(now), id = liveId });
            return true;
        }
    }

    class LiveInfo
    {
        public string Id;
        public DateTime Start;
    }

    static class Twitch
    {
        // Тот же публичный запрос, что делает сам сайт Twitch. null — канал офлайн.
        public static LiveInfo GetLive()
        {
            var user = Query(Config.Channel, "stream{id createdAt}");
            if (user == null) throw new Exception("Twitch не вернул данные о канале");
            var stream = Get(user, "stream");
            if (stream == null) return null;

            DateTime start = DateTime.Parse(Convert.ToString(stream["createdAt"]), CultureInfo.InvariantCulture,
                                            DateTimeStyles.RoundtripKind);
            if (start.Kind == DateTimeKind.Utc) start = start.ToLocalTime();
            return new LiveInfo { Id = Convert.ToString(stream["id"]), Start = start };
        }

        // Есть ли такой канал: его отображаемое имя или null, если канала нет
        public static string FindUser(string login)
        {
            var user = Query(login, "login displayName");
            if (user == null) return null;
            object name;
            return user.TryGetValue("displayName", out name) && Convert.ToString(name) != "" ? Convert.ToString(name) : login;
        }

        // Запрос user(login) к публичному GQL Twitch; null — такого канала нет
        static Dictionary<string, object> Query(string login, string fields)
        {
            if (!Regex.IsMatch(login ?? "", @"^[A-Za-z0-9_]{1,25}$")) throw new Exception("неверный ник канала");
            var req = (HttpWebRequest)WebRequest.Create("https://gql.twitch.tv/gql");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");
            byte[] body = Encoding.UTF8.GetBytes(
                "{\"query\":\"query{user(login:\\\"" + login + "\\\"){" + fields + "}}\"}");
            using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);

            string text;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = r.ReadToEnd();

            var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("data")) throw new Exception("Twitch не ответил");
            return Get(Get(root, "data"), "user");
        }

        static Dictionary<string, object> Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
    }

    static class Desktop
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        // Открыта ли сейчас игра или видео на весь экран (тогда напоминаем уведомлением в углу, а не окном)
        public static bool FullscreenAppActive()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;   // рабочий стол и панель задач
                RECT r;
                if (!GetWindowRect(h, out r)) return false;
                var screen = Screen.FromHandle(h).Bounds;
                return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
            }
            catch { return false; }
        }
    }

    static class Icons
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        // Значок в трее: число стримов за месяц. Фиолетовый — обычно, красный — в эфире, зелёный — цель выполнена.
        public static Icon Tray(int count, bool live)
        {
            int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(s, s))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Color bg = live ? Color.FromArgb(235, 4, 0)
                         : count >= Config.Goal ? Color.FromArgb(0, 150, 70)
                         : Color.FromArgb(145, 70, 255);
                using (var path = Rounded(new RectangleF(0, 0, s - 1, s - 1), s / 4f))
                using (var b = new SolidBrush(bg))
                    g.FillPath(b, path);

                string txt = Math.Min(count, 99).ToString(CultureInfo.InvariantCulture);
                float size = s * (txt.Length > 1 ? 0.58f : 0.7f);
                using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(txt, f, Brushes.White, new RectangleF(-1, 0, s + 2, s + 1), sf);

                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------- Оформление: тёмная тема в стиле Twitch ----------

    static class Theme
    {
        public static readonly Color Bg = C(0x0E0E10);
        public static readonly Color Card = C(0x18181B);
        public static readonly Color CardHi = C(0x26262C);
        public static readonly Color Line = C(0x2F2F35);
        public static readonly Color Text = C(0xEFEFF1);
        public static readonly Color Muted = C(0xADADB8);
        public static readonly Color Faint = C(0x5C5C66);
        public static readonly Color Accent = C(0x9146FF);
        public static readonly Color AccentHi = C(0xA970FF);
        public static readonly Color AccentLight = C(0xC9A8FF);
        public static readonly Color Red = C(0xEB0400);
        public static readonly Color Green = C(0x2BD67B);
        public static readonly Color Orange = C(0xFFA62B);

        public static readonly string IconFont = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
        public const string GlyphCalendar = "";
        public const string GlyphRefresh = "";
        public const string GlyphCheck = "";

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        static string Pick(params string[] names)
        {
            using (var fc = new InstalledFontCollection())
            {
                var installed = new HashSet<string>(fc.Families.Select(f => f.Name));
                foreach (var n in names)
                    if (installed.Contains(n)) return n;
            }
            return "Segoe UI Symbol";
        }

        // Segoe UI Variable — шрифт Windows 11 (оптические размеры: Text для мелкого, Display для крупного).
        // На Windows 10 его нет — тогда обычный Segoe UI.
        public static readonly bool HasVariable = Pick("Segoe UI Variable Text", "") == "Segoe UI Variable Text";

        static Font Get(string family, float px, FontStyle style)
        {
            string key = family + "|" + px + "|" + style;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(family, px, style, GraphicsUnit.Pixel);
                fonts[key] = f;
            }
            return f;
        }

        // Размеры — в пикселях при 96 dpi; при рисовании текст масштабируется под экран
        public static Font F(float px)
        {
            return Get(!HasVariable ? "Segoe UI" : px >= 18 ? "Segoe UI Variable Display" : "Segoe UI Variable Text",
                       px, FontStyle.Regular);
        }

        public static Font Semi(float px)
        {
            return Get(!HasVariable ? "Segoe UI Semibold"
                       : px >= 18 ? "Segoe UI Variable Display Semib" : "Segoe UI Variable Text Semibold",
                       px, FontStyle.Regular);
        }

        public static Font Bold(float px)
        {
            return Get(HasVariable ? "Segoe UI Variable Display" : "Segoe UI", px, FontStyle.Bold);
        }

        public static Font Icon(float px) { return Get(IconFont, px, FontStyle.Regular); }

        // Шрифт в пунктах для стандартных элементов (меню трея, подсказки)
        public static Font Ui(float pt, bool semibold = false)
        {
            string family = HasVariable ? (semibold ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text")
                                        : (semibold ? "Segoe UI Semibold" : "Segoe UI");
            return new Font(family, pt);
        }

        // Тот же шрифт в реальных пикселях экрана
        static Font Scaled(Font f, float k)
        {
            return k == 1f ? f : Get(f.Name, (float)Math.Round(f.Size * k, 1), f.Style);
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c); }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color c, float width, RectangleF r, float rad)
        {
            using (var p = Round(r, rad))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        // Текст рисуется системным GDI с ClearType (как в обычных окнах Windows) — чётко, без «мыла».
        // GDI не знает про масштаб Graphics, поэтому координаты и шрифт пересчитываются вручную.
        const TextFormatFlags Base = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        const TextFormatFlags Line1 = Base | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping;

        static float ScaleOf(Graphics g)
        {
            using (var m = g.Transform) return m.Elements[0];
        }

        static Rectangle ToPx(RectangleF r, float k)
        {
            return Rectangle.FromLTRB((int)Math.Round(r.Left * k), (int)Math.Round(r.Top * k),
                                      (int)Math.Round(r.Right * k), (int)Math.Round(r.Bottom * k));
        }

        public static void Str(Graphics g, string s, Font f, Color c, float x, float y)
        {
            float k = ScaleOf(g);
            TextRenderer.DrawText(g, s, Scaled(f, k), new Point((int)Math.Round(x * k), (int)Math.Round(y * k)), c, Line1);
        }

        public static void StrRight(Graphics g, string s, Font f, Color c, float right, float y)
        {
            float k = ScaleOf(g);
            TextRenderer.DrawText(g, s, Scaled(f, k), ToPx(new RectangleF(right - 1000, y, 1000, 200), k), c,
                                  Line1 | TextFormatFlags.Right);
        }

        public static void StrCenter(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            float k = ScaleOf(g);
            TextRenderer.DrawText(g, s, Scaled(f, k), ToPx(r, k), c,
                                  Line1 | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public static void StrWrap(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            float k = ScaleOf(g);
            TextRenderer.DrawText(g, s, Scaled(f, k), ToPx(r, k), c, Base | TextFormatFlags.WordBreak);
        }

        public static float Width(Graphics g, string s, Font f)
        {
            float k = ScaleOf(g);
            return TextRenderer.MeasureText(g, s, Scaled(f, k), Size.Empty, Line1).Width / k;
        }

        public static float WrapHeight(string s, Font f, float width, float k)
        {
            var sz = TextRenderer.MeasureText(s, Scaled(f, k), new Size((int)(width * k), int.MaxValue),
                                              Base | TextFormatFlags.WordBreak);
            return sz.Height / k;
        }

        public static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static string Plural(int n, string one, string few, string many)
        {
            int a = Math.Abs(n) % 100, b = a % 10;
            if (a > 10 && a < 20) return many;
            if (b == 1) return one;
            if (b >= 2 && b <= 4) return few;
            return many;
        }

        public static readonly string[] MonthIn =
            { "январе", "феврале", "марте", "апреле", "мае", "июне", "июле", "августе", "сентябре", "октябре", "ноябре", "декабре" };

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Тёмный заголовок окна и скруглённые углы (Windows 11; на Windows 10 просто игнорируется)
        public static void StyleWindow(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            }
            catch { }
        }

        // Скругление углов всплывающего окна, как у меню Windows 11 (3 — малый радиус)
        public static void RoundCorners(IntPtr hwnd, bool small)
        {
            try
            {
                int pref = small ? 3 : 2;
                DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
            }
            catch { }
        }

        public static float ScreenScale()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        public static float Scale(Control c)
        {
            using (var g = c.CreateGraphics()) return g.DpiX / 96f;
        }
    }

    // Окно, которое целиком рисует себя само; кликабельные места — прямоугольники с действием и подсказкой
    abstract class PaintedForm : Form
    {
        protected class Hit
        {
            public string Key;
            public RectangleF R;
            public Action Click;
            public string Tip;
        }

        protected readonly List<Hit> Hits = new List<Hit>();
        protected string HoverKey;
        protected float K = 1f;
        readonly ToolTip tip = new ToolTip { OwnerDraw = true, InitialDelay = 250, ReshowDelay = 50, AutoPopDelay = 15000 };
        static readonly Font TipFont = Theme.Ui(9.5f);
        string currentTip;

        protected PaintedForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            K = Theme.Scale(this);

            tip.Popup += (s, e) =>
            {
                var sz = TextRenderer.MeasureText(tip.GetToolTip(this), TipFont);
                e.ToolTipSize = new Size(sz.Width + 20, sz.Height + 12);
            };
            tip.Draw += (s, e) =>
            {
                e.Graphics.Clear(Theme.CardHi);
                using (var pen = new Pen(Theme.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, new Point(10, 6), Theme.Text,
                    TextFormatFlags.NoPrefix);
            };
        }

        protected void SetSize(int w, int h)
        {
            ClientSize = new Size((int)Math.Round(w * K), (int)Math.Round(h * K));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle);
        }

        protected abstract void Draw(Graphics g);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.Prepare(g);
            g.ScaleTransform(K, K);
            Hits.Clear();
            Draw(g);
        }

        protected bool IsHover(string key) { return key != null && key == HoverKey; }

        Hit Find(Point p)
        {
            var pt = new PointF(p.X / K, p.Y / K);
            for (int i = Hits.Count - 1; i >= 0; i--)
                if (Hits[i].R.Contains(pt)) return Hits[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = Find(e.Location);
            string key = h == null ? null : h.Key;
            Cursor = h != null && h.Click != null ? Cursors.Hand : Cursors.Default;
            if (key != HoverKey)
            {
                HoverKey = key;
                Invalidate();
            }
            string t = h == null ? null : h.Tip;
            if (t != currentTip)
            {
                currentTip = t;
                tip.SetToolTip(this, t ?? "");
                if (t == null) tip.Hide(this);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            HoverKey = null;
            currentTip = null;
            tip.Hide(this);
            Invalidate();
        }

        // Клик засчитывается, только если кнопку мыши нажали и отпустили на одном и том же месте.
        // Иначе окно, открывшееся прямо под курсором, «нажимало» бы то, что оказалось под ним.
        string pressedKey;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var h = Find(e.Location);
            pressedKey = h == null ? null : h.Key;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            string pressed = pressedKey;
            pressedKey = null;
            var h = Find(e.Location);
            if (h != null && h.Click != null && h.Key == pressed)
            {
                tip.Hide(this);
                h.Click();
            }
        }

        // Кнопка-плашка; primary — залитая цветом, иначе приглушённая
        protected void DrawButton(Graphics g, string key, RectangleF r, string text, Color fill, Color fg, Action click, string tipText = null)
        {
            Color c = IsHover(key) ? ControlPaint.Light(fill, 0.25f) : fill;
            Theme.FillRound(g, c, r, 10);
            Theme.StrCenter(g, text, Theme.Semi(13.5f), fg, r);
            Hits.Add(new Hit { Key = key, R = r, Click = click, Tip = tipText });
        }
    }

    // Тёмный диалог вместо системного MessageBox
    class DarkDialog : PaintedForm
    {
        const int W = 400;
        readonly string title, message, okText, cancelText;
        readonly Color okColor;
        readonly float msgHeight;
        int height;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        DarkDialog(string title, string message, string okText, string cancelText, Color okColor, bool standalone)
        {
            this.title = title;
            this.message = message;
            this.okText = okText;
            this.cancelText = cancelText;
            this.okColor = okColor;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.Card;
            ShowInTaskbar = standalone;
            StartPosition = standalone ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            Text = title;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            msgHeight = Theme.WrapHeight(message, Theme.F(14.5f), W - 56, K);
            height = (int)(28 + 28 + 12 + msgHeight + 28 + 40 + 24);
            SetSize(W, height);
        }

        public static bool Ask(IWin32Window owner, string title, string message, string ok, string cancel, Color okColor,
                               bool topMost = false)
        {
            using (var d = new DarkDialog(title, message, ok, cancel, okColor, owner == null))
            {
                d.TopMost = topMost;
                var r = owner == null ? d.ShowDialog() : d.ShowDialog(owner);
                return r == DialogResult.OK;
            }
        }

        // Немодальное уведомление поверх всех окон: не забирает фокус (печатать в чате или играть можно дальше)
        // и не блокирует трекер, пока висит. onOk вызывается, если нажали главную кнопку.
        bool noActivate;
        protected override bool ShowWithoutActivation { get { return noActivate; } }

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static DarkDialog Notice(string title, string message, string ok, string cancel, Color okColor, Action onOk)
        {
            var d = new DarkDialog(title, message, ok, cancel, okColor, true) { noActivate = true };
            // Поверх всех окон — напрямую через Windows с флагом «не активировать»: свойство TopMost
            // у WinForms при этом активирует окно и забирает фокус у игры или чата
            const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10;
            SetWindowPos(d.Handle, new IntPtr(-1), 0, 0, 0, 0, NoSize | NoMove | NoActivate);
            d.FormClosed += delegate
            {
                // Немодальное окно WinForms освобождает само после закрытия
                if (d.DialogResult == DialogResult.OK && onOk != null) onOk();
            };
            d.Show();
            return d;
        }

        protected override void Draw(Graphics g)
        {
            Theme.StrokeRound(g, Theme.Line, 1, new RectangleF(0.5f, 0.5f, W - 1, height - 1), 8);
            Theme.Str(g, title, Theme.Semi(19), Theme.Text, 28, 26);
            Theme.StrWrap(g, message, Theme.F(14.5f), Theme.Muted, new RectangleF(28, 66, W - 56, msgHeight + 4));

            float bh = 40, by = height - 24 - bh;
            float okW = Math.Max(110, Theme.Width(g, okText, Theme.Semi(13.5f)) + 40);
            var okR = new RectangleF(W - 28 - okW, by, okW, bh);
            DrawButton(g, "ok", okR, okText, okColor, Color.White, () => Close(DialogResult.OK));
            if (cancelText != null)
            {
                float cW = Math.Max(110, Theme.Width(g, cancelText, Theme.Semi(13.5f)) + 40);
                DrawButton(g, "cancel", new RectangleF(okR.X - 10 - cW, by, cW, bh), cancelText, Theme.CardHi, Theme.Text,
                       () => Close(DialogResult.Cancel));
            }
        }

        void Close(DialogResult r)
        {
            DialogResult = r;
            Close();
        }

        // Esc — отмена. Enter специально не подтверждает: в окнах «Убрать стрим?» и «Выйти?» случайный Enter опасен
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(DialogResult.Cancel); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // Окно без рамки можно таскать за любое место, кроме кнопок
            if (e.Button == MouseButtons.Left && HoverKey == null)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
        }
    }

    // Меню трея по меркам Windows 11: строка 32 px, текст 14 px, подсветка со скруглением 4 px
    // и отступом 4 px от краёв, слева колонка под галочку. Всё рисуется вручную.
    class TrayMenu : ContextMenuStrip
    {
        public const int ItemW = 248, ItemH = 32, SepH = 9;
        static readonly Color MenuBg = Color.FromArgb(255, 32, 32, 36);
        static readonly Color HoverBg = Color.FromArgb(255, 48, 48, 54);
        readonly float k = Theme.ScreenScale();

        public TrayMenu()
        {
            Renderer = new TrayMenuRenderer(k);
            ShowCheckMargin = false;
            ShowImageMargin = false;
            BackColor = MenuBg;
            Padding = new Padding(0, S(4), 0, S(4));
            DropShadowEnabled = true;
        }

        int S(float v) { return (int)Math.Round(v * k); }

        public ToolStripMenuItem AddItem(string text, Action click)
        {
            var it = new ToolStripMenuItem(text) { AutoSize = false, Size = new Size(S(ItemW), S(ItemH)), Margin = Padding.Empty };
            it.Click += delegate { click(); };
            Items.Add(it);
            return it;
        }

        public void AddSeparator()
        {
            Items.Add(new ToolStripSeparator { AutoSize = false, Size = new Size(S(ItemW), S(SepH)), Margin = Padding.Empty });
        }

        // Размер задаём сами: иначе меню ужимается по самому короткому расчёту WinForms и режет текст
        protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
        {
            AutoSize = false;
            int h = Padding.Vertical;
            foreach (ToolStripItem it in Items) h += it.Height;
            Size = new Size(S(ItemW) + 2, h + 2);
            base.OnOpening(e);
        }

        protected override void OnOpened(EventArgs e)
        {
            Theme.RoundCorners(Handle, true);
            base.OnOpened(e);
        }

        class TrayMenuRenderer : ToolStripRenderer
        {
            readonly float k;
            readonly Font font, glyph;

            public TrayMenuRenderer(float k)
            {
                this.k = k;
                font = new Font(Theme.HasVariable ? "Segoe UI Variable Text" : "Segoe UI", 14 * k, GraphicsUnit.Pixel);
                glyph = new Font(Theme.IconFont, 12 * k, GraphicsUnit.Pixel);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(MenuBg);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var pen = new Pen(Theme.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(4 * k, 1 * k, e.Item.Width - 8 * k, e.Item.Height - 2 * k);
                if (e.Item.Selected && e.Item.Enabled) Theme.FillRound(g, HoverBg, r, 4 * k);
                var mi = e.Item as ToolStripMenuItem;
                if (mi != null && mi.Checked)
                    TextRenderer.DrawText(g, Theme.GlyphCheck, glyph, new Rectangle((int)(16 * k), 0, (int)(16 * k), e.Item.Height),
                                          Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var r = new Rectangle((int)(44 * k), 0, e.Item.Width - (int)(56 * k), e.Item.Height);
                TextRenderer.DrawText(e.Graphics, e.Text, font, r, e.Item.Enabled ? Theme.Text : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var pen = new Pen(Theme.Line))
                    e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }
        }
    }

    // ---------- Приложение в трее ----------

    class TrayApp : ApplicationContext
    {
        public TrackerData Data;
        public DateTime? LastCheck;
        public bool LastFailed;
        public bool IsLive;
        // Когда начался идущий сейчас стрим (по записи в календаре). Стрим, начатый 21-го и идущий после полуночи,
        // относится к 21-му — календарь подсвечивает «в эфире» именно этот день, а не «сегодня».
        public DateTime? LiveStart;
        public bool Busy { get { return busy; } }
        public Settings Settings;

        readonly NotifyIcon tray;
        ReportForm reportForm;
        bool syncing;
        readonly MainForm form;
        readonly System.Windows.Forms.Timer checkTimer;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly ToolStripMenuItem autostartItem;
        Icon trayIcon;
        bool busy;

        public TrayApp(Settings settings, bool startHidden, EventWaitHandle showEvent)
        {
            Data = Store.Load();
            Settings = settings;
            form = new MainForm(this);
            var handle = form.Handle;   // создаём окно заранее, чтобы в него можно было слать BeginInvoke

            var menu = new TrayMenu();
            menu.AddItem("Открыть трекер", ShowMain);
            menu.AddSeparator();
            autostartItem = menu.AddItem("Запускать вместе с Windows", () => SetAutostart(!IsAutostart()));
            menu.AddItem("Выход", ExitApp);
            menu.Opening += delegate { autostartItem.Checked = IsAutostart(); };

            tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
            tray.BalloonTipClicked += delegate { ShowMain(); };

            if (IsAutostart()) SetAutostart(true);   // обновить путь, если exe перенесли

            // Повторный запуск exe (например, с ярлыка) просто показывает окно уже работающего трекера
            var listener = new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    form.BeginInvoke(new Action(ShowMain));
                }
            });
            listener.IsBackground = true;
            listener.Start();

            // Скрытый браузер от прошлого запуска (если трекер закрыли во время сбора) — закрываем
            // (через общую очередь, чтобы не столкнуться с первой сверкой через 3 секунды)
            Task.Run(async () =>
            {
                await Chrome.Lock.WaitAsync();
                try { await Chrome.CloseStrayHeadless(); }
                finally { Chrome.Lock.Release(); }
            });

            checkTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            checkTimer.Tick += delegate
            {
                checkTimer.Interval = Config.CheckMinutes * 60 * 1000;
                CheckNow();
                // Раз в день сверяем календарь со статистикой Twitch (если в браузере трекера выполнен вход;
                // канал по ссылке — без входа, браузер не трогаем)
                if (!Config.ByLink && Settings.lastSync != DateTime.Today.ToString("yyyy-MM-dd") &&
                    Directory.Exists(Chrome.ProfileDir) && !IsLive)
                    SyncMonth(true);
            };
            checkTimer.Start();

            uiTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            uiTimer.Tick += delegate { UpdateUi(); };
            uiTimer.Start();

            UpdateUi();
            if (!startHidden) ShowMain();

            if (Store.Recovered)
            {
                // Файл со стримами был повреждён — сразу сверяемся со статистикой Twitch, чтобы восстановить календарь
                Settings.lastSync = "";
                tray.ShowBalloonTip(10000, "Файл со стримами был повреждён",
                    "Копия сохранена в папке данных. Трекер восстановит стримы из статистики Twitch.", ToolTipIcon.Warning);
            }
        }

        public int MonthCount() { return Logic.MonthStreams(Data, DateTime.Now).Count; }

        public void UpdateUi()
        {
            int count = MonthCount();
            var old = trayIcon;
            trayIcon = Icons.Tray(count, IsLive);
            tray.Icon = trayIcon;
            if (old != null) old.Dispose();

            DateTime next = Logic.CurrentDeadline(DateTime.Now);
            string tip = "Стримы: " + count + "/" + Config.Goal + " · отчёт " + next.ToString("dd.MM") +
                         (IsLive ? " · в эфире" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            if (form.Visible) form.RefreshView();
        }

        public void ShowMain()
        {
            form.RefreshView();
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        IWin32Window DialogOwner() { return form.Visible ? form : null; }

        public async void CheckNow()
        {
            if (busy) return;
            busy = true;
            if (form.Visible) form.RefreshView();
            try
            {
                LiveInfo live = null;
                bool ok = true;
                try { live = await Task.Run(() => Twitch.GetLive()); }
                catch { ok = false; }

                DateTime now = DateTime.Now;
                LastCheck = now;
                LastFailed = !ok;
                if (ok)
                {
                    IsLive = live != null;
                    LiveStart = null;
                    if (live != null)
                    {
                        bool isNew = Logic.RegisterLive(Data, live.Id, live.Start, now);
                        var rec = Data.streams.FirstOrDefault(s => s.id != "" && s.id.Split(',').Contains(live.Id));
                        LiveStart = rec != null ? Store.Parse(rec.start) : live.Start;
                        Store.Save(Data);
                        if (isNew)
                            tray.ShowBalloonTip(10000, "Стрим засчитан",
                                "Стримов в этом месяце: " + MonthCount() + " из " + Config.Goal, ToolTipIcon.Info);
                    }
                }
                busy = false;
                UpdateUi();
                MaybeRemind(now);
            }
            finally { busy = false; }
        }

        void MaybeRemind(DateTime now)
        {
            DateTime? dl = Logic.TodayDeadline(now);
            if (dl == null || now >= dl.Value || now.Hour < Config.RemindFromHour) return;
            if (Data.reports.Contains(Logic.Key(dl.Value))) return;
            if (Data.lastReminder != "" &&
                (now - Store.Parse(Data.lastReminder)).TotalHours < Config.RemindEveryHours - 0.1) return;

            // Прошлое напоминание ещё висит на экране — новое не добавляем
            if (reminder != null && !reminder.IsDisposed) return;

            Data.lastReminder = Store.Format(now);
            Store.Save(Data);
            int count = MonthCount();

            if (IsLive || Desktop.FullscreenAppActive())
            {
                // Во время эфира, игры или видео на весь экран не выкидываем окно — только уведомление в углу
                tray.ShowBalloonTip(15000, "Сегодня отчёт до " + Config.Until + "!",
                    "Стримов в этом месяце: " + count + " из " + Config.Goal + ". " +
                    (IsLive ? "После стрима собери отчёт в трекере." : "Собери отчёт в трекере."),
                    ToolTipIcon.Warning);
                return;
            }

            // Окно не модальное и не забирает фокус: трекер продолжает проверять эфир, пока оно висит
            string text = "Стримов в этом месяце: " + count + " из " + Config.Goal +
                          (count < Config.Goal ? " (осталось " + (Config.Goal - count) + ")" : "") + ".\n\n" +
                          (count < Config.Goal && dl.Value.Day != Config.ReportDays[0]
                              ? "Если сегодня ещё будет стрим — собери отчёт после него."
                              : Config.ByLink ? "Не забудь отправить отчёт о стримах."
                                              : "Трекер соберёт скриншоты статистики Twitch и список стримов в папку.");
            reminder = DarkDialog.Notice("Сегодня отчёт до " + Config.Until, text, "Собрать отчёт", "Позже", Theme.Accent, ShowReport);
        }

        DarkDialog reminder;

        public void AddManual(DateTime date)
        {
            DateTime stamp = date.Date == DateTime.Today ? DateTime.Now : date.Date.AddHours(12);
            Data.streams.Add(new StreamRec { start = Store.Format(stamp), end = "", id = "" });
            Store.Save(Data);
            UpdateUi();
            if (!form.Visible)
                tray.ShowBalloonTip(5000, "Стрим добавлен",
                    "Стримов в этом месяце: " + MonthCount() + " из " + Config.Goal, ToolTipIcon.Info);
        }

        public void ShowReport()
        {
            if (reportForm == null || reportForm.IsDisposed)
            {
                reportForm = new ReportForm(this);
                reportForm.FormClosed += delegate { reportForm = null; };
            }
            reportForm.Show();
            if (reportForm.WindowState == FormWindowState.Minimized) reportForm.WindowState = FormWindowState.Normal;
            reportForm.Activate();
        }

        // Стримы из статистики Twitch → календарь трекера. Возвращает, сколько добавлено новых.
        public int Import(IEnumerable<StreamSummary> list)
        {
            int added = 0;
            foreach (var s in list)
            {
                string start = Store.Format(s.Start), end = Store.Format(s.Start + s.Length);
                var near = Data.streams.FirstOrDefault(x => Math.Abs((Store.Parse(x.start) - s.Start).TotalMinutes) <= 90) ??
                           Data.streams.FirstOrDefault(x => x.id == "" && Store.Parse(x.start).Date == s.Start.Date);
                if (near != null)
                {
                    if (near.id == "") near.id = "twitch";
                    near.start = start;
                    if (near.end == "" || string.CompareOrdinal(near.end, end) < 0) near.end = end;
                    continue;
                }
                Data.streams.Add(new StreamRec { start = start, end = end, id = "twitch" });
                added++;
            }
            Store.Save(Data);
            UpdateUi();
            return added;
        }

        // Подтянуть стримы текущего месяца (и текущего периода отчёта) со статистики Twitch
        public async void SyncMonth(bool silent)
        {
            if (syncing) return;
            syncing = true;
            try
            {
                DateTime from, to;
                Dashboard.DefaultPeriod(DateTime.Now, out from, out to);
                var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                if (monthStart < from) from = monthStart;
                var list = await Task.Run(() => Dashboard.Collect(from, DateTime.Today, null, null));
                int added = Import(list);
                Settings.lastSync = DateTime.Today.ToString("yyyy-MM-dd");
                Settings.Save();
                if (!silent || added > 0)
                    tray.ShowBalloonTip(6000, "Стримы с Twitch",
                        added > 0 ? "В календарь добавлено " + added + " " + Theme.Plural(added, "стрим", "стрима", "стримов") +
                                    ". Всего в этом месяце: " + MonthCount() + " из " + Config.Goal
                                  : "Календарь уже совпадает со статистикой Twitch", ToolTipIcon.Info);
            }
            catch (NeedLoginException)
            {
                // Без входа не пытаемся каждые 10 минут (каждый раз это запуск браузера) — до завтра
                // или до сохранения настроек после входа
                Settings.lastSync = DateTime.Today.ToString("yyyy-MM-dd");
                Settings.Save();
                if (!silent)
                    tray.ShowBalloonTip(8000, "Нужно войти в Twitch",
                        "Откройте «Собрать отчёт» — трекер подскажет, как войти.", ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                if (!silent) tray.ShowBalloonTip(8000, "Не удалось подтянуть стримы", ex.Message, ToolTipIcon.Warning);
            }
            finally { syncing = false; }
        }

        public void RemoveStreams(IEnumerable<StreamRec> items)
        {
            foreach (var s in items.ToList()) Data.streams.Remove(s);
            Store.Save(Data);
            UpdateUi();
        }

        public void MarkReport(DateTime deadline)
        {
            Data.reports.Add(Logic.Key(deadline));
            Store.Save(Data);
            UpdateUi();
            if (reminder != null && !reminder.IsDisposed) reminder.Close();   // напоминание больше не нужно
            if (!form.Visible)
                tray.ShowBalloonTip(5000, "Отчёт отмечен", "Отчёт за " + deadline.ToString("dd.MM") + " сдан",
                    ToolTipIcon.Info);
        }

        public void UnmarkReport(DateTime deadline)
        {
            Data.reports.Remove(Logic.Key(deadline));
            Store.Save(Data);
            UpdateUi();
        }

        static bool IsAutostart() { return AppSetup.IsAutostart(); }
        static void SetAutostart(bool on) { AppSetup.SetAutostart(on); }

        public void ShowSettings() { ShowSettings(null); }

        public void ShowSettings(Form owner)
        {
            string oldChannel = Settings.channel;
            if (owner == null) owner = form.Visible ? form : null;
            using (var f = new SetupForm(Settings, false))
            {
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                if (f.ShowDialog(owner) != DialogResult.OK) return;
            }
            Config.Apply(Settings);

            // Сменили канал — стримы прежнего канала иначе считались бы для нового
            if (!string.Equals(oldChannel, Settings.channel, StringComparison.OrdinalIgnoreCase) && Data.streams.Count > 0 &&
                DarkDialog.Ask(owner, "Канал изменён",
                    "В календаре остались стримы канала " + oldChannel + ". Очистить их, чтобы считались только стримы " +
                    Settings.channel + "?" + (Config.ByLink ? "" : " Стримы нового канала трекер подтянет из статистики Twitch."),
                    "Очистить", "Оставить", Theme.Accent))
            {
                Data.streams.Clear();
                Data.reports.Clear();
                Data.lastReminder = "";
                Store.Save(Data);
            }

            IsLive = false;
            UpdateUi();
            CheckNow();
            // Сразу сверяемся со статистикой Twitch: могли только что войти или сменить канал
            Settings.lastSync = "";
            if (!Config.ByLink && Directory.Exists(Chrome.ProfileDir)) SyncMonth(true);
        }

        void ExitApp()
        {
            if (!DarkDialog.Ask(DialogOwner(), "Закрыть трекер?",
                    "Пока трекер закрыт, стримы не засчитываются и напоминаний об отчёте не будет.",
                    "Выйти", "Отмена", Theme.Red)) return;
            checkTimer.Stop();
            uiTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            // Если в этот момент шёл сбор отчёта или сверка, не оставляем скрытый браузер висеть
            try { Task.Run(() => Chrome.CloseStrayHeadless()).Wait(5000); } catch { }
            form.AllowClose = true;
            form.Close();
            ExitThread();
        }
    }

    // ---------- Главное окно ----------

    class MainForm : PaintedForm
    {
        const int W = 440, H = 750;
        readonly TrayApp app;
        public bool AllowClose;

        public MainForm(TrayApp app)
        {
            this.app = app;
            Text = "Трекер стримов";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            SetSize(W, H);
        }

        public void RefreshView() { Invalidate(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик прячет окно в трей, трекер продолжает работать
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void Draw(Graphics g)
        {
            DateTime now = DateTime.Now;
            var month = Logic.MonthStreams(app.Data, now);
            DrawHeader(g, now);
            DrawProgress(g, now, month.Count);
            DrawReport(g, now);
            DrawCalendar(g, now, month);
            DrawLegend(g);
        }

        void DrawHeader(Graphics g, DateTime now)
        {
            Theme.Str(g, now.ToString("MMMM yyyy", Config.Ru).ToUpper(Config.Ru), Theme.Semi(12), Theme.Accent, 22, 20);
            Theme.Str(g, "Трекер стримов", Theme.Bold(25), Theme.Text, 20, 37);

            // Статус эфира — плашка справа, по клику проверяет сейчас
            string label;
            Color bg, fg;
            bool dot = false;
            if (app.Busy) { label = "проверяю…"; bg = Theme.Card; fg = Theme.Muted; }
            else if (app.IsLive) { label = "В ЭФИРЕ"; bg = Theme.Red; fg = Color.White; dot = true; }
            else if (app.LastFailed) { label = "нет связи"; bg = Theme.Alpha(Theme.Orange, 40); fg = Theme.Orange; }
            else if (app.LastCheck.HasValue) { label = "офлайн · " + app.LastCheck.Value.ToString("HH:mm"); bg = Theme.Card; fg = Theme.Muted; }
            else { label = "офлайн"; bg = Theme.Card; fg = Theme.Muted; }

            Font f = app.IsLive ? Theme.Bold(12) : Theme.Semi(12.5f);
            float tw = Theme.Width(g, label, f);
            float pw = tw + 26 + (dot ? 14 : 20);
            var r = new RectangleF(W - 20 - pw, 30, pw, 30);
            Theme.FillRound(g, IsHover("pill") ? ControlPaint.Light(bg, 0.3f) : bg, r, 15);
            float x = r.X + 13;
            if (dot)
            {
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, r.Y + 11, 8, 8);
                x += 14;
            }
            Theme.Str(g, label, f, fg, x, r.Y + 7);
            if (!dot) Theme.Str(g, Theme.GlyphRefresh, Theme.Icon(12), fg, x + tw + 8, r.Y + 9);
            var gear = new RectangleF(r.X - 8 - 30, r.Y, 30, 30);
            if (IsHover("gear")) Theme.FillRound(g, Theme.Card, gear, 15);
            Theme.StrCenter(g, "", Theme.Icon(14), IsHover("gear") ? Theme.Text : Theme.Muted, gear);
            Hits.Add(new Hit { Key = "gear", R = gear, Click = app.ShowSettings, Tip = "Настройки: канал, план, вход в Twitch" });
            Hits.Add(new Hit { Key = "pill", R = r, Click = app.CheckNow,
                               Tip = "twitch.tv/" + Config.Channel + (Config.ByLink ? " (по ссылке)" : "") +
                                     "\nЭфир проверяется каждые " + Config.CheckMinutes +
                                     " минут.\nКлик — проверить сейчас." });
        }

        void DrawProgress(Graphics g, DateTime now, int count)
        {
            var card = new RectangleF(20, 84, 400, 176);
            Theme.FillRound(g, Theme.Card, card, 16);

            int left = Math.Max(0, Config.Goal - count);
            int daysIncl = DateTime.DaysInMonth(now.Year, now.Month) - now.Day + 1;
            bool done = left == 0;

            // Кольцо прогресса
            float cx = card.X + 100, cy = card.Y + card.Height / 2, R = 62, th = 13;
            var ring = new RectangleF(cx - R, cy - R, 2 * R, 2 * R);
            using (var pen = new Pen(Theme.Line, th)) g.DrawEllipse(pen, ring);
            float sweep = 360f * Math.Min(count, Config.Goal) / Config.Goal;
            if (sweep > 0)
            {
                Color a = done ? Theme.Green : Theme.AccentLight, b = done ? Theme.Green : Theme.Accent;
                using (var br = new LinearGradientBrush(ring, a, b, 135f))
                using (var pen = new Pen(br, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, ring, -90, Math.Min(sweep, 359.9f));
            }
            Theme.StrCenter(g, count.ToString(), Theme.Bold(46), Theme.Text, new RectangleF(cx - 60, cy - 36, 120, 52));
            Theme.StrCenter(g, "из " + Config.Goal, Theme.F(14), Theme.Muted, new RectangleF(cx - 60, cy + 16, 120, 20));

            // Цифры справа, по центру карточки по высоте
            float sx = card.X + 206, sy = card.Y + 40;
            Stat(g, sx, sy, "Осталось", done ? "цель выполнена" : left + " " + Theme.Plural(left, "стрим", "стрима", "стримов"),
                 done ? Theme.Green : Theme.Text);
            Stat(g, sx, sy + 58, "До конца месяца", daysIncl + " " + Theme.Plural(daysIncl, "день", "дня", "дней"), Theme.Text);
        }

        void Stat(Graphics g, float x, float y, string label, string value, Color c)
        {
            Theme.Str(g, label, Theme.F(12.5f), Theme.Muted, x, y);
            Theme.Str(g, value, Theme.Semi(18), c, x, y + 17);
        }

        void DrawReport(Graphics g, DateTime now)
        {
            var card = new RectangleF(20, 274, 400, 132);
            Theme.FillRound(g, Theme.Card, card, 16);

            DateTime next = Logic.CurrentDeadline(now);
            bool done = app.Data.reports.Contains(Logic.Key(next));
            int days = (next.Date - now.Date).Days;
            bool overdue = !done && now > next;

            Color tone = done ? Theme.Green : days == 0 ? Theme.Red : days == 1 ? Theme.Orange : Theme.Accent;
            var tile = new RectangleF(card.X + 16, card.Y + 18, 48, 48);
            Theme.FillRound(g, Theme.Alpha(tone, 45), tile, 12);
            Theme.StrCenter(g, done ? Theme.GlyphCheck : Theme.GlyphCalendar, Theme.Icon(21), tone, tile);

            float tx = card.X + 78;
            Theme.Str(g, "Ближайший отчёт", Theme.F(12.5f), Theme.Muted, tx, card.Y + 15);
            Theme.Str(g, next.ToString("d MMMM", Config.Ru) + ", до " + Config.Until, Theme.Semi(17), Theme.Text, tx, card.Y + 32);
            string when = done ? "отмечен как сданный"
                        : overdue ? "срок прошёл — отметьте, если сдали"
                        : days == 0 ? "сегодня!"
                        : days == 1 ? "завтра"
                        : "через " + days + " " + Theme.Plural(days, "день", "дня", "дней");
            Theme.Str(g, when, Theme.Semi(12.5f), done ? Theme.Green : days <= 1 ? tone : Theme.Muted, tx, card.Y + 56);

            float bw = (card.Width - 32 - 10) / 2;
            DrawButton(g, "collect", new RectangleF(card.X + 16, card.Y + 82, bw, 36), "Собрать отчёт", Theme.Accent, Color.White,
                       app.ShowReport, "Скриншоты статистики Twitch за период\nи список стримов — в папку");
            var br = new RectangleF(card.X + 16 + bw + 10, card.Y + 82, bw, 36);
            if (!done)
                DrawButton(g, "report", br, "Отчёт сдан", Theme.CardHi, Theme.Text, () => app.MarkReport(next),
                       "Отметить отчёт за " + next.ToString("d MMMM", Config.Ru) + " как сданный.\nНапоминаний по нему больше не будет.");
            else
                DrawButton(g, "report", br, "Снять отметку", Theme.CardHi, Theme.Muted, () =>
                {
                    if (DarkDialog.Ask(this, "Снять отметку?", "Отчёт за " + next.ToString("d MMMM", Config.Ru) +
                                       " снова будет считаться несданным, и напоминания вернутся.", "Снять", "Отмена", Theme.Accent))
                        app.UnmarkReport(next);
                });
        }

        void DrawCalendar(Graphics g, DateTime now, List<StreamRec> month)
        {
            var card = new RectangleF(20, 420, 400, 282);
            Theme.FillRound(g, Theme.Card, card, 16);
            Theme.Str(g, "Стримы в " + Theme.MonthIn[now.Month - 1], Theme.Semi(16), Theme.Text, card.X + 18, card.Y + 16);
            Theme.StrRight(g, "клик по дню — добавить или убрать", Theme.F(12), Theme.Faint, card.Right - 18, card.Y + 20);

            string[] wd = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
            float gx = card.X + 12, cw = (card.Width - 24) / 7f, gy = card.Y + 74, rh = 34;
            for (int i = 0; i < 7; i++)
                Theme.StrCenter(g, wd[i], Theme.Semi(12), i >= 5 ? Theme.Faint : Theme.Muted,
                                new RectangleF(gx + i * cw, card.Y + 48, cw, 18));

            var first = new DateTime(now.Year, now.Month, 1);
            int offset = ((int)first.DayOfWeek + 6) % 7;
            int dim = DateTime.DaysInMonth(now.Year, now.Month);
            var deadlines = Logic.Deadlines(now);

            for (int day = 1; day <= dim; day++)
            {
                var date = first.AddDays(day - 1);
                int idx = offset + day - 1;
                var cell = new RectangleF(gx + (idx % 7) * cw + (cw - 38) / 2, gy + (idx / 7) * rh, 38, 30);
                var onDay = month.Where(s => Store.Parse(s.start).Date == date).ToList();
                bool today = date == now.Date, future = date > now.Date;
                bool any = onDay.Count > 0, auto = onDay.Any(s => s.id != "");
                bool liveNow = app.IsLive && app.LiveStart.HasValue && app.LiveStart.Value.Date == date;
                string key = "d" + day;

                Color fill = Color.Transparent;
                if (liveNow) fill = Theme.Red;
                else if (auto) fill = Theme.Accent;
                else if (any) fill = Theme.Alpha(Theme.Accent, 95);
                if (IsHover(key) && !future) fill = any || liveNow ? ControlPaint.Light(fill, 0.2f) : Theme.CardHi;
                if (fill != Color.Transparent) Theme.FillRound(g, fill, cell, 9);
                if (today) Theme.StrokeRound(g, Theme.Text, 1.6f, RectangleF.Inflate(cell, 1, 1), 10);

                Color tc = any || liveNow ? Color.White : future ? Theme.Faint : Theme.Muted;
                Theme.StrCenter(g, day.ToString(), any || today ? Theme.Semi(13.5f) : Theme.F(13.5f), tc,
                                new RectangleF(cell.X, cell.Y - 1, cell.Width, cell.Height));

                // Отметка дня отчёта
                DateTime? dl = deadlines.Where(d => d.Date == date).Select(d => (DateTime?)d).FirstOrDefault();
                bool reportDone = dl.HasValue && app.Data.reports.Contains(Logic.Key(dl.Value));
                if (dl.HasValue)
                    using (var b = new SolidBrush(reportDone ? Theme.Green : Theme.Orange))
                        g.FillEllipse(b, cell.X + cell.Width / 2 - 2.5f, cell.Bottom - 6.5f, 5, 5);
                if (onDay.Count > 1)
                {
                    // Несколько стримов за день — маленький кружок с числом в углу клетки
                    var badge = new RectangleF(cell.Right - 9, cell.Y - 5, 14, 14);
                    using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, badge);
                    Theme.StrCenter(g, onDay.Count.ToString(), Theme.Bold(9.5f), Theme.Accent,
                                    new RectangleF(badge.X, badge.Y + 0.5f, badge.Width, badge.Height));
                }

                // Подсказка и клик
                var tip = new StringBuilder(date.ToString("d MMMM, dddd", Config.Ru));
                foreach (var s in onDay)
                {
                    DateTime st = Store.Parse(s.start);
                    if (s.id != "" && s.end != "")
                    {
                        DateTime en = Store.Parse(s.end);
                        int mins = (int)(en - st).TotalMinutes;
                        tip.Append("\nСтрим " + st.ToString("HH:mm") + "–" + en.ToString("HH:mm") +
                                   " (~" + (mins / 60 > 0 ? mins / 60 + " ч " : "") + mins % 60 + " мин), найден на Twitch");
                    }
                    else tip.Append("\nСтрим добавлен вручную");
                }
                if (liveNow) tip.Append("\nСейчас в эфире");
                if (dl.HasValue) tip.Append("\nОтчёт до " + Config.Until + (reportDone ? " — сдан" : ""));
                Action click = null;
                if (liveNow)
                {
                    // Идущий стрим трекер всё равно засчитает заново при следующей проверке
                    tip.Append("\n\nИдёт эфир — убрать стрим можно после его окончания");
                }
                else if (!future)
                {
                    if (any)
                    {
                        tip.Append("\n\nКлик — убрать");
                        var items = onDay;
                        var d0 = date;
                        click = () =>
                        {
                            string what = items.Count > 1 ? "стримы за " : "стрим за ";
                            if (DarkDialog.Ask(this, "Убрать " + what + d0.ToString("d MMMM", Config.Ru) + "?",
                                    "Он перестанет учитываться в счётчике за месяц." +
                                    (items.Any(s => s.id != "") ? " Этот стрим трекер нашёл на Twitch сам." : ""),
                                    "Убрать", "Отмена", Theme.Red))
                                app.RemoveStreams(items);
                            Invalidate();
                        };
                    }
                    else
                    {
                        tip.Append("\n\nКлик — добавить стрим вручную");
                        var d0 = date;
                        click = () => { app.AddManual(d0); Invalidate(); };
                    }
                }
                Hits.Add(new Hit { Key = key, R = cell, Click = click, Tip = tip.ToString() });
            }
        }

        void DrawLegend(Graphics g)
        {
            float x = 24, y = 720;
            x = LegendItem(g, x, y, "с Twitch", r => Theme.FillRound(g, Theme.Accent, r, 3));
            x = LegendItem(g, x, y, "вручную", r => Theme.FillRound(g, Theme.Alpha(Theme.Accent, 95), r, 3));
            x = LegendItem(g, x, y, "сегодня", r => Theme.StrokeRound(g, Theme.Text, 1.4f, r, 3));
            LegendItem(g, x, y, "отчёт", r =>
            {
                using (var b = new SolidBrush(Theme.Orange)) g.FillEllipse(b, r.X + 2, r.Y + 2, 7, 7);
            });
        }

        float LegendItem(Graphics g, float x, float y, string text, Action<RectangleF> swatch)
        {
            swatch(new RectangleF(x, y + 3, 11, 11));
            Theme.Str(g, text, Theme.F(12.5f), Theme.Muted, x + 17, y);
            return x + 17 + Theme.Width(g, text, Theme.F(12.5f)) + 20;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "StreamTracker_single_instance", out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "StreamTracker_show"))
            {
                if (!created)
                {
                    showEvent.Set();
                    return;
                }
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Непредвиденная ошибка (например, файл на секунду занят антивирусом) не должна ни показывать
                // окно ошибки .NET, ни закрывать трекер: записываем в журнал и работаем дальше
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Error("ошибка в окне", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("фатальная ошибка", e.ExceptionObject as Exception);
                TaskScheduler.UnobservedTaskException += (s, e) => { Log.Error("фоновая задача", e.Exception); e.SetObserved(); };

                // Запуск прямо из zip: Windows распаковывает exe во временную папку и потом удаляет — автозапуск и ярлык указывали бы в пустоту
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    DarkDialog.Ask(null, "Сначала распакуйте архив",
                        "Трекер запущен прямо из архива. Распакуйте его в обычную папку (например, «Документы») и запустите StreamTracker.exe оттуда — иначе автозапуск и ярлык не будут работать.",
                        "Понятно", null, Theme.Accent);
                    return;
                }

                Paths.MigrateFromAppFolder();   // данные старых версий лежали рядом с exe

                var settings = Settings.Load();
                bool firstRun = settings.channel == "";
                if (firstRun)
                {
                    using (var setup = new SetupForm(settings, true))
                        if (setup.ShowDialog() != DialogResult.OK) return;
                }
                Config.Apply(settings);
                Application.Run(new TrayApp(settings, args.Contains("--autostart") && !firstRun, showEvent));
            }
        }
    }
}
