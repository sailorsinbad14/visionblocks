using System.Windows;

namespace BenchTarget;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var opt = TargetOptions.Parse(args);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Window w = opt.Scene == "inventory" ? new InventoryWindow(opt) : new MainWindow(opt);
        return app.Run(w);
    }
}
