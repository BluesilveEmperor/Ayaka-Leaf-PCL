using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Logging;
using PCL.Core.UI.Controls;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class Application
{
    public Application()
    {
        // 注册生命周期事件
        Lifecycle.When(LifecycleState.Loaded, _ApplicationStartup);
        Lifecycle.When(LifecycleState.WindowCreated, _ShowEnvironmentWarning);
        SessionEnding += _ApplicationSessionEnding;
    }

    // 开始
    private static void _ApplicationStartup()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 创建自定义跟踪监听器，用于检测是否存在 Binding 失败
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorTraceListener());
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            StartupValidation.EnsureWpfFont();

            // 检查参数调用
            var args = Basics.CommandLineArguments;
            if (args.Length > 0)
                if (args[0] == "--gpu")
                    // 调整显卡设置
                    try
                    {
                        ModMain.SetGPUPreference(args[1].Trim('"'));
                        Environment.Exit((int)ModBase.ProcessReturnValues.TaskDone);
                    }
                    catch (Exception)
                    {
                        Environment.Exit((int)ModBase.ProcessReturnValues.Fail);
                    }
                else if (args[0] == "update")
                    // 更新替换：update <旧进程pid> "<旧exe路径>" "<新exe路径>" <是否重启>
                    _HandleUpdateArguments(args);

            // 初始化文件结构
            Directory.CreateDirectory(ModBase.exePath + @"PCL\Pictures");
            Directory.CreateDirectory(ModBase.exePath + @"PCL\Musics");
            Directory.CreateDirectory(Path.Combine(ModBase.pathTemp, "Cache"));
            Directory.CreateDirectory(Path.Combine(ModBase.pathTemp, "Download"));
            Directory.CreateDirectory(ModBase.pathAppdata);

            // 设置 ToolTipService 默认值
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(DependencyObject),
                new FrameworkPropertyMetadata(100));
            Tooltip.Enable();

            // 设置初始窗口
            if (Config.Preference.ShowStartupLogo)
            {
                ModMain.frmStart = new SplashScreen(@"Images\icon.ico");
                ModMain.frmStart.Show(false, true);
            }

            // 设置初始化
            _ = Config.Debug.Enabled;
            _ = Config.Debug.AnimationSpeed;
            _ = Config.Network.HttpProxy.CustomAddress;
            _ = Config.Network.HttpProxy.CustomUsername;
            _ = Config.Network.HttpProxy.Type;
            _ = Config.Download.ThreadLimit;
            _ = Config.Download.SpeedLimit;
            _ = Config.Preference.Font;
            var updateBranchCfg = Config.Update.UpdateChannelConfig;
            if (updateBranchCfg.IsDefault())
                updateBranchCfg.SetValue(ModBase.versionBaseName.Contains("beta")
                    ? Core.App.UpdateChannel.Beta
                    : Core.App.UpdateChannel.Release);

            // 删除旧日志
            for (var i = 1; i <= 5; i++)
            {
                var oldLogFile = $@"{ModBase.exePath}PCL\Log-CE{i}.log";
                if (File.Exists(oldLogFile))
                    File.Delete(oldLogFile);
            }

            // 计时
            ModBase.Log("[Start] 第一阶段加载用时：" + (TimeUtils.GetTimeTick() - ModBase.applicationStartTick) + " ms");
            ModBase.applicationStartTick = TimeUtils.GetTimeTick();
            ModAnimation.AniControlEnabled += 1;
        }
        catch (Exception ex)
        {
            var filePath = Basics.ExecutablePath;
            var summary = Lang.Text("Application.InitializationError.Path",
                string.IsNullOrEmpty(filePath)
                    ? Lang.Text("Application.InitializationError.PathUnavailable")
                    : filePath);
            MessageBox.Show(
                ExceptionDetails.Compose(summary, ex),
                Lang.Text("SystemDialog.Startup.InitializationTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            FormMain.EndProgramForce(ModBase.ProcessReturnValues.Exception);
        }
    }

    /// <summary>
    ///     处理更新替换参数：update <旧进程pid> "<旧exe路径>" "<新exe路径>" <是否重启>。
    ///     由旧版本进程以隐藏窗口方式启动，等待其退出后把新 exe 覆盖到原位置，全程日志写入 PCL\update.log。
    /// </summary>
    private static void _HandleUpdateArguments(string[] args)
    {
        var logFile = ModBase.exePath + @"PCL\update.log";

        void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(ModBase.exePath + "PCL");
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
            }
            catch
            {
                // 日志失败不影响更新流程
            }
        }

        try
        {
            var pid = int.Parse(args[1]);
            var oldPath = args[2].Trim('"');
            var newPath = args[3].Trim('"');
            var restart = args.Length > 4 && bool.TryParse(args[4], out var shouldRestart) && shouldRestart;
            Log($"更新程序启动：pid={pid}，旧={oldPath}，新={newPath}，restart={restart}");

            // 等待旧进程退出（≤10s；超时继续尝试，不破坏现场）
            try
            {
                using var oldProcess = Process.GetProcessById(pid);
                if (oldProcess.WaitForExit(10000))
                    Log($"旧进程 {pid} 已退出");
                else
                    Log($"警告：旧进程 {pid} 在 10 秒内未退出，继续尝试替换");
            }
            catch (ArgumentException)
            {
                Log($"旧进程 {pid} 已不存在");
            }

            // 替换文件（重试 3 次，间隔 500ms）
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(newPath, oldPath, true);
                    Log($"文件替换成功（第 {attempt} 次尝试）");
                    break;
                }
                catch (Exception ex)
                {
                    Log($"文件替换失败（第 {attempt} 次尝试）：{ex.Message}");
                    if (attempt >= 3)
                    {
                        Log("替换重试次数耗尽，更新失败");
                        MessageBox.Show(
                            "更新替换文件失败，请手动将新版本文件覆盖到原位置。\r\n" + ex.Message,
                            "AyakaLauncher 更新", MessageBoxButton.OK, MessageBoxImage.Error);
                        Environment.Exit((int)ModBase.ProcessReturnValues.Fail);
                    }

                    System.Threading.Thread.Sleep(500);
                }
            }

            if (restart)
            {
                Log($"重启程序：{oldPath}");
                Process.Start(new ProcessStartInfo(oldPath));
            }

            Environment.Exit((int)ModBase.ProcessReturnValues.TaskDone);
        }
        catch (Exception ex)
        {
            Log("更新流程异常：" + ex);
            Environment.Exit((int)ModBase.ProcessReturnValues.Fail);
        }
    }

    // 检测异常环境
    private static void _ShowEnvironmentWarning()
    {
        var problemList = new List<string>();
        var currentOsVersion = NtInterop.GetCurrentOsVersion();
        if (currentOsVersion.Build < 17763)
            problemList.Add(Lang.Text("Application.EnvironmentWarning.WindowsVersion"));
        if (SystemInfo.Is32BitSystem)
            problemList.Add(Lang.Text("Application.EnvironmentWarning.System32Bit"));
        if (ModBase.exePath.Contains(Path.GetTempPath()) || ModBase.exePath.Contains(@"AppData\Local\Temp\"))
            problemList.Add(Lang.Text("Application.EnvironmentWarning.TempFolder"));
        if (ModBase.exePath.ContainsF("wechat_files", true) || ModBase.exePath.ContainsF("WeChat Files", true) ||
            ModBase.exePath.ContainsF("Tencent Files", true))
            problemList.Add(Lang.Text("Application.EnvironmentWarning.SocialSoftwareFolder"));
        if (problemList.Count == 0) return;

        ModMain.MyMsgBox(
            Lang.Text("Application.EnvironmentWarning.Message", problemList.Join("\r\n")),
            Lang.Text("Application.EnvironmentWarning.Title"),
            Lang.Text("Application.EnvironmentWarning.IKnow"),
            isWarn: true);
    }

    // 结束
    private static void _ApplicationSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        ModMain.frmMain.EndProgram(false);
    }

    /**
     * Error handling for unhandled exceptions
     */
    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (ModBase.isProgramEnded) return;

            ModBase.FeedbackInfo();

            var detail = e.Exception.ToString();

            // Automatic error analysis for environment issues
            if (detail.Contains("System.Windows.Threading.Dispatcher.Invoke") ||
                detail.Contains("MS.Internal.AppModel.ITaskbarList.HrInit") ||
                detail.Contains("未能加载文件或程序集"))
            {
                ModBase.OpenWebsite("https://get.dot.net/10");
                LogWrapper.Error(
                    e.Exception,
                    Lang.Text("SystemDialog.Startup.DotNetRuntimeOutdated.Message"));
            }
            else
            {
                LogWrapper.Error(e.Exception, Lang.Text("SystemDialog.Error.Unexpected.Message"));
            }
        }
        catch
        {
            // Equivalent to On Error Resume Next for safety in the global handler
        }
    }

    // Win32 API declaration for DLL directory configuration
    [DllImport("kernel32", EntryPoint = "SetDllDirectoryA", CharSet = CharSet.Ansi)]
    private static extern bool _SetDllDirectory(string lpPathName);
    // 切换窗口

    // 控件模板事件
    private void _MyIconButtonClick(object sender, EventArgs e)
    {
    }

    // 自定义监听器类
    public class BindingErrorTraceListener : TraceListener
    {
        public override void Write(string message)
        {
            ModBase.Log($"警告，检测到 Binding 失败：{message}");
        }

        public override void WriteLine(string message)
        {
            ModBase.Log($"警告，检测到 Binding 失败：{message}");
        }
    }
}