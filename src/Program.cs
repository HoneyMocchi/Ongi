using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("Ongi")]
[assembly: System.Reflection.AssemblyDescription("Ongi — Claude·GPT 사용량과 프롬프트 캐시 온기")]
[assembly: System.Reflection.AssemblyProduct("Ongi")]
// 버전을 올릴 때는 아래 세 줄을 함께 바꿉니다. package.ps1은 InformationalVersion으로 zip 이름을 정합니다.
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.1.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.3.1")]

namespace Ongi
{
    sealed class Options
    {
        public bool Mock, Probe, Exit, Floating, SelfTest, Caches, TestAlert, OpenSettings, CheckUpdate;
        public string Preview, TestUpdateZip;
        public int ExitAfter, AfterUpdatePid;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--mock") o.Mock = true;
                else if (a == "--probe") o.Probe = true;
                else if (a == "--selftest") o.SelfTest = true;
                else if (a == "--caches") o.Caches = true;
                else if (a == "--test-alert") { o.TestAlert = true; o.Mock = true; }
                else if (a == "--exit") o.Exit = true;
                else if (a == "--floating") o.Floating = true;
                else if (a == "--preview" && next != null) { o.Preview = next; i++; }
                else if (a == "--exit-after" && next != null) { int.TryParse(next, out o.ExitAfter); i++; }
                else if (a == "--after-update" && next != null) { int.TryParse(next, out o.AfterUpdatePid); i++; }
                else if (a == "--open-settings") o.OpenSettings = true;
                else if (a == "--check-update") o.CheckUpdate = true;
                else if (a == "--test-update" && next != null) { o.TestUpdateZip = next; i++; }
            }
            return o;
        }
    }

    static class Program
    {
        public const string HostTitle = "Ongi.Host";

        // 한 번에 하나만 실행되게 잡는 잠금입니다. 업데이트할 때는 새 버전이 뜰 수 있게 잠시 놓았다가, 실패하면 다시 잡습니다.
        static Mutex instance;
        static string mutexName;
        static string[] launchArgs;

        public static void ReleaseInstance()
        {
            if (instance == null) return;
            try { instance.ReleaseMutex(); } catch { }
            instance.Dispose();
            instance = null;
        }

        public static void ReacquireInstance()
        {
            bool created;
            var m = new Mutex(true, mutexName, out created);
            if (!created) { try { m.WaitOne(5000); } catch (AbandonedMutexException) { } }
            instance = m;
        }

        // 업데이트 후 새 버전을 같은 옵션으로 띄웁니다(업데이트용 옵션은 빼고).
        public static string RestartArgs()
        {
            var keep = new List<string>();
            for (int i = 0; i < launchArgs.Length; i++)
            {
                string a = launchArgs[i].ToLowerInvariant();
                if (a == "--after-update" || a == "--test-update") { i++; continue; }
                if (a == "--open-settings") continue;
                keep.Add(launchArgs[i].Contains(" ") ? "\"" + launchArgs[i] + "\"" : launchArgs[i]);
            }
            return string.Join(" ", keep);
        }

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // TLS 1.2 | 1.3
            launchArgs = args;
            var opt = Options.Parse(args);

            if (opt.Preview != null)
            {
                Preview.Save(opt.Preview);
                Preview.SaveSettings(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opt.Preview)), Path.GetFileNameWithoutExtension(opt.Preview) + "-settings.png"));
                return 0;
            }
            if (opt.Probe) { AttachConsoleOut(); return Diagnostics.Probe(); }
            if (opt.SelfTest) { AttachConsoleOut(); return Diagnostics.SelfTest(); }
            if (opt.Caches) { AttachConsoleOut(); return Diagnostics.Caches(); }
            if (opt.CheckUpdate) { AttachConsoleOut(); return Diagnostics.CheckUpdate(); }
            if (opt.Exit)
            {
                foreach (var title in new[] { HostTitle, HostTitle + ".Mock" })
                {
                    var h = Native.FindWindow(null, title);
                    if (h != IntPtr.Zero) Native.PostMessage(h, Native.WM_APP_EXIT, IntPtr.Zero, IntPtr.Zero);
                }
                return 0;
            }

            bool created;
            mutexName = opt.Mock ? @"Local\Ongi.Mock" : @"Local\Ongi.Singleton";
            instance = new Mutex(true, mutexName, out created);
            // 업데이트로 띄워진 경우 이전 버전이 잠금을 막 놓는 중일 수 있어 잠시 기다립니다.
            if (!created && opt.AfterUpdatePid > 0)
            {
                try { created = instance.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                // 이미 실행 중이면 그쪽에 위젯을 다시 붙이고 새로고침하라고 알립니다.
                instance.Dispose();
                var h = Native.FindWindow(null, opt.Mock ? HostTitle + ".Mock" : HostTitle);
                if (h != IntPtr.Zero) Native.PostMessage(h, Native.WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }
            try
            {
                if (!opt.Mock) Autostart.Repair();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI 예외: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("처리되지 않은 예외: " + e.ExceptionObject);
                Application.Run(new App(opt));
            }
            finally { ReleaseInstance(); }
            return 0;
        }

        static void AttachConsoleOut()
        {
            Native.AttachConsole(-1);
            try
            {
                var w = new StreamWriter(System.Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                System.Console.SetOut(w);
                System.Console.WriteLine();
            }
            catch { }
        }
    }

    static class Diagnostics
    {
        // 실제 자격 증명으로 한 번 조회해 결과만 출력합니다. 토큰 값은 출력하지 않습니다.
        public static int Probe()
        {
            var list = new[] { new KeyValuePair<string, IProvider>("Claude", new ClaudeProvider()), new KeyValuePair<string, IProvider>("GPT(Codex)", new CodexProvider()) };
            int fails = 0;
            foreach (var kv in list)
            {
                try { Print(kv.Key, kv.Value.Fetch()); }
                catch (Exception ex) { fails++; Say(kv.Key + ": 실패 — " + ex.Message); }
            }
            return fails;
        }

        // 자격 증명 없이 내장 예시 응답으로 파서를 검사합니다.
        public static int SelfTest()
        {
            int fails = 0;
            Action<bool, string> check = (ok, what) => { if (!ok) fails++; Say((ok ? "  통과  " : "  실패  ") + what); };
            var now = DateTime.UtcNow;

            var c = ClaudeProvider.Parse(J.Parse(
                "{\"five_hour\":{\"utilization\":37.0,\"resets_at\":\"2026-10-10T08:59:59.943648+00:00\"}," +
                "\"seven_day\":{\"utilization\":12,\"resets_at\":\"2026-10-14T03:59:59.943679+00:00\"},\"seven_day_oauth_apps\":null," +
                "\"seven_day_opus\":{\"utilization\":0.0,\"resets_at\":null},\"seven_day_sonnet\":{\"utilization\":4.0,\"resets_at\":\"2026-10-14T03:59:59+00:00\"}}"), "Max 5x");
            check(c.Session.Percent == 37 && c.Session.ResetUtc == new DateTime(2026, 10, 10, 8, 59, 59, DateTimeKind.Utc).AddTicks(9436480), "Claude 5시간 37% · 리셋 08:59:59Z");
            check(c.Weekly.Percent == 12 && c.Weekly.LengthSec == 604800, "Claude 주간 12%");
            check(c.Extras.Count == 1 && c.Extras[0].Name == "Sonnet 주간", "Claude 추가 창: 0%·리셋 없음인 Opus는 숨기고 Sonnet만 표시");

            var x = CodexProvider.ParseApi(J.Parse(
                "{\"plan_type\":\"plus\",\"rate_limit\":{\"allowed\":true,\"limit_reached\":false," +
                "\"primary_window\":{\"used_percent\":23,\"limit_window_seconds\":18000,\"reset_after_seconds\":5400,\"reset_at\":1791640800}," +
                "\"secondary_window\":{\"used_percent\":41,\"limit_window_seconds\":604800,\"reset_after_seconds\":200000,\"reset_at\":1791900000}}}"));
            check(x.Plan == "plus" && x.Session.Percent == 23 && x.Session.ResetUtc == Util.FromUnix(1791640800), "Codex API 5시간 23% · reset_at(초)");
            check(x.Weekly.Percent == 41 && x.Weekly.LengthSec == 604800, "Codex API 주간 41%");

            var swapped = CodexProvider.ParseApi(J.Parse(
                "{\"rate_limit\":{\"primary_window\":{\"used_percent\":70,\"limit_window_seconds\":604800,\"reset_after_seconds\":100}," +
                "\"secondary_window\":null}}"));
            check(swapped.Session == null && swapped.Weekly != null && swapped.Weekly.Percent == 70, "Codex API 주간 창만 있는 플랜");

            var idle = CodexProvider.ParseApi(J.Parse(
                "{\"rate_limit\":{\"primary_window\":{\"used_percent\":0,\"limit_window_seconds\":18000,\"reset_after_seconds\":18000,\"reset_at\":1791640800}," +
                "\"secondary_window\":{\"used_percent\":9,\"limit_window_seconds\":604800,\"reset_after_seconds\":300000,\"reset_at\":1791900000}}}"));
            check(idle.Session.Percent == 0 && !idle.Session.ResetUtc.HasValue && idle.Weekly.ResetUtc.HasValue, "Codex API 0%·리셋=지금+5시간이면 '시작 전'으로 처리");

            string tmp = Path.Combine(Path.GetTempPath(), "ongi-selftest.jsonl");
            File.WriteAllLines(tmp, new[]
            {
                "{\"timestamp\":\"2026-10-10T05:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"primary\":{\"used_percent\":12.0,\"window_minutes\":300,\"resets_at\":1791640800},\"secondary\":{\"used_percent\":30.0,\"window_minutes\":10080,\"resets_at\":1791900000}}}}",
                "{\"timestamp\":\"2026-10-10T05:01:00.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":[]}}",
                "{\"timestamp\":\"2026-10-10T06:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{},\"rate_limits\":{\"primary\":{\"used_percent\":55.0,\"window_minutes\":299,\"resets_in_seconds\":3600},\"secondary\":{\"used_percent\":20.0,\"window_minutes\":10079,\"resets_in_seconds\":86400}}}}",
                "{\"timestamp\":\"2026-10-10T06:00:05.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"done\"}}",
            });
            var lg = CodexProvider.ScanRollout(tmp);
            File.Delete(tmp);
            var ts = new DateTime(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc);
            check(lg != null && lg.Session.Percent == 55 && lg.Session.ResetUtc == ts.AddSeconds(3600) && lg.UpdatedUtc == ts, "Codex 로그: 마지막 rate_limits 줄 · resets_in_seconds 형식");
            check(lg != null && lg.Weekly.Percent == 20 && lg.Weekly.LengthSec == 10079 * 60, "Codex 로그: 주간 20%");

            var cl = CacheScanner.ParseClaude(new[]
            {
                "{\"type\":\"ai-title\",\"aiTitle\":\"테스트 채팅\",\"sessionId\":\"s\"}",
                "{\"type\":\"assistant\",\"isSidechain\":false,\"requestId\":\"r1\",\"timestamp\":\"2026-10-10T05:00:00.000Z\",\"cwd\":\"C:\\\\Projects\\\\Sample\",\"message\":{\"model\":\"claude-opus-5-5\",\"usage\":{\"cache_read_input_tokens\":4000,\"cache_creation_input_tokens\":100,\"cache_creation\":{\"ephemeral_1h_input_tokens\":100,\"ephemeral_5m_input_tokens\":0}}}}",
                "{\"parentUuid\":\"p\",\"isSidechain\":false,\"type\":\"user\",\"message\":{\"content\":\"tool result \\\"timestamp\\\":\\\"2026-10-10T05:10:01.000Z\\\"\"},\"uuid\":\"u1\",\"timestamp\":\"2026-10-10T05:09:58.000Z\",\"toolUseResult\":{\"timestamp\":\"2026-10-10T05:10:01.500Z\"}}",
                "{\"type\":\"assistant\",\"isSidechain\":false,\"requestId\":\"r2\",\"timestamp\":\"2026-10-10T05:10:02.500Z\",\"message\":{\"model\":\"claude-opus-5-5\",\"usage\":{\"cache_read_input_tokens\":5000,\"cache_creation_input_tokens\":0,\"cache_creation\":{\"ephemeral_1h_input_tokens\":0,\"ephemeral_5m_input_tokens\":0}}}}",
                "{\"type\":\"assistant\",\"isSidechain\":false,\"requestId\":\"r2\",\"timestamp\":\"2026-10-10T05:10:05.000Z\",\"message\":{\"model\":\"claude-opus-5-5\",\"usage\":{\"cache_read_input_tokens\":5000,\"cache_creation_input_tokens\":0}}}",
                "{\"type\":\"assistant\",\"isSidechain\":true,\"requestId\":\"r3\",\"timestamp\":\"2026-10-10T05:20:00.000Z\",\"message\":{\"model\":\"claude-haiku-5-5\",\"usage\":{\"cache_read_input_tokens\":1}}}",
            }, "sess");
            check(cl != null && cl.LastUtc == new DateTime(2026, 10, 10, 5, 9, 58, DateTimeKind.Utc) && cl.TtlSec == 3600 && cl.CachedTokens == 5000 && cl.Title == "테스트 채팅",
                  "Claude 캐시: 요청 직전 입력 시각(내부 timestamp 무시) · 이전 요청에서 1시간 TTL 확인 · 서브에이전트 제외");

            var cx = CacheScanner.ParseCodex(new[]
            {
                "{\"timestamp\":\"2026-10-10T05:00:00.000Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6.1-sol\",\"cwd\":\"C:\\\\Projects\"}}",
                "{\"timestamp\":\"2026-10-10T05:00:30.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":67262,\"cached_input_tokens\":66048}}}}",
                "{\"timestamp\":\"2026-10-10T05:01:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}",
            }, "id", "스레드", 0);
            check(cx != null && cx.LastUtc == new DateTime(2026, 10, 10, 5, 0, 30, DateTimeKind.Utc) && cx.TtlSec == 1800 && cx.CachedTokens == 66048 && cx.Title == "스레드",
                  "Codex 캐시: 마지막 token_count 시각 · GPT-6.x는 30분");
            check(CacheScanner.CodexTtl("gpt-5.4") == 300 && CacheScanner.CodexTtl("gpt-5.6-codex") == 1800, "Codex 캐시 TTL 모델 판별");
            check(Util.Timer(TimeSpan.FromSeconds(272)) == "4:32" && Util.Timer(TimeSpan.FromMinutes(52)) == "52m" && Util.LongTimer(TimeSpan.FromSeconds(272)) == "4분 32초",
                  "캐시 타이머 표기(10분 미만은 초 단위)");

            check(Util.Compact(TimeSpan.FromMinutes(83)) == "1h23m" && Util.Compact(TimeSpan.FromSeconds(30)) == "1m" && Util.Compact(TimeSpan.FromHours(98)) == "4d2h", "짧은 남은 시간 표기");
            check(Util.Long(TimeSpan.FromMinutes(83)) == "1시간 23분" && Util.Long(TimeSpan.FromHours(48)) == "2일", "긴 남은 시간 표기");
            check(Util.Pct(99.7) == "99%" && Util.Pct(100) == "100%" && Util.Pct(-3) == "0%", "퍼센트 표기(100% 직전은 내림)");
            var today = DateTime.Today;
            check(Util.Clock(today.AddHours(15).AddMinutes(44).ToUniversalTime(), today.AddHours(9).ToUniversalTime()) == "오후 3:44" &&
                  Util.Clock(today.AddDays(1).AddHours(9).ToUniversalTime(), today.AddHours(9).ToUniversalTime()) == "내일 오전 9:00" &&
                  Util.Clock(today.AddHours(17).AddMinutes(29).AddSeconds(59.7).ToUniversalTime(), today.AddHours(9).ToUniversalTime()) == "오후 5:30" &&
                  Util.Clock(today.AddHours(-1).ToUniversalTime(), today.AddHours(9).ToUniversalTime()) == "어제 오후 11:00" &&
                  Util.Clock(today.AddDays(-3).AddHours(9).ToUniversalTime(), today.AddHours(9).ToUniversalTime()) == today.AddDays(-3).ToString("M'/'d") + " 오전 9:00",
                  "12시간제 시각 표기 · 분 반올림 · 어제/지난 날짜");
            var expired = new UsageWindow { Percent = 80, ResetUtc = now.AddMinutes(-1) };
            check(expired.Effective(now) == 0, "리셋 시각이 지난 캐시 값은 0%로 표시");

            // 설정 창: 저장 버튼 없이 값을 바꾸는 즉시 설정에 반영되는지(읽기 전용 설정이라 파일은 그대로)
            var testCfg = Settings.Load(true);
            int applied = 0;
            bool recreate = false;
            int oldRefresh = testCfg.RefreshSeconds;
            string oldMode = testCfg.Mode;
            using (var sf = new SettingsForm(testCfg, r => { applied++; recreate |= r; }, 1f, true))
            {
                Stepper firstStepper = null;
                Segmented firstSeg = null;
                foreach (Control ctl in sf.Controls)
                {
                    if (firstStepper == null) firstStepper = ctl as Stepper;
                    if (firstSeg == null) firstSeg = ctl as Segmented;
                }
                int before = applied;
                firstStepper.Value = oldRefresh >= 600 ? oldRefresh - 30 : oldRefresh + 30;
                bool stepOk = applied == before + 1 && testCfg.RefreshSeconds == firstStepper.Value && !recreate;
                firstSeg.Selected = oldMode == "floating" ? 0 : 1;
                bool modeOk = testCfg.Mode != oldMode && recreate;
                check(applied == 0 + 2 && stepOk && modeOk, "설정 창: 바꾸는 즉시 적용(조회 주기 → 바로 반영, 위치 → 위젯 다시 만들기)");
            }

            check(Updater.ParseVersion("v1.2.0") == new Version(1, 2, 0) && Updater.ParseVersion("1.2") == new Version(1, 2, 0) &&
                  Updater.ParseVersion("v1.10.0") > Updater.ParseVersion("v1.9.3") && Updater.ParseVersion("최신") == null,
                  "업데이트 버전 비교(v 접두, 두 자리, 1.10 > 1.9, 잘못된 태그)");

            Func<double, double, PaceState> pace = (pct, hoursLeft) =>
            {
                double u, e;
                return Pace.Of(new UsageWindow { Percent = pct, ResetUtc = now.AddHours(hoursLeft), LengthSec = 604800 }, now, out u, out e);
            };
            double u0, e0;
            check(pace(10, 124) == PaceState.Room && pace(1, 167) == PaceState.Even && pace(61, 53) == PaceState.Even &&
                  pace(50, 120) == PaceState.Fast && pace(100, 10) == PaceState.Spent &&
                  Pace.Of(new UsageWindow { Percent = 0 }, now, out u0, out e0) == PaceState.Unknown && e0 < 0,
                  "주간 페이스: 여유(10%·26% 경과) · 적정 · 과속(50%·29% 경과) · 소진 · 리셋 모름");

            Say(fails == 0 ? "셀프테스트 모두 통과" : "셀프테스트 실패 " + fails + "건");
            return fails;
        }

        // 로컬 대화 기록만 읽어 채팅별 캐시 상태를 출력합니다(자격 증명은 쓰지 않습니다).
        public static int Caches()
        {
            var now = DateTime.UtcNow;
            var all = new List<ChatCache>();
            all.AddRange(CacheScanner.Claude(now));
            all.AddRange(CacheScanner.Codex(now, Settings.Load(true).CodexCacheMinutes));
            all.Sort((a, b) => b.LastUtc.CompareTo(a.LastUtc));
            Say("최근 65분 안에 요청한 채팅 " + all.Count + "개");
            foreach (var c in all)
                Say("  " + (c.Key == "claude" ? "Claude" : "GPT   ") + " | " + c.Title + " | 마지막 요청 " + Util.Clock(c.LastUtc, now) +
                    " | TTL " + Util.Ttl(c.TtlSec) + " | " + (c.Hot(now) ? "남음 " + Util.LongTimer(c.Remaining(now)) : "만료") +
                    " | 캐시 " + (c.CachedTokens / 1000) + "K 토큰");
            return 0;
        }

        // GitHub 최신 릴리스와 이 위치에서 스스로 업데이트할 수 있는지 출력합니다.
        public static int CheckUpdate()
        {
            try
            {
                var r = Updater.Latest();
                Say("지금 " + Updater.Format(Updater.Current) + " · 최신 " + (r == null ? "없음" : r.VersionText));
                if (r != null)
                    Say("  zip: " + (r.ZipUrl ?? "없음") + "\n  sha256: " + (r.ShaUrl ?? "없음"));
                Say("  이 위치: " + Updater.Block());
                return 0;
            }
            catch (Exception ex) { Say("확인 실패 — " + ex.Message); return 1; }
        }

        static void Print(string name, Snapshot s)
        {
            var now = DateTime.UtcNow;
            Say(name + ": 출처=" + s.Source + " 플랜=" + (s.Plan ?? "-") + " 기준시각=" + s.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd tt h:mm:ss", Util.Ko));
            Say("  5시간 " + Describe(s.Session, now));
            Say("  주간  " + Describe(s.Weekly, now));
            foreach (var e in s.Extras) Say("  " + e.Name + " " + Describe(e.Window, now));
            if (s.Note != null) Say("  참고: " + s.Note);
        }

        static string Describe(UsageWindow w, DateTime now)
        {
            if (w == null) return "—";
            string reset = w.ResetUtc.HasValue ? Util.Long(w.ResetUtc.Value - now) + " 후 (" + Util.Clock(w.ResetUtc.Value, now) + ")" : "리셋 정보 없음";
            return Util.Pct(w.Percent) + " · " + reset + (w.LengthSec > 0 ? " · 창 " + (w.LengthSec / 60) + "분" : "");
        }

        static void Say(string s)
        {
            Log.Write("[진단] " + s);
            try { System.Console.WriteLine(s); } catch { }
        }
    }

    // 자격 증명 없이 샘플 데이터로 위젯과 상세 패널을 PNG로 그립니다(디자인 확인용).
    static class Preview
    {
        public static void Save(string path)
        {
            var cfg = new Settings();
            var now = DateTime.UtcNow;
            var normal = States(now, 0);
            var alert = States(now, 1);

            var mixed = States(now, 2);
            var shots = new List<Bitmap>();
            foreach (bool dark in new[] { true, false })
            {
                var p = new Palette(dark, cfg);
                shots.Add(Strip(p, 1f, normal, now, cfg, false));
                shots.Add(Strip(p, 1f, alert, now, cfg, true));   // 마우스를 올린 상태
                shots.Add(Strip(p, 1f, mixed, now, cfg, false));
                shots.Add(Strip(p, 1.5f, normal, now, cfg, false));
                shots.Add(Strip(p, 1f, normal, now, cfg, false, 32)); // 낮은 작업표시줄
                if (dark) shots.Add(Strip(p, 3f, mixed, now, cfg, false)); // 디테일 확인용 3배 확대
            }
            var popDark = Popup(new Palette(true, cfg), 1f, normal, now);
            var popAlert = Popup(new Palette(true, cfg), 1f, alert, now);
            var popLight = Popup(new Palette(false, cfg), 1f, mixed, now);
            var popZoom = Popup(new Palette(true, cfg), 2f, alert, now);

            int gap = 16;
            int leftW = 0, leftH = gap;
            foreach (var b in shots) { leftW = Math.Max(leftW, b.Width); leftH += b.Height + gap; }
            int rightW = popDark.Width + popAlert.Width + popLight.Width + gap * 2;
            int rightH = Math.Max(popDark.Height, Math.Max(popAlert.Height, popLight.Height)) + gap + popZoom.Height;
            rightW = Math.Max(rightW, popZoom.Width);
            using (var canvas = new Bitmap(gap + leftW + gap + rightW + gap, Math.Max(leftH, rightH + gap * 2)))
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(120, 120, 128));
                int y = gap;
                foreach (var b in shots) { g.DrawImage(b, gap, y); y += b.Height + gap; }
                int x = gap + leftW + gap;
                g.DrawImage(popDark, x, gap); x += popDark.Width + gap;
                g.DrawImage(popAlert, x, gap); x += popAlert.Width + gap;
                g.DrawImage(popLight, x, gap);
                g.DrawImage(popZoom, gap + leftW + gap, gap * 2 + Math.Max(popDark.Height, Math.Max(popAlert.Height, popLight.Height)));
                canvas.Save(path, ImageFormat.Png);
            }
            foreach (var b in shots) b.Dispose();
            popDark.Dispose(); popAlert.Dispose(); popLight.Dispose(); popZoom.Dispose();
        }

        // 미리보기용 업데이트 상태(네트워크 없이)
        sealed class FakeUpdates : IUpdateHost
        {
            readonly bool newer;
            public FakeUpdates(bool newer) { this.newer = newer; }
            public string CurrentVersion { get { return Updater.Format(Updater.Current); } }
            public void CheckUpdate(Action<UpdateCheck> done)
            {
                var v = new Version(Updater.Current.Major, Updater.Current.Minor + 1, 0);
                done(new UpdateCheck { Newer = newer, Block = UpdateBlock.None, Latest = new ReleaseInfo { Version = v, VersionText = Updater.Format(v) } });
            }
            public void StartUpdate(ReleaseInfo release, Action<int> progress, Action<string> failed) { }
        }

        // 설정 창을 화면에 띄우지 않고 비트맵으로 그립니다(다크·라이트 나란히).
        public static void SaveSettings(string path)
        {
            var shots = new List<Bitmap>();
            foreach (bool dark in new[] { true, false })
            {
                // 다크는 "새 버전 있음", 라이트는 "최신" 상태로 그려 업데이트 버튼 모양을 함께 봅니다.
                using (var f = new SettingsForm(new Settings(), _ => { }, 1f, dark, new FakeUpdates(dark)))
                {
                    var unused = f.Handle;
                    foreach (Control c in f.Controls) unused = c.Handle; // 숨은 창에서도 자식 컨트롤을 그리도록 핸들을 만듭니다.
                    var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.ClientSize));
                    shots.Add(bmp);
                }
            }
            using (var canvas = new Bitmap(shots[0].Width + shots[1].Width + 48, Math.Max(shots[0].Height, shots[1].Height) + 32))
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(120, 120, 128));
                g.DrawImage(shots[0], 16, 16);
                g.DrawImage(shots[1], 32 + shots[0].Width, 16);
                canvas.Save(path, ImageFormat.Png);
            }
            foreach (var b in shots) b.Dispose();
        }

        static List<ProviderState> States(DateTime now, int variant)
        {
            var c = new ProviderState("claude", "Claude", "Claude", null) { Data = MockProvider.Make("claude", variant, now), Caches = MockProvider.Caches("claude", now) };
            var g = new ProviderState("codex", "GPT", "GPT Codex", null) { Caches = MockProvider.Caches("codex", now) };
            if (variant == 0) g.Data = MockProvider.Make("codex", 0, now);
            else if (variant == 1) g.Error = "인증 실패(401) — Codex에서 다시 로그인하세요";
            else
            {
                // 반쯤 찬 점(주간 50%)과, 오래된 값(흐림) + 주간 한도 소진(주간 리셋까지 빨간 시간)을 보여 줍니다.
                c.Data.Session.Percent = 63;
                c.Data.Weekly.Percent = 50;
                g.Data = MockProvider.Make("codex", 0, now);
                g.Data.UpdatedUtc = now.AddMinutes(-40);
                g.Data.Session.Percent = 30;
                g.Data.Weekly.Percent = 100;
                g.Error = "네트워크 오류 (Timeout)";
            }
            return new List<ProviderState> { c, g };
        }

        // 작업표시줄 배경 위에 위젯을 합성해 실제 모습과 비슷하게 만듭니다.
        static Bitmap Strip(Palette p, float s, List<ProviderState> states, DateTime now, Settings cfg, bool hover, int logicalH = 48)
        {
            int h = (int)(logicalH * s), w = BarRenderer.MeasureWidth(s, h, states.Count);
            int stripW = w + (int)(150 * s);
            var bmp = new Bitmap(stripW, h);
            using (var g = Graphics.FromImage(bmp))
            using (var widget = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                g.Clear(p.Dark ? Color.FromArgb(28, 28, 30) : Color.FromArgb(238, 240, 244));
                using (var wg = Graphics.FromImage(widget)) BarRenderer.Render(wg, widget.Size, s, p, states, now, cfg, hover, false);
                g.DrawImage(widget, 0, 0);
                // 오른쪽에는 알림 영역 자리를 흉내 내 시계를 그립니다.
                var clock = Draw.Font("Segoe UI", 12f * s);
                Draw.Setup(g, false);
                Draw.Text(g, "오후 2:05", clock, p.Text, stripW - 12 * s, h * 0.33f, 1);
                Draw.Text(g, "2026-10-10", clock, p.Text, stripW - 12 * s, h * 0.68f, 1);
            }
            return bmp;
        }

        static Bitmap Popup(Palette p, float s, List<ProviderState> states, DateTime now)
        {
            int w = (int)Math.Ceiling(PopupRenderer.Width * s), h;
            using (var tmp = new Bitmap(1, 1)) using (var g = Graphics.FromImage(tmp)) h = PopupRenderer.Render(g, s, w, p, states, now, false);
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(p.PanelBg);
                PopupRenderer.Render(g, s, w, p, states, now, true);
                using (var pen = new Pen(p.PanelBorder)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            }
            return bmp;
        }
    }
}
