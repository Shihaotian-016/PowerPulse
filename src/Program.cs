using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PowerPulse
{
    internal static class Program
    {
        private static string LogPath
        {
            get
            {
                return Path.Combine(Application.StartupPath, "data", "error.log");
            }
        }

        private static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch
            {
            }
        }

        [STAThread]
        private static void Main()
        {
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log("THREAD: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("DOMAIN: " + e.ExceptionObject);
            };
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\PowerPulse.SingleInstance", out created))
            {
                if (!created)
                {
                    // I1: 已有实例运行时, 通知其唤起主窗口后退出
                    try
                    {
                        using (System.Threading.EventWaitHandle h =
                            System.Threading.EventWaitHandle.OpenExisting(@"Local\PowerPulse.ShowEvent"))
                        {
                            h.Set();
                        }
                    }
                    catch
                    {
                    }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }
}
