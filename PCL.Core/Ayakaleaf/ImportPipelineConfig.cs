using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf;

/// <summary>
///     文档导入管道（Word / Markdown → LaTeX，US1）接线与状态。
///     <para>
///     CLSI 的 ConversionManager 通过 LocalCommandRunner 直接调用镜像内的
///     <c>pandoc</c>（DOCKER_RUNNER 默认 false，无需沙箱同级容器），因此导入
///     管道不需要额外环境变量 —— 前提是运行时镜像内置 pandoc 3.x。镜像侧由
///     tools/runtime/pandoc/Dockerfile + tools/runtime/build-images.py 串联构建
///     保证；本类在服务启动后探测可用性，供设置页展示与日志排查。
///     </para>
/// </summary>
public static class ImportPipelineConfig
{
    private const string Container = AyakaleafService.OverleafContainer;

    /// <summary>容器内 pandoc 是否可用（null = 尚未探测）。</summary>
    public static bool? PandocAvailable { get; private set; }

    /// <summary>pandoc 版本首行（如 <c>pandoc 3.9</c>），不可用时为 null。</summary>
    public static string? PandocVersion { get; private set; }

    /// <summary>
    ///     服务启动后探测镜像内 pandoc。探测失败只影响导入功能，不阻断服务启动；
    ///     结果缓存于 <see cref="PandocAvailable"/>，设置页加载时调用刷新。
    /// </summary>
    public static async Task ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await WslcInterop.RunAsync($"exec {Container} pandoc --version", 15_000, ct)
                .ConfigureAwait(false);
            var firstLine = ((r.Stdout ?? "").Split('\n').FirstOrDefault() ?? "").Trim();
            if (r.IsSuccess && firstLine.StartsWith("pandoc", StringComparison.OrdinalIgnoreCase))
            {
                PandocVersion = firstLine;
                PandocAvailable = true;
                LogWrapper.Info($"[Ayakaleaf] 文档导入就绪：镜像内 {firstLine}");
            }
            else
            {
                PandocAvailable = false;
                PandocVersion = null;
                LogWrapper.Warn("[Ayakaleaf] 镜像内未找到 pandoc：Word/Markdown 文档导入不可用（请更新运行时镜像包）");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 探测异常（超时 / wslc 不可用等）不阻断启动，导入状态置为不可用
            PandocAvailable = false;
            PandocVersion = null;
            LogWrapper.Warn(ex, "[Ayakaleaf] pandoc 探测失败（Word/Markdown 文档导入状态未知）");
        }
    }
}
