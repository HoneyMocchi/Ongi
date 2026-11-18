using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ongi
{
    // Claude Code의 OAuth 토큰을 읽고, 만료되면 Claude Code CLI가 직접 갱신하게 합니다.
    // Ongi는 토큰 파일을 쓰지 않습니다. 갱신·저장은 CLI가 잠금을 잡고 하므로, 실행 중인 Claude Code와 겹쳐도 로그아웃되지 않습니다.
    static class ClaudeCli
    {
        static readonly object Gate = new object();
        static DateTime lastTryUtc = DateTime.MinValue;
        static string lastError;
        const int RetryMinutes = 15;

        public static string CredentialsPath
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Paths.Home, ".claude");
                return Path.Combine(dir, ".credentials.json");
            }
        }

        public static object ReadOAuth(string path) { return J.Get(J.Parse(Util.ReadShared(path)), "claudeAiOauth"); }
        public static string Token(object oauth) { return J.Str(J.Get(oauth, "accessToken")); }

        // 만료 시각을 모르면 유효한 것으로 봅니다. 곧 만료될 토큰도 미리 갱신하도록 여유를 둡니다.
        public static bool Fresh(object oauth, int marginSec = 60)
        {
            var exp = J.Num(J.Get(oauth, "expiresAt"));
            return !exp.HasValue || exp.Value <= 0 || Util.FromUnix(exp.Value) > DateTime.UtcNow.AddSeconds(marginSec);
        }

        // PATH → 네이티브 설치(~/.local/bin) → npm → VS Code·Cursor·Windsurf 확장에 든 claude.exe 순서로 찾습니다.
        public static string Find()
        {
            var list = new List<string>();
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string t = d.Trim().Trim('"');
                    if (t.Length > 0) { list.Add(Path.Combine(t, "claude.exe")); list.Add(Path.Combine(t, "claude.cmd")); }
                }
                catch { }
            }
            list.Add(Path.Combine(Paths.Home, @".local\bin\claude.exe"));
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"npm\claude.cmd"));
            string hit = list.FirstOrDefault(File.Exists);
            if (hit != null) return hit;

            // 확장은 버전마다 폴더가 생기므로 가장 최근에 바뀐 것을 씁니다.
            return new[] { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" }
                .Select(n => Path.Combine(Paths.Home, n, "extensions"))
                .Where(Directory.Exists)
                .SelectMany(d => { try { return Directory.GetDirectories(d, "anthropic.claude-code-*"); } catch { return new string[0]; } })
                .Select(d => Path.Combine(d, @"resources\native-binary\claude.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        // 갱신된 토큰을 돌려줍니다. rejected는 서버가 거절한 토큰으로, 만료 시각 전이어도 다른 토큰이 나올 때까지 갱신합니다.
        // 1) auth status: 사용량을 쓰지 않습니다.
        // 2) 그래도 그대로면 Haiku로 아주 짧게 한 번 묻습니다. CLI는 요청 전에 토큰을 갱신하고, 대화 기록은 남기지 않습니다.
        public static object Refresh(string credPath, string rejected = null) { return Refresh(credPath, rejected, Find()); }

        internal static object Refresh(string credPath, string rejected, string exe)
        {
            lock (Gate)
            {
                Func<object> current = () =>
                {
                    try
                    {
                        var o = ReadOAuth(credPath);
                        string t = Token(o);
                        return !string.IsNullOrEmpty(t) && t != rejected && Fresh(o) ? o : null;
                    }
                    catch { return null; }   // CLI가 쓰는 중이면 다음 단계에서 다시 읽습니다.
                };

                // 다른 스레드나 Claude Code가 이미 갱신했으면 그대로 씁니다.
                var now = current();
                if (now != null) return now;
                if (exe == null) throw new FetchException("토큰 만료 — Claude Code를 한 번 실행하면 갱신됩니다", 300);
                if (lastError != null && DateTime.UtcNow - lastTryUtc < TimeSpan.FromMinutes(RetryMinutes)) throw new FetchException(lastError, 300);

                lastTryUtc = DateTime.UtcNow;
                var steps = new[]
                {
                    new { Name = "auth status", Args = "auth status --json", Timeout = 30000 },
                    new { Name = "짧은 요청", Args = "-p --model haiku --no-session-persistence --tools \"\" --strict-mcp-config --disable-slash-commands --setting-sources local", Timeout = 90000 },
                };
                foreach (var s in steps)
                {
                    string result = Run(exe, s.Args, s.Timeout);
                    now = current();
                    if (now != null)
                    {
                        Log.Write("Claude 토큰 갱신: " + s.Name);
                        lastError = null;
                        return now;
                    }
                    Log.Write("Claude 토큰 갱신 안 됨: " + s.Name + " (" + result + ")");
                }
                lastError = "토큰 갱신 실패 — Claude Code에서 다시 로그인하세요";
                throw new FetchException(lastError, 300);
            }
        }

        // 창 없이 실행하고 결과 요약만 돌려줍니다. 출력에는 계정 정보가 있으므로 기록하지 않습니다.
        static string Run(string exe, string args, int timeoutMs)
        {
            bool script = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe,
                                           script ? "/d /s /c \"\"" + exe + "\" " + args + "\"" : args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = Paths.Dir,
            };
            // Claude Code 안에서 Ongi를 띄운 경우에도 독립된 실행으로 보이게 합니다.
            psi.EnvironmentVariables.Remove("CLAUDECODE");
            psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENTRYPOINT");
            try
            {
                Directory.CreateDirectory(Paths.Dir);
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Write("1");
                    p.StandardInput.Close();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "시간 초과"; }
                    return "종료 코드 " + p.ExitCode;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
