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

    // Канал — это аккаунт, под которым выполнен вход в Twitch: статистику Twitch всё равно видит только владелец,
    // поэтому отдельно вводить ссылку на канал не нужно и ошибиться с каналом нельзя.
    class SetupForm : PaintedForm
    {
        const int W = 480, H = 552;
        readonly Settings settings;
        readonly bool first;
        readonly System.Windows.Forms.Timer loginPoll = new System.Windows.Forms.Timer { Interval = 3000 };

        int goal;
        List<int> days;
        string loginName;               // ник, под которым выполнен вход (null — не выполнен или ещё проверяем)
        string loginStatus = "";
        bool loginBusy, loginFailed, loginChecked;
        bool autostart, shortcut;
        DateTime pollUntil;

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

            loginPoll.Tick += delegate
            {
                if (DateTime.Now > pollUntil) loginPoll.Stop();
                else if (!loginBusy && loginName == null) CheckLogin(true);
            };
            Shown += delegate { CheckLogin(true); };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            loginPoll.Stop();
            base.OnFormClosed(e);
        }

        // Канал для сохранения: аккаунт входа, а если вход сейчас не подтвердился (например, сессия истекла) —
        // прежний канал из настроек, чтобы можно было поменять план, не входя заново
        string Channel { get { return loginName ?? (first ? null : NullIfEmpty(settings.channel)); } }
        static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        protected override void Draw(Graphics g)
        {
            Theme.Str(g, first ? "ПЕРВЫЙ ЗАПУСК" : "НАСТРОЙКИ", Theme.Semi(12), Theme.Accent, 26, 20);
            Theme.Str(g, "Трекер стримов", Theme.Bold(25), Theme.Text, 24, 37);

            // 1. Аккаунт Twitch
            Theme.Str(g, "Аккаунт Twitch", Theme.Semi(15), Theme.Text, 24, 92);
            Theme.StrWrap(g, "Трекер следит за каналом этого аккаунта. Вход хранится в отдельном профиле браузера " +
                             "на этом компьютере — пароль трекер не видит.",
                          Theme.F(12.5f), Theme.Muted, new RectangleF(24, 114, W - 48, 40));
            DrawAccount(g, new RectangleF(24, 160, W - 48, 64));
            Theme.Str(g, loginStatus, Theme.F(13), loginFailed ? Theme.Red : Theme.AccentLight, 26, 234);

            // 2. План
            Theme.Str(g, "План", Theme.Semi(15), Theme.Text, 24, 270);
            float sw = 136, sx = W - 24 - sw;
            Row(g, 302, "Стримов в месяц");
            Stepper(g, "goal", sx, 296, sw, goal + " " + Theme.Plural(goal, "стрим", "стрима", "стримов"),
                    () => goal = Math.Max(1, goal - 1), () => goal = Math.Min(31, goal + 1));
            Row(g, 346, "Дни отчётов");
            Stepper(g, "day0", sx - 10 - sw, 340, sw, days[0] + "-го", () => ShiftDay(0, -1), () => ShiftDay(0, 1));
            Stepper(g, "day1", sx, 340, sw, days[1] + "-го", () => ShiftDay(1, -1), () => ShiftDay(1, 1));

            // 3. Удобства
            Checkbox(g, "auto", 24, 398, "Запускать вместе с Windows", autostart, () => autostart = !autostart);
            Checkbox(g, "lnk", 24, 432, "Ярлык на рабочем столе", shortcut, () => shortcut = !shortcut);

            // Кнопки
            var ok = new RectangleF(W - 24 - 160, H - 64, 160, 40);
            if (Channel != null) DrawButton(g, "ok", ok, first ? "Готово" : "Сохранить", Theme.Accent, Color.White, Save);
            else DrawButton(g, "ok", ok, first ? "Готово" : "Сохранить", Theme.CardHi, Theme.Faint, null, "Сначала войдите в Twitch");
            DrawButton(g, "cancel", new RectangleF(ok.X - 10 - 120, H - 64, 120, 40), first ? "Выйти" : "Отмена",
                       Theme.CardHi, Theme.Text, () => { DialogResult = DialogResult.Cancel; Close(); });
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
            string sub = !first && settings.channel != "" ? "сейчас отслеживается " + settings.channel : "нужен, чтобы трекер знал ваш канал";
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

        void OpenLogin()
        {
            try
            {
                Chrome.OpenVisible("https://www.twitch.tv/login");
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

        async void CheckLogin(bool quiet)
        {
            if (loginBusy) return;
            loginBusy = true;
            if (!quiet) { loginStatus = ""; loginFailed = false; }
            Invalidate();
            try
            {
                string name = await Task.Run(() => Chrome.TwitchLogin());   // запуск браузера — не в потоке окна
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
                if (!quiet)
                {
                    loginFailed = true;
                    loginStatus = "Не получилось проверить вход: " + ex.Message;
                }
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
