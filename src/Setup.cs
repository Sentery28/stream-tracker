// Настройка трекера: при первом запуске и по шестерёнке в главном окне.
// Аккаунт Twitch (он же отслеживаемый канал), план (стримов в месяц, дни отчётов), автозапуск и ярлык.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StreamTracker
{
    static class AppSetup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "StreamTracker";

        public static bool IsAutostart()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunName) != null;
        }

        public static void SetAutostart(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --autostart");
                else key.DeleteValue(RunName, false);
            }
        }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Трекер стримов.lnk"); }
        }

        public static bool HasShortcut() { return File.Exists(ShortcutPath); }

        public static void SetShortcut(bool on)
        {
            if (!on)
            {
                if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
                return;
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath });
                var t = lnk.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { AppDomain.CurrentDomain.BaseDirectory });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath + ",0" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.ReleaseComObject(lnk);
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
    }

    // Канал задаётся двумя способами:
    //  • «Войти в Twitch» — канал = аккаунт входа; так работают и отчёты со скриншотами (статистику видит только владелец);
    //  • «По ссылке» — без входа: трекер только считает стримы и напоминает об отчётах.
    class SetupForm : PaintedForm
    {
        const int W = 480, H = 598;
        const float FieldY = 172;
        readonly Settings settings;
        readonly bool first;
        readonly System.Windows.Forms.Timer loginPoll = new System.Windows.Forms.Timer { Interval = 3000 };
        readonly System.Windows.Forms.Timer findTimer = new System.Windows.Forms.Timer { Interval = 700 };

        int goal;
        List<int> days;
        string loginName;               // ник, под которым выполнен вход (null — не выполнен или ещё проверяем)
        string loginStatus = "";
        bool loginBusy, loginFailed, loginChecked;
        bool autostart, shortcut;
        DateTime pollUntil;

        // Режим «По ссылке»
        bool linkMode;
        readonly TextBox box;
        readonly Font boxFont;
        float prefixY;
        string linkLogin;               // найденный канал (null — ещё не найден)
        string linkStatus = "";
        int linkState;                  // 0 — пусто, 1 — ищу, 2 — найден, 3 — ошибка
        int findSeq;
        bool normalizing;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public SetupForm(Settings settings, bool first)
        {
            this.settings = settings;
            this.first = first;
            Text = first ? "Настройка трекера" : "Настройки";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = first;
            StartPosition = first ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            SetSize(W, H);

            goal = settings.goal;
            days = settings.reportDays.ToList();
            while (days.Count < 2) days.Add(days.Count == 0 ? 13 : 29);
            autostart = first || AppSetup.IsAutostart();
            shortcut = first || AppSetup.HasShortcut();

            // Поле для ссылки — настоящее поле ввода (вставка, выделение, Ctrl+Z), нарисованное в стиле окна.
            // Слева от него рисуется приставка «twitch.tv/», а вставленная ссылка целиком сокращается до ника.
            boxFont = new Font(Theme.F(15).Name, (float)Math.Round(15 * K, 1), FontStyle.Regular, GraphicsUnit.Pixel);
            box = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = Theme.CardHi, ForeColor = Theme.Text, Font = boxFont,
                MaxLength = 200, Visible = false
            };
            int prefixW = TextRenderer.MeasureText("twitch.tv/", boxFont, Size.Empty,
                                                   TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            var field = FieldRect();
            int left = (int)Math.Round((field.X + 14) * K) + prefixW;
            box.SetBounds(left, (int)Math.Round(FieldY * K + (44 * K - box.PreferredHeight) / 2),
                          (int)Math.Round((field.Right - 10) * K) - left, box.PreferredHeight);
            prefixY = box.Top / K;
            Controls.Add(box);
            box.HandleCreated += delegate
            {
                SendMessage(box.Handle, 0xD3, (IntPtr)3, IntPtr.Zero);              // без внутренних отступов — ник сразу за «twitch.tv/»
                SendMessage(box.Handle, 0x1501, (IntPtr)1, "ник канала");           // подсказка в пустом поле
            };
            box.GotFocus += delegate { Invalidate(); };
            box.LostFocus += delegate { Invalidate(); };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindChannel(); }
            };

            linkMode = !first && settings.byLink && settings.channel != "";
            if (linkMode)
            {
                box.Text = settings.channel;
                box.Visible = true;
                linkLogin = settings.channel;
                linkState = 2;
                linkStatus = "✓ Отслеживается twitch.tv/" + settings.channel;
            }
            box.TextChanged += delegate { OnLinkTextChanged(); };
            findTimer.Tick += delegate { FindChannel(); };

            loginPoll.Tick += delegate
            {
                if (DateTime.Now > pollUntil) { loginPoll.Stop(); return; }
                if (loginBusy || loginName != null) return;
                if (!Chrome.IsRunning())
                {
                    // Окно входа закрыли, не войдя, — перестаём ждать (и не запускаем скрытый браузер каждые 3 секунды)
                    loginPoll.Stop();
                    loginStatus = "Окно входа закрыто — нажмите «Войти в Twitch», чтобы открыть его снова";
                    loginFailed = false;
                    Invalidate();
                    return;
                }
                // Пока страница входа открыта — человек ещё входит: не подключаемся к браузеру вообще.
                // Проверяем вход один раз, когда Twitch ушёл со страницы входа.
                if (Chrome.LoginPageOpen()) return;
                CheckLogin(true, false);
            };
            Shown += delegate
            {
                // В режиме ссылки браузер не нужен — вход проверяем, только когда переключатся на «Войти в Twitch»
                if (linkMode) { box.Focus(); box.SelectionStart = box.Text.Length; }
                else CheckLogin(true);
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            loginPoll.Stop();
            findTimer.Stop();
            findSeq++;   // ответ на запоздавший поиск канала больше не нужен
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { boxFont.Dispose(); loginPoll.Dispose(); findTimer.Dispose(); }
        }

        // Канал для сохранения. Со входом — аккаунт входа, а если вход сейчас не подтвердился (например, сессия
        // истекла) — прежний канал из настроек, чтобы можно было поменять план, не входя заново.
        // По ссылке — найденный канал.
        string Channel
        {
            get
            {
                if (linkMode) return linkLogin;
                return loginName ?? (first || settings.byLink ? null : NullIfEmpty(settings.channel));
            }
        }
        static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        RectangleF FieldRect() { return new RectangleF(24, FieldY, W - 48 - 120, 44); }

        protected override void Draw(Graphics g)
        {
            Theme.Str(g, first ? "ПЕРВЫЙ ЗАПУСК" : "НАСТРОЙКИ", Theme.Semi(12), Theme.Accent, 26, 20);
            Theme.Str(g, "Трекер стримов", Theme.Bold(25), Theme.Text, 24, 37);

            // 1. Канал: со входом или по ссылке
            Theme.Str(g, "Канал на Twitch", Theme.Semi(15), Theme.Text, 24, 92);
            DrawSegment(g, new RectangleF(24, 120, W - 48, 40));
            if (linkMode) DrawLink(g);
            else
            {
                Theme.StrWrap(g, "Трекер следит за каналом этого аккаунта. Вход хранится в отдельном профиле браузера " +
                                 "на этом компьютере — пароль трекер не видит.",
                              Theme.F(12.5f), Theme.Muted, new RectangleF(24, FieldY, W - 48, 40));
                DrawAccount(g, new RectangleF(24, 212, W - 48, 64));
                Theme.Str(g, loginStatus, Theme.F(13), loginFailed ? Theme.Red : Theme.AccentLight, 26, 286);
            }

            // 2. План
            const float P = 316;
            Theme.Str(g, "План", Theme.Semi(15), Theme.Text, 24, P);
            float sw = 136, sx = W - 24 - sw;
            Row(g, P + 32, "Стримов в месяц");
            Stepper(g, "goal", sx, P + 26, sw, goal + " " + Theme.Plural(goal, "стрим", "стрима", "стримов"),
                    () => goal = Math.Max(1, goal - 1), () => goal = Math.Min(31, goal + 1));
            Row(g, P + 76, "Дни отчётов");
            Stepper(g, "day0", sx - 10 - sw, P + 70, sw, days[0] + "-го", () => ShiftDay(0, -1), () => ShiftDay(0, 1));
            Stepper(g, "day1", sx, P + 70, sw, days[1] + "-го", () => ShiftDay(1, -1), () => ShiftDay(1, 1));

            // 3. Удобства
            Checkbox(g, "auto", 24, P + 128, "Запускать вместе с Windows", autostart, () => autostart = !autostart);
            Checkbox(g, "lnk", 24, P + 162, "Ярлык на рабочем столе", shortcut, () => shortcut = !shortcut);

            // Кнопки
            var ok = new RectangleF(W - 24 - 160, H - 64, 160, 40);
            if (Channel != null) DrawButton(g, "ok", ok, first ? "Готово" : "Сохранить", Theme.Accent, Color.White, Save);
            else DrawButton(g, "ok", ok, first ? "Готово" : "Сохранить", Theme.CardHi, Theme.Faint, null,
                            linkMode ? "Сначала укажите канал" : "Сначала войдите в Twitch");
            DrawButton(g, "cancel", new RectangleF(ok.X - 10 - 120, H - 64, 120, 40), first ? "Выйти" : "Отмена",
                       Theme.CardHi, Theme.Text, () => { DialogResult = DialogResult.Cancel; Close(); });
        }

        // Переключатель «Войти в Twitch / По ссылке»: выбранная половина светлее и с фиолетовой чертой снизу
        void DrawSegment(Graphics g, RectangleF r)
        {
            Theme.FillRound(g, Theme.Card, r, 10);
            float half = (r.Width - 6) / 2;
            for (int i = 0; i < 2; i++)
            {
                bool link = i == 1, on = link == linkMode;
                string key = link ? "segLink" : "segLogin";
                var s = new RectangleF(r.X + 3 + i * half, r.Y + 3, half, r.Height - 6);
                if (on)
                {
                    Theme.FillRound(g, Theme.CardHi, s, 8);
                    Theme.FillRound(g, Theme.Accent, new RectangleF(s.X + s.Width / 2 - 20, s.Bottom - 3, 40, 3), 1.5f);
                }
                Theme.StrCenter(g, link ? "По ссылке" : "Войти в Twitch", Theme.Semi(13.5f),
                                on || IsHover(key) ? Theme.Text : Theme.Muted, s);
                Hits.Add(new Hit
                {
                    Key = key, R = s, Click = on ? (Action)null : () => SetMode(link),
                    Tip = on ? null : link ? "Без входа: трекер считает стримы и напоминает об отчётах"
                                           : "Со входом: ещё и отчёты со скриншотами статистики Twitch"
                });
            }
        }

        // Поле «twitch.tv/ник» с кнопкой «Найти», под ним — результат проверки
        void DrawLink(Graphics g)
        {
            var field = FieldRect();
            Theme.FillRound(g, Theme.CardHi, field, 10);
            if (box.Focused) Theme.StrokeRound(g, Theme.Accent, 1.5f, field, 10);
            else if (linkState == 3) Theme.StrokeRound(g, Theme.Alpha(Theme.Red, 160), 1.5f, field, 10);
            Theme.Str(g, "twitch.tv/", Theme.F(15), Theme.Faint, field.X + 14, prefixY);
            // Ник начинается ровно там, где кончается нарисованная приставка (меряем тем же способом, что и рисуем)
            int left = (int)Math.Round((field.X + 14 + Theme.Width(g, "twitch.tv/", Theme.F(15))) * K);
            if (box.Left != left)
                box.SetBounds(left, box.Top, (int)Math.Round((field.Right - 10) * K) - left, box.Height);
            Hits.Add(new Hit { Key = "field", R = field, Click = () => { box.Focus(); box.SelectionStart = box.Text.Length; } });

            var find = new RectangleF(W - 24 - 110, FieldY, 110, 44);
            if (linkState == 1) DrawButton(g, "find", find, "Ищу…", Theme.CardHi, Theme.Muted, null);
            else if (linkState == 2) DrawButton(g, "find", find, "Найти", Theme.CardHi, Theme.Text, FindChannel, "Проверить канал ещё раз");
            else DrawButton(g, "find", find, "Найти", Theme.Accent, Color.White, FindChannel,
                            "Проверить, что такой канал есть на Twitch\n(можно вставить ссылку целиком)");

            Color c = linkState == 2 ? Theme.Green : linkState == 3 ? Theme.Red : Theme.AccentLight;
            Theme.Str(g, linkStatus, Theme.F(13), c, 26, FieldY + 54);
            Theme.StrWrap(g, "Трекер будет следить за эфиром канала и напоминать об отчётах. " +
                             "Скриншоты статистики — только со входом.",
                          Theme.F(12.5f), Theme.Muted, new RectangleF(24, FieldY + 80, W - 48, 40));
        }

        void SetMode(bool link)
        {
            linkMode = link;
            box.Visible = link;
            if (link)
            {
                // Подставляем уже известный канал, чтобы не вводить его заново
                if (box.Text.Trim() == "")
                {
                    string known = loginName ?? NullIfEmpty(settings.channel);
                    if (known != null) box.Text = known;
                }
                box.Focus();
                box.SelectionStart = box.Text.Length;
            }
            else
            {
                ActiveControl = null;
                if (!loginChecked && !loginBusy) CheckLogin(true);
            }
            Invalidate();
        }

        // Ник из ссылки вида twitch.tv/ник (с https://, www., m., /popout/, @ и хвостом после ника) или просто ника
        public static string ParseLogin(string text)
        {
            text = (text ?? "").Trim();
            var m = Regex.Match(text, @"twitch\.tv/(?:popout/)?@?([A-Za-z0-9_]{2,25})(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(text, @"^@?([A-Za-z0-9_]{2,25})$");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        void OnLinkTextChanged()
        {
            if (normalizing) return;
            // Вставили ссылку целиком — оставляем в поле только ник (приставка twitch.tv/ уже нарисована)
            string t = box.Text;
            if (t.IndexOf("twitch.tv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string login = ParseLogin(t);
                if (login != null && login != t)
                {
                    normalizing = true;
                    box.Text = login;
                    box.SelectionStart = login.Length;
                    normalizing = false;
                }
            }
            // Канал проверяется сам, когда перестали печатать
            findSeq++;
            linkLogin = null;
            linkState = 0;
            linkStatus = "";
            findTimer.Stop();
            if (box.Text.Trim() != "") findTimer.Start();
            Invalidate();
        }

        async void FindChannel()
        {
            findTimer.Stop();
            int seq = ++findSeq;
            linkLogin = null;
            string text = box.Text.Trim();
            string login = ParseLogin(text);
            if (text == "") { linkState = 0; linkStatus = ""; }
            else if (login == null) { linkState = 3; linkStatus = "Это не похоже на ссылку на канал Twitch"; }
            else { linkState = 1; linkStatus = "Ищу канал…"; }
            Invalidate();
            if (linkState != 1) return;

            string display = null, error = null;
            try { display = await Task.Run(() => Twitch.FindUser(login)); }
            catch (Exception ex) { error = ex.Message; }
            if (seq != findSeq || IsDisposed) return;   // пока искали, текст поменяли или окно закрыли

            if (error != null) { linkState = 3; linkStatus = "Не получилось проверить канал: " + error; }
            else if (display == null) { linkState = 3; linkStatus = "Канала twitch.tv/" + login + " нет на Twitch"; }
            else
            {
                linkState = 2;
                linkLogin = login;
                linkStatus = "✓ " + display + " — канал найден";
            }
            Invalidate();
        }

        // Карточка аккаунта: кружок с буквой, ник и кнопка «Войти» / «Сменить»
        void DrawAccount(Graphics g, RectangleF card)
        {
            Theme.FillRound(g, Theme.Card, card, 12);
            var avatar = new RectangleF(card.X + 14, card.Y + 14, 36, 36);
            float tx = avatar.Right + 12;
            var btn = new RectangleF(card.Right - 12 - 150, card.Y + 13, 150, 38);

            if (loginName != null)
            {
                using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, avatar);
                Theme.StrCenter(g, loginName.Substring(0, 1).ToUpperInvariant(), Theme.Bold(16), Color.White, avatar);
                Theme.Str(g, loginName, Theme.Semi(16), Theme.Text, tx, card.Y + 12);
                Theme.Str(g, "✓ вход выполнен", Theme.F(12.5f), Theme.Green, tx, card.Y + 35);
                if (loginBusy) DrawButton(g, "login", btn, "Подождите…", Theme.CardHi, Theme.Muted, null);
                else DrawButton(g, "login", btn, "Сменить", Theme.CardHi, Theme.Text, SwitchAccount,
                                "Выйти из этого аккаунта в профиле трекера\nи войти в другой");
                return;
            }

            using (var b = new SolidBrush(Theme.CardHi)) g.FillEllipse(b, avatar);
            Theme.StrCenter(g, "", Theme.Icon(16), Theme.Muted, avatar);   // значок человека
            if (!loginChecked || (loginBusy && loginStatus == ""))
            {
                Theme.Str(g, "Проверяю вход…", Theme.Semi(15), Theme.Muted, tx, card.Y + 21);
                DrawButton(g, "login", btn, "Войти в Twitch", Theme.CardHi, Theme.Muted, null);
                return;
            }
            Theme.Str(g, "Вход не выполнен", Theme.Semi(15), Theme.Text, tx, card.Y + 12);
            string sub = !first && settings.channel != "" ? "сейчас: " + settings.channel : "для учёта стримов и отчётов";
            Theme.Str(g, sub, Theme.F(12.5f), Theme.Muted, tx, card.Y + 35);
            DrawButton(g, "login", btn, "Войти в Twitch", Theme.Accent, Color.White, OpenLogin,
                       "Откроется окно браузера со страницей входа Twitch");
        }

        void Row(Graphics g, float y, string label)
        {
            Theme.Str(g, label, Theme.F(14), Theme.Muted, 24, y + 2);
        }

        void Stepper(Graphics g, string key, float x, float y, float w, string text, Action dec, Action inc)
        {
            var r = new RectangleF(x, y, w, 34);
            Theme.FillRound(g, Theme.Card, r, 9);
            Theme.StrCenter(g, text, Theme.Semi(13.5f), Theme.Text, r);
            var left = new RectangleF(x + 3, y + 3, 28, 28);
            var right = new RectangleF(r.Right - 31, y + 3, 28, 28);
            foreach (var part in new[] { Tuple.Create(left, "-", "", dec), Tuple.Create(right, "+", "", inc) })
            {
                string k = key + part.Item2;
                if (IsHover(k)) Theme.FillRound(g, Theme.CardHi, part.Item1, 7);
                Theme.StrCenter(g, part.Item3, Theme.Icon(10), Theme.Text, part.Item1);
                var act = part.Item4;
                Hits.Add(new Hit { Key = k, R = part.Item1, Click = () => { act(); Invalidate(); } });
            }
        }

        void Checkbox(Graphics g, string key, float x, float y, string text, bool value, Action toggle)
        {
            var box = new RectangleF(x, y + 1, 20, 20);
            if (value)
            {
                Theme.FillRound(g, IsHover(key) ? Theme.AccentHi : Theme.Accent, box, 5);
                Theme.StrCenter(g, Theme.GlyphCheck, Theme.Icon(11), Color.White, box);
            }
            else Theme.StrokeRound(g, IsHover(key) ? Theme.Text : Theme.Faint, 1.5f, box, 5);
            Theme.Str(g, text, Theme.F(14), Theme.Text, x + 32, y + 1);
            var row = new RectangleF(x, y - 4, 32 + Theme.Width(g, text, Theme.F(14)) + 8, 30);
            Hits.Add(new Hit { Key = key, R = row, Click = () => { toggle(); Invalidate(); } });
        }

        void ShiftDay(int i, int delta)
        {
            int v = days[i];
            do { v = (v + delta + 30) % 31 + 1; } while (v == days[1 - i]);
            days[i] = v;
        }

        async void OpenLogin()
        {
            loginFailed = false;
            loginStatus = "Открываю окно входа…";
            Invalidate();
            try
            {
                await Task.Run(() => Chrome.OpenVisible("https://www.twitch.tv/login"));
                loginFailed = false;
                loginStatus = "Войдите в открывшемся окне браузера — трекер заметит вход сам";
                pollUntil = DateTime.Now.AddMinutes(10);
                loginPoll.Start();
            }
            catch (Exception ex)
            {
                loginFailed = true;
                loginStatus = "Не получилось открыть браузер: " + ex.Message;
            }
            Invalidate();
        }

        async void SwitchAccount()
        {
            if (loginBusy) return;
            loginBusy = true;
            loginFailed = false;
            loginStatus = "Выхожу из аккаунта " + loginName + "…";
            Invalidate();
            try
            {
                await Task.Run(() => Chrome.Logout());
                loginName = null;
                loginStatus = "";
            }
            catch (Exception ex)
            {
                loginFailed = true;
                loginStatus = "Не получилось выйти: " + ex.Message;
            }
            finally
            {
                loginBusy = false;
                Invalidate();
            }
            if (loginName == null) OpenLogin();
        }

        // launch = false — только спросить уже открытое окно входа, не запуская браузер
        async void CheckLogin(bool quiet, bool launch = true)
        {
            if (loginBusy) return;
            loginBusy = true;
            if (!quiet) { loginStatus = ""; loginFailed = false; }
            Invalidate();
            try
            {
                string name = await Task.Run(() => Chrome.TwitchLogin(launch));   // запуск браузера — не в потоке окна
                loginName = name;
                if (name != null)
                {
                    loginStatus = "";
                    loginFailed = false;
                    bool waitedForLogin = loginPoll.Enabled;
                    loginPoll.Stop();
                    // Вход выполнен в окне, которое открыл трекер, — закрываем его, оно больше не нужно
                    if (waitedForLogin) await Task.Run(() => Chrome.CloseLoginWindow());
                }
            }
            catch (Exception ex)
            {
                // Показываем всегда: иначе при первом запуске человек видел бы «Вход не выполнен» без причины
                loginFailed = true;
                loginStatus = "Не получилось проверить вход: " + ex.Message;
            }
            finally
            {
                loginBusy = false;
                loginChecked = true;
                Invalidate();
            }
        }

        void Save()
        {
            string channel = Channel;
            if (channel == null) return;
            settings.channel = channel;
            settings.byLink = linkMode;
            settings.goal = goal;
            settings.reportDays = days.ToList();
            settings.Save();
            try { AppSetup.SetAutostart(autostart); } catch { }
            try { if (shortcut != AppSetup.HasShortcut()) AppSetup.SetShortcut(shortcut); } catch { }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
