using System;
using System.Threading.Tasks;
using PCL.Core.App.IoC;
using PCL.Core.Logging;

namespace PCL.Core.App.Essentials;

/// <summary>
/// 遥测服务。AyakaLauncher 本地版已完全停用：不上报任何数据。
/// 注意：必须保持可实例化（sealed 非 static），Lifecycle IoC 会通过反射创建此服务。
/// </summary>
[LifecycleScope("telemetry", "遥测")]
[LifecycleService(LifecycleState.Running)]
public sealed partial class TelemetryService
{
    /// <summary>
    /// 兼容旧调用点：本地版不上报，直接忽略。
    /// </summary>
    public static void ReportException(Exception ex, string plain, LogLevel level)
    {
        // 本地版不上报任何异常数据
    }

    [LifecycleStart]
    private static Task _StartAsync()
    {
        // 本地版不上报任何遥测数据
        return Task.CompletedTask;
    }
}

