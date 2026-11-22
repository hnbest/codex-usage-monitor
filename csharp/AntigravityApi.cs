using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexUsageMonitor
{
    public class OfflineException : Exception
    {
        public OfflineException(string msg) : base(msg) { }
    }

    public class AgyCloudPermissionException : Exception
    {
        public AgyCloudPermissionException(string message) : base(message) { }
    }

    public static class AntigravityApi
    {
        public const string LocalUid = "agy:local";
        public const string LocalHome = "agy://local";

        private const string DefaultClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
        private const string OAuthAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
        private const string OAuthTokenUrl = "https://oauth2.googleapis.com/token";
        private const string OAuthUserInfoUrl = "https://www.googleapis.com/oauth2/v2/userinfo";
        private const string OAuthScopes = "https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/cclog https://www.googleapis.com/auth/experimentsandconfigs";

        private static readonly string[] CloudCodeEndpoints = new string[]
        {
            "https://cloudcode-pa.googleapis.com",
            "https://daily-cloudcode-pa.googleapis.com"
        };

        private static readonly object LocalLock = new object();
        private static int _cachedPid;
        private static int _cachedPort;
        private static string _cachedScheme;
        private static string _cachedCsrf;
        private static string _cachedLocalEmail;
        private static bool _certCallbackHooked;

        private static readonly object OAuthLock = new object();
        private static HttpListener _activeListener;
        private const string OAuthCallbackPath = "/oauth-callback";

        // ---------------- Win32 TCP 端口枚举 (按 PID 查本地监听端口，支持 IPv4/IPv6，无子进程) ----------------
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] localAddr;
            public uint localScopeId;
            public uint localPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] remoteAddr;
            public uint remoteScopeId;
            public uint remotePort;
            public uint state;
            public uint owningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort,
            int ipVersion, int tblClass, uint reserved);

        private static List<int> GetListeningPortsForPid(int pid)
        {
            List<int> ports = new List<int>();
            CollectPorts(pid, AF_INET, ports);
            CollectPorts(pid, AF_INET6, ports);
            return ports;
        }

        private static void CollectPorts(int pid, int ipVersion, List<int> ports)
        {
            int bufSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufSize, false, ipVersion, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (bufSize <= 0) return;
            IntPtr ptr = Marshal.AllocHGlobal(bufSize);
            try
            {
                uint ret = GetExtendedTcpTable(ptr, ref bufSize, false, ipVersion, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (ret != 0) return;
                int count = Marshal.ReadInt32(ptr);
                IntPtr rowPtr = new IntPtr(ptr.ToInt64() + 4);
                if (ipVersion == AF_INET)
                {
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCPROW_OWNER_PID row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCPROW_OWNER_PID));
                        if ((int)row.owningPid == pid)
                        {
                            int port = ((int)(row.localPort & 0xFF) << 8) | ((int)((row.localPort >> 8) & 0xFF));
                            if (port > 0 && !ports.Contains(port)) ports.Add(port);
                        }
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                    }
                }
                else
                {
                    int rowSize = Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID));
                    for (int i = 0; i < count; i++)
                    {
                        MIB_TCP6ROW_OWNER_PID row = (MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(rowPtr, typeof(MIB_TCP6ROW_OWNER_PID));
                        if ((int)row.owningPid == pid)
                        {
                            int port = ((int)(row.localPort & 0xFF) << 8) | ((int)((row.localPort >> 8) & 0xFF));
                            if (port > 0 && !ports.Contains(port)) ports.Add(port);
                        }
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                    }
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static void EnsureLoopbackCertCallback()
        {
            lock (LocalLock)
            {
                if (_certCallbackHooked) return;
                RemoteCertificateValidationCallback prev = ServicePointManager.ServerCertificateValidationCallback;
                ServicePointManager.ServerCertificateValidationCallback = delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
                {
                    if (errors == SslPolicyErrors.None) return true;
                    HttpWebRequest req = sender as HttpWebRequest;
                    if (req != null && req.RequestUri != null && req.RequestUri.IsLoopback)
                        return true;
                    return prev != null && prev(sender, cert, chain, errors);
                };
                _certCallbackHooked = true;
            }
        }

        // ---------------- 账号列表与管理 ----------------
        public static List<AccountInfo> ListAccounts(bool enabled)
        {
            List<AccountInfo> list = new List<AccountInfo>();
            if (!enabled) return list;

            string localEmail;
            lock (LocalLock) { localEmail = _cachedLocalEmail; }
            list.Add(new AccountInfo
            {
                Provider = ProviderKind.Antigravity,
                Uid = LocalUid,
                Home = LocalHome,
                IsAgyLocal = true,
                Email = localEmail ?? "",
                Nick = string.IsNullOrEmpty(localEmail) ? "Antigravity (本机)" : localEmail,
                Label = string.IsNullOrEmpty(localEmail) ? "Antigravity (本机)" : (localEmail + " (本机)")
            });

            if (Directory.Exists(AppPaths.AgyAccountsDir))
            {
                string[] files = Directory.GetFiles(AppPaths.AgyAccountsDir, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    Dictionary<string, object> d = JsonUtil.ReadDict(file);
                    if (d == null) continue;
                    string email = JsonUtil.GetStr(d, "email") ?? "";
                    string name = JsonUtil.GetStr(d, "name") ?? "";
                    string label = JsonUtil.GetStr(d, "label") ?? "";
                    string uid = JsonUtil.GetStr(d, "uid");
                    if (string.IsNullOrEmpty(uid))
                        uid = "agy:" + (string.IsNullOrEmpty(email) ? Path.GetFileNameWithoutExtension(file) : email.ToLowerInvariant());
                    bool authOk = JsonUtil.GetBool(d, "auth_ok", true);
                    list.Add(new AccountInfo
                    {
                        Provider = ProviderKind.Antigravity,
                        Uid = uid,
                        Home = file,
                        IsAgyLocal = false,
                        Email = email,
                        Nick = !string.IsNullOrEmpty(name) ? name : email,
                        Label = label,
                        AuthBad = !authOk
                    });
                }
            }
            return list;
        }

        public static bool RenameAccount(string uid, string label)
        {
            if (string.IsNullOrEmpty(uid) || uid == LocalUid) return false;
            if (!Directory.Exists(AppPaths.AgyAccountsDir)) return false;
            foreach (string file in Directory.GetFiles(AppPaths.AgyAccountsDir, "*.json"))
            {
                Dictionary<string, object> d = JsonUtil.ReadDict(file);
                if (d == null) continue;
                string fuid = JsonUtil.GetStr(d, "uid") ?? ("agy:" + Path.GetFileNameWithoutExtension(file));
                if (string.Equals(fuid, uid, StringComparison.OrdinalIgnoreCase))
                {
                    d["label"] = (label ?? "").Trim();
                    JsonUtil.WriteAtomic(file, d);
                    return true;
                }
            }
            return false;
        }

        public static bool RemoveAccount(string uid)
        {
            if (string.IsNullOrEmpty(uid) || uid == LocalUid) return false;
            if (!Directory.Exists(AppPaths.AgyAccountsDir)) return false;
            foreach (string file in Directory.GetFiles(AppPaths.AgyAccountsDir, "*.json"))
            {
                Dictionary<string, object> d = JsonUtil.ReadDict(file);
                if (d == null) continue;
                string fuid = JsonUtil.GetStr(d, "uid") ?? ("agy:" + Path.GetFileNameWithoutExtension(file));
                if (string.Equals(fuid, uid, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); return true; }
                    catch { return false; }
                }
            }
            return false;
        }

        public static void MarkAuthState(string home, bool ok)
        {
            if (string.IsNullOrEmpty(home) || home == LocalHome || !File.Exists(home)) return;
            try
            {
                Dictionary<string, object> d = JsonUtil.ReadDict(home);
                if (d == null) return;
                bool cur = JsonUtil.GetBool(d, "auth_ok", true);
                if (cur != ok)
                {
                    d["auth_ok"] = ok;
                    JsonUtil.WriteAtomic(home, d);
                }
            }
            catch { }
        }

        // ---------------- 统一拉取入口 ----------------
        public static UsageData FetchUsage(AccountInfo acct, AppConfig cfg)
        {
            if (acct == null) throw new Exception("无效账号");
            if (acct.IsAgyLocal || acct.Home == LocalHome)
            {
                UsageData data = FetchLocalUsage();
                if (!data.MinRemaining.HasValue)
                    throw new OfflineException("未读取到 Antigravity 额度（请确认已登录）");
                SaveLocalQuotaSnapshot(data);
                return data;
            }
            return FetchCloudUsage(acct.Home, cfg);
        }

        private sealed class LocalQuotaSnapshot
        {
            public double CapturedAt;
            public string Plan;
            public List<QuotaGroup> Groups;
        }

        private static string LocalQuotaSnapshotPath
        {
            get { return Path.Combine(AppPaths.DataDir, "agy_local_quota.json"); }
        }

        private static void SaveLocalQuotaSnapshot(UsageData data)
        {
            try
            {
                JsonUtil.WriteAtomic(LocalQuotaSnapshotPath, new LocalQuotaSnapshot
                {
                    CapturedAt = data.Ts,
                    Plan = data.Plan,
                    Groups = data.Groups
                });
            }
            catch (IOException ex) { Logger.Log("AGY 额度快照保存失败: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Logger.Log("AGY 额度快照保存失败: " + ex.Message); }
        }

        public static UsageData ReadLocalQuotaSnapshot()
        {
            LocalQuotaSnapshot snapshot = JsonUtil.ReadObject<LocalQuotaSnapshot>(LocalQuotaSnapshotPath);
            if (snapshot == null || !(snapshot.CapturedAt > 0 && snapshot.CapturedAt <= FormatUtil.NowUnix() + 86400)
                || snapshot.Groups == null || snapshot.Groups.Exists(g => g == null || g.Buckets == null || g.Buckets.Exists(b => b == null)))
                return null;

            UsageData data = BuildUsageFromGroups(LocalHome, "", snapshot.Plan, "本机快照", snapshot.Groups);
            data.Ts = snapshot.CapturedAt;
            return data.MinRemaining.HasValue ? data : null;
        }

        // ---------------- 通道 1: 本机 Antigravity 自动发现 ----------------
        private class PortEndpoint
        {
            public int Port;
            public string PreferredScheme; // "https" | "http" | null
        }

        private class LocalServerCandidate
        {
            public int Pid;
            public string CsrfToken;
            public List<PortEndpoint> Endpoints = new List<PortEndpoint>();

            public void AddPort(int port, string scheme)
            {
                if (port <= 0) return;
                foreach (PortEndpoint ep in Endpoints)
                {
                    if (ep.Port == port)
                    {
                        if (string.IsNullOrEmpty(ep.PreferredScheme) && !string.IsNullOrEmpty(scheme))
                            ep.PreferredScheme = scheme;
                        return;
                    }
                }
                Endpoints.Add(new PortEndpoint { Port = port, PreferredScheme = scheme });
            }
        }

        private static bool IsAntigravityProcess(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return false;
                    string n = (p.ProcessName ?? "").ToLowerInvariant();
                    return n.Contains("language_server") || n.Contains("antigravity") || n == "agy";
                }
            }
            catch { return false; }
        }

        private static List<LocalServerCandidate> DiscoverLocalServers()
        {
            Dictionary<int, LocalServerCandidate> byPid = new Dictionary<int, LocalServerCandidate>();
            List<LocalServerCandidate> list = new List<LocalServerCandidate>();

            Func<int, LocalServerCandidate> getOrCreate = delegate(int pid)
            {
                LocalServerCandidate c;
                if (pid > 0 && byPid.TryGetValue(pid, out c)) return c;
                c = new LocalServerCandidate { Pid = pid, CsrfToken = "" };
                if (pid > 0) byPid[pid] = c;
                list.Add(c);
                return c;
            };

            // 1. 优先读取 ~/.gemini/antigravity/daemon/*.json 发现文件 (按最新修改时间倒序)
            try
            {
                string daemonDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gemini", "antigravity", "daemon");
                if (Directory.Exists(daemonDir))
                {
                    string[] files = Directory.GetFiles(daemonDir, "*.json");
                    Array.Sort(files, delegate(string a, string b)
                    {
                        return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
                    });
                    foreach (string file in files)
                    {
                        Dictionary<string, object> d = JsonUtil.ReadDict(file);
                        if (d == null) continue;
                        int pid = JsonUtil.GetInt(d, "pid") ?? JsonUtil.GetInt(d, "processId") ?? 0;
                        if (pid > 0 && !IsAntigravityProcess(pid)) continue;
                        string csrf = JsonUtil.GetStr(d, "csrfToken")
                            ?? JsonUtil.GetStr(d, "csrf_token")
                            ?? JsonUtil.GetStr(d, "token")
                            ?? "";
                        LocalServerCandidate c = getOrCreate(pid);
                        if (!string.IsNullOrEmpty(csrf)) c.CsrfToken = csrf;

                        foreach (string pk in new string[] { "httpsPort", "https_port" })
                        {
                            int? pv = JsonUtil.GetInt(d, pk);
                            if (pv.HasValue) c.AddPort(pv.Value, "https");
                        }
                        foreach (string pk in new string[] { "httpPort", "http_port", "extensionServerPort", "extension_server_port", "port" })
                        {
                            int? pv = JsonUtil.GetInt(d, pk);
                            if (pv.HasValue) c.AddPort(pv.Value, pk.Contains("http") ? "http" : null);
                        }
                        if (pid > 0)
                        {
                            foreach (int p in GetListeningPortsForPid(pid))
                                c.AddPort(p, null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Antigravity daemon 目录扫描异常: " + ex.Message);
            }

            // 2. 通过 WMI 扫描运行中的 language_server 进程命令行与监听端口
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE 'language_server%'"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        try
                        {
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            string cmd = Convert.ToString(mo["CommandLine"]) ?? "";

                            LocalServerCandidate c = getOrCreate(pid);
                            Match mCsrf = Regex.Match(cmd, @"--csrf[-_]token(?:=|\s+)""?([^\s""]+)""?", RegexOptions.IgnoreCase);
                            if (mCsrf.Success && string.IsNullOrEmpty(c.CsrfToken))
                                c.CsrfToken = mCsrf.Groups[1].Value;

                            foreach (string flag in new string[] { "--https_port", "--https_server_port" })
                            {
                                Match mp = Regex.Match(cmd, flag + @"(?:=|\s+)(\d+)", RegexOptions.IgnoreCase);
                                int p;
                                if (mp.Success && int.TryParse(mp.Groups[1].Value, out p)) c.AddPort(p, "https");
                            }
                            foreach (string flag in new string[] { "--http_port", "--port", "--extension_server_port" })
                            {
                                Match mp = Regex.Match(cmd, flag + @"(?:=|\s+)(\d+)", RegexOptions.IgnoreCase);
                                int p;
                                if (mp.Success && int.TryParse(mp.Groups[1].Value, out p)) c.AddPort(p, flag.Contains("http") ? "http" : null);
                            }
                            foreach (int p in GetListeningPortsForPid(pid))
                                c.AddPort(p, null);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Antigravity 本地服务发现异常: " + ex.Message);
            }

            // 过滤掉无端口候选，优先把有 csrf 的排前面
            List<LocalServerCandidate> valid = new List<LocalServerCandidate>();
            foreach (LocalServerCandidate c in list)
            {
                if (c.Endpoints.Count > 0) valid.Add(c);
            }
            valid.Sort(delegate(LocalServerCandidate a, LocalServerCandidate b)
            {
                int sa = string.IsNullOrEmpty(a.CsrfToken) ? 1 : 0;
                int sb = string.IsNullOrEmpty(b.CsrfToken) ? 1 : 0;
                return sa.CompareTo(sb);
            });
            return valid;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch { return false; }
        }

        private static Dictionary<string, object> CallLocalRpc(string scheme, int port, string csrf, string method, string jsonBody)
        {
            EnsureLoopbackCertCallback();
            string url = string.Format("{0}://127.0.0.1:{1}/exa.language_server_pb.LanguageServerService/{2}", scheme, port, method);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = 1200;
            req.ReadWriteTimeout = 1200;
            req.ContentType = "application/json";
            req.Headers["Connect-Protocol-Version"] = "1";
            if (!string.IsNullOrEmpty(csrf))
                req.Headers["X-Codeium-Csrf-Token"] = csrf;
            req.Proxy = null;

            byte[] body = Encoding.UTF8.GetBytes(jsonBody ?? "{}");
            req.ContentLength = body.Length;
            using (Stream st = req.GetRequestStream())
                st.Write(body, 0, body.Length);

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string txt = sr.ReadToEnd();
                return JsonUtil.ParseDict(txt);
            }
        }

        private static Dictionary<string, object> CallLocalGetUserStatus(string scheme, int port, string csrf)
        {
            return CallLocalRpc(scheme, port, csrf, "GetUserStatus",
                "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}");
        }

        private static Dictionary<string, object> TryCallLocalQuotaSummary(string scheme, int port, string csrf)
        {
            try
            {
                return CallLocalRpc(scheme, port, csrf, "RetrieveUserQuotaSummary", "{}");
            }
            catch
            {
                return null;
            }
        }

        private static UsageData FetchLocalUsage()
        {
            int cPid, cPort;
            string cScheme, cCsrf;
            lock (LocalLock)
            {
                cPid = _cachedPid;
                cPort = _cachedPort;
                cScheme = _cachedScheme;
                cCsrf = _cachedCsrf;
            }

            Dictionary<string, object> resp = null;
            if (cPid > 0 && cPort > 0 && !string.IsNullOrEmpty(cCsrf) && IsProcessAlive(cPid))
            {
                try
                {
                    resp = CallLocalGetUserStatus(cScheme, cPort, cCsrf);
                }
                catch
                {
                    resp = null;
                }
            }

            if (resp == null)
            {
                List<LocalServerCandidate> cands = DiscoverLocalServers();
                Logger.Log("Antigravity 本地发现候选数: " + cands.Count);
                if (cands.Count == 0)
                    throw new OfflineException("未检测到运行中的 Antigravity");

                foreach (LocalServerCandidate c in cands)
                {
                    Logger.Log(string.Format("AGY 候选 pid={0}, hasCsrf={1}, eps={2}",
                        c.Pid, !string.IsNullOrEmpty(c.CsrfToken),
                        string.Join(",", c.Endpoints.ConvertAll<string>(delegate(PortEndpoint x) { return (x.PreferredScheme ?? "*") + ":" + x.Port; }).ToArray())));
                    foreach (PortEndpoint ep in c.Endpoints)
                    {
                        string[] schemes = string.IsNullOrEmpty(ep.PreferredScheme)
                            ? new string[] { "https", "http" }
                            : new string[] { ep.PreferredScheme };
                        foreach (string scheme in schemes)
                        {
                            try
                            {
                                Dictionary<string, object> r = CallLocalGetUserStatus(scheme, ep.Port, c.CsrfToken);
                                if (r != null && (r.ContainsKey("userStatus") || r.ContainsKey("cascadeModelConfigData")))
                                {
                                    resp = r;
                                    lock (LocalLock)
                                    {
                                        _cachedPid = c.Pid;
                                        _cachedPort = ep.Port;
                                        _cachedScheme = scheme;
                                        _cachedCsrf = c.CsrfToken;
                                    }
                                    Logger.Log(string.Format("Antigravity 本地连接成功: pid={0}, {1}://127.0.0.1:{2}", c.Pid, scheme, ep.Port));
                                    break;
                                }
                                else
                                {
                                    Logger.Log(string.Format("AGY {0}://127.0.0.1:{1} 响应无 userStatus", scheme, ep.Port));
                                }
                            }
                            catch (WebException wex)
                            {
                                HttpWebResponse hr = wex.Response as HttpWebResponse;
                                string code = hr != null ? ((int)hr.StatusCode).ToString() : wex.Status.ToString();
                                Logger.Log(string.Format("AGY {0}://127.0.0.1:{1} 失败: {2}", scheme, ep.Port, code));
                            }
                            catch (Exception ex)
                            {
                                Logger.Log(string.Format("AGY {0}://127.0.0.1:{1} 异常: {2}", scheme, ep.Port, ex.Message));
                            }
                        }
                        if (resp != null) break;
                    }
                    if (resp != null) break;
                }
            }

            if (resp == null)
                throw new OfflineException("Antigravity 服务未响应（请确认已登录）");

            string useScheme, useCsrf;
            int usePort;
            lock (LocalLock)
            {
                useScheme = _cachedScheme;
                usePort = _cachedPort;
                useCsrf = _cachedCsrf;
            }
            Dictionary<string, object> summaryResp = (usePort > 0 && !string.IsNullOrEmpty(useScheme))
                ? TryCallLocalQuotaSummary(useScheme, usePort, useCsrf)
                : null;

            return ParseLocalResponse(resp, summaryResp);
        }

        private static UsageData ParseLocalResponse(Dictionary<string, object> root, Dictionary<string, object> summaryResp)
        {
            Dictionary<string, object> us = JsonUtil.GetDict(root, "userStatus") ?? root;
            string email = JsonUtil.GetStr(us, "email") ?? "";
            string name = JsonUtil.GetStr(us, "name") ?? email;
            if (!string.IsNullOrEmpty(email))
            {
                lock (LocalLock) { _cachedLocalEmail = email; }
            }

            string plan = ExtractPlanFromLocal(us);

            // 优先使用官方分组接口 RetrieveUserQuotaSummary (包含每组的 5h + Weekly 双窗口)
            List<QuotaGroup> groups = ParseGroupsFromResponse(summaryResp)
                ?? ParseGroupsFromResponse(us)
                ?? ParseGroupsFromResponse(root);

            UsageData data;
            if (groups != null && groups.Count > 0)
            {
                data = BuildUsageFromGroups(LocalHome, name, plan, "本机", groups);
            }
            else
            {
                List<RawModelQuota> raw = new List<RawModelQuota>();
                Dictionary<string, object> cascade = JsonUtil.GetDict(us, "cascadeModelConfigData")
                    ?? JsonUtil.GetDict(root, "cascadeModelConfigData");
                if (cascade != null)
                {
                    List<object> models = JsonUtil.GetList(cascade, "clientModelConfigs")
                        ?? JsonUtil.GetList(cascade, "models");
                    if (models != null)
                    {
                        foreach (object item in models)
                        {
                            Dictionary<string, object> md = item as Dictionary<string, object>;
                            if (md == null) continue;
                            CollectRawModel(raw, md, null);
                        }
                    }
                }
                data = BuildUsageFromRaw(LocalHome, name, plan, "本机", raw);
            }

            return data;
        }

        private static string ExtractPlanFromLocal(Dictionary<string, object> us)
        {
            Dictionary<string, object> tier = JsonUtil.GetDict(us, "userTier");
            if (tier != null)
            {
                string tn = JsonUtil.GetStr(tier, "name") ?? JsonUtil.GetStr(tier, "id");
                if (!string.IsNullOrEmpty(tn)) return NormalizePlanName(tn);
            }
            string tierStr = JsonUtil.GetStr(us, "userTier");
            if (!string.IsNullOrEmpty(tierStr)) return NormalizePlanName(tierStr);

            Dictionary<string, object> ps = JsonUtil.GetDict(us, "planStatus");
            if (ps != null)
            {
                Dictionary<string, object> pi = JsonUtil.GetDict(ps, "planInfo");
                if (pi != null)
                {
                    string pn = JsonUtil.GetStr(pi, "planDisplayName") ?? JsonUtil.GetStr(pi, "planName") ?? JsonUtil.GetStr(pi, "teamsTier");
                    if (!string.IsNullOrEmpty(pn)) return NormalizePlanName(pn);
                }
            }
            return "AGY";
        }

        // ---------------- 通道 2: 独立云端多 Google 账号 OAuth + Cloud Code API ----------------
        private static string GetClientId(AppConfig cfg)
        {
            if (cfg != null && !string.IsNullOrEmpty(cfg.agy_oauth_client_id))
                return cfg.agy_oauth_client_id.Trim();
            return DefaultClientId;
        }

        private static string GetClientSecret(AppConfig cfg)
        {
            if (cfg != null && !string.IsNullOrEmpty(cfg.agy_oauth_client_secret))
                return cfg.agy_oauth_client_secret.Trim();
            return "";
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string RandomBase64Url(int byteLen)
        {
            byte[] buf = new byte[byteLen];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                rng.GetBytes(buf);
            return Base64UrlEncode(buf);
        }

        private static string Sha256Base64Url(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return Base64UrlEncode(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        public static bool StartCloudOAuthLogin(AppConfig cfg, Action<bool, string> onDone, out string immediateMsg)
        {
            string clientId = cfg == null ? "" : (cfg.agy_oauth_client_id ?? "").Trim();
            string clientSecret = GetClientSecret(cfg);
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                immediateMsg = "请先配置 Google 桌面 OAuth 客户端 ID 和密钥";
                return false;
            }

            HttpListener listener = null;
            int port = 0;
            lock (OAuthLock)
            {
                if (_activeListener != null)
                {
                    immediateMsg = "Google 授权正在进行，请返回浏览器";
                    return false;
                }
                for (int p = 51121; p <= 51135; p++)
                {
                    HttpListener test = new HttpListener();
                    try
                    {
                        test.Prefixes.Add(string.Format("http://localhost:{0}/", p));
                        test.Start();
                        listener = test;
                        port = p;
                        break;
                    }
                    catch (HttpListenerException ex)
                    {
                        test.Close();
                        Logger.Log("Google OAuth 回调监听失败: " + ex.ErrorCode);
                    }
                }
                if (listener == null)
                {
                    immediateMsg = "无法启动 Google 授权回调，请查看监控日志";
                    return false;
                }
                _activeListener = listener;
            }

            string redirectUri = string.Format("http://localhost:{0}{1}", port, OAuthCallbackPath);
            string state = RandomBase64Url(16);
            string verifier = RandomBase64Url(32);
            string challenge = Sha256Base64Url(verifier);

            string authUrl = OAuthAuthUrl
                + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString(OAuthScopes)
                + "&access_type=offline"
                + "&prompt=consent"
                + "&state=" + Uri.EscapeDataString(state)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256";

            try
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CloseOAuthListener(listener);
                immediateMsg = "打开浏览器失败: " + ex.Message;
                return false;
            }

            Thread t = new Thread((ThreadStart)delegate
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = WaitForOAuthCallback(listener, state, TimeSpan.FromMinutes(5));
                    if (ctx == null)
                    {
                        if (onDone != null) onDone(false, "Google 登录超时，请重试");
                        return;
                    }
                    string code = ctx.Request.QueryString["code"];
                    string err = ctx.Request.QueryString["error"];

                    if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(code))
                    {
                        string message = err == "access_denied" ? "Google 授权已取消" : "Google 授权未完成，请重新发起授权";
                        WriteHtmlResponse(ctx.Response, false, message);
                        if (onDone != null) onDone(false, message);
                        return;
                    }

                    Dictionary<string, object> tok = ExchangeOAuthCode(clientId, clientSecret, code, redirectUri, verifier);
                    string accessToken = JsonUtil.GetStr(tok, "access_token");
                    string refreshToken = JsonUtil.GetStr(tok, "refresh_token");
                    int expiresIn = JsonUtil.GetInt(tok, "expires_in") ?? 3600;
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        WriteHtmlResponse(ctx.Response, false, "换取 Token 失败，请关闭后重试");
                        if (onDone != null) onDone(false, "换取 Google Token 失败");
                        return;
                    }

                    Dictionary<string, object> ui = FetchGoogleUserInfo(accessToken);
                    string email = (JsonUtil.GetStr(ui, "email") ?? "").Trim();
                    string name = (JsonUtil.GetStr(ui, "name") ?? email).Trim();
                    if (string.IsNullOrEmpty(email))
                        email = "google_" + DateTime.Now.ToString("yyyyMMddHHmmss");

                    string projectId = "";
                    string plan = "AGY";
                    try
                    {
                        LoadCodeAssistInfo(accessToken, out projectId, out plan);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("loadCodeAssist 失败(仍保存账号): " + ex.Message);
                    }

                    SaveCloudAccount(email, name, accessToken, refreshToken, FormatUtil.NowUnix() + expiresIn, projectId, plan);
                    WriteHtmlResponse(ctx.Response, true, "已成功连接账号 " + email + "，可以关闭此页面返回监控窗口。");
                    if (onDone != null) onDone(true, "已添加 Antigravity 账号: " + email);
                }
                catch (Exception ex)
                {
                    AuthException authError = ex as AuthException;
                    string message = authError != null ? authError.Message : "Google 登录失败，请查看监控日志";
                    Logger.Log("Antigravity OAuth 异常: " + (authError != null ? authError.Message : ex.GetType().Name));
                    if (ctx != null) WriteHtmlResponse(ctx.Response, false, message);
                    if (onDone != null) onDone(false, message);
                }
                finally
                {
                    CloseOAuthListener(listener);
                }
            });
            t.IsBackground = true;
            t.Start();

            immediateMsg = "已打开浏览器进行 Google 授权，完成后自动加入";
            return true;
        }

        private static HttpListenerContext WaitForOAuthCallback(HttpListener listener, string state, TimeSpan timeout)
        {
            Stopwatch timer = Stopwatch.StartNew();
            for (TimeSpan remaining = timeout; remaining > TimeSpan.Zero; remaining = timeout - timer.Elapsed)
            {
                IAsyncResult pending = listener.BeginGetContext(null, null);
                if (!pending.AsyncWaitHandle.WaitOne(remaining)) return null;
                HttpListenerContext ctx = listener.EndGetContext(pending);
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == OAuthCallbackPath)
                {
                    if (ctx.Request.QueryString["state"] == state) return ctx;
                    WriteHtmlResponse(ctx.Response, false, "此授权链接已失效，请返回监控窗口重新发起授权。");
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
            return null;
        }

        private static void CloseOAuthListener(HttpListener listener)
        {
            lock (OAuthLock)
            {
                try { listener.Close(); } catch { }
                if (_activeListener == listener) _activeListener = null;
            }
        }

        private static void WriteHtmlResponse(HttpListenerResponse resp, bool ok, string message)
        {
            try
            {
                string color = ok ? "#3ddc84" : "#ff5b5b";
                string title = ok ? "授权成功" : "授权失败";
                string html = "<!doctype html><html><head><meta charset='utf-8'><title>" + title + "</title>"
                    + "<style>body{background:#17181d;color:#eceef3;font-family:'Microsoft YaHei UI',sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}"
                    + ".card{background:#202128;border:1px solid #2e3039;border-radius:12px;padding:28px 36px;max-width:420px;text-align:center;}"
                    + "h2{color:" + color + ";margin:0 0 12px;}p{color:#b0b3be;font-size:14px;line-height:1.6;margin:0;}</style></head>"
                    + "<body><div class='card'><h2>" + title + "</h2><p>" + WebUtility.HtmlEncode(message) + "</p></div></body></html>";
                byte[] buf = Encoding.UTF8.GetBytes(html);
                resp.StatusCode = ok ? 200 : 400;
                resp.Headers["Cache-Control"] = "no-store";
                resp.Headers["Referrer-Policy"] = "no-referrer";
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                resp.OutputStream.Write(buf, 0, buf.Length);
                resp.OutputStream.Close();
            }
            catch { }
        }

        private static Dictionary<string, object> PostForm(string url, Dictionary<string, string> fields)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in fields)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value ?? ""));
            }
            byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = 15000;
            req.ContentType = "application/x-www-form-urlencoded";
            req.ContentLength = data.Length;
            try
            {
                using (Stream st = req.GetRequestStream())
                    st.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return JsonUtil.ParseDict(sr.ReadToEnd()) ?? new Dictionary<string, object>();
            }
            catch (WebException wex)
            {
                HttpWebResponse response = wex.Response as HttpWebResponse;
                if (response == null) throw;
                using (response)
                {
                    Dictionary<string, object> error;
                    using (StreamReader sr = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        error = JsonUtil.ParseDict(sr.ReadToEnd());
                    switch (JsonUtil.GetStr(error, "error"))
                    {
                        case "invalid_client":
                        case "unauthorized_client":
                            throw new AuthException("Google 拒绝了 OAuth 客户端，请核对客户端 ID 和密钥");
                        case "invalid_grant":
                            throw new AuthException("Google 授权已失效，请重新登录");
                        case "invalid_request":
                            if (JsonUtil.GetStr(error, "error_description") == "client_secret is missing.")
                                throw new AuthException("Google 登录缺少 OAuth 客户端密钥");
                            break;
                    }
                    Logger.Log("Google 令牌请求失败（HTTP " + (int)response.StatusCode + "）");
                    throw;
                }
            }
        }

        private static Dictionary<string, object> ExchangeOAuthCode(string clientId, string clientSecret, string code, string redirectUri, string verifier)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["client_id"] = clientId;
            if (!string.IsNullOrEmpty(clientSecret)) f["client_secret"] = clientSecret;
            f["code"] = code;
            f["redirect_uri"] = redirectUri;
            f["grant_type"] = "authorization_code";
            f["code_verifier"] = verifier;
            return PostForm(OAuthTokenUrl, f);
        }

        private static Dictionary<string, object> RefreshOAuthToken(string clientId, string clientSecret, string refreshToken)
        {
            Dictionary<string, string> f = new Dictionary<string, string>();
            f["client_id"] = clientId;
            if (!string.IsNullOrEmpty(clientSecret)) f["client_secret"] = clientSecret;
            f["refresh_token"] = refreshToken;
            f["grant_type"] = "refresh_token";
            return PostForm(OAuthTokenUrl, f);
        }

        private static Dictionary<string, object> FetchGoogleUserInfo(string accessToken)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(OAuthUserInfoUrl);
            req.Method = "GET";
            req.Timeout = 12000;
            req.Headers["Authorization"] = "Bearer " + accessToken;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return JsonUtil.ParseDict(sr.ReadToEnd()) ?? new Dictionary<string, object>();
        }

        private static Dictionary<string, object> PostCloudCode(string path, string accessToken, string jsonBody)
        {
            Exception lastEx = null;
            foreach (string ep in CloudCodeEndpoints)
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ep + path);
                    req.Method = "POST";
                    req.Timeout = 15000;
                    req.ContentType = "application/json";
                    req.UserAgent = "antigravity/1.104.0 windows/amd64";
                    req.Headers["Authorization"] = "Bearer " + accessToken;
                    req.Headers["X-Goog-Api-Client"] = "google-cloud-sdk vscode_cloudshelleditor/0.1";

                    byte[] bytes = Encoding.UTF8.GetBytes(jsonBody ?? "{}");
                    req.ContentLength = bytes.Length;
                    using (Stream st = req.GetRequestStream())
                        st.Write(bytes, 0, bytes.Length);

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return JsonUtil.ParseDict(sr.ReadToEnd()) ?? new Dictionary<string, object>();
                }
                catch (WebException wex)
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    using (hr)
                    {
                        if (hr != null && hr.StatusCode == HttpStatusCode.Unauthorized)
                            throw new AuthException("Google 授权已失效，请重新登录");
                        if (hr != null && hr.StatusCode == HttpStatusCode.Forbidden)
                            throw new AgyCloudPermissionException(GetCloudPermissionMessage(hr));
                    }
                    lastEx = wex;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }
            throw lastEx ?? new Exception("请求 Google Cloud Code API 失败");
        }

        private static string GetCloudPermissionMessage(HttpWebResponse response)
        {
            try
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    Dictionary<string, object> error = JsonUtil.GetDict(JsonUtil.ParseDict(reader.ReadToEnd()), "error");
                    List<object> details = JsonUtil.GetList(error, "details");
                    if (details != null)
                    {
                        foreach (object detail in details)
                        {
                            string reason = JsonUtil.GetStr(detail as Dictionary<string, object>, "reason");
                            if (reason == "SERVICE_DISABLED")
                                return "Google Cloud 项目未启用 Cloud Code Private API（403）";
                            if (reason == "ACCESS_TOKEN_SCOPE_INSUFFICIENT")
                                return "Google 授权范围不足，无法读取云端额度（403）";
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (WebException) { }
            return "Google 拒绝访问 AGY 云端额度接口（403）";
        }

        private static void LoadCodeAssistInfo(string accessToken, out string projectId, out string plan)
        {
            projectId = "";
            plan = "AGY";
            string body = "{\"metadata\":{\"ideType\":\"IDE_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}";
            Dictionary<string, object> r = PostCloudCode("/v1internal:loadCodeAssist", accessToken, body);
            if (r == null) return;
            projectId = JsonUtil.GetStr(r, "cloudaicompanionProject") ?? "";
            Dictionary<string, object> paid = JsonUtil.GetDict(r, "paidTier");
            Dictionary<string, object> cur = JsonUtil.GetDict(r, "currentTier");
            string tn = JsonUtil.GetStr(paid, "name") ?? JsonUtil.GetStr(paid, "id")
                ?? JsonUtil.GetStr(cur, "name") ?? JsonUtil.GetStr(cur, "id");
            if (!string.IsNullOrEmpty(tn))
                plan = NormalizePlanName(tn);
        }

        private static void SaveCloudAccount(string email, string name, string accessToken, string refreshToken, double expiresAt, string projectId, string plan)
        {
            Directory.CreateDirectory(AppPaths.AgyAccountsDir);
            string safe = Regex.Replace(email.ToLowerInvariant(), @"[^a-z0-9._-]", "_");
            string file = Path.Combine(AppPaths.AgyAccountsDir, safe + ".json");
            Dictionary<string, object> prev = JsonUtil.ReadDict(file);
            string prevLabel = JsonUtil.GetStr(prev, "label") ?? "";
            string prevRefresh = JsonUtil.GetStr(prev, "refresh_token") ?? "";

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["uid"] = "agy:" + email.ToLowerInvariant();
            d["email"] = email;
            d["name"] = name;
            d["label"] = prevLabel;
            d["access_token"] = accessToken;
            d["refresh_token"] = !string.IsNullOrEmpty(refreshToken) ? refreshToken : prevRefresh;
            d["expires_at"] = expiresAt;
            d["project_id"] = projectId ?? "";
            d["plan"] = plan ?? "AGY";
            d["auth_ok"] = true;
            JsonUtil.WriteAtomic(file, d);
        }

        private static UsageData FetchCloudUsage(string filePath, AppConfig cfg)
        {
            Dictionary<string, object> acct = JsonUtil.ReadDict(filePath);
            if (acct == null) throw new AuthException("账号凭证缺失，请重新登录");

            string accessToken = JsonUtil.GetStr(acct, "access_token") ?? "";
            string refreshToken = JsonUtil.GetStr(acct, "refresh_token") ?? "";
            double expiresAt = JsonUtil.GetDouble(acct, "expires_at") ?? 0;
            string email = JsonUtil.GetStr(acct, "email") ?? "";
            string name = JsonUtil.GetStr(acct, "name") ?? email;
            string projectId = JsonUtil.GetStr(acct, "project_id") ?? "";
            string plan = JsonUtil.GetStr(acct, "plan") ?? "AGY";

            double now = FormatUtil.NowUnix();
            if (string.IsNullOrEmpty(accessToken) || now >= expiresAt - 120)
            {
                if (string.IsNullOrEmpty(refreshToken))
                    throw new AuthException("Google 登录已过期，请重新登录");
                try
                {
                    Dictionary<string, object> tok = RefreshOAuthToken(GetClientId(cfg), GetClientSecret(cfg), refreshToken);
                    string newAt = JsonUtil.GetStr(tok, "access_token");
                    if (string.IsNullOrEmpty(newAt))
                        throw new AuthException("刷新 Google Token 失败");
                    int exp = JsonUtil.GetInt(tok, "expires_in") ?? 3600;
                    accessToken = newAt;
                    acct["access_token"] = accessToken;
                    acct["expires_at"] = FormatUtil.NowUnix() + exp;
                    string newRt = JsonUtil.GetStr(tok, "refresh_token");
                    if (!string.IsNullOrEmpty(newRt)) acct["refresh_token"] = newRt;
                    acct["auth_ok"] = true;
                    JsonUtil.WriteAtomic(filePath, acct);
                }
                catch (AuthException) { throw; }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new AuthException("Google 授权已失效，请重新登录");
                    throw;
                }
            }

            if (string.IsNullOrEmpty(projectId))
            {
                try
                {
                    string pid, pl;
                    LoadCodeAssistInfo(accessToken, out pid, out pl);
                    if (!string.IsNullOrEmpty(pid))
                    {
                        projectId = pid;
                        acct["project_id"] = projectId;
                    }
                    if (!string.IsNullOrEmpty(pl))
                    {
                        plan = pl;
                        acct["plan"] = plan;
                    }
                    JsonUtil.WriteAtomic(filePath, acct);
                }
                catch (AuthException) { throw; }
                catch (AgyCloudPermissionException) { throw; }
                catch { }
            }

            string body = string.IsNullOrEmpty(projectId)
                ? "{}"
                : JsonUtil.Serialize(new Dictionary<string, object> { { "project", projectId } });

            // 优先尝试官方分组汇总接口 retrieveUserQuotaSummary
            try
            {
                Dictionary<string, object> sr = PostCloudCode("/v1internal:retrieveUserQuotaSummary", accessToken, body);
                List<QuotaGroup> groups = ParseGroupsFromResponse(sr);
                if (groups != null && groups.Count > 0)
                    return BuildUsageFromGroups(filePath, name, plan, "云端", groups);
            }
            catch (AuthException) { throw; }
            catch (AgyCloudPermissionException) { throw; }
            catch { }

            Dictionary<string, object> r = PostCloudCode("/v1internal:fetchAvailableModels", accessToken, body);
            List<RawModelQuota> raw = new List<RawModelQuota>();

            if (r != null && r.ContainsKey("models") && r["models"] != null)
            {
                Dictionary<string, object> mDict = r["models"] as Dictionary<string, object>;
                if (mDict != null)
                {
                    foreach (KeyValuePair<string, object> kv in mDict)
                    {
                        Dictionary<string, object> md = kv.Value as Dictionary<string, object>;
                        if (md != null) CollectRawModel(raw, md, kv.Key);
                    }
                }
                else
                {
                    List<object> mList = JsonUtil.GetList(r, "models");
                    if (mList != null)
                    {
                        foreach (object item in mList)
                        {
                            Dictionary<string, object> md = item as Dictionary<string, object>;
                            if (md != null) CollectRawModel(raw, md, null);
                        }
                    }
                }
            }

            return BuildUsageFromRaw(filePath, name, plan, "云端", raw);
        }

        // ---------------- 通用模型池解析与聚合 ----------------
        private class RawModelQuota
        {
            public string ModelId;
            public string DisplayName;
            public double? RemainingPct;
            public double? ResetAt;
            public bool IsExhausted;
        }

        private static void CollectRawModel(List<RawModelQuota> list, Dictionary<string, object> md, string fallbackId)
        {
            string id = JsonUtil.GetStr(md, "modelId")
                ?? JsonUtil.GetStr(md, "model")
                ?? JsonUtil.GetStr(md, "modelName")
                ?? JsonUtil.GetStr(md, "name")
                ?? fallbackId
                ?? "";
            Dictionary<string, object> alias = JsonUtil.GetDict(md, "modelOrAlias");
            if (string.IsNullOrEmpty(id) && alias != null)
                id = JsonUtil.GetStr(alias, "model") ?? JsonUtil.GetStr(alias, "alias") ?? "";

            string label = JsonUtil.GetStr(md, "label")
                ?? JsonUtil.GetStr(md, "displayName")
                ?? id;

            Dictionary<string, object> qi = JsonUtil.GetDict(md, "quotaInfo");
            if (qi == null) return;

            double? frac = JsonUtil.GetDouble(qi, "remainingFraction");
            bool exhausted = JsonUtil.GetBool(qi, "isExhausted", false);
            double? pct = null;
            if (frac.HasValue)
                pct = Math.Max(0.0, Math.Min(100.0, frac.Value * 100.0));
            else if (exhausted)
                pct = 0.0;

            string rtStr = JsonUtil.GetStr(qi, "resetTime");
            double? resetAt = FormatUtil.ParseIsoUnix(rtStr);
            if (!pct.HasValue && !resetAt.HasValue) return;

            list.Add(new RawModelQuota
            {
                ModelId = id,
                DisplayName = label,
                RemainingPct = pct,
                ResetAt = resetAt,
                IsExhausted = exhausted || (pct.HasValue && pct.Value <= 0.01)
            });
        }

        private static string ShortGroupName(string displayName)
        {
            string s = (displayName ?? "").ToLowerInvariant();
            if (s.Contains("argon")) return "Arg";
            if (s.Contains("claude") || s.Contains("gpt") || s.Contains("3p")) return "Cld";
            if (s.Contains("gemini")) return "Gem";
            return FormatUtil.Ellipsize(displayName ?? "AGY", 3);
        }

        private static string WindowLabel(string bucketId, string displayName, string window)
        {
            string s = ((bucketId ?? "") + " " + (displayName ?? "") + " " + (window ?? "")).ToLowerInvariant();
            if (s.Contains("5h") || s.Contains("five hour") || s.Contains("5-hour") || s.Contains("5 hour"))
                return "5h";
            if (s.Contains("weekly") || s.Contains("7d") || s.Contains("week"))
                return "周";
            if (!string.IsNullOrWhiteSpace(displayName))
                return FormatUtil.Ellipsize(displayName.Trim(), 10);
            return "限额";
        }

        private static List<QuotaGroup> ParseGroupsFromResponse(Dictionary<string, object> root)
        {
            if (root == null) return null;
            List<object> rawGroups = JsonUtil.GetList(root, "groups");
            if (rawGroups == null)
            {
                Dictionary<string, object> inner = JsonUtil.GetDict(root, "response")
                    ?? JsonUtil.GetDict(root, "quotaSummary")
                    ?? JsonUtil.GetDict(root, "userQuotaSummary");
                if (inner != null) rawGroups = JsonUtil.GetList(inner, "groups");
            }
            if (rawGroups == null || rawGroups.Count == 0) return null;

            double now = FormatUtil.NowUnix();
            List<QuotaGroup> groups = new List<QuotaGroup>();
            foreach (object gObj in rawGroups)
            {
                Dictionary<string, object> gd = gObj as Dictionary<string, object>;
                if (gd == null) continue;
                string gName = JsonUtil.GetStr(gd, "displayName")
                    ?? JsonUtil.GetStr(gd, "name")
                    ?? JsonUtil.GetStr(gd, "groupId")
                    ?? "模型分组";
                List<object> rawBuckets = JsonUtil.GetList(gd, "buckets") ?? JsonUtil.GetList(gd, "quotaBuckets");
                if (rawBuckets == null || rawBuckets.Count == 0) continue;

                QuotaGroup grp = new QuotaGroup
                {
                    DisplayName = gName.Trim(),
                    ShortName = ShortGroupName(gName)
                };
                foreach (object bObj in rawBuckets)
                {
                    Dictionary<string, object> bd = bObj as Dictionary<string, object>;
                    if (bd == null) continue;
                    string bid = JsonUtil.GetStr(bd, "bucketId") ?? JsonUtil.GetStr(bd, "id") ?? "";
                    string bDisp = JsonUtil.GetStr(bd, "displayName") ?? JsonUtil.GetStr(bd, "label") ?? "";
                    string bWin = JsonUtil.GetStr(bd, "window") ?? "";
                    double? frac = JsonUtil.GetDouble(bd, "remainingFraction");
                    bool exhausted = JsonUtil.GetBool(bd, "isExhausted", false);
                    double? pct = null;
                    if (frac.HasValue) pct = Math.Max(0.0, Math.Min(100.0, frac.Value * 100.0));
                    else if (exhausted) pct = 0.0;
                    double? resetAt = FormatUtil.ParseIsoUnix(JsonUtil.GetStr(bd, "resetTime"));
                    if (!pct.HasValue && !resetAt.HasValue) continue;

                    string winLbl = WindowLabel(bid, bDisp, bWin);
                    grp.Buckets.Add(new QuotaBucket
                    {
                        Id = !string.IsNullOrEmpty(bid) ? bid : (grp.DisplayName + "_" + winLbl),
                        Label = grp.DisplayName + " · " + winLbl,
                        ShortLabel = winLbl,
                        RemainingPct = pct,
                        ResetAt = resetAt,
                        ResetLeft = resetAt.HasValue ? (double?)Math.Max(0.0, resetAt.Value - now) : null,
                        IsExhausted = exhausted || (pct.HasValue && pct.Value <= 0.01)
                    });
                }
                if (grp.Buckets.Count > 0)
                {
                    // 同一组内 5h 在上，周 在下，与 Codex 视觉习惯一致
                    grp.Buckets.Sort(delegate(QuotaBucket a, QuotaBucket b)
                    {
                        int oa = a.ShortLabel == "5h" ? 0 : (a.ShortLabel == "周" ? 1 : 2);
                        int ob = b.ShortLabel == "5h" ? 0 : (b.ShortLabel == "周" ? 1 : 2);
                        return oa.CompareTo(ob);
                    });
                    groups.Add(grp);
                }
            }
            return groups.Count > 0 ? groups : null;
        }

        private static UsageData BuildUsageFromGroups(string home, string nick, string plan, string sourceHint, List<QuotaGroup> groups)
        {
            double now = FormatUtil.NowUnix();
            List<QuotaBucket> flat = new List<QuotaBucket>();
            foreach (QuotaGroup g in groups)
            {
                foreach (QuotaBucket b in g.Buckets)
                    flat.Add(b);
            }

            UsageData data = new UsageData
            {
                Provider = ProviderKind.Antigravity,
                Home = home,
                Nick = nick,
                Plan = plan,
                SourceHint = sourceHint,
                Groups = groups,
                Buckets = flat,
                Ts = now
            };

            if (groups.Count >= 1 && groups[0].MinRemaining.HasValue)
            {
                data.PUsed = 100.0 - groups[0].MinRemaining.Value;
                data.PResetAt = groups[0].EarliestResetAt;
                data.PLeft = data.PResetAt.HasValue ? (double?)Math.Max(0.0, data.PResetAt.Value - now) : null;
            }
            if (groups.Count >= 2 && groups[1].MinRemaining.HasValue)
            {
                data.SUsed = 100.0 - groups[1].MinRemaining.Value;
                data.SResetAt = groups[1].EarliestResetAt;
                data.SLeft = data.SResetAt.HasValue ? (double?)Math.Max(0.0, data.SResetAt.Value - now) : null;
            }
            return data;
        }

        private static UsageData BuildUsageFromRaw(string home, string nick, string plan, string sourceHint, List<RawModelQuota> raw)
        {
            // 若接口仅返回模型列表未返回官方 groups，则按官方三大共享池分组聚合，避免平铺造成误解
            double now = FormatUtil.NowUnix();
            Dictionary<string, QuotaGroup> gmap = new Dictionary<string, QuotaGroup>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> gorder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (RawModelQuota m in raw)
            {
                string s = ((m.ModelId ?? "") + " " + (m.DisplayName ?? "")).ToLowerInvariant();
                string gKey, gName, gShort;
                int ord;
                if (s.Contains("argon"))
                {
                    gKey = "argon";
                    gName = "Gemini 4 Argon";
                    gShort = "Arg";
                    ord = 3;
                }
                else if (s.Contains("claude") || s.Contains("sonnet") || s.Contains("opus") || s.Contains("gpt"))
                {
                    gKey = "claude_gpt";
                    gName = "Claude and GPT models";
                    gShort = "Cld";
                    ord = 2;
                }
                else if (s.Contains("gemini"))
                {
                    gKey = "gemini";
                    gName = "Gemini Models";
                    gShort = "Gem";
                    ord = 1;
                }
                else
                {
                    gKey = "other_" + (string.IsNullOrEmpty(m.ModelId) ? "model" : m.ModelId.ToLowerInvariant());
                    gName = !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName.Trim() : "Other Models";
                    gShort = ShortGroupName(gName);
                    ord = 4;
                }

                // 根据重置时间距离推断窗口类型 (<= 12h 视为 5h 窗口，> 12h 视为周窗口)
                string winLbl = "当前";
                if (m.ResetAt.HasValue)
                {
                    double diffH = (m.ResetAt.Value - now) / 3600.0;
                    winLbl = diffH <= 12.0 ? "5h" : "周";
                }

                QuotaGroup grp;
                if (!gmap.TryGetValue(gKey, out grp))
                {
                    grp = new QuotaGroup { DisplayName = gName, ShortName = gShort };
                    gmap[gKey] = grp;
                    gorder[gKey] = ord;
                }

                QuotaBucket existing = null;
                foreach (QuotaBucket b in grp.Buckets)
                {
                    if (b.ShortLabel == winLbl) { existing = b; break; }
                }
                if (existing == null)
                {
                    grp.Buckets.Add(new QuotaBucket
                    {
                        Id = gKey + "_" + winLbl,
                        Label = gName + " · " + winLbl,
                        ShortLabel = winLbl,
                        RemainingPct = m.RemainingPct,
                        ResetAt = m.ResetAt,
                        ResetLeft = m.ResetAt.HasValue ? (double?)Math.Max(0.0, m.ResetAt.Value - now) : null,
                        IsExhausted = m.IsExhausted
                    });
                }
                else
                {
                    if (m.RemainingPct.HasValue && (!existing.RemainingPct.HasValue || m.RemainingPct.Value < existing.RemainingPct.Value))
                        existing.RemainingPct = m.RemainingPct;
                    if (m.ResetAt.HasValue && (!existing.ResetAt.HasValue || m.ResetAt.Value < existing.ResetAt.Value))
                    {
                        existing.ResetAt = m.ResetAt;
                        existing.ResetLeft = Math.Max(0.0, m.ResetAt.Value - now);
                    }
                    existing.IsExhausted = existing.IsExhausted || m.IsExhausted;
                }
            }

            List<QuotaGroup> groups = new List<QuotaGroup>(gmap.Values);
            groups.Sort(delegate(QuotaGroup a, QuotaGroup b)
            {
                int oa = 99, ob = 99;
                foreach (KeyValuePair<string, QuotaGroup> kv in gmap)
                {
                    if (kv.Value == a) oa = gorder[kv.Key];
                    if (kv.Value == b) ob = gorder[kv.Key];
                }
                return oa.CompareTo(ob);
            });
            return BuildUsageFromGroups(home, nick, plan, sourceHint, groups);
        }

        private static string NormalizePlanName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "AGY";
            string s = raw.Trim();
            string lo = s.ToLowerInvariant();
            if (lo.Contains("ultra")) return "AI ULTRA";
            if (lo.Contains("pro") || lo.Contains("standard")) return "AI PRO";
            if (lo.Contains("free") || lo.Contains("legacy")) return "FREE";
            if (lo.Contains("enterprise")) return "ENT";
            return FormatUtil.Ellipsize(s.ToUpperInvariant(), 10);
        }
    }
}
