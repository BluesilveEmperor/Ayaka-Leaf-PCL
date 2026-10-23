using System;
using System.Threading.Tasks;
using PCL.Core.App.IoC;
using PCL.Core.Logging;

namespace PCL.Core.App.Essentials;

/// <summary>
/// 遥测服务。AyakaLauncher 本地版已完全停用：不上报任何数据，仅保留空的 lifecycle 占位。
/// </summary>
[LifecycleScope("telemetry", "遥测")]
[LifecycleService(LifecycleState.Running)]
public static class TelemetryService
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

