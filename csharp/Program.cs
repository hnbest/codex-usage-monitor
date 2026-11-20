using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageMonitor
{
    static class Program
    {
        internal static readonly string VersionLabel = "版本 " + typeof(Program).Assembly.GetName().Version.ToString(3);

        [STAThread]
        static void Main()
        {
            // 启用 TLS 1.2 (3072) 以访问 OpenAI / ChatGPT HTTPS 接口
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "CodexUsageTray_Mutex", out createdNew))
            {
                if (!createdNew)
                    return;

                NativeWin.EnableDpiAwareness();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    Logger.Log("C# 版启动: DataDir=" + AppPaths.DataDir);
                    Application.Run(new MainForm());
                }
                catch (Exception ex)
                {
                    Logger.Log("FATAL: " + ex.ToString());
                    MessageBox.Show("Codex Usage Monitor 发生异常:\r\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
