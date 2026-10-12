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

    enum UpdateBlock { None, GitClone, ReadOnly }

    sealed class UpdateCheck
    {
        public ReleaseInfo Latest;       // 조회에 실패하면 null
        public bool Newer;
        public UpdateBlock Block;
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

        // 이 위치에서 스스로 교체해도 되는지 봅니다. git 작업 사본 안이면 git이 관리하므로 건드리지 않습니다.
        public static UpdateBlock Block()
        {
            string dir = Path.GetDirectoryName(ExePath);
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                string git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return UpdateBlock.GitClone;
            }
            try
            {
                string probe = Path.Combine(dir, ".ongi-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch { return UpdateBlock.ReadOnly; }
            return UpdateBlock.None;
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
}
