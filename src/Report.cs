// Отчёт о стримах: скриншоты «Отчёта о трансляции» Twitch по каждому стриму за период
// и список «дата длительность» — в папку Отчёты рядом с программой.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamTracker
{
    class StreamSummary
    {
        public DateTime Start;
        public TimeSpan Length;
        public string Image;   // путь к скриншоту или null
    }

    class NeedLoginException : Exception
    {
        public NeedLoginException() : base("Нужно войти в Twitch") { }
    }

    static class Dashboard
    {
        const string Base = "https://dashboard.twitch.tv";

        // Ждёт, пока загрузится страница отчёта о трансляции, и возвращает её данные.
        // EXPECT — какой стрим должен открыться (start-end), null — любой (самый свежий).
        const string WaitJs = @"new Promise(res => { const t0 = Date.now(); (function poll() {
  if (location.hostname === 'www.twitch.tv' && location.pathname.indexOf('/login') === 0) return res({login: false});
  const m = location.pathname.match(/stream-summary\/(\d+)-(\d+)/);
  const prev = document.querySelector('[aria-label=""Перейти к дате предыдущей трансляции""]');
  const label = m && prev ? [...document.querySelectorAll('body *')].find(e => e.children.length === 0 && e.textContent.trim() === 'Длительность трансляции') : null;
  if (label && (EXPECT === null || m[1] + '-' + m[2] === EXPECT)) {
    let card = label; while (card.parentElement && card.getBoundingClientRect().height < 100) card = card.parentElement;
    const leaves = [...card.querySelectorAll('*')].filter(e => e.children.length === 0 && e.textContent.trim());
    return res({login: true, start: m[1], end: m[2], prev: prev.getAttribute('href') || '',
                duration: leaves.length ? leaves[leaves.length - 1].textContent.trim() : ''});
  }
  if (Date.now() - t0 > 30000) return res({timeout: true});
  setTimeout(poll, 300); })(); })";

        // Область скриншота: от полосы с датой до строки «Хотите узнать…», как в отчёте вручную
        const string ClipJs = @"(() => {
  let nav = document.querySelector('[aria-label=""Перейти к дате предыдущей трансляции""]');
  while (nav.parentElement && nav.getBoundingClientRect().width < 700) nav = nav.parentElement;
  const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  let help = null; while (w.nextNode()) if (w.currentNode.textContent.includes('Хотите узнать')) { help = w.currentNode.parentElement; break; }
  const a = nav.getBoundingClientRect(), b = (help || nav).getBoundingClientRect(), pad = 12;
  return {x: a.x - pad, y: a.y - pad + window.scrollY, w: a.width + 2 * pad, h: Math.max(b.bottom, a.bottom + 330) - a.y + 2 * pad};
})()";

        static long ToMs(DateTime local) { return new DateTimeOffset(local).ToUnixTimeMilliseconds(); }
        static DateTime FromMs(long ms) { return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime; }

        // Стримы, начавшиеся с from по to включительно (по дате начала). outDir == null — без скриншотов.
        public static async Task<List<StreamSummary>> Collect(DateTime from, DateTime to, string outDir, Action<string> progress)
        {
            await Chrome.Lock.WaitAsync();
            try
            {
                var result = new List<StreamSummary>();
                using (var chrome = await Chrome.Start())
                {
                    var p = chrome.Page;
                    try
                    {
                        var ver = await p.Send("Browser.getVersion");
                        string ua = Convert.ToString(ver["userAgent"]).Replace("HeadlessChrome", "Chrome");
                        await p.Send("Network.setUserAgentOverride", new Dictionary<string, object> { { "userAgent", ua } });
                    }
                    catch { }
                    await p.Send("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
                    {
                        { "width", 1250 }, { "height", 1400 }, { "deviceScaleFactor", 2 }, { "mobile", false }
                    });

                    long fromMs = ToMs(from.Date), toMs = ToMs(to.Date.AddDays(1));
                    string href = "/u/" + Config.Channel + "/stream-summary";
                    string expect = null;
                    for (int i = 0; i < 120 && !string.IsNullOrEmpty(href); i++)
                    {
                        if (progress != null)
                            progress(i == 0 ? "Открываю статистику Twitch…"
                                     : "Ищу стримы за период… найдено " + result.Count);
                        await p.Send("Page.navigate", new Dictionary<string, object> { { "url", Base + href } });
                        var info = await p.Eval(WaitJs.Replace("EXPECT", expect == null ? "null" : "'" + expect + "'"))
                                   as Dictionary<string, object>;
                        if (info == null) throw new Exception("страница статистики не ответила");
                        if (info.ContainsKey("timeout")) throw new Exception("страница статистики Twitch не загрузилась за 30 секунд");
                        if (!(bool)info["login"]) throw new NeedLoginException();

                        long s = long.Parse(Convert.ToString(info["start"])), e = long.Parse(Convert.ToString(info["end"]));
                        if (s < fromMs) break;
                        if (s < toMs)
                        {
                            var item = new StreamSummary
                            {
                                Start = FromMs(s),
                                Length = ParseDuration(Convert.ToString(info["duration"]), TimeSpan.FromMilliseconds(e - s))
                            };
                            if (outDir != null)
                            {
                                if (progress != null)
                                    progress("Скриншот стрима " + item.Start.ToString("d MMMM", Config.Ru) + "…");
                                await Task.Delay(700);   // дать дорисоваться шрифтам и карточкам
                                item.Image = Path.Combine(outDir, item.Start.ToString("yyyy-MM-dd_HH-mm") + ".png");
                                await Capture(p, item.Image);
                            }
                            result.Add(item);
                        }
                        href = Convert.ToString(info["prev"]);
                        var m = Regex.Match(href, @"(\d+-\d+)$");
                        expect = m.Success ? m.Groups[1].Value : null;
                    }
                }
                result.Reverse();
                return result;
            }
            finally { Chrome.Lock.Release(); }
        }

        static async Task Capture(Cdp p, string path)
        {
            var c = await p.Eval(ClipJs) as Dictionary<string, object>;
            var shot = await p.Send("Page.captureScreenshot", new Dictionary<string, object>
            {
                { "format", "png" }, { "captureBeyondViewport", true },
                { "clip", new Dictionary<string, object>
                    {
                        { "x", Convert.ToDouble(c["x"]) }, { "y", Convert.ToDouble(c["y"]) },
                        { "width", Convert.ToDouble(c["w"]) }, { "height", Convert.ToDouble(c["h"]) }, { "scale", 1 }
                    } }
            });
            File.WriteAllBytes(path, Convert.FromBase64String(Convert.ToString(shot["data"])));
        }

        // «2 ч 2 м» → 2:02; если не разобрали — берём по времени из адреса страницы
        static TimeSpan ParseDuration(string text, TimeSpan fallback)
        {
            var h = Regex.Match(text ?? "", @"(\d+)\s*ч");
            var m = Regex.Match(text ?? "", @"(\d+)\s*м");
            if (!h.Success && !m.Success) return TimeSpan.FromMinutes(Math.Round(fallback.TotalMinutes));
            return new TimeSpan(h.Success ? int.Parse(h.Groups[1].Value) : 0, m.Success ? int.Parse(m.Groups[1].Value) : 0, 0);
        }

        public static string Length(TimeSpan t) { return string.Format("{0:00}:{1:00}:00", (int)t.TotalHours, t.Minutes); }

        // Текст как в отчёте вручную: «14.09.2026 01:58:00» по строке на стрим и итог
        public static string Text(List<StreamSummary> list)
        {
            var sb = new StringBuilder();
            foreach (var s in list) sb.AppendLine(s.Start.ToString("dd.MM.yyyy") + " " + Length(s.Length));
            sb.AppendLine();
            sb.Append(list.Count + " " + Theme.Plural(list.Count, "стрим", "стрима", "стримов"));
            return sb.ToString();
        }

        // Период по умолчанию: со дня после прошлого срока по день текущего срока включительно (14–29, 30–13)
        public static void DefaultPeriod(DateTime now, out DateTime from, out DateTime to)
        {
            DateTime d = Logic.CurrentDeadline(now);
            var first = new DateTime(d.Year, d.Month, 1);
            DateTime prev = Logic.Deadlines(first.AddMonths(-1)).Concat(Logic.Deadlines(first)).Where(x => x.Date < d.Date).Max();
            from = prev.Date.AddDays(1);
            to = d.Date;
        }
    }

    // ---------- Окно отчёта ----------

    class ReportForm : PaintedForm
    {
        const int W = 620, H = 680;
        readonly TrayApp app;
        DateTime from, to;
        List<StreamSummary> items;
        string status = "", outDir;
        bool working, failed;
        readonly Dictionary<string, Image> thumbs = new Dictionary<string, Image>();

        public ReportForm(TrayApp app)
        {
            this.app = app;
            Text = "Отчёт о стримах";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            SetSize(W, H);
            Dashboard.DefaultPeriod(DateTime.Now, out from, out to);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var t in thumbs.Values) t.Dispose();
            thumbs.Clear();
            base.OnFormClosed(e);
        }

        string PeriodTitle()
        {
            return from.Month == to.Month && from.Year == to.Year
                ? from.Day + " – " + to.ToString("d MMMM", Config.Ru)
                : from.ToString("d MMMM", Config.Ru) + " – " + to.ToString("d MMMM", Config.Ru);
        }

        protected override void Draw(Graphics g)
        {
            Theme.Str(g, "ОТЧЁТ О СТРИМАХ", Theme.Semi(12), Theme.Accent, 22, 20);
            Theme.Str(g, PeriodTitle(), Theme.Bold(25), Theme.Text, 20, 37);

            DateChip(g, "from", 20, 88, "с", from);
            DateChip(g, "to", 230, 88, "по", to);
            var cr = new RectangleF(W - 20 - 150, 88, 150, 40);
            if (working) DrawButton(g, "collect", cr, "Собираю…", Theme.CardHi, Theme.Muted, null);
            else DrawButton(g, "collect", cr, items == null ? "Собрать" : "Собрать заново", Theme.Accent, Color.White, Collect,
                            "Открыть статистику Twitch за период\nи сделать скриншоты каждого стрима");

            Theme.Str(g, status, Theme.F(13), failed ? Theme.Red : working ? Theme.AccentLight : Theme.Muted, 22, 142);

            var card = new RectangleF(20, 172, W - 40, H - 172 - 86);
            Theme.FillRound(g, Theme.Card, card, 16);
            if (items == null)
            {
                Theme.StrCenter(g, Theme.GlyphCalendar, Theme.Icon(34), Theme.Faint,
                                new RectangleF(card.X, card.Y + card.Height / 2 - 70, card.Width, 40));
                Theme.StrCenter(g, "Нажмите «Собрать»", Theme.Semi(16), Theme.Text,
                                new RectangleF(card.X, card.Y + card.Height / 2 - 18, card.Width, 24));
                Theme.StrCenter(g, "Трекер откроет статистику Twitch и сделает скриншот каждого стрима за период",
                                Theme.F(13), Theme.Muted, new RectangleF(card.X, card.Y + card.Height / 2 + 12, card.Width, 20));
            }
            else DrawResult(g, card);

            DrawFooter(g);
        }

        void DateChip(Graphics g, string key, float x, float y, string label, DateTime value)
        {
            var r = new RectangleF(x, y, 200, 40);
            Theme.FillRound(g, Theme.Card, r, 10);
            Theme.Str(g, label, Theme.F(13), Theme.Muted, x + 14, y + 11);
            Theme.StrCenter(g, value.ToString("d MMMM, ddd", Config.Ru), Theme.Semi(13.5f), Theme.Text,
                            new RectangleF(x + 54, y, 110, 40));
            var left = new RectangleF(x + 30, y + 4, 28, 32);
            var right = new RectangleF(x + 166, y + 4, 28, 32);
            foreach (var pair in new[] { Tuple.Create(left, -1, ""), Tuple.Create(right, 1, "") })
            {
                string k = key + pair.Item2;
                if (IsHover(k) && !working) Theme.FillRound(g, Theme.CardHi, pair.Item1, 8);
                Theme.StrCenter(g, pair.Item3, Theme.Icon(11), working ? Theme.Faint : Theme.Text, pair.Item1);
                int delta = pair.Item2;
                Hits.Add(new Hit
                {
                    Key = k, R = pair.Item1, Tip = delta < 0 ? "На день раньше" : "На день позже",
                    Click = working ? (Action)null : () => Shift(key, delta)
                });
            }
        }

        // Сдвиг даты считается от текущего значения, а не от нарисованного — быстрые клики не теряются
        void Shift(string key, int delta)
        {
            if (key == "from")
            {
                from = from.AddDays(delta);
                if (to < from) to = from;
            }
            else
            {
                to = to.AddDays(delta);
                if (from > to) from = to;
            }
            items = null;
            status = "";
            Invalidate();
        }

        void DrawResult(Graphics g, RectangleF card)
        {
            if (items.Count == 0)
            {
                Theme.StrCenter(g, "За этот период стримов на Twitch нет", Theme.Semi(15), Theme.Muted, card);
                return;
            }
            // Слева — список как в сообщении
            float x = card.X + 20, y = card.Y + 18;
            Theme.Str(g, "Список", Theme.Semi(12.5f), Theme.Muted, x, y);
            y += 26;
            float rowH = Math.Min(24, (card.Height - 110) / Math.Max(1, items.Count));
            foreach (var s in items)
            {
                Theme.Str(g, s.Start.ToString("dd.MM.yyyy"), Theme.F(13.5f), Theme.Text, x, y);
                Theme.Str(g, Dashboard.Length(s.Length), Theme.Semi(13.5f), Theme.AccentLight, x + 96, y);
                y += rowH;
            }
            Theme.Str(g, items.Count + " " + Theme.Plural(items.Count, "стрим", "стрима", "стримов"),
                      Theme.Bold(18), Theme.Text, x, card.Bottom - 40);

            // Справа — миниатюры скриншотов, клик открывает картинку целиком
            float gx = card.X + 230, gw = card.Right - 16 - gx, cols = 2, gap = 10;
            float tw = (gw - gap) / cols, th = tw * 0.46f;
            float maxRows = (float)Math.Floor((card.Height - 32 + gap) / (th + gap));
            int shown = (int)Math.Min(items.Count, maxRows * cols);
            for (int i = 0; i < shown; i++)
            {
                var s = items[i];
                var r = new RectangleF(gx + (i % 2) * (tw + gap), card.Y + 16 + (i / 2) * (th + gap), tw, th);
                var img = Thumb(s.Image);
                using (var path = Theme.Round(r, 8))
                {
                    var state = g.Save();
                    g.SetClip(path);
                    if (img != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, r);
                    }
                    else Theme.FillRound(g, Theme.CardHi, r, 8);
                    g.Restore(state);
                }
                string k = "img" + i;
                Theme.StrokeRound(g, IsHover(k) ? Theme.Accent : Theme.Line, IsHover(k) ? 2 : 1, r, 8);
                string file = s.Image;
                Hits.Add(new Hit
                {
                    Key = k, R = r, Tip = s.Start.ToString("d MMMM, HH:mm", Config.Ru) + "\nКлик — открыть скриншот",
                    Click = file == null ? (Action)null : () => { try { Process.Start(file); } catch { } }
                });
            }
            if (shown < items.Count)
                Theme.StrRight(g, "и ещё " + (items.Count - shown) + " — в папке", Theme.F(12.5f), Theme.Muted, card.Right - 16,
                               card.Bottom - 26);
        }

        Image Thumb(string file)
        {
            if (file == null || !File.Exists(file)) return null;
            Image img;
            if (!thumbs.TryGetValue(file, out img))
            {
                using (var src = Image.FromFile(file))
                {
                    int w = 400, h = (int)(src.Height * 400.0 / src.Width);
                    var bmp = new Bitmap(w, h);
                    using (var tg = Graphics.FromImage(bmp))
                    {
                        tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        tg.DrawImage(src, 0, 0, w, h);
                    }
                    img = bmp;
                }
                thumbs[file] = img;
            }
            return img;
        }

        void DrawFooter(Graphics g)
        {
            float y = H - 64;
            bool has = items != null && items.Count > 0 && !working;
            bool hasShots = has && items.Any(s => s.Image != null && File.Exists(s.Image));
            // Справа главная кнопка, левее — вспомогательные
            var shots = new RectangleF(W - 20 - 200, y, 200, 40);
            var copy = new RectangleF(shots.X - 10 - 180, y, 180, 40);
            var open = new RectangleF(copy.X - 10 - 150, y, 150, 40);
            if (hasShots) DrawButton(g, "shots", shots, "Скриншоты в буфер", Theme.Accent, Color.White, CopyShots,
                                     "Скопировать все скриншоты —\nCtrl+V в Telegram вставит их альбомом");
            else DrawButton(g, "shots", shots, "Скриншоты в буфер", Theme.CardHi, Theme.Faint, null);
            if (has) DrawButton(g, "copy", copy, "Скопировать текст", Theme.CardHi, Theme.Text, CopyText,
                                "Скопировать список стримов,\nчтобы вставить его в сообщение (Ctrl+V)");
            else DrawButton(g, "copy", copy, "Скопировать текст", Theme.CardHi, Theme.Faint, null);
            if (has) DrawButton(g, "folder", open, "Открыть папку", Theme.CardHi, Theme.Text, () => Process.Start(outDir),
                                "Документы\\Трекер стримов\\Отчёты");
            else DrawButton(g, "folder", open, "Открыть папку", Theme.CardHi, Theme.Faint, null);
        }

        // Файлы в буфер так же, как «Копировать» в Проводнике: Telegram, почта и папки принимают их по Ctrl+V
        void CopyShots()
        {
            var files = new System.Collections.Specialized.StringCollection();
            foreach (var s in items)
                if (s.Image != null && File.Exists(s.Image)) files.Add(s.Image);
            if (files.Count == 0) return;
            try
            {
                Clipboard.SetFileDropList(files);
                failed = false;
                status = "Скопировано " + files.Count + " " + Theme.Plural(files.Count, "скриншот", "скриншота", "скриншотов") +
                         " — вставьте в Telegram (Ctrl+V)";
            }
            catch (Exception ex)
            {
                failed = true;
                status = "Не получилось скопировать: " + ex.Message;
            }
            Invalidate();
        }

        void CopyText()
        {
            try
            {
                Clipboard.SetText(Dashboard.Text(items));
                failed = false;
                status = "Список скопирован — вставьте его в сообщение (Ctrl+V)";
            }
            catch (Exception ex)
            {
                // Буфер обмена может быть занят другой программой
                failed = true;
                status = "Не получилось скопировать: " + ex.Message;
            }
            Invalidate();
        }
        async void Collect()
        {
            if (working) return;
            bool needLogin = false;
            working = true;
            failed = false;
            items = null;
            foreach (var t in thumbs.Values) t.Dispose();
            thumbs.Clear();
            status = "Запускаю браузер…";
            Invalidate();
            try
            {
                outDir = Path.Combine(Paths.Reports, from.ToString("yyyy-MM-dd") + "_" + to.ToString("yyyy-MM-dd"));
                // Старые скриншоты и список этого периода убираем сразу: если сбор оборвётся,
                // в папке не останется устаревший отчёт.txt рядом с новыми картинками
                if (Directory.Exists(outDir))
                {
                    foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);
                    File.Delete(Path.Combine(outDir, "отчёт.txt"));
                }
                Directory.CreateDirectory(outDir);

                // Весь сбор — в фоновом потоке, чтобы окно не замирало; статус обновляем через поток окна.
                // Окно могут закрыть посреди сбора — тогда сбор доходит до конца молча, и стримы всё равно
                // попадают в календарь.
                var list = await Task.Run(() => Dashboard.Collect(from, to, outDir, s =>
                {
                    try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => { status = s; Invalidate(); })); }
                    catch (InvalidOperationException) { }
                }));
                if (list.Count > 0)
                    File.WriteAllText(Path.Combine(outDir, "отчёт.txt"), Dashboard.Text(list), new UTF8Encoding(true));
                else
                {
                    // Пустой период — папку не оставляем
                    try { Directory.Delete(outDir, true); } catch { }
                    outDir = null;
                }
                items = list;
                int added = app.Import(list);
                status = list.Count == 0 ? "Стримов за период не найдено"
                       : "Готово: " + list.Count + " " + Theme.Plural(list.Count, "стрим", "стрима", "стримов") +
                         (added > 0 ? " (в календарь добавлено " + added + ")" : "") + ". Скриншоты и отчёт.txt — в Документах.";
            }
            catch (NeedLoginException)
            {
                failed = true;
                status = "Нужно войти в Twitch";
                needLogin = true;
            }
            catch (Exception ex)
            {
                failed = true;
                status = "Не получилось: " + ex.Message;
            }
            finally
            {
                working = false;
                Invalidate();
            }

            // Окно входа — уже вне try/catch (в C# 5 нельзя ждать внутри catch)
            if (needLogin && DarkDialog.Ask(IsDisposed ? null : this, "Нужно войти в Twitch",
                    "Трекер откроет окно браузера со своим отдельным профилем. Войдите в аккаунт " + Config.Channel +
                    ", закройте окно браузера и нажмите «Собрать» ещё раз.", "Войти в Twitch", "Отмена", Theme.Accent))
            {
                string error = null;
                try { await Task.Run(() => Chrome.OpenVisible("https://www.twitch.tv/login")); }
                catch (Exception ex) { error = ex.Message; }
                if (error != null)
                {
                    status = "Не получилось открыть окно входа: " + error;
                    Invalidate();
                }
            }
        }
    }
}
