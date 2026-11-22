using System.Windows;

namespace VisionBlocks.Studio;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 2026-11-22：靶子做进同一个 exe（单个 exe 发布时没法另带 BenchTarget.exe）。编辑器用「自己 --bench-target 参数」启动靶子。
        if (args.Length > 0 && args[0] == "--bench-target") return BenchTarget.Program.Main(args[1..]);

        L.Load();   // 读上次选的界面语言（默认中文）
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, L.T("appError"), MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        Ui.ApplyTheme(AppSettings.Current.Theme);   // 建窗口之前先定好浅色 / 深色
        return app.Run(new MainWindow(args.FirstOrDefault()));
    }
}
