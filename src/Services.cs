// Настройки трекера и браузер (Chrome или Edge) со своим профилем для скриншотов статистики Twitch.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace StreamTracker
{
    // Где трекер хранит данные. В папке программы остаётся только StreamTracker.exe.
    static class Paths
    {
        // Настройки и стримы: %APPDATA%\StreamTracker
        public static readonly string Data =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamTracker");
        // Профиль браузера со входом в Twitch (большой и привязан к компьютеру): %LOCALAPPDATA%\StreamTracker
        public static readonly string Local =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamTracker");
        // Отчёты — это файлы пользователя: Документы\Трекер стримов\Отчёты
        public static readonly string Reports =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Трекер стримов", "Отчёты");

        // Раньше всё лежало рядом с программой — переносим на новые места один раз.
        // Сначала профили браузера: если какой-то занят (открыт браузер трекера), не трогаем ничего
        // и пробуем при следующем запуске, чтобы настройки и вход не разъехались по разным местам.
        // Переносим только то, что точно создал трекер: exe могут положить в «Загрузки» или на рабочий стол,
        // где лежат чужие settings.json, папки «Отчёты» и т. п. — их трогать нельзя.
        static readonly string[] ProfileNames =
            { "chrome-profile", "msedge-profile", "brave-profile", "vivaldi-profile", "chromium-profile", "browser-profile" };

        public static void MigrateFromAppFolder()
        {
            string app = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                Directory.CreateDirectory(Data);
                Directory.CreateDirectory(Local);
                foreach (var name in ProfileNames)
                {
                    string src = Path.Combine(app, name), dst = Path.Combine(Local, name);
                    // Профиль браузера Chromium всегда содержит файл «Local State»
                    if (Directory.Exists(src) && File.Exists(Path.Combine(src, "Local State")) && !Directory.Exists(dst))
                        MoveDir(src, dst);
                }
                MoveOurJson(Path.Combine(app, "settings.json"), Path.Combine(Data, "settings.json"), "\"channel\"", "\"lastSync\"");
                MoveOurJson(Path.Combine(app, "streams.json"), Path.Combine(Data, "streams.json"), "\"streams\"", "\"reports\"");

                string oldReports = Path.Combine(app, "Отчёты");
                if (Directory.Exists(oldReports))
                {
                    // Только папки отчётов трекера: «2026-09-14_2026-09-28»
                    foreach (var d in Directory.GetDirectories(oldReports))
                    {
                        string n = Path.GetFileName(d);
                        if (!Regex.IsMatch(n, @"^\d{4}-\d{2}-\d{2}_\d{4}-\d{2}-\d{2}$")) continue;
                        string dst = Path.Combine(Reports, n);
                        Directory.CreateDirectory(Reports);
                        if (!Directory.Exists(dst)) MoveDir(d, dst);
                    }
                    if (!Directory.EnumerateFileSystemEntries(oldReports).Any()) Directory.Delete(oldReports);
                }
            }
            catch (Exception ex) { Log.Error("перенос данных из папки программы", ex); }
        }

        static void MoveOurJson(string src, string dst, string marker1, string marker2)
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            string text = File.ReadAllText(src, Encoding.UTF8);
            if (text.Contains(marker1) && text.Contains(marker2)) File.Move(src, dst);
        }

        // Перенос папки; между разными дисками — копированием
        static void MoveDir(string src, string dst)
        {
            if (Path.GetPathRoot(src).Equals(Path.GetPathRoot(dst), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(src, dst);
                return;
            }
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(src, dst));
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(src, dst), true);
            Directory.Delete(src, true);
        }
    }

    // Журнал непредвиденных ошибок: %APPDATA%\StreamTracker\errors.log (не больше ~1 МБ)
    static class Log
    {
        static readonly object sync = new object();

        public static void Error(string where, Exception ex)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Paths.Data);
                    string path = Path.Combine(Paths.Data, "errors.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 1024 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + "\r\n" +
                                             (ex == null ? "(нет подробностей)" : ex.ToString()) + "\r\n\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // Настройки трекера (%APPDATA%\StreamTracker\settings.json). Пустой канал — значит, первый запуск.
    public class Settings
    {
        public string channel { get; set; }        // ник на Twitch (login)
        public bool byLink { get; set; }           // канал указан ссылкой, без входа: только учёт стримов и напоминания
        public int goal { get; set; }              // стримов в месяц
        public List<int> reportDays { get; set; }  // дни сдачи отчётов
        public string lastSync { get; set; }       // когда последний раз подтягивали стримы со статистики Twitch (yyyy-MM-dd)

        static readonly string SettingsPath = Path.Combine(Paths.Data, "settings.json");

        public Settings()
        {
            channel = "";
            goal = 20;
            reportDays = new List<int> { 13, 29 };
            lastSync = "";
        }

        public static Settings Load()
        {
            Settings s = null;
            try
            {
                if (File.Exists(SettingsPath))
                    s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                // Испорченный файл не теряем: откладываем копию рядом (настройки придётся задать заново)
                Log.Error("settings.json повреждён", ex);
                try { File.Copy(SettingsPath, SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
            }
            var d = new Settings();
            if (s == null) return d;
            if (s.channel == null) s.channel = "";
            if (s.goal < 1 || s.goal > 31) s.goal = d.goal;
            if (s.reportDays == null || s.reportDays.Count == 0 || s.reportDays.Any(x => x < 1 || x > 31)) s.reportDays = d.reportDays;
            if (s.lastSync == null) s.lastSync = "";
            return s;
        }

        public void Save()
        {
            reportDays = reportDays.Distinct().OrderBy(x => x).ToList();
            Directory.CreateDirectory(Paths.Data);
            // Через временный файл: если выключат питание посреди записи, прежние настройки останутся целыми
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
            if (File.Exists(SettingsPath)) File.Replace(tmp, SettingsPath, null);
            else File.Move(tmp, SettingsPath);
        }
    }
    // ---------- Chrome через DevTools Protocol ----------

    class Cdp : IDisposable
    {
        readonly ClientWebSocket ws = new ClientWebSocket();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending =
            new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        int nextId;

        public static async Task<Cdp> Connect(string wsUrl)
        {
            var c = new Cdp();
            await c.ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).ConfigureAwait(false);
            c.ReceiveLoop();
            return c;
        }

        async void ReceiveLoop()
        {
            var buf = new byte[1 << 16];
            var ms = new MemoryStream();
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    var msg = json.DeserializeObject(text) as Dictionary<string, object>;
                    object idObj;
                    if (msg == null || !msg.TryGetValue("id", out idObj)) continue;   // события не нужны
                    int id = Convert.ToInt32(idObj);
                    TaskCompletionSource<Dictionary<string, object>> tcs;
                    lock (pending)
                    {
                        if (!pending.TryGetValue(id, out tcs)) continue;
                        pending.Remove(id);
                    }
                    object err;
                    if (msg.TryGetValue("error", out err)) tcs.TrySetException(new Exception("Браузер: " + json.Serialize(err)));
                    else tcs.TrySetResult(msg.ContainsKey("result") ? msg["result"] as Dictionary<string, object> : null);
                }
            }
            catch { }
            lock (pending)
            {
                foreach (var t in pending.Values) t.TrySetException(new Exception("Связь с браузером прервалась"));
                pending.Clear();
            }
        }

        public async Task<Dictionary<string, object>> Send(string method, Dictionary<string, object> args = null)
        {
            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            lock (pending) pending[id] = tcs;
            var payload = Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object>
            {
                { "id", id }, { "method", method }, { "params", args ?? new Dictionary<string, object>() }
            }));
            await sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            finally { sendLock.Release(); }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
            if (done != tcs.Task) throw new TimeoutException("Браузер не ответил на " + method);
            return await tcs.Task.ConfigureAwait(false) ?? new Dictionary<string, object>();
        }

        public async Task<object> Eval(string js)
        {
            var r = await Send("Runtime.evaluate", new Dictionary<string, object>
            {
                { "expression", js }, { "returnByValue", true }, { "awaitPromise", true }
            }).ConfigureAwait(false);
            object ex;
            if (r.TryGetValue("exceptionDetails", out ex)) throw new Exception("Ошибка на странице: " + json.Serialize(ex));
            var res = r["result"] as Dictionary<string, object>;
            object v;
            return res != null && res.TryGetValue("value", out v) ? v : null;
        }

        public void Dispose()
        {
            try { ws.Abort(); } catch { }
            ws.Dispose();
        }
    }

    // Браузер с отдельным профилем трекера: вход в Twitch сохраняется в нём, обычный браузер не затрагивается.
    // Берём браузер по умолчанию, если он на Chromium (только такими можно управлять по DevTools);
    // иначе Microsoft Edge — он есть в любой Windows 10/11. Chrome — только если Edge удалён.
    static class Chrome
    {
        public const int Port = 9333;

        // Одна очередь на все дела с браузером (сбор отчёта, сверка, проверка входа):
        // иначе одно могло закрыть браузер, которым как раз пользуется другое
        public static readonly SemaphoreSlim Lock = new SemaphoreSlim(1, 1);

        // Окно входа, которое открыл трекер (его можно закрыть, когда вход выполнен)
        static Process loginWindow;
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static readonly Dictionary<string, string> Chromium = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome.exe", "Chrome" }, { "msedge.exe", "Edge" }, { "brave.exe", "Brave" },
            { "vivaldi.exe", "Vivaldi" }, { "chromium.exe", "Chromium" }, { "browser.exe", "Яндекс Браузер" }
        };
        static string exe, name;

        static void Detect()
        {
            if (exe != null) return;
            string def = DefaultBrowser();
            if (def != null)
            {
                string file = Path.GetFileName(def);
                bool yandex = file.Equals("browser.exe", StringComparison.OrdinalIgnoreCase);
                if (Chromium.ContainsKey(file) && (!yandex || def.IndexOf("Yandex", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    exe = def;
                    name = Chromium[file];
                    return;
                }
            }
            exe = FindApp("msedge.exe", @"Microsoft\Edge\Application\msedge.exe");
            name = "Edge";
            if (exe == null)
            {
                exe = FindApp("chrome.exe", @"Google\Chrome\Application\chrome.exe");
                name = "Chrome";
            }
        }

        // Путь к браузеру по умолчанию (для ссылок https) или null
        static string DefaultBrowser()
        {
            try
            {
                string progId;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                    progId = k == null ? null : k.GetValue("ProgId") as string;
                if (string.IsNullOrEmpty(progId)) return null;
                string cmd;
                using (var c = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                    cmd = c == null ? null : c.GetValue(null) as string;
                if (string.IsNullOrEmpty(cmd)) return null;
                cmd = cmd.Trim();
                string path = cmd.StartsWith("\"") ? cmd.Substring(1, cmd.IndexOf('"', 1) - 1)
                            : cmd.Substring(0, cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4);
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        static string FindApp(string name, string rel)
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using (var k = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name))
                {
                    var v = k == null ? null : k.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                }
            }
            return new[]
            {
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData
            }.Select(f => Path.Combine(Environment.GetFolderPath(f), rel)).FirstOrDefault(File.Exists);
        }

        public static string Name { get { Detect(); return name ?? "браузер"; } }

        // Свой профиль для каждого браузера: chrome-profile, msedge-profile, browser-profile…
        public static string ProfileDir
        {
            get
            {
                Detect();
                string id = exe == null ? "browser" : Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                return Path.Combine(Paths.Local, id + "-profile");
            }
        }

        static string Exe()
        {
            Detect();
            if (exe == null) throw new Exception("не нашёл подходящий браузер — нужен браузер на Chromium, например Microsoft Edge");
            return exe;
        }

        static string Get(string path)
        {
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                return wc.DownloadString("http://127.0.0.1:" + Port + path);
        }

        // Смотрим список открытых портов, а не стучимся в порт: подключение к закрытому порту
        // на Windows висит около секунды, и окно на это время замирало
        public static bool IsRunning()
        {
            try
            {
                return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners().Any(e => e.Port == Port);
            }
            catch { return false; }
        }

        static string Args()
        {
            return "--user-data-dir=\"" + ProfileDir + "\" --remote-debugging-port=" + Port +
                   " --no-first-run --no-default-browser-check";
        }

        // Видимое окно браузера — для входа в Twitch
        public static void OpenVisible(string url)
        {
            if (IsRunning())
            {
                if (IsHeadless())
                {
                    // Работает скрытый браузер (сверка, сбор отчёта, проверка входа) с тем же профилем.
                    // Новый Chrome передал бы страницу входа ему, и она открылась бы невидимо — закрываем его
                    // (дождавшись, пока он освободится) и запускаем видимый.
                    if (!Lock.Wait(TimeSpan.FromSeconds(20)))
                        throw new Exception("сейчас трекер собирает статистику Twitch — попробуйте через минуту");
                    try
                    {
                        Task.Run(() => CloseBrowser()).Wait(5000);
                        for (int i = 0; i < 25 && IsRunning(); i++) Thread.Sleep(200);
                    }
                    finally { Lock.Release(); }
                    if (IsRunning()) throw new Exception("не получилось закрыть фоновый браузер трекера — попробуйте ещё раз");
                }
                else
                {
                    // Видимый браузер уже открыт: страница входа есть — показываем её, а не плодим вкладки
                    try
                    {
                        var pages = json.DeserializeObject(Get("/json/list")) as object[];
                        var open = pages == null ? null : pages.OfType<Dictionary<string, object>>().FirstOrDefault(p =>
                            Convert.ToString(p["type"]) == "page" && Convert.ToString(p["url"]).Contains("twitch.tv/login"));
                        if (open != null)
                        {
                            Get("/json/activate/" + Convert.ToString(open["id"]));
                            return;
                        }
                    }
                    catch { }
                    // Иначе — новое окно, а не вкладка: если все окна закрыли, а Chrome остался работать в фоне,
                    // вкладка открылась бы без окна и была бы не видна
                    try
                    {
                        Task.Run(async () =>
                        {
                            var v = json.DeserializeObject(Get("/json/version")) as Dictionary<string, object>;
                            using (var browser = await Cdp.Connect(Convert.ToString(v["webSocketDebuggerUrl"])))
                                await browser.Send("Target.createTarget", new Dictionary<string, object>
                                {
                                    { "url", url }, { "newWindow", true }, { "background", false }
                                });
                        }).Wait(10000);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("новое окно входа", ex);
                        throw new Exception("не получилось открыть окно в уже запущенном браузере трекера — закройте его и попробуйте ещё раз");
                    }
                }
            }
            Directory.CreateDirectory(ProfileDir);
            loginWindow = Process.Start(Exe(), Args() + " \"" + url + "\"");
            // Ждём, пока браузер поднимется: иначе ожидание входа на медленном компьютере решило бы, что окно уже закрыли
            for (int i = 0; i < 75 && !IsRunning(); i++) Thread.Sleep(200);
        }

        // Открыта ли ещё страница входа Twitch. Смотрим только список вкладок (простой HTTP-запрос),
        // не подключаясь к браузеру: подключение во время входа Twitch может принять за бота.
        public static bool LoginPageOpen()
        {
            try
            {
                var pages = json.DeserializeObject(Get("/json/list")) as object[];
                return pages != null && pages.OfType<Dictionary<string, object>>().Any(p =>
                    Convert.ToString(p["type"]) == "page" &&
                    (Convert.ToString(p["url"]).Contains("twitch.tv/login") || Convert.ToString(p["url"]).Contains("passport.twitch.tv")));
            }
            catch { return false; }
        }

        static bool IsHeadless()
        {
            try
            {
                var v = json.DeserializeObject(Get("/json/version")) as Dictionary<string, object>;
                return v != null && Convert.ToString(v["User-Agent"]).IndexOf("Headless", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // Закрыть окно входа, открытое трекером, — после того как вход выполнен
        public static async Task CloseLoginWindow()
        {
            var p = loginWindow;
            loginWindow = null;
            if (p == null || !IsRunning()) return;
            try { if (p.HasExited) return; } catch { return; }
            await Lock.WaitAsync();
            try { await CloseBrowser(); }
            finally { Lock.Release(); }
        }

        // Скрытый браузер мог остаться после сбоя или выхода во время сбора отчёта — закрываем его.
        // Видимое окно (например, вход в Twitch) не трогаем.
        public static async Task CloseStrayHeadless()
        {
            if (!IsRunning()) return;
            try
            {
                var v = json.DeserializeObject(Get("/json/version")) as Dictionary<string, object>;
                if (v == null || Convert.ToString(v["User-Agent"]).IndexOf("Headless", StringComparison.OrdinalIgnoreCase) < 0) return;
                await CloseBrowser();
            }
            catch (Exception ex) { Log.Error("закрытие скрытого браузера", ex); }
        }

        static async Task CloseBrowser()
        {
            try
            {
                var v = json.DeserializeObject(Get("/json/version")) as Dictionary<string, object>;
                using (var browser = await Cdp.Connect(Convert.ToString(v["webSocketDebuggerUrl"])))
                    await Task.WhenAny(browser.Send("Browser.close"), Task.Delay(3000));
            }
            catch { }
        }

        // Браузер без окна. Возвращает процесс, если запускали сами (его потом закрываем), иначе null.
        static async Task<Process> EnsureRunning()
        {
            if (IsRunning()) return null;
            Directory.CreateDirectory(ProfileDir);
            var proc = Process.Start(new ProcessStartInfo(Exe(), Args() +
                " --headless=new --window-size=1600,1200 --hide-scrollbars --mute-audio about:blank")
                { UseShellExecute = false, CreateNoWindow = true });
            for (int i = 0; i < 50 && !IsRunning(); i++) await Task.Delay(200);
            if (!IsRunning()) throw new Exception("браузер не запустился");
            return proc;
        }

        public static async Task CloseOwn(Process proc)
        {
            if (proc == null) return;
            await CloseBrowser();
            try { if (!proc.WaitForExit(5000)) proc.Kill(); } catch { }
        }

        // Новая вкладка для работы
        public static async Task<ChromeSession> Start()
        {
            var own = await EnsureRunning();
            string created;
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                created = wc.UploadString("http://127.0.0.1:" + Port + "/json/new?about:blank", "PUT", "");
            var target = json.DeserializeObject(created) as Dictionary<string, object>;
            var cdp = await Cdp.Connect(Convert.ToString(target["webSocketDebuggerUrl"]));
            return new ChromeSession(cdp, Convert.ToString(target["id"]), own);
        }

        // Выйти из Twitch в профиле трекера (для «Сменить аккаунт»). Профиль только трекера,
        // поэтому стираем все его куки — обычный браузер пользователя это не затрагивает.
        public static async Task Logout()
        {
            if (!Directory.Exists(ProfileDir)) return;
            await Lock.WaitAsync();
            try
            {
                var own = await EnsureRunning();
                Exception error = null;
                try
                {
                    var v = json.DeserializeObject(Get("/json/version")) as Dictionary<string, object>;
                    using (var browser = await Cdp.Connect(Convert.ToString(v["webSocketDebuggerUrl"])))
                        await browser.Send("Storage.clearCookies");
                }
                catch (Exception ex) { error = ex; }
                await CloseOwn(own);
                if (error != null) throw error;
            }
            finally { Lock.Release(); }
        }

        // Под каким ником выполнен вход в Twitch в профиле трекера (null — не выполнен)
        // launchIfNeeded = false — только спросить уже открытый браузер (для ожидания входа в открытом окне),
        // чтобы проверка каждые 3 секунды не запускала скрытый браузер, когда окно входа закрыли
        public static async Task<string> TwitchLogin(bool launchIfNeeded = true)
        {
            if (!Directory.Exists(ProfileDir)) return null;
            if (!launchIfNeeded && !IsRunning()) return null;
            await Lock.WaitAsync();
            try { return await TwitchLoginLocked(); }
            finally { Lock.Release(); }
        }

        static async Task<string> TwitchLoginLocked()
        {
            var own = await EnsureRunning();
            string login = null;
            Exception error = null;
            try
            {
                var v = json.DeserializeObject(Get("/json/version")) as Dictionary<string, object>;
                using (var browser = await Cdp.Connect(Convert.ToString(v["webSocketDebuggerUrl"])))
                {
                    var r = await browser.Send("Storage.getCookies");
                    var cookies = r.ContainsKey("cookies") ? r["cookies"] as object[] : null;
                    if (cookies != null)
                        login = cookies.OfType<Dictionary<string, object>>()
                            .Where(c => Convert.ToString(c["name"]) == "login" && Convert.ToString(c["domain"]).EndsWith("twitch.tv"))
                            .Select(c => Convert.ToString(c["value"])).FirstOrDefault();
                }
            }
            catch (Exception ex) { error = ex; }
            await CloseOwn(own);   // браузер без окна, запущенный ради проверки, сразу закрываем
            if (error != null) throw error;
            return login;
        }
    }

    class ChromeSession : IDisposable
    {
        public readonly Cdp Page;
        readonly string targetId;
        readonly Process ownProcess;

        public ChromeSession(Cdp page, string targetId, Process ownProcess)
        {
            Page = page;
            this.targetId = targetId;
            this.ownProcess = ownProcess;
        }

        public void Dispose()
        {
            try
            {
                using (var wc = new WebClient()) wc.DownloadString("http://127.0.0.1:" + Chrome.Port + "/json/close/" + targetId);
            }
            catch { }
            Page.Dispose();
            // Браузер без окна, который запускали сами, закрываем целиком
            if (ownProcess != null) Task.Run(() => Chrome.CloseOwn(ownProcess)).Wait(10000);
        }
    }
}
