using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexUsageMonitor
{
    public static class Theme
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#17181d");
        public static readonly Color Card = ColorTranslator.FromHtml("#202128");
        public static readonly Color CardHover = ColorTranslator.FromHtml("#2a2c36");
        public static readonly Color Border = ColorTranslator.FromHtml("#2e3039");
        public static readonly Color Text = ColorTranslator.FromHtml("#eceef3");
        public static readonly Color Sub = ColorTranslator.FromHtml("#8e9099");
        public static readonly Color Muted = ColorTranslator.FromHtml("#6b6d76");
        public static readonly Color Accent = ColorTranslator.FromHtml("#7cb8ff");
        public static readonly Color Green = ColorTranslator.FromHtml("#3ddc84");
        public static readonly Color Yellow = ColorTranslator.FromHtml("#f5c542");
        public static readonly Color Red = ColorTranslator.FromHtml("#ff5b5b");
        public static readonly Color Track = ColorTranslator.FromHtml("#3a3b44");

        public static Color ColorFor(double? remainingPct)
        {
            if (!remainingPct.HasValue) return Sub;
            if (remainingPct.Value >= 50.0) return Green;
            if (remainingPct.Value >= 20.0) return Yellow;
            return Red;
        }
    }

    public enum WindowFontSize
    {
        Small,
        Medium,
        Large
    }

    public class AppConfig
    {
        public double alpha = 0.80;
        public WindowFontSize font_size = WindowFontSize.Small;
        public int interval = 60;
        public int? window_x = null;
        public int? window_y = null;
        public string display_uid = "";
        public string view_mode = "single"; // single | all
        public bool notify_low = true;
        public int notify_threshold = 20;
        public bool show_in_taskbar = true;
        public int taskbar_offset_x = 0;
    }

    public class AccountInfo
    {
        public string Nick;
        public string Email;
        public string Uid;
        public string Home;
        public bool Live;
        public string Label;
        public bool AuthBad;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Label)) return Label;
                if (!string.IsNullOrEmpty(Email)) return Email;
                if (!string.IsNullOrEmpty(Nick)) return Nick;
                return "账号";
            }
        }
    }

    public class ResetCredit
    {
        public string Title;
        public double? ExpiresAt;
    }

    public class UsageData
    {
        public string Home;
        public string Plan;
        public string Nick;
        public double? SubUntil;
        public double? SubStart;
        public double? PUsed;
        public double? PResetAt;
        public double? PLeft;
        public double? SUsed;
        public double? SResetAt;
        public double? SLeft;
        public int? Credits;
        public int? ApplicableCredits;
        public List<ResetCredit> ResetCredits;
        public double? CreditsExpireAt;
        public string CreditsBalance;
        public bool CreditsUnlimited;
        public bool LimitReached;
        public double Ts;

        public double? PRemaining { get { return PUsed.HasValue ? (double?)(100.0 - PUsed.Value) : null; } }
        public double? SRemaining { get { return SUsed.HasValue ? (double?)(100.0 - SUsed.Value) : null; } }
    }

    public class FetchResult
    {
        public UsageData Data;
        public string Error;
        public string ErrorKind; // null | "auth" | "net"
    }

    public class AuthException : Exception
    {
        public AuthException(string msg) : base(msg) { }
    }

    public static class AppPaths
    {
        public static readonly string DataDir;
        public static readonly string ConfigPath;
        public static readonly string LogPath;
        public static readonly string AccountsDir;

        static AppPaths()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            // 若在项目源码目录下运行(同级有 codex_tray.py 或 accounts), 复用同级目录;
            // 若单独放到桌面等位置, 统一存到 %LOCALAPPDATA%\CodexUsageMonitor 保持桌面整洁
            if (File.Exists(Path.Combine(exeDir, "codex_tray.py")) || Directory.Exists(Path.Combine(exeDir, "accounts")))
            {
                DataDir = exeDir;
            }
            else
            {
                DataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CodexUsageMonitor");
                Directory.CreateDirectory(DataDir);
            }
            ConfigPath = Path.Combine(DataDir, "config.json");
            LogPath = Path.Combine(DataDir, "codex_tray.log");
            AccountsDir = Path.Combine(DataDir, "accounts");
        }
    }

    public static class Logger
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object LockObj = new object();

        public static void Log(string msg)
        {
            try
            {
                lock (LockObj)
                {
                    if (File.Exists(AppPaths.LogPath))
                    {
                        FileInfo fi = new FileInfo(AppPaths.LogPath);
                        if (fi.Length >= MaxBytes)
                        {
                            string bak = AppPaths.LogPath + ".1";
                            try
                            {
                                if (File.Exists(bak)) File.Delete(bak);
                                File.Move(AppPaths.LogPath, bak);
                            }
                            catch { }
                        }
                    }
                    string line = string.Format("[{0}] {1}\r\n", DateTime.Now.ToString("MM-dd HH:mm:ss"), msg);
                    File.AppendAllText(AppPaths.LogPath, line, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    public static class JsonUtil
    {
        private static JavaScriptSerializer NewSer()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            return ser;
        }

        public static Dictionary<string, object> ReadDict(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string txt = File.ReadAllText(path, Encoding.UTF8);
                return NewSer().Deserialize<Dictionary<string, object>>(txt);
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, object> ParseDict(string json)
        {
            try
            {
                return NewSer().Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return null;
            }
        }

        public static string Serialize(object obj)
        {
            return NewSer().Serialize(obj);
        }

        public static void WriteAtomic(string path, object obj)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = string.Format("{0}.{1}.tmp", path, Thread.CurrentThread.ManagedThreadId);
            File.WriteAllText(tmp, Serialize(obj), new UTF8Encoding(false));
            ReplaceFile(tmp, path);
        }

        public static void CopyAtomic(string src, string dst)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = string.Format("{0}.{1}.tmp", dst, Thread.CurrentThread.ManagedThreadId);
            File.Copy(src, tmp, true);
            ReplaceFile(tmp, dst);
        }

        private static void ReplaceFile(string tmp, string dst)
        {
            if (File.Exists(dst))
            {
                try
                {
                    File.Replace(tmp, dst, null);
                    return;
                }
                catch
                {
                    File.Delete(dst);
                }
            }
            File.Move(tmp, dst);
        }

        public static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return null;
            return Convert.ToString(d[key]);
        }

        public static double? GetDouble(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return null;
            try { return Convert.ToDouble(d[key]); }
            catch { return null; }
        }

        public static int? GetInt(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return null;
            try { return Convert.ToInt32(d[key]); }
            catch { return null; }
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool defVal)
        {
            if (d == null || !d.ContainsKey(key) || d[key] == null) return defVal;
            try { return Convert.ToBoolean(d[key]); }
            catch { return defVal; }
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.ContainsKey(key)) return null;
            return d[key] as Dictionary<string, object>;
        }
    }

    public static class FormatUtil
    {
        public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double NowUnix()
        {
            return (DateTime.UtcNow - UnixEpoch).TotalSeconds;
        }

        public static DateTime FromUnixLocal(double epoch)
        {
            return UnixEpoch.AddSeconds(epoch).ToLocalTime();
        }

        public static string FmtDuration(double? sec)
        {
            if (!sec.HasValue) return "--";
            long s = Math.Max(0, (long)sec.Value);
            long d = s / 86400;
            long rem = s % 86400;
            long h = rem / 3600;
            long m = (rem % 3600) / 60;
            if (d > 0) return string.Format("{0}d{1}h{2}m", d, h, m);
            if (h > 0) return string.Format("{0}h{1}m", h, m);
            return string.Format("{0}m", m);
        }

        public static string FmtDurationCompact(double? sec)
        {
            if (!sec.HasValue) return "";
            long s = Math.Max(0, (long)sec.Value);
            long d = s / 86400;
            long rem = s % 86400;
            long h = rem / 3600;
            long m = (rem % 3600) / 60;
            if (d > 0) return string.Format("{0}d{1}h", d, h);
            if (h > 0) return string.Format("{0}h{1}m", h, m);
            return string.Format("{0}m", m);
        }

        public static string FmtReset(double? resetAt)
        {
            if (!resetAt.HasValue || resetAt.Value <= 0) return "--";
            DateTime dt = FromUnixLocal(resetAt.Value);
            DateTime now = DateTime.Now;
            string day;
            if (dt.Date == now.Date) day = "今天";
            else if (dt.Date == now.Date.AddDays(1)) day = "明天";
            else day = string.Format("{0}月{1}日", dt.Month, dt.Day);
            return string.Format("{0} {1:00}:{2:00}:{3:00}", day, dt.Hour, dt.Minute, dt.Second);
        }

        public static string FmtResetShort(double? resetAt)
        {
            if (!resetAt.HasValue || resetAt.Value <= 0) return "--";
            DateTime dt = FromUnixLocal(resetAt.Value);
            DateTime now = DateTime.Now;
            if (dt.Date == now.Date) return dt.ToString("HH:mm");
            if (dt.Date == now.Date.AddDays(1)) return "明天 " + dt.ToString("HH:mm");
            return dt.ToString("MM/dd HH:mm");
        }

        public static string Ellipsize(string text, int maxUnits)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int units = 0;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                int w = ch > 127 ? 2 : 1;
                if (units + w > maxUnits)
                {
                    sb.Append("…");
                    return sb.ToString();
                }
                sb.Append(ch);
                units += w;
            }
            return text;
        }
    }
}
