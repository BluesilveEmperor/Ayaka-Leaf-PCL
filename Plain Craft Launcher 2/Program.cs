using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;

namespace PCL;

internal static class Program
{
    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        Console.WriteLine("Welcome to Plain Craft Launcher 2 Community Edition!");
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return app;
        };
        MainWindowService.Loading = static () =>
        {
            try
            {
                return new FormMain();
            }
            catch (Exception ex)
            {
                // 主窗体构造失败（典型如 XAML 解析错误：事件处理器签名与委托不匹配）时，
                // 若不在这里兜底，生命周期会继续推进到 WindowCreating 之后，
                // 表现为停留在启动图标、主窗体空白，且没有任何错误提示。
                ModBase.Log(ex, "主窗体创建失败，程序将退出");
                try
                {
                    ModMain.frmStart?.Close(TimeSpan.Zero);
                }
                catch
                {
                    // 启动画面关闭失败不应掩盖真正的错误信息
                }

                MessageBox.Show(
                    "启动器界面加载失败，程序将退出。\r\n\r\n" + ex.Message +
                    "\r\n\r\n详细信息已写入程序目录 PCL\\Log 下的日志文件。",
                    "AyakaLauncher 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit((int)ModBase.ProcessReturnValues.Exception);
                throw;
            }
        };
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }
}