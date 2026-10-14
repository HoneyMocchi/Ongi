using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Ongi
{
    sealed class ReleaseInfo
    {
        public Version Version;
        public string VersionText, ZipName, ZipUrl, ShaUrl;
    }

    // Release: 릴리스 zip을 받아 교체, Git: git 작업 사본이라 git으로 받아 교체, ReadOnly: 이 폴더에 쓸 수 없음
    enum UpdateRoute { Release, Git, ReadOnly }

    sealed class UpdateCheck
    {
        public ReleaseInfo Latest;       // 조회에 실패하면 null
        public bool Newer;
        public UpdateRoute Route;
    }

    // 설정 창이 업데이트 상태를 묻고 업데이트를 시작할 때 쓰는 통로입니다(App이 구현).
    interface IUpdateHost
    {
        string CurrentVersion { get; }
        void CheckUpdate(Action<UpdateCheck> done);
        void StartUpdate(ReleaseInfo release, Action<int> progress, Action<string> failed);
    }

    // GitHub 릴리스에서 새 버전을 받아 지금 exe를 교체합니다.
    //   최신 릴리스 조회 → zip·.sha256 받기 → 해시·exe 버전 검증 → 실행 중인 exe 이름을 .old로 바꾸고 새 exe 넣기
    //   → 새 exe 실행 → 새 exe가 "정상 시작" 신호를 보내면 끝, 아니면 되돌리고 원래 버전으로 계속 실행
    // git 작업 사본이면 zip 대신 git fetch → exe를 .old로 비키고 merge --ff-only로 받습니다. 되돌릴 때는 reset --keep으로 이전 커밋에 맞춥니다.
    static class Updater
    {
        public const string Repo = "HoneyMocchi/Ongi";
        public const string ReadyEventName = @"Local\Ongi.UpdateReady";

        public static string Folder { get { return Path.Combine(Paths.Dir, "update"); } }
        static string ExePath { get { return Application.ExecutablePath; } }
        public static string OldExePath { get { return ExePath + ".old"; } }

        public static Version Current { get { return ParseVersion(Application.ProductVersion) ?? new Version(0, 0, 0); } }

        // "v1.2.0", "1.2" → 1.2.0 (세 자리로 맞춰 비교합니다)
        public static Version ParseVersion(string s)
        {
            var m = Regex.Match(s ?? "", @"^\s*[vV]?(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        public static string Format(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        // 정식 릴리스 중 최신 것(초안·사전 릴리스는 GitHub가 latest에서 뺍니다). 첨부 파일 규칙: Ongi-x.y.z.zip, Ongi-x.y.z.zip.sha256
        public static ReleaseInfo Latest()
        {
            var r = J.Parse(Http.Get("https://api.github.com/repos/" + Repo + "/releases/latest", new Dictionary<string, string>(), "GitHub"));
            var v = ParseVersion(J.Str(J.Get(r, "tag_name")));
            if (v == null) return null;
            var info = new ReleaseInfo { Version = v, VersionText = Format(v) };
            info.ZipName = "Ongi-" + info.VersionText + ".zip";
            foreach (var a in (J.Get(r, "assets") as object[]) ?? new object[0])
            {
                string name = J.Str(J.Get(a, "name")), url = J.Str(J.Get(a, "browser_download_url"));
                if (string.Equals(name, info.ZipName, StringComparison.OrdinalIgnoreCase)) info.ZipUrl = url;
                else if (string.Equals(name, info.ZipName + ".sha256", StringComparison.OrdinalIgnoreCase)) info.ShaUrl = url;
            }
            return info;
        }

        // 이 위치에서 어떻게 교체할지 봅니다. exe가 git 리포에 커밋된 파일이면 git으로 받습니다.
        // 다른 리포 안에 zip을 풀었거나 git을 찾지 못하면 릴리스 zip으로 받습니다(zip으로 바꾼 exe도 다음 git 업데이트가 그대로 덮어씁니다).
        public static UpdateRoute Route()
        {
            string dir = Path.GetDirectoryName(ExePath);
            try
            {
                string probe = Path.Combine(dir, ".ongi-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch { return UpdateRoute.ReadOnly; }
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                string git = Path.Combine(d.FullName, ".git");
                if (!Directory.Exists(git) && !File.Exists(git)) continue;
                string unused;
                return Git.Exe != null && Git.Run("ls-files --error-unmatch -- \"" + Path.GetFileName(ExePath) + "\"", out unused) == 0 ? UpdateRoute.Git : UpdateRoute.Release;
            }
            return UpdateRoute.Release;
        }

        // git 작업 사본: 원격을 받아 온 뒤 지금 exe를 .old로 비키고 fast-forward로 새 커밋을 꺼냅니다. 받기 전 커밋을 돌려줍니다(되돌리기용).
        // 하나라도 어긋나면 원래대로 되돌리고 예외를 던집니다.
        public static string GitUpdate(Action<int> progress)
        {
            string head = GitOrThrow("rev-parse HEAD", "지금 커밋을 읽지 못했습니다").Trim();
            GitOrThrow("rev-parse --abbrev-ref --symbolic-full-name @{u}", "추적하는 원격 브랜치가 없습니다");
            progress(10);
            GitOrThrow("fetch --quiet", "git fetch 실패", 120000);
            progress(70);
            // 실행 중인 exe는 git이 덮어쓸 수 없지만 이름은 바꿀 수 있습니다. 비워 둔 자리에 merge가 새 exe를 씁니다.
            if (File.Exists(OldExePath)) File.Delete(OldExePath);
            File.Move(ExePath, OldExePath);
            try
            {
                GitOrThrow("merge --ff-only @{u}", "git merge 실패");
                // 같은 경로를 바로 읽으면 Windows가 이미 올라와 있는 지금 exe의 버전 정보를 돌려주므로 복사본으로 읽습니다.
                Version got = null;
                if (File.Exists(ExePath))
                {
                    Directory.CreateDirectory(Folder);
                    string copy = Path.Combine(Folder, "git-check.exe");
                    File.Copy(ExePath, copy, true);
                    got = ParseVersion(FileVersionInfo.GetVersionInfo(copy).ProductVersion);
                }
                if (got == null || got <= Current) throw new InvalidOperationException("받은 커밋의 exe가 새 버전이 아닙니다");
            }
            catch { GitRollback(head); throw; }
            progress(100);
            return head;
        }

        // 새 exe를 지우고 이전 커밋으로 돌린 뒤 .old를 원래 이름으로 되돌립니다. merge 전에 실패했으면 reset은 아무것도 바꾸지 않습니다.
        public static void GitRollback(string head)
        {
            try { if (File.Exists(ExePath) && File.Exists(OldExePath)) File.Delete(ExePath); } catch { }
            string output;
            if (Git.Run("reset --quiet --keep " + head, out output) != 0) Log.Write("git 되돌리기 실패: " + output.Trim());
            Restore();
        }

        static string GitOrThrow(string args, string what, int timeoutMs = 30000)
        {
            string output;
            if (Git.Run(args, out output, timeoutMs) != 0)
            {
                var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
                throw new InvalidOperationException(what + (last != null ? ": " + last : ""));
            }
            return output;
        }

        // 받아서 검증까지 마친 새 exe의 경로를 돌려줍니다. 하나라도 어긋나면 예외를 던지고 아무것도 바꾸지 않습니다.
        public static string Download(ReleaseInfo r, Action<int> progress)
        {
            if (r.ZipUrl == null) throw new InvalidOperationException("릴리스에 " + r.ZipName + "이 없습니다");
            if (r.ShaUrl == null) throw new InvalidOperationException("릴리스에 해시 파일(.sha256)이 없습니다");
            Directory.CreateDirectory(Folder);

            string expected = Regex.Match(DownloadText(r.ShaUrl), "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
            if (expected.Length != 64) throw new InvalidOperationException("해시 파일 형식이 맞지 않습니다");

            string zip = Path.Combine(Folder, r.ZipName);
            DownloadFile(r.ZipUrl, zip, progress);
            if (Sha256(zip) != expected) throw new InvalidOperationException("받은 파일의 해시가 맞지 않습니다");

            string outDir = Path.Combine(Folder, r.VersionText);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            string exe = Path.Combine(outDir, "Ongi.exe");
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "Ongi.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException("압축 파일에 Ongi.exe가 없습니다");
                entry.ExtractToFile(exe, true);
            }
            var got = ParseVersion(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
            if (got == null || got != r.Version) throw new InvalidOperationException("받은 exe의 버전이 릴리스와 다릅니다");
            return exe;
        }

        static WebResponse Open(string url)
        {
            var req = WebRequest.Create(url);
            var http = req as HttpWebRequest;
            if (http != null)
            {
                http.UserAgent = "Ongi/" + Format(Current) + " (Windows)";
                http.Timeout = 30000;
                http.ReadWriteTimeout = 30000;
            }
            return req.GetResponse();
        }

        static string DownloadText(string url)
        {
            using (var resp = Open(url))
            using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }

        static void DownloadFile(string url, string path, Action<int> progress)
        {
            using (var resp = Open(url))
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(path))
            {
                long total = resp.ContentLength, done = 0;
                var buf = new byte[81920];
                int n, last = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != last && progress != null) { last = pct; progress(pct); }
                }
            }
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        // 실행 중인 exe는 지울 수 없지만 이름은 바꿀 수 있습니다. 지금 exe를 .old로 비키고 새 exe를 그 자리에 둡니다.
        public static void Swap(string newExe)
        {
            if (File.Exists(OldExePath)) File.Delete(OldExePath);
            File.Move(ExePath, OldExePath);
            try { File.Copy(newExe, ExePath, true); }
            catch { Restore(); throw; }
        }

        // 새 exe를 지우고 .old를 원래 이름으로 되돌립니다.
        public static void Restore()
        {
            try
            {
                if (!File.Exists(OldExePath)) return;
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(OldExePath, ExePath);
            }
            catch (Exception ex) { Log.Write("업데이트 되돌리기 실패: " + ex.Message); }
        }

        // 새 버전이 정상으로 뜬 뒤, 이전 프로세스가 끝나기를 기다렸다가 .old와 받은 파일을 지웁니다.
        public static void CleanupAfter(int oldPid)
        {
            try { Process.GetProcessById(oldPid).WaitForExit(30000); } catch { }
            try { if (File.Exists(OldExePath)) File.Delete(OldExePath); } catch (Exception ex) { Log.Write("이전 exe 정리 실패: " + ex.Message); }
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
        }
    }

    // git 작업 사본 업데이트용 git 호출입니다. exe 폴더에서 실행하고, 자격 증명·편집기 입력을 기다리지 않게 프롬프트를 끕니다.
    static class Git
    {
        static string exe;
        static bool looked;

        // PATH → Git for Windows 기본 위치 → GitHub Desktop 내장 git 순서로 찾습니다.
        public static string Exe
        {
            get
            {
                if (looked) return exe;
                var list = new List<string>();
                foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    try { if (d.Trim().Length > 0) list.Add(Path.Combine(d.Trim().Trim('"'), "git.exe")); } catch { }
                }
                foreach (var v in new[] { "ProgramW6432", "ProgramFiles" })
                {
                    string root = Environment.GetEnvironmentVariable(v);
                    if (!string.IsNullOrEmpty(root)) list.Add(Path.Combine(root, @"Git\cmd\git.exe"));
                }
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                list.Add(Path.Combine(local, @"Programs\Git\cmd\git.exe"));
                try
                {
                    string desktop = Path.Combine(local, "GitHubDesktop");
                    if (Directory.Exists(desktop))
                        list.AddRange(Directory.GetDirectories(desktop, "app-*").OrderByDescending(d => d).Select(d => Path.Combine(d, @"resources\app\git\cmd\git.exe")));
                }
                catch { }
                exe = list.FirstOrDefault(File.Exists);
                looked = true;
                return exe;
            }
        }

        // 종료 코드를 돌려줍니다. output에는 표준 출력과 오류 출력을 합쳐 담습니다.
        public static int Run(string args, out string output, int timeoutMs = 30000)
        {
            output = "";
            if (Exe == null) { output = "git을 찾지 못했습니다"; return -1; }
            var psi = new ProcessStartInfo(Exe, "-c core.quotepath=false " + args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            psi.EnvironmentVariables["GIT_EDITOR"] = "true";
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "시간 초과"; return -1; }
                    output = outTask.Result + errTask.Result;
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }
    }
}
