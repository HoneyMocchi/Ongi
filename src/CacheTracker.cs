using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ongi
{
    // 채팅 하나의 프롬프트 캐시 상태입니다.
    // 서버는 캐시 상태를 알려 주지 않으므로 "마지막 요청 시각 + TTL"로 만료 시각을 추정합니다.
    // 캐시는 쓰일 때마다 TTL이 다시 시작되므로, 마지막 요청 시각이 곧 TTL의 기준점입니다.
    sealed class ChatCache
    {
        public string Key, Id, Title;
        public string AiTitle;   // 기록에서 찾은 제목(없으면 null). 다음 스캔에서 3MB까지 거슬러 올라가지 않으려고 둡니다.
        public DateTime LastUtc;
        public int TtlSec;
        public long CachedTokens;

        public DateTime ExpiresUtc { get { return LastUtc.AddSeconds(TtlSec); } }
        public TimeSpan Remaining(DateTime now) { return ExpiresUtc - now; }
        public bool Hot(DateTime now) { return ExpiresUtc > now; }
        public double Fraction(DateTime now) { return TtlSec > 0 ? Math.Max(0, Math.Min(1, (ExpiresUtc - now).TotalSeconds / TtlSec)) : 0; }

        public static List<ChatCache> HotList(IEnumerable<ChatCache> list, DateTime now)
        {
            return list.Where(c => c.Hot(now)).OrderByDescending(c => c.LastUtc).ToList();
        }

        public static List<ChatCache> ColdList(IEnumerable<ChatCache> list, DateTime now)
        {
            return list.Where(c => !c.Hot(now)).OrderByDescending(c => c.LastUtc).ToList();
        }
    }

    static class CacheScanner
    {
        const int RecentSec = 65 * 60;          // 이보다 오래 수정되지 않은 대화 기록은 보지 않습니다.
        const int TailBytes = 3 * 1024 * 1024;  // 기록 파일 끝에서 읽을 최대 크기
        const int QuickTailBytes = 256 * 1024;  // 먼저 읽어 보는 크기. 필요한 정보가 그 안에 없을 때만 TailBytes로 넓힙니다.
        static readonly Dictionary<string, Tuple<DateTime, long, ChatCache>> Memo = new Dictionary<string, Tuple<DateTime, long, ChatCache>>();
        static readonly Regex UuidTail = new Regex(@"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.jsonl$", RegexOptions.IgnoreCase);
        // 기록 항목의 최상위 키는 ...,"uuid":"…","timestamp":"…",toolUseResult 순서입니다.
        // 뒤따르는 toolUseResult 안에도 timestamp 키가 있을 수 있어 uuid 바로 뒤의 것만 잡습니다.
        static readonly Regex TopTimestamp = new Regex("\"uuid\":\"[^\"]+\",\"timestamp\":\"([^\"]+)\"");
        static Dictionary<string, string> codexTitles = new Dictionary<string, string>();
        static DateTime codexTitlesStamp;

        // Claude Code: ~/.claude/projects/<프로젝트>/<세션ID>.jsonl 한 파일이 채팅 하나입니다.
        public static List<ChatCache> Claude(DateTime now)
        {
            var result = new List<ChatCache>();
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Paths.Home, ".claude");
            var root = new DirectoryInfo(Path.Combine(dir, "projects"));
            if (!root.Exists) return result;
            // EnumerateFiles는 목록을 받을 때 수정 시각·크기도 함께 받아 파일마다 다시 조회하지 않습니다.
            foreach (var project in root.EnumerateDirectories())
                foreach (var fi in project.EnumerateFiles("*.jsonl"))
                {
                    var c = Memoized(fi.FullName, fi, now, prev => ReadClaude(fi.FullName, Path.GetFileNameWithoutExtension(fi.Name), prev));
                    if (c != null) result.Add(c);
                }
            return result;
        }

        // 끝 256KB만 먼저 봅니다. 마지막 요청의 시작(그 앞 요청과의 경계)이나 제목을 그 안에서 못 찾았을 때만 3MB로 넓힙니다.
        // 제목은 지난번에 찾아 둔 것이 있으면 그것으로 대신합니다.
        static ChatCache ReadClaude(string path, string id, ChatCache prev)
        {
            string known = prev != null ? prev.AiTitle : null;
            bool cut, complete;
            var c = ParseClaude(ReadTailLines(path, QuickTailBytes, out cut), id, known, out complete);
            if (cut && !complete) c = ParseClaude(ReadTailLines(path, TailBytes, out cut), id, known, out complete);
            return c;
        }

        // Codex: ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl 한 파일이 채팅(스레드) 하나입니다.
        public static List<ChatCache> Codex(DateTime now, int ttlOverrideMin)
        {
            var result = new List<ChatCache>();
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Paths.Home, ".codex");
            string root = Path.Combine(home, "sessions");
            if (!Directory.Exists(root)) return result;
            LoadCodexTitles(Path.Combine(home, "session_index.jsonl"));

            var days = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in new[] { DateTime.Now, DateTime.Now.AddDays(-1), DateTime.UtcNow, DateTime.UtcNow.AddDays(-1) })
                days.Add(Path.Combine(root, t.ToString("yyyy"), t.ToString("MM"), t.ToString("dd")));
            foreach (var day in days)
            {
                if (!Directory.Exists(day)) continue;
                foreach (var fi in new DirectoryInfo(day).EnumerateFiles("rollout-*.jsonl"))
                {
                    var m = UuidTail.Match(fi.Name);
                    string id = m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fi.Name);
                    string title;
                    codexTitles.TryGetValue(id, out title);
                    var c = Memoized(fi.FullName + "|" + ttlOverrideMin, fi, now, prev => ReadCodex(fi.FullName, id, title, ttlOverrideMin));
                    if (c != null) result.Add(c);
                }
            }
            return result;
        }

        static ChatCache ReadCodex(string path, string id, string title, int ttlOverrideMin)
        {
            bool cut, complete;
            var c = ParseCodex(ReadTailLines(path, QuickTailBytes, out cut), id, title, ttlOverrideMin, out complete);
            if (cut && !complete) c = ParseCodex(ReadTailLines(path, TailBytes, out cut), id, title, ttlOverrideMin, out complete);
            return c;
        }

        // parse에는 바뀌기 전 결과(없으면 null)를 넘깁니다.
        static ChatCache Memoized(string key, FileInfo fi, DateTime now, Func<ChatCache, ChatCache> parse)
        {
            Tuple<DateTime, long, ChatCache> hit;
            bool known = Memo.TryGetValue(key, out hit);
            if ((now - fi.LastWriteTimeUtc).TotalSeconds > RecentSec) { if (known) Memo.Remove(key); return null; }
            if (known && hit.Item1 == fi.LastWriteTimeUtc && hit.Item2 == fi.Length) return hit.Item3;
            ChatCache c = null;
            try { c = parse(known ? hit.Item3 : null); } catch (Exception ex) { Log.Write("캐시 기록 읽기 실패: " + fi.Name + " " + ex.Message); }
            Memo[key] = Tuple.Create(fi.LastWriteTimeUtc, fi.Length, c);
            return c;
        }

        // 파일 끝에서 maxBytes만큼 읽어 줄로 나눕니다. cut은 파일 앞부분을 건너뛰었는지입니다.
        static string[] ReadTailLines(string path, int maxBytes, out bool cut)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - maxBytes);
                cut = start > 0;
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int n = 0, k;
                while (n < buf.Length && (k = fs.Read(buf, n, buf.Length - n)) > 0) n += k;
                var lines = Encoding.UTF8.GetString(buf, 0, n).Split('\n');
                // 중간부터 읽었으면 첫 줄은 잘렸으므로 버립니다.
                return start > 0 && lines.Length > 0 ? lines.Skip(1).ToArray() : lines;
            }
        }

        static object TryParse(string line)
        {
            try { return J.Parse(line); } catch { return null; }
        }

        // 마지막 API 요청(requestId)의 가장 이른 기록 시각과, 그 요청이 쓴 캐시의 TTL(1시간/5분)을 찾습니다.
        internal static ChatCache ParseClaude(string[] lines, string id)
        {
            bool complete;
            return ParseClaude(lines, id, null, out complete);
        }

        // knownTitle: 지난 스캔에서 찾은 제목. 있으면 제목을 찾으러 더 거슬러 올라가지 않습니다.
        // complete: 마지막 요청의 시작 경계와 제목을 모두 확인했는지(아니면 더 넓게 읽어야 함)
        static ChatCache ParseClaude(string[] lines, string id, string knownTitle, out bool complete)
        {
            string title = null, lastReq = null, cwd = null;
            DateTime? first = null;
            int ttl = 0;
            long cached = 0;
            bool usageDone = false;
            var userTimes = new List<DateTime>();
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (title == null && line.IndexOf("\"ai-title\"", StringComparison.Ordinal) >= 0)
                {
                    title = J.Str(J.Get(TryParse(line), "aiTitle"));
                    if (usageDone && title != null) break;
                    continue;
                }
                // 마지막 요청 직전의 사용자 입력·도구 결과 시각을 모아 둡니다(요청을 보낸 시각에 더 가깝습니다).
                if (!usageDone && lastReq != null && line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0
                    && line.IndexOf("\"isSidechain\":false", StringComparison.Ordinal) >= 0)
                {
                    var m = TopTimestamp.Match(line);
                    var t = m.Success ? Util.ParseIso(m.Groups[1].Value) : null;
                    if (t.HasValue) userTimes.Add(t.Value);
                    continue;
                }
                if (usageDone || line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 || line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;

                var o = TryParse(line);
                if (J.Str(J.Get(o, "type")) != "assistant" || J.Bool(J.Get(o, "isSidechain")) == true) continue;
                var msg = J.Get(o, "message");
                var usage = J.Get(msg, "usage");
                if (usage == null || J.Str(J.Get(msg, "model")) == "<synthetic>") continue;
                var ts = Util.ParseIso(J.Str(J.Get(o, "timestamp")));
                if (!ts.HasValue) continue;
                string rid = J.Str(J.Get(o, "requestId")) ?? J.Str(J.Get(msg, "id")) ?? ts.Value.ToString("o");

                if (lastReq == null)
                {
                    lastReq = rid;
                    cwd = J.Str(J.Get(o, "cwd"));
                    cached = (long)((J.Num(J.Get(usage, "cache_read_input_tokens")) ?? 0) + (J.Num(J.Get(usage, "cache_creation_input_tokens")) ?? 0));
                }
                if (rid == lastReq && (!first.HasValue || ts.Value < first.Value)) first = ts;
                if (ttl == 0)
                {
                    double h1 = J.Num(J.Get(usage, "cache_creation", "ephemeral_1h_input_tokens")) ?? 0;
                    double m5 = J.Num(J.Get(usage, "cache_creation", "ephemeral_5m_input_tokens")) ?? 0;
                    if (h1 > 0) ttl = 3600;
                    else if (m5 > 0) ttl = 300;
                }
                // 더 이전 요청까지 내려왔고 TTL도 알아냈으면 사용량 탐색은 끝냅니다.
                if (rid != lastReq && ttl != 0)
                {
                    usageDone = true;
                    if (title != null || knownTitle != null) break;
                }
            }
            title = title ?? knownTitle;
            complete = usageDone && title != null;
            if (!first.HasValue) return null;
            // 응답 기록은 블록이 끝날 때 남으므로, 생각이 긴 요청은 실제 요청 시각보다 늦게 찍힙니다.
            // 바로 앞 사용자 입력(또는 도구 결과) 시각이 15분 안이면 그 시각을 요청 시각으로 씁니다.
            DateTime start = first.Value;
            foreach (var t in userTimes)
                if (t <= first.Value && (first.Value - t).TotalMinutes < 15 && (start == first.Value || t > start)) start = t;
            return new ChatCache
            {
                Key = "claude", Id = id, LastUtc = start, TtlSec = ttl == 0 ? 300 : ttl, CachedTokens = cached, AiTitle = title,
                Title = title ?? (string.IsNullOrEmpty(cwd) ? "제목 없음" : Path.GetFileName(cwd.TrimEnd('\\', '/'))),
            };
        }

        internal static ChatCache ParseCodex(string[] lines, string id, string title, int ttlOverrideMin)
        {
            bool complete;
            return ParseCodex(lines, id, title, ttlOverrideMin, out complete);
        }

        // complete: 마지막 token_count와 모델(turn_context)을 모두 찾았는지
        static ChatCache ParseCodex(string[] lines, string id, string title, int ttlOverrideMin, out bool complete)
        {
            DateTime? last = null;
            long cached = 0;
            string model = null, cwd = null;
            for (int i = lines.Length - 1; i >= 0 && (!last.HasValue || model == null); i--)
            {
                string line = lines[i];
                if (!last.HasValue && line.IndexOf("\"token_count\"", StringComparison.Ordinal) >= 0)
                {
                    var o = TryParse(line);
                    var info = J.Get(o, "payload", "info");
                    var ts = Util.ParseIso(J.Str(J.Get(o, "timestamp")));
                    if (info == null || !ts.HasValue) continue;
                    last = ts;
                    cached = (long)(J.Num(J.Get(info, "last_token_usage", "cached_input_tokens")) ?? 0);
                }
                else if (model == null && line.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0)
                {
                    var o = TryParse(line);
                    model = J.Str(J.Get(o, "payload", "model"));
                    cwd = J.Str(J.Get(o, "payload", "cwd"));
                }
            }
            complete = last.HasValue && model != null;
            if (!last.HasValue) return null;
            return new ChatCache
            {
                Key = "codex", Id = id, LastUtc = last.Value, CachedTokens = cached,
                TtlSec = ttlOverrideMin > 0 ? ttlOverrideMin * 60 : CodexTtl(model),
                Title = title ?? (string.IsNullOrEmpty(cwd) ? "제목 없음" : Path.GetFileName(cwd.TrimEnd('\\', '/'))),
            };
        }

        // OpenAI 문서 기준: GPT-5.6 이후 모델은 캐시 최소 유지 30분, 그 이전 모델의 메모리 캐시는 유휴 5~10분입니다.
        internal static int CodexTtl(string model)
        {
            var m = Regex.Match(model ?? "", @"gpt-(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int major = int.Parse(m.Groups[1].Value), minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                if (major > 5 || (major == 5 && minor >= 6)) return 1800;
            }
            return 300;
        }

        static void LoadCodexTitles(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == codexTitlesStamp) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in Util.ReadShared(path).Split('\n'))
                {
                    var o = TryParse(line);
                    string id = J.Str(J.Get(o, "id")), name = J.Str(J.Get(o, "thread_name"));
                    if (id != null && !string.IsNullOrEmpty(name)) map[id] = name;
                }
                codexTitles = map;
                codexTitlesStamp = stamp;
            }
            catch (Exception ex) { Log.Write("Codex 채팅 제목 읽기 실패: " + ex.Message); }
        }
    }
}
