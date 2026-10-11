using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Ongi
{
    interface IProvider
    {
        // 백그라운드 스레드에서 호출됩니다. 실패하면 FetchException을 던집니다.
        Snapshot Fetch();
    }

    static class Http
    {
        public static string Get(string url, Dictionary<string, string> headers, string authHint)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Accept = "application/json";
            req.UserAgent = "Ongi/1.0 (Windows)";
            foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new FetchException("네트워크 오류 (" + ex.Status + ")", 60);
                int code = (int)resp.StatusCode, retry;
                int.TryParse(resp.Headers["Retry-After"], out retry);
                resp.Dispose();
                if (code == 401) throw new FetchException("인증 실패(401) — " + authHint, 300);
                if (code == 403) throw new FetchException("접근 거부(403)", 300);
                if (code == 429) throw new FetchException("요청 제한(429) — 잠시 뒤 재시도", Math.Max(retry, 300)) { RateLimited = true };
                throw new FetchException("서버 응답 " + code, 120);
            }
        }
    }

    // Claude Code의 OAuth 토큰으로 /api/oauth/usage를 조회합니다. 토큰은 읽기만 하고 갱신·저장하지 않습니다.
    sealed class ClaudeProvider : IProvider
    {
        public Snapshot Fetch()
        {
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Paths.Home, ".claude");
            string credPath = Path.Combine(dir, ".credentials.json");
            if (!File.Exists(credPath)) throw new FetchException("Claude Code 로그인 정보가 없습니다", 600);

            var oauth = J.Get(J.Parse(Util.ReadShared(credPath)), "claudeAiOauth");
            string token = J.Str(J.Get(oauth, "accessToken"));
            if (string.IsNullOrEmpty(token)) throw new FetchException("구독(OAuth) 로그인이 아닙니다", 600);
            var exp = J.Num(J.Get(oauth, "expiresAt"));
            if (exp.HasValue && exp.Value > 0 && Util.FromUnix(exp.Value) < DateTime.UtcNow)
                throw new FetchException("토큰 만료 — Claude Code를 쓰면 자동 갱신됩니다", 120);

            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + token },
                { "anthropic-beta", "oauth-2025-04-20" },
            };
            var r = J.Parse(Http.Get("https://api.anthropic.com/api/oauth/usage", headers, "Claude Code에서 다시 로그인하세요"));
            return Parse(r, PlanName(oauth));
        }

        internal static Snapshot Parse(object r, string plan)
        {
            var snap = new Snapshot { Source = "API", UpdatedUtc = DateTime.UtcNow, Plan = plan };
            snap.Session = Win(J.Get(r, "five_hour"), 5 * 3600);
            snap.Weekly = Win(J.Get(r, "seven_day"), 7 * 86400);
            AddExtra(snap, r, "seven_day_opus", "Opus 주간");
            AddExtra(snap, r, "seven_day_sonnet", "Sonnet 주간");
            if (snap.Session == null && snap.Weekly == null) throw new FetchException("응답 형식을 해석하지 못했습니다", 600);
            return snap;
        }

        static UsageWindow Win(object o, int lengthSec)
        {
            var pct = J.Num(J.Get(o, "utilization"));
            if (!pct.HasValue) return null;
            return new UsageWindow { Percent = pct.Value, ResetUtc = Util.ParseTime(J.Get(o, "resets_at")), LengthSec = lengthSec };
        }

        static void AddExtra(Snapshot snap, object r, string key, string name)
        {
            var w = Win(J.Get(r, key), 7 * 86400);
            if (w != null && (w.Percent > 0 || w.ResetUtc.HasValue)) snap.Extras.Add(new NamedWindow { Name = name, Window = w });
        }

        static string PlanName(object oauth)
        {
            string tier = (J.Str(J.Get(oauth, "rateLimitTier")) ?? "").ToLowerInvariant();
            string sub = J.Str(J.Get(oauth, "subscriptionType"));
            if (tier.Contains("max_20x")) return "Max 20x";
            if (tier.Contains("max_5x")) return "Max 5x";
            return sub;
        }
    }

    // Codex(ChatGPT 로그인)의 한도를 조회합니다. API가 실패하면 Codex 세션 로그의 rate_limits 기록을 씁니다.
    sealed class CodexProvider : IProvider
    {
        public Snapshot Fetch()
        {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Paths.Home, ".codex");

            FetchException apiError;
            try { return FromApi(home); }
            catch (FetchException ex) { apiError = ex; }
            catch (Exception ex) { apiError = new FetchException("오류: " + ex.Message, 300); }

            Snapshot fromLog = null;
            try { fromLog = FromLogs(home); } catch (Exception ex) { Log.Write("Codex 로그 읽기 실패: " + ex.Message); }
            if (fromLog == null) throw apiError;
            fromLog.Note = "API " + apiError.Message + " · 로그 기준";
            return fromLog;
        }

        static Snapshot FromApi(string home)
        {
            string authPath = Path.Combine(home, "auth.json");
            if (!File.Exists(authPath)) throw new FetchException("Codex 로그인 정보가 없습니다", 600);
            var auth = J.Parse(Util.ReadShared(authPath));
            string token = J.Str(J.Get(auth, "tokens", "access_token"));
            string account = J.Str(J.Get(auth, "tokens", "account_id"));
            if (string.IsNullOrEmpty(token))
                throw new FetchException(J.Get(auth, "OPENAI_API_KEY") != null ? "API 키 로그인에는 구독 한도가 없습니다" : "ChatGPT 로그인이 필요합니다", 600);

            var headers = new Dictionary<string, string> { { "Authorization", "Bearer " + token } };
            if (!string.IsNullOrEmpty(account)) headers["ChatGPT-Account-Id"] = account;
            return ParseApi(J.Parse(Http.Get("https://chatgpt.com/backend-api/wham/usage", headers, "Codex에서 다시 로그인하세요")));
        }

        internal static Snapshot ParseApi(object r)
        {
            var rl = J.Get(r, "rate_limit");
            var a = ApiWin(J.Get(rl, "primary_window"));
            var b = ApiWin(J.Get(rl, "secondary_window"));
            if (a == null && b == null) throw new FetchException("응답 형식을 해석하지 못했습니다", 600);
            var snap = new Snapshot { Source = "API", UpdatedUtc = DateTime.UtcNow, Plan = J.Str(J.Get(r, "plan_type")) };
            Assign(snap, a, b);
            return snap;
        }

        static UsageWindow ApiWin(object o)
        {
            var pct = J.Num(J.Get(o, "used_percent"));
            if (!pct.HasValue) return null;
            var w = new UsageWindow { Percent = pct.Value, LengthSec = (int)(J.Num(J.Get(o, "limit_window_seconds")) ?? 0) };
            w.ResetUtc = Util.ParseTime(J.Get(o, "reset_at"));
            var after = J.Num(J.Get(o, "reset_after_seconds"));
            if (!w.ResetUtc.HasValue && after.HasValue) w.ResetUtc = DateTime.UtcNow.AddSeconds(after.Value);
            // 아직 쓰지 않은 창은 리셋 시각을 "지금 + 창 길이"로 계속 밀어서 보내므로 '시작 전'으로 봅니다.
            if (w.Percent <= 0 && w.LengthSec > 0 && after.HasValue && after.Value >= w.LengthSec - 120) w.ResetUtc = null;
            return w;
        }

        // 창 길이로 5시간/주간을 가립니다. 길이를 모르면 primary=5시간, secondary=주간으로 둡니다.
        static void Assign(Snapshot s, UsageWindow primary, UsageWindow secondary)
        {
            foreach (var w in new[] { primary, secondary })
            {
                if (w == null) continue;
                if (w.LengthSec > 0 && w.LengthSec <= 86400) { if (s.Session == null) s.Session = w; }
                else if (w.LengthSec >= 2 * 86400) { if (s.Weekly == null) s.Weekly = w; }
                else if (w == primary && s.Session == null) s.Session = w;
                else if (s.Weekly == null) s.Weekly = w;
            }
        }

        internal static Snapshot FromLogs(string home)
        {
            string root = Path.Combine(home, "sessions");
            if (!Directory.Exists(root)) return null;
            foreach (var file in NewestRollouts(root, 4))
            {
                var snap = ScanRollout(file);
                if (snap != null) return snap;
            }
            return null;
        }

        static IEnumerable<string> NumericDirs(string path)
        {
            return Directory.GetDirectories(path)
                .Where(d => Path.GetFileName(d).All(char.IsDigit))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);
        }

        // sessions/YYYY/MM/DD/rollout-*.jsonl 중 최근 사흘 폴더에서 가장 최근에 쓰인 파일부터 봅니다.
        static List<string> NewestRollouts(string root, int max)
        {
            var days = new List<string>();
            foreach (var y in NumericDirs(root))
            {
                foreach (var m in NumericDirs(y))
                {
                    foreach (var d in NumericDirs(m)) { days.Add(d); if (days.Count >= 3) break; }
                    if (days.Count >= 3) break;
                }
                if (days.Count >= 3) break;
            }
            return days.SelectMany(d => Directory.GetFiles(d, "*.jsonl"))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max)
                .Select(f => f.FullName)
                .ToList();
        }

        internal static Snapshot ScanRollout(string file)
        {
            string text;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 4 * 1024 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int n = 0, k;
                while (n < buf.Length && (k = fs.Read(buf, n, buf.Length - n)) > 0) n += k;
                text = Encoding.UTF8.GetString(buf, 0, n);
            }

            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0 || line.IndexOf("used_percent", StringComparison.Ordinal) < 0) continue;
                object o;
                try { o = J.Parse(line); } catch { continue; }
                var rl = J.Find(o, "rate_limits");
                if (rl == null) continue;
                DateTime ts = Util.ParseIso(J.Str(J.Get(o, "timestamp"))) ?? File.GetLastWriteTimeUtc(file);
                var a = LogWin(J.Get(rl, "primary"), ts);
                var b = LogWin(J.Get(rl, "secondary"), ts);
                if (a == null && b == null) continue;
                var snap = new Snapshot { Source = "로그", UpdatedUtc = ts, Plan = J.Str(J.Get(rl, "plan_type")) };
                Assign(snap, a, b);
                return snap;
            }
            return null;
        }

        static UsageWindow LogWin(object o, DateTime ts)
        {
            var pct = J.Num(J.Get(o, "used_percent"));
            if (!pct.HasValue) return null;
            var w = new UsageWindow { Percent = pct.Value };
            var mins = J.Num(J.Get(o, "window_minutes"));
            if (mins.HasValue) w.LengthSec = (int)(mins.Value * 60);
            w.ResetUtc = Util.ParseTime(J.Get(o, "resets_at"));
            var inSec = J.Num(J.Get(o, "resets_in_seconds"));
            if (!w.ResetUtc.HasValue && inSec.HasValue) w.ResetUtc = ts.AddSeconds(inSec.Value);
            return w;
        }
    }

    // 자격 증명 없이 화면을 확인하기 위한 샘플 데이터입니다(--mock, --preview).
    sealed class MockProvider : IProvider
    {
        readonly string key;
        readonly int variant;
        int calls;

        public MockProvider(string key, int variant = 0) { this.key = key; this.variant = variant; }

        public Snapshot Fetch()
        {
            System.Threading.Thread.Sleep(300);
            return Make(key, variant, DateTime.UtcNow, calls++);
        }

        public static List<ChatCache> Caches(string key, DateTime now)
        {
            if (key == "claude")
                return new List<ChatCache>
                {
                    new ChatCache { Key = key, Id = "a", Title = "작업표시줄 위젯 만들기", LastUtc = now.AddMinutes(-8), TtlSec = 3600, CachedTokens = 267000 },
                    new ChatCache { Key = key, Id = "b", Title = "로그인 버그 추적", LastUtc = now.AddMinutes(-51), TtlSec = 3600, CachedTokens = 120000 },
                    new ChatCache { Key = key, Id = "c", Title = "릴리스 노트 작성", LastUtc = now.AddMinutes(-55.5), TtlSec = 3600, CachedTokens = 88000 },
                };
            return new List<ChatCache>
            {
                new ChatCache { Key = key, Id = "d", Title = "데이터 파이프라인 점검", LastUtc = now.AddMinutes(-23), TtlSec = 1800, CachedTokens = 66000 },
            };
        }

        public static Snapshot Make(string key, int variant, DateTime now, int step = 0)
        {
            var s = new Snapshot { Source = "샘플", UpdatedUtc = now.AddSeconds(-20) };
            if (key == "claude")
            {
                s.Plan = "Max 5x";
                if (variant == 0)
                {
                    s.Session = new UsageWindow { Percent = 42 + step, ResetUtc = now.AddMinutes(83), LengthSec = 18000 };
                    s.Weekly = new UsageWindow { Percent = 18, ResetUtc = now.AddHours(98), LengthSec = 604800 };
                    s.Extras.Add(new NamedWindow { Name = "Opus 주간", Window = new UsageWindow { Percent = 7, ResetUtc = now.AddHours(98) } });
                }
                else
                {
                    s.Session = new UsageWindow { Percent = 100, ResetUtc = now.AddMinutes(37), LengthSec = 18000 };
                    s.Weekly = new UsageWindow { Percent = 83, ResetUtc = now.AddHours(100), LengthSec = 604800 };
                }
            }
            else
            {
                s.Plan = "plus";
                s.Session = new UsageWindow { Percent = 77 + step, ResetUtc = now.AddMinutes(185), LengthSec = 18000 };
                s.Weekly = new UsageWindow { Percent = 61, ResetUtc = now.AddHours(53), LengthSec = 604800 };
            }
            return s;
        }
    }
}
