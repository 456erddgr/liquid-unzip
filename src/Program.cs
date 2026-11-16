using System;
using System.IO;
using System.Windows;

namespace LiquidUnzip
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // 卸载：删除自身文件、快捷方式与注册表
            if (args.Length > 0 && args[0] == "--uninstall")
            {
                Uninstall();
                return;
            }

            // 单实例：已有窗口就激活它
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "LiquidUnzip.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在跑：把命令行里的压缩包转交给它（简化处理：直接退出）
                    return;
                }

                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnMainWindowClose
                };

                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show(e.Exception.Message, "Liquid Unzip 出错",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };

                var w = new MainWindow();
                app.MainWindow = w;

                // 支持拖到 exe 图标上启动
                if (args.Length > 0 && File.Exists(args[0]) && Extractor.IsArchive(args[0]))
                {
                    w.Loaded += (s, e) => w.BeginExtractFromArgs(args[0]);
                }

                app.Run(w);
            }
        }

        static void Uninstall()
        {
            var res = MessageBox.Show(
                "确定要卸载 Liquid Unzip 吗？\n\n解压历史记录会一并删除，已解压的文件不受影响。",
                "卸载 Liquid Unzip", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (res != MessageBoxResult.OK) return;

            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            try
            {
                // 快捷方式
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Delete(Path.Combine(desktop, "Liquid Unzip.lnk"));
                string prog = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                Delete(Path.Combine(prog, "Liquid Unzip", "Liquid Unzip.lnk"));
                try { Directory.Delete(Path.Combine(prog, "Liquid Unzip"), true); } catch { }

                // 注册表
                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LiquidUnzip", false);
                }
                catch { }
                try
                {
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                        @"Software\Classes\*\shell\LiquidUnzip", false);
                }
                catch { }

                // 历史
                string hist = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiquidUnzip");
                try { if (Directory.Exists(hist)) Directory.Delete(hist, true); } catch { }

                // 自身：延迟删除（进程退出后才能删掉自己）
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" +
                                Path.Combine(dir, "LiquidUnzip.exe") + "\" \"" +
                                Path.Combine(dir, "7z.exe") + "\" \"" +
                                Path.Combine(dir, "7z.dll") + "\" & rmdir \"" + dir + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载时出错：" + ex.Message, "Liquid Unzip");
            }
        }

        static void Delete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}
