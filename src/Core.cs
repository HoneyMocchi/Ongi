using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Ongi
{
    static class Paths
    {
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ongi");
        public static string File(string name) { Directory.CreateDirectory(Dir); return Path.Combine(Dir, name); }    }

    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string path = Paths.File("ongi.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    System.IO.File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // JavaScriptSerializer 결과(Dictionary/object[])를 경로로 꺼내는 도우미입니다.
    static class J
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static object Parse(string s) { return S.DeserializeObject(s); }
        public static string Write(object o) { return S.Serialize(o); }

        public static object Get(object o, params string[] path)
        {
            foreach (var key in path)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(key, out o)) return null;
            }
            return o;
        }

        public static string Str(object o) { return o as string; }

        public static double? Num(object o)
        {
            if (o is int) return (int)o;
            if (o is long) return (long)o;
            if (o is decimal) return (double)(decimal)o;
            if (o is double) return (double)o;
            double v;
            var s = o as string;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        public static bool? Bool(object o) { return o is bool ? (bool?)(bool)o : null; }

        // 키 이름으로 객체 트리를 깊이 우선 탐색합니다.
        public static object Find(object o, string key, int depth = 0)
        {
            if (depth > 8 || o == null) return null;
            var d = o as Dictionary<string, object>;
            if (d != null)
            {
                object v;
                if (d.TryGetValue(key, out v) && v != null) return v;
                foreach (var child in d.Values) { var r = Find(child, key, depth + 1); if (r != null) return r; }
                return null;
            }
            var a = o as object[];
            if (a != null) foreach (var child in a) { var r = Find(child, key, depth + 1); if (r != null) return r; }
            return null;
        }
    }

    static class Util
    {
        public static readonly CultureInfo Ko = new CultureInfo("ko-KR");

        public static DateTime FromUnix(double v)
        {
            // 13자리 값은 밀리초로 봅니다.
            if (v > 1e12) v /= 1000.0;
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(v);
        }

        public static DateTime? ParseIso(string s)
        {
            DateTimeOffset dto;
            if (!string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                return dto.UtcDateTime;
            return null;
        }

        public static DateTime? ParseTime(object o)
        {
            var n = J.Num(o);
            if (n.HasValue && !(o is string)) return FromUnix(n.Value);
            return ParseIso(o as string);
        }

        // 다른 프로세스가 쓰는 중인 파일도 읽을 수 있게 공유 모드로 엽니다.
        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) return sr.ReadToEnd();
        }

        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
            else System.IO.File.Move(tmp, path);
        }

        static int CeilMinutes(TimeSpan t) { return Math.Max(0, (int)Math.Ceiling(t.TotalMinutes)); }

        // 작업표시줄용 짧은 표기: 42m, 1h05m, 3d4h
        public static string Compact(TimeSpan t)
        {
            int mins = CeilMinutes(t);
            int d = mins / 1440, h = (mins % 1440) / 60, m = mins % 60;
            if (d > 0) return d + "d" + h + "h";
            if (h > 0) return h + "h" + m.ToString("00") + "m";
            return m + "m";
        }

        // 캐시 타이머: 10분 이상은 52m / 1h00m, 10분 미만은 초까지 4:32
        public static string Timer(TimeSpan t)
        {
            if (t.TotalMinutes >= 10) return Compact(t);
            int sec = Math.Max(0, (int)Math.Ceiling(t.TotalSeconds));
            return (sec / 60) + ":" + (sec % 60).ToString("00");
        }

        public static string LongTimer(TimeSpan t)
        {
            if (t.TotalMinutes >= 10) return Long(t);
            int sec = Math.Max(0, (int)Math.Ceiling(t.TotalSeconds));
            return sec >= 60 ? (sec / 60) + "분 " + (sec % 60).ToString("00") + "초" : sec + "초";
        }

        public static string Tokens(long n)
        {
            if (n >= 1000000) return (n / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            return (n / 1000) + "K";
        }

        public static string Ttl(int sec) { return sec >= 3600 ? (sec / 3600) + "시간" : (sec / 60) + "분"; }

        // 상세 패널용 표기: 1시간 23분, 4일 2시간
        public static string Long(TimeSpan t)
        {
            int mins = CeilMinutes(t);
            if (mins <= 0) return "곧";
            int d = mins / 1440, h = (mins % 1440) / 60, m = mins % 60;
            if (d > 0) return h > 0 ? d + "일 " + h + "시간" : d + "일";
            if (h > 0) return m > 0 ? h + "시간 " + m + "분" : h + "시간";
            return m + "분";
        }

        // 12시간제: 오후 3:44, 내일 오전 9:00, 수 오후 4:22(7일 안), 10/20 오후 4:22
        public static string Clock(DateTime utc, DateTime nowUtc)
        {
            // 17:29:59.7처럼 오는 값을 오후 5:30으로 보이도록 분 단위로 반올림합니다.
            DateTime l = utc.ToLocalTime().AddSeconds(30), n = nowUtc.ToLocalTime();
            string t = l.ToString("tt h:mm", Ko);
            if (l.Date == n.Date) return t;
            if (l.Date == n.Date.AddDays(1)) return "내일 " + t;
            if (l.Date == n.Date.AddDays(-1)) return "어제 " + t;
            if (l.Date > n.Date && l.Date < n.Date.AddDays(7)) return l.ToString("ddd", Ko) + " " + t;
            return l.ToString("M'/'d ", Ko) + t;
        }

        public static string Pct(double v)
        {
            if (v >= 100) return "100%";
            return Math.Max(0, Math.Floor(v)).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static string Ago(DateTime utc, DateTime nowUtc)
        {
            var t = nowUtc - utc;
            if (t.TotalSeconds < 60) return "방금";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + "분 전";
            if (t.TotalHours < 24) return (int)t.TotalHours + "시간 전";
            return utc.ToLocalTime().ToString("M'/'d tt h:mm", Ko);
        }
    }

    sealed class FetchException : Exception
    {
        public readonly int RetryAfterSec;
        public bool RateLimited;
        public bool SignedOut;               // 로그인 정보 자체가 없음(작업표시줄에서 아이콘을 숨김)
        public FetchException(string msg, int retryAfterSec = 0) : base(msg) { RetryAfterSec = retryAfterSec; }
    }

    // 한도 창 하나(5시간 또는 주간)의 상태입니다.
    sealed class UsageWindow
    {
        public double Percent;
        public DateTime? ResetUtc;
        public int LengthSec;

        // 리셋 시각이 지난 캐시 값은 0%로 봅니다.
        public bool Expired(DateTime now) { return ResetUtc.HasValue && ResetUtc.Value <= now; }
        public double Effective(DateTime now) { return Expired(now) ? 0 : Math.Max(0, Percent); }

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "pct", Percent },
                { "reset", ResetUtc.HasValue ? ResetUtc.Value.ToString("o") : null },
                { "len", LengthSec },
            };
        }

        public static UsageWindow FromJson(object o)
        {
            var pct = J.Num(J.Get(o, "pct"));
            if (!pct.HasValue) return null;
            return new UsageWindow { Percent = pct.Value, ResetUtc = Util.ParseIso(J.Str(J.Get(o, "reset"))), LengthSec = (int)(J.Num(J.Get(o, "len")) ?? 0) };
        }
    }

    sealed class NamedWindow
    {
        public string Name;
        public UsageWindow Window;
    }

    sealed class Snapshot
    {
        public string Plan, Source, Note;
        public DateTime UpdatedUtc;
        public UsageWindow Session, Weekly;
        public List<NamedWindow> Extras = new List<NamedWindow>();

        public DateTime? NextResetAfter(DateTime now)
        {
            DateTime? best = null;
            foreach (var w in new[] { Session, Weekly })
                if (w != null && w.ResetUtc.HasValue && w.ResetUtc.Value > now && (!best.HasValue || w.ResetUtc.Value < best.Value))
                    best = w.ResetUtc;
            return best;
        }

        public Dictionary<string, object> ToJson()
        {
            var extras = new List<object>();
            foreach (var e in Extras) { var j = e.Window.ToJson(); j["name"] = e.Name; extras.Add(j); }
            return new Dictionary<string, object>
            {
                { "plan", Plan }, { "source", Source }, { "updated", UpdatedUtc.ToString("o") },
                { "session", Session != null ? Session.ToJson() : null },
                { "weekly", Weekly != null ? Weekly.ToJson() : null },
                { "extras", extras },
            };
        }

        public static Snapshot FromJson(object o)
        {
            var updated = Util.ParseIso(J.Str(J.Get(o, "updated")));
            if (!updated.HasValue) return null;
            var s = new Snapshot
            {
                Plan = J.Str(J.Get(o, "plan")), Source = J.Str(J.Get(o, "source")), UpdatedUtc = updated.Value,
                Session = UsageWindow.FromJson(J.Get(o, "session")), Weekly = UsageWindow.FromJson(J.Get(o, "weekly")),
            };
            var extras = J.Get(o, "extras") as object[];
            if (extras != null)
                foreach (var e in extras)
                {
                    var w = UsageWindow.FromJson(e);
                    if (w != null) s.Extras.Add(new NamedWindow { Name = J.Str(J.Get(e, "name")), Window = w });
                }
            return s;
        }
    }

    sealed class ProviderState
    {
        public readonly string Key, Name, Title;
        public readonly IProvider Provider;
        public Snapshot Data;
        public List<ChatCache> Caches = new List<ChatCache>();
        public string Error;
        public bool Busy;
        public bool SignedOut;               // 마지막 조회에서 로그인 정보가 없었고 보여 줄 값도 없음
        public int RateLimitHits;           // 연속 429 횟수(지수 백오프용)
        public DateTime NextFetchUtc, LastAttemptUtc;

        public ProviderState(string key, string name, string title, IProvider provider)
        {
            Key = key; Name = name; Title = title; Provider = provider;
        }
    }

    sealed class Settings
    {
        public string Mode = "taskbar";      // taskbar | floating
        public List<string> MonitorList = new List<string>(); // 작업표시줄 모드에서 위젯을 붙일 모니터(Screen.DeviceName, 예: \\.\DISPLAY2). 비면 주 모니터
        public int Offset = 0;               // 작업표시줄 모드: 알림 영역 왼쪽 끝에서 띄울 거리(논리 px). Offsets에 없는 모니터가 씁니다
        public Dictionary<string, int> Offsets = new Dictionary<string, int>(); // 모니터별로 드래그해 둔 거리(Screen.DeviceName → 논리 px)
        public int? FloatX, FloatY;          // 떠 있는 창 모드 위치(물리 px)
        public int RefreshSeconds = 90;
        public bool ShowClaude = true, ShowCodex = true;
        public int WarnPercent = 75, CritPercent = 90;
        public int CodexCacheMinutes;        // 0이면 모델로 추정(GPT-5.6 이후 30분, 이전 5분)
        public bool CacheAlerts = true;      // 큰 캐시가 식기 전에 Windows 알림
        public int AlertTokens = 500000;     // 이 토큰 수 이상인 채팅만 알림
        public int AlertMinutes = 5;         // 식기 몇 분 전에 알릴지(TTL이 짧으면 TTL의 1/5)
        public int IconSize = 34;            // 작업표시줄 아이콘 크기(논리 px): 30 | 34 | 38
        public static readonly int[] IconSizes = { 30, 34, 38 };
        bool readOnly;

        static string FilePath { get { return Paths.File("settings.json"); } }

        public static Settings Load(bool readOnly)
        {
            var s = new Settings { readOnly = readOnly };
            try
            {
                if (!System.IO.File.Exists(FilePath)) return s;
                var o = J.Parse(System.IO.File.ReadAllText(FilePath));
                s.Mode = J.Str(J.Get(o, "mode")) == "floating" ? "floating" : "taskbar";
                var list = J.Get(o, "monitorList") as object[];
                if (list != null) foreach (var m in list) { var n = m as string; if (!string.IsNullOrEmpty(n)) s.MonitorList.Add(n); }
                // 1.3 이하의 "monitors"(주 모니터 | 모든 모니터 | 직접 선택)를 모니터 목록으로 옮깁니다.
                string mon = J.Str(J.Get(o, "monitors"));
                if (mon == "primary") s.MonitorList.Clear();
                else if (mon == "all") foreach (var sc in System.Windows.Forms.Screen.AllScreens) if (!s.MonitorList.Contains(sc.DeviceName)) s.MonitorList.Add(sc.DeviceName);
                s.Offset = (int)(J.Num(J.Get(o, "offset")) ?? s.Offset);
                var offsets = J.Get(o, "offsets") as Dictionary<string, object>;
                if (offsets != null) foreach (var kv in offsets) { var v = J.Num(kv.Value); if (v.HasValue) s.Offsets[kv.Key] = Math.Max(0, (int)v.Value); }
                var fx = J.Num(J.Get(o, "floatX")); var fy = J.Num(J.Get(o, "floatY"));
                if (fx.HasValue && fy.HasValue) { s.FloatX = (int)fx.Value; s.FloatY = (int)fy.Value; }
                s.RefreshSeconds = Math.Max(30, (int)(J.Num(J.Get(o, "refreshSeconds")) ?? s.RefreshSeconds));
                s.ShowClaude = J.Bool(J.Get(o, "showClaude")) ?? true;
                s.ShowCodex = J.Bool(J.Get(o, "showCodex")) ?? true;
                s.WarnPercent = (int)(J.Num(J.Get(o, "warnPercent")) ?? s.WarnPercent);
                s.CritPercent = (int)(J.Num(J.Get(o, "critPercent")) ?? s.CritPercent);
                s.CodexCacheMinutes = Math.Max(0, (int)(J.Num(J.Get(o, "codexCacheMinutes")) ?? 0));
                s.CacheAlerts = J.Bool(J.Get(o, "cacheAlerts")) ?? true;
                s.AlertTokens = Math.Max(0, (int)(J.Num(J.Get(o, "alertTokens")) ?? s.AlertTokens));
                s.AlertMinutes = Math.Max(1, (int)(J.Num(J.Get(o, "alertMinutes")) ?? s.AlertMinutes));
                int icon = (int)(J.Num(J.Get(o, "iconSize")) ?? s.IconSize);
                s.IconSize = Array.IndexOf(IconSizes, icon) >= 0 ? icon : 34;
                if (!s.ShowClaude && !s.ShowCodex) s.ShowClaude = s.ShowCodex = true;
            }
            catch (Exception ex) { Log.Write("설정 읽기 실패: " + ex.Message); }
            return s;
        }

        // 위젯을 붙일 모니터인지 봅니다. 고른 모니터가 하나도 연결되어 있지 않으면 주 모니터에 붙입니다.
        public bool WantsMonitor(System.Windows.Forms.Screen sc)
        {
            foreach (var a in System.Windows.Forms.Screen.AllScreens)
                if (MonitorList.Contains(a.DeviceName)) return MonitorList.Contains(sc.DeviceName);
            return sc.Primary;
        }

        public int OffsetFor(string monitor)
        {
            int v;
            return monitor != null && Offsets.TryGetValue(monitor, out v) ? v : Offset;
        }

        public void Save()
        {
            if (readOnly) return;
            try
            {
                var d = new Dictionary<string, object>
                {
                    { "mode", Mode }, { "monitorList", MonitorList }, { "offset", Offset }, { "offsets", Offsets }, { "floatX", FloatX }, { "floatY", FloatY },
                    { "refreshSeconds", RefreshSeconds }, { "showClaude", ShowClaude }, { "showCodex", ShowCodex },
                    { "warnPercent", WarnPercent }, { "critPercent", CritPercent }, { "codexCacheMinutes", CodexCacheMinutes },
                    { "cacheAlerts", CacheAlerts }, { "alertTokens", AlertTokens }, { "alertMinutes", AlertMinutes }, { "iconSize", IconSize },
                };
                Util.WriteAtomic(FilePath, J.Write(d));
            }
            catch (Exception ex) { Log.Write("설정 저장 실패: " + ex.Message); }
        }
    }

    // 마지막으로 받은 값을 저장해 두었다가 재시작 직후 바로 보여 줍니다.
    static class Cache
    {
        static string FilePath { get { return Paths.File("cache.json"); } }

        public static void Load(IList<ProviderState> states)
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return;
                var o = J.Parse(System.IO.File.ReadAllText(FilePath));
                foreach (var st in states)
                {
                    var snap = Snapshot.FromJson(J.Get(o, st.Key));
                    if (snap != null) st.Data = snap;
                }
            }
            catch (Exception ex) { Log.Write("캐시 읽기 실패: " + ex.Message); }
        }

        public static void Save(IList<ProviderState> states)
        {
            try
            {
                var d = new Dictionary<string, object>();
                foreach (var st in states) if (st.Data != null) d[st.Key] = st.Data.ToJson();
                Util.WriteAtomic(FilePath, J.Write(d));
            }
            catch (Exception ex) { Log.Write("캐시 저장 실패: " + ex.Message); }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Ongi") != null;
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("Ongi", "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                else k.DeleteValue("Ongi", false);
            }
        }

        // 휴대용 exe라 사용자가 파일을 옮길 수 있습니다. 자동 실행이 켜져 있는데 등록된 경로가 지금 exe와 다르면 고칩니다.
        public static void Repair()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string current = "\"" + System.Windows.Forms.Application.ExecutablePath + "\"";
                    var stored = k == null ? null : k.GetValue("Ongi") as string;
                    if (stored == null || string.Equals(stored, current, StringComparison.OrdinalIgnoreCase)) return;
                    k.SetValue("Ongi", current);
                    Log.Write("자동 실행 경로를 지금 위치로 고쳤습니다");
                }
            }
            catch (Exception ex) { Log.Write("자동 실행 경로 확인 실패: " + ex.Message); }
        }
    }

    // 작업표시줄과 같은 시스템 테마(SystemUsesLightTheme)를 따릅니다.
    sealed class Palette
    {
        public readonly bool Dark;
        public readonly Color Text, Dim, Faint, Track, Hover, Warn, Crit, PanelBg, PanelBorder, Divider, BadgeText, Label, TimeInfo, Meta;
        readonly int warnAt, critAt;
        public readonly long BigCacheTokens;   // 이 크기 이상 캐시는 "큰 캐시"(만료 알림 기준과 같음)

        public Palette(bool dark, Settings cfg)
        {
            Dark = dark;
            warnAt = cfg.WarnPercent; critAt = cfg.CritPercent;
            BigCacheTokens = cfg.AlertTokens;
            // 보조 글자는 역할별로 옅은 색을 나눠 줍니다: 항목 라벨(모래빛) · 시간 정보(청회색) · 부가 정보(중립 회색)
            if (dark)
            {
                Text = Color.FromArgb(240, 255, 255, 255); Dim = Color.FromArgb(196, 196, 200); Faint = Color.FromArgb(146, 146, 152);
                Label = Color.FromArgb(206, 192, 172); TimeInfo = Color.FromArgb(150, 177, 206); Meta = Color.FromArgb(140, 142, 150);
                Track = Color.FromArgb(48, 255, 255, 255); Hover = Color.FromArgb(22, 255, 255, 255);
                Warn = Color.FromArgb(255, 199, 66); Crit = Color.FromArgb(255, 102, 99);
                PanelBg = Color.FromArgb(255, 36, 36, 38); PanelBorder = Color.FromArgb(60, 255, 255, 255); Divider = Color.FromArgb(26, 255, 255, 255);
                BadgeText = Color.FromArgb(21, 21, 21);
            }
            else
            {
                Text = Color.FromArgb(230, 0, 0, 0); Dim = Color.FromArgb(86, 86, 92); Faint = Color.FromArgb(124, 124, 130);
                Label = Color.FromArgb(112, 94, 72); TimeInfo = Color.FromArgb(56, 90, 128); Meta = Color.FromArgb(120, 122, 130);
                Track = Color.FromArgb(36, 0, 0, 0); Hover = Color.FromArgb(16, 0, 0, 0);
                Warn = Color.FromArgb(196, 128, 0); Crit = Color.FromArgb(214, 52, 48);
                PanelBg = Color.FromArgb(255, 249, 249, 249); PanelBorder = Color.FromArgb(40, 0, 0, 0); Divider = Color.FromArgb(22, 0, 0, 0);
                BadgeText = Color.White;
            }
        }

        public Color Brand(string key)
        {
            if (key == "claude") return Dark ? Color.FromArgb(234, 140, 104) : Color.FromArgb(196, 94, 58);
            return Dark ? Color.FromArgb(56, 201, 160) : Color.FromArgb(14, 138, 108);
        }

        public bool IsHot(double pct) { return pct >= warnAt; }

        public Color Level(double pct, Color normal)
        {
            if (pct >= critAt) return Crit;
            if (pct >= warnAt) return Warn;
            return normal;
        }

        // 아이콘 안쪽 캐시 액체 색: Heat와 같은 색 흐름이지만 채도·명도를 낮추고 반투명으로 깔아 가운데 숫자 대비를 지킵니다.
        public Color Liquid(double f)
        {
            Color fresh = Dark ? Color.FromArgb(104, 158, 136) : Color.FromArgb(118, 176, 150);
            Color mid = Dark ? Color.FromArgb(170, 150, 104) : Color.FromArgb(204, 178, 120);
            Color low = Dark ? Color.FromArgb(178, 112, 98) : Color.FromArgb(214, 146, 128);
            Color c = f >= 0.5 ? Mix(mid, fresh, (f - 0.5) / 0.5) : f >= 0.15 ? Mix(low, mid, (f - 0.15) / 0.35) : low;
            return Color.FromArgb(Dark ? 107 : 115, c);
        }

        // 캐시 남은 비율(1 → 0)에 따른 차분한 색: 세이지 초록 → 모래 → 산호. 쨍한 원색은 피합니다.
        public Color Heat(double f)
        {
            Color fresh = Dark ? Color.FromArgb(132, 196, 166) : Color.FromArgb(52, 128, 100);
            Color mid = Dark ? Color.FromArgb(212, 186, 126) : Color.FromArgb(160, 118, 40);
            Color low = Dark ? Color.FromArgb(222, 136, 116) : Color.FromArgb(186, 84, 66);
            if (f >= 0.5) return Mix(mid, fresh, (f - 0.5) / 0.5);
            if (f >= 0.15) return Mix(low, mid, (f - 0.15) / 0.35);
            return low;
        }

        static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Fade(Color c, double k) { return Color.FromArgb((int)(c.A * k), c.R, c.G, c.B); }

        public static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return !(v is int && (int)v == 1);
                }
            }
            catch { return true; }
        }
    }
}
