using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.Logging;
using PCL.Core.Users;

namespace PCL.Core.Ayakaleaf;

/// <summary>同步方向（plan R10：v1 固定双向）。</summary>
public enum SyncDirection
{
    /// <summary>双向同步。</summary>
    Bidirectional,
}

/// <summary>冲突策略（FR-017：v1 固定保留双方版本）。</summary>
public enum SyncConflictPolicy
{
    /// <summary>保留双方版本并显式提示。</summary>
    KeepBoth,
}

/// <summary>同步通知类别。</summary>
public enum SyncNoticeKind
{
    /// <summary>同步冲突（已按约定保留双方版本）。</summary>
    Conflict,

    /// <summary>同步出错（本轮失败，下轮自动重试）。</summary>
    Error,

    /// <summary>常规信息（如二进制文件修改不支持推送的说明）。</summary>
    Info,
}

/// <summary>
/// 本地文件夹同步绑定（US11 / plan R10 SyncBinding）。
/// 一个本地目录同一时刻至多绑定一个项目，反之亦然（<see cref="FolderSyncService"/> 绑定时校验）。
/// </summary>
public sealed class SyncBinding
{
    /// <summary>绑定标识（GUID N 格式）。</summary>
    public string BindingId { get; set; } = "";

    /// <summary>本地目录（Windows 绝对路径）。</summary>
    public string LocalPath { get; set; } = "";

    /// <summary>Overleaf 项目标识（24 位十六进制 ObjectId）。</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>项目显示名（仅用于界面展示与通知文案）。</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>容器内项目目录（信息字段：当前同步经 HTTP/DB 通道，不依赖该路径）。</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>同步方向。</summary>
    public SyncDirection Direction { get; set; } = SyncDirection.Bidirectional;

    /// <summary>冲突策略。</summary>
    public SyncConflictPolicy ConflictPolicy { get; set; } = SyncConflictPolicy.KeepBoth;

    /// <summary>是否暂停（暂停期间不监听、不同步）。</summary>
    public bool Paused { get; set; }

    /// <summary>最近一次成功同步时间（UTC）。</summary>
    public DateTime LastSyncAtUtc { get; set; }
}

/// <summary>同步通知（冲突 / 错误 / 信息；UI 订阅后转中文提示）。</summary>
public sealed record SyncNotice(
    SyncNoticeKind Kind,
    string BindingId,
    string ProjectName,
    string Message);

/// <summary>一次同步周期的统计结果。</summary>
public sealed record SyncCycleStats(
    int Pulled,
    int Pushed,
    int Conflicts,
    int Errors,
    string? Error)
{
    /// <summary>空统计（跳过或无可同步内容）。</summary>
    public static readonly SyncCycleStats Empty = new(0, 0, 0, 0, null);
}

/// <summary>
/// 本地文件夹双向同步服务（US11 / FR-016 / FR-017，plan R10）。
/// <para>
/// 架构（HTTP + 容器内执行混合通道，2026-11 对镜像内服务实证）：
/// </para>
/// <para>
/// <b>拉取（容器 → 本地）</b>：会话 <c>GET /Project/:id/download/zip</c> 下载项目 zip
/// （该路由在打包前先执行 <c>DocumentUpdaterHandler.flushProjectToMongo</c>，
/// 编辑中的未落盘内容也会包含，无需手动 flush）；与本地清单
/// <c>.ayaka-sync.json</c>（记录每个文件上次同步时的 SHA256）三方比对后应用。
/// </para>
/// <para>
/// <b>推送（本地 → 容器）</b>：修改的文本文档经 doc-updater 内部 HTTP 写入
/// （<c>POST http://127.0.0.1:3003/project/:pid/doc/:doc_id</c>，setDoc 契约
/// <c>{lines, source}</c>，容器内 curl 执行；与编辑器实时写入同通道，
/// 已连接的协作者会收到实时更新）；文档 <c>doc_id</c> 与根目录 ID 由
/// mongo 只读查询（<c>wslc exec mongo mongosh</c> 遍历 <c>rootFolder</c> 树）获得；
/// 新增文件经会话 <c>POST /Project/:id/upload</c>（multipart 字段 <c>qqfile</c>，
/// 表单 <c>name</c>/<c>relativePath</c>，查询 <c>folder_id</c>）上传。
/// </para>
/// <para>
/// <b>冲突（FR-017）</b>：同一文件双端都改过且内容不同 → 以远端为当前版本落盘，
/// 本地修改保存为冲突副本 <c>名称.conflict-YYYYMMDD-HHMMSS.扩展名</c>，
/// 双方版本均保留且显式通知，绝不静默覆盖。
/// </para>
/// <para>
/// <b>v1 范围限制</b>：删除/重命名不主动传播（无 HTTP 删除实体的路由）；
/// 本地删除的文件远端保留、远端删除的文件本地保留；已存在二进制文件的修改
/// 不支持推送（请在工作台替换），新增二进制文件可正常上传。
/// </para>
/// </summary>
public static partial class FolderSyncService
{
    /// <summary>同步清单文件名（存于绑定目录根部，记录每文件上次同步的 SHA256）。</summary>
    private const string ManifestFileName = ".ayaka-sync.json";

    /// <summary>冲突副本命名：名称.conflict-YYYYMMDD-HHMMSS.扩展名。</summary>
    private const string ConflictInfix = ".conflict-";

    /// <summary>容器侧同步负载目录（doc-updater setDoc JSON / 上传中转）。</summary>
    private const string ContainerWorkDir = "/tmp/ayaka-sync";

    /// <summary>远端轮询间隔（秒）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>本地变更防抖窗口（FileSystemWatcher 事件聚合）。</summary>
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromSeconds(1.5);

    /// <summary>错误通知节流：同一绑定至少间隔 10 分钟才重复提示同类错误。</summary>
    private static readonly TimeSpan ErrorNoticeThrottle = TimeSpan.FromMinutes(10);

    /// <summary>项目 ObjectId 校验。</summary>
    [GeneratedRegex(@"^[0-9a-fA-F]{24}$")]
    private static partial Regex ProjectIdRegex();

    /// <summary>冲突副本匹配（含无扩展名形态）。</summary>
    [GeneratedRegex(@"\.conflict-\d{8}-\d{6}(\.[^.]*)?$")]
    private static partial Regex ConflictCopyRegex();

    private static readonly HttpClient Http = CreateClient();

    /// <summary>同步通知（冲突 / 错误 / 信息）。后台线程触发，订阅方需自行调度到 UI 线程。</summary>
    public static event Action<SyncNotice>? NoticeReceived;

    /// <summary>绑定列表（配置持久化，详见 <see cref="Config"/> 的 AyakaleafSyncBindings 项）。</summary>
    public static IReadOnlyList<SyncBinding> GetBindings() => Config.Ayakaleaf.SyncBindings;

    /// <summary>按标识取绑定；不存在返回 null。</summary>
    public static SyncBinding? GetBinding(string bindingId) =>
        Config.Ayakaleaf.SyncBindings.FirstOrDefault(b => b.BindingId == bindingId);

    /// <summary>按项目标识取绑定；未绑定返回 null。</summary>
    public static SyncBinding? FindBindingByProject(string projectId) =>
        Config.Ayakaleaf.SyncBindings.FirstOrDefault(b => b.ProjectId == projectId);

    /// <summary>按本地路径取绑定（完全一致才命中）；未绑定返回 null。</summary>
    public static SyncBinding? FindBindingByLocalPath(string localPath)
    {
        var normalized = NormalizeLocalPath(localPath);
        return Config.Ayakaleaf.SyncBindings.FirstOrDefault(b =>
            string.Equals(NormalizeLocalPath(b.LocalPath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false, // 本地服务，绝不能走系统代理
            UseCookies = false, // 会话 Cookie 按请求手动附加
            AllowAutoRedirect = false, // 会话失效时的 /login 跳转按失败处理，不跟随
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    // ---------------- 运行时 ----------------

    private sealed class BindingRuntime
    {
        public required SyncBinding Binding;
        public FileSystemWatcher? Watcher;
        public Timer? Debounce;
        public Timer? Poll;
        public readonly SemaphoreSlim CycleLock = new(1, 1);
        public DateTime LastErrorNoticeUtc = DateTime.MinValue;
        public string? LastErrorMessage;
    }

    private static readonly object RuntimeGate = new();
    private static readonly Dictionary<string, BindingRuntime> Runtimes = [];
    private static bool _started;

    /// <summary>配置列表读改写互斥（后台同步周期与界面操作并发更新绑定）。</summary>
    private static readonly object ConfigWriteGate = new();

    /// <summary>
    /// 启动全部已保存绑定的监听与轮询（幂等；应用启动后调用一次）。
    /// 服务栈未运行时周期自动跳过，服务就绪后自动恢复同步。
    /// </summary>
    public static void StartAll()
    {
        lock (RuntimeGate)
        {
            if (_started) return;
            _started = true;
            foreach (var binding in Config.Ayakaleaf.SyncBindings)
                StartRuntime(binding);
        }
    }

    /// <summary>停止全部监听（应用退出时调用；绑定配置保留）。</summary>
    public static void StopAll()
    {
        lock (RuntimeGate)
        {
            foreach (var runtime in Runtimes.Values)
                DisposeRuntime(runtime);
            Runtimes.Clear();
            _started = false;
        }
    }

    private static BindingRuntime StartRuntime(SyncBinding binding)
    {
        var runtime = new BindingRuntime { Binding = binding };

        // 远端轮询：容器内变更（编辑器保存）宿主无法挂监听，轮询是最可靠通道（plan R10）
        runtime.Poll = new Timer(
            _ => ScheduleCycle(runtime), null, PollInterval, PollInterval);

        // 本地监听：准实时感知本地编辑器保存
        try
        {
            var watcher = new FileSystemWatcher(binding.LocalPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 256 * 1024, // 大目录批量保存时降低丢事件概率
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, _) => TriggerDebouncedCycle(runtime);
            watcher.Created += (_, _) => TriggerDebouncedCycle(runtime);
            watcher.Deleted += (_, _) => TriggerDebouncedCycle(runtime);
            watcher.Renamed += (_, _) => TriggerDebouncedCycle(runtime);
            watcher.Error += (_, e) =>
            {
                LogWrapper.Warn($"[Ayakaleaf] 本地同步监听异常（{binding.ProjectName}）：{e.GetException().Message}");
                TriggerDebouncedCycle(runtime); // 缓冲区溢出等：立即做一次全量比对兜底
            };
            watcher.EnableRaisingEvents = true;
            runtime.Watcher = watcher;
        }
        catch (Exception ex)
        {
            // 目录被删除/不可访问等：监听不可用但轮询仍可工作（拉取不依赖本地监听）
            LogWrapper.Warn($"[Ayakaleaf] 本地同步监听启动失败（{binding.ProjectName}）：{ex.Message}");
        }

        Runtimes[binding.BindingId] = runtime;
        LogWrapper.Info($"[Ayakaleaf] 本地同步已启动：{binding.LocalPath} ↔ {binding.ProjectName}（{binding.ProjectId}）");
        return runtime;
    }

    /// <summary>本地事件防抖：聚合连续保存，静置后触发一轮同步。</summary>
    private static void TriggerDebouncedCycle(BindingRuntime runtime)
    {
        lock (runtime)
        {
            runtime.Debounce?.Dispose();
            runtime.Debounce = new Timer(
                _ => ScheduleCycle(runtime), null, WatcherDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>调度一轮同步：已在执行则跳过（不排队，下一轮轮询自然补上）。</summary>
    private static void ScheduleCycle(BindingRuntime runtime)
    {
        _ = Task.Run(async () =>
        {
            if (!await runtime.CycleLock.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                await RunCycleAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "[Ayakaleaf] 本地同步周期执行失败");
                RaiseError(runtime, "本地同步执行失败：" + ex.Message);
            }
            finally
            {
                runtime.CycleLock.Release();
            }
        });
    }

    /// <summary>
    /// 立即执行一轮完整同步（绑定管理界面的「立即同步」按钮）。
    /// 等待进行中的周期结束后执行；暂停中的绑定执行后将恢复暂停状态。
    /// </summary>
    public static async Task<SyncCycleStats> SyncNowAsync(string bindingId, CancellationToken ct = default)
    {
        BindingRuntime? runtime;
        lock (RuntimeGate)
            Runtimes.TryGetValue(bindingId, out runtime);
        if (runtime is null)
            throw new InvalidOperationException("同步绑定不存在或已解除。");

        await runtime.CycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunCycleAsync(runtime, ct).ConfigureAwait(false);
        }
        finally
        {
            runtime.CycleLock.Release();
        }
    }

    // ---------------- 绑定管理 ----------------

    /// <summary>
    /// 建立绑定：校验（路径存在且可写、单目录单项目、项目可达、容器执行通道可用）后
    /// 持久化并启动监听，随后立即执行首轮同步把项目内容拉到本地。
    /// </summary>
    /// <exception cref="InvalidOperationException">校验失败（中文原因）。</exception>
    public static async Task<SyncBinding> BindAsync(
        string localPath, string projectId, string projectName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !Directory.Exists(localPath))
            throw new InvalidOperationException("绑定目录不存在，请先创建本地文件夹。");
        var normalizedPath = NormalizeLocalPath(localPath);
        if (string.IsNullOrWhiteSpace(projectId) || !ProjectIdRegex().IsMatch(projectId))
            throw new InvalidOperationException("项目标识格式非法，无法绑定。");

        // 一个本地目录/项目同一时刻至多一个绑定；目录也不允许互相嵌套（监听会重叠）
        foreach (var existing in Config.Ayakaleaf.SyncBindings)
        {
            if (string.Equals(existing.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"项目已绑定到 {existing.LocalPath}，请先解除原绑定。");
            var existingPath = NormalizeLocalPath(existing.LocalPath);
            if (string.Equals(existingPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"目录已绑定到项目「{existing.ProjectName}」，请先解除原绑定。");
            if (IsAncestorPath(existingPath, normalizedPath) || IsAncestorPath(normalizedPath, existingPath))
                throw new InvalidOperationException("绑定目录与既有绑定目录嵌套，无法绑定（监听会互相干扰）。");
        }

        // 服务栈须在运行（mongo/web 容器可达），否则后续每轮同步都会失败
        if (!WslcInterop.IsAvailable)
            throw new InvalidOperationException("WSL 容器运行时不可用，无法建立同步。");
        if (!await AyakaleafService.IsContainerRunningAsync(AyakaleafService.OverleafContainer).ConfigureAwait(false))
            throw new InvalidOperationException("Ayakaleaf 服务未在运行，请先启动服务再绑定。");

        // 项目可达性：mongo 树查询（同时验证项目存在与查询通道）
        var tree = await GetRemoteTreeAsync(projectId, ct).ConfigureAwait(false);
        if (tree is null)
            throw new InvalidOperationException("项目不存在或无法读取项目文件树，请确认项目标识。");

        // 容器执行通道往返一致性校验（FR-017 边界：特殊字符路径 / 通道可用性）
        if (!await WslcInterop.VerifyContainerPathRoundtripAsync(
                AyakaleafService.OverleafContainer, "/tmp/ayaka-sync-bind-probe.txt", ct).ConfigureAwait(false))
            throw new InvalidOperationException("容器执行通道不可用（写入校验失败），无法建立同步。");

        var binding = new SyncBinding
        {
            BindingId = Guid.NewGuid().ToString("N"),
            LocalPath = normalizedPath,
            ProjectId = projectId.ToLowerInvariant(),
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? projectId : projectName,
            Direction = SyncDirection.Bidirectional,
            ConflictPolicy = SyncConflictPolicy.KeepBoth,
            Paused = false,
        };
        SaveBinding(binding);

        // 首轮同步：把项目内容物化到本地（目录非空时内容不同的同名文件按冲突处理）
        BindingRuntime? runtime;
        lock (RuntimeGate)
        {
            _started = true;
            runtime = StartRuntime(binding);
        }
        await runtime.CycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunCycleAsync(runtime, ct).ConfigureAwait(false);
        }
        finally
        {
            runtime.CycleLock.Release();
        }
        return binding;
    }

    /// <summary>解除绑定：停止监听并删除配置（本地文件与清单保留）。</summary>
    public static void Unbind(string bindingId)
    {
        BindingRuntime? runtime;
        lock (RuntimeGate)
        {
            if (Runtimes.Remove(bindingId, out runtime))
                DisposeRuntime(runtime);
        }
        lock (ConfigWriteGate)
        {
            var list = Config.Ayakaleaf.SyncBindings.Where(b => b.BindingId != bindingId).ToList();
            Config.Ayakaleaf.SyncBindings = list;
        }
        LogWrapper.Info($"[Ayakaleaf] 已解除本地同步绑定：{bindingId}");
    }

    /// <summary>暂停 / 恢复绑定（配置持久化，暂停期间不监听、不同步）。</summary>
    public static void SetPaused(string bindingId, bool paused)
    {
        var binding = GetBinding(bindingId) ?? throw new InvalidOperationException("同步绑定不存在或已解除。");
        binding.Paused = paused;
        SaveBinding(binding);
        lock (RuntimeGate)
        {
            if (Runtimes.TryGetValue(bindingId, out var runtime))
                runtime.Binding = binding;
        }
        LogWrapper.Info($"[Ayakaleaf] 本地同步{(paused ? "已暂停" : "已恢复")}：{binding.ProjectName}");
    }

    /// <summary>更新绑定（含最近同步时间）并持久化。总是写入新列表实例以强制落盘。</summary>
    private static void SaveBinding(SyncBinding binding)
    {
        lock (ConfigWriteGate)
        {
            var list = Config.Ayakaleaf.SyncBindings;
            var index = list.FindIndex(b => b.BindingId == binding.BindingId);
            var copy = list.ToList();
            if (index >= 0) copy[index] = binding;
            else copy.Add(binding);
            Config.Ayakaleaf.SyncBindings = copy;
        }
    }

    private static void DisposeRuntime(BindingRuntime runtime)
    {
        lock (runtime)
        {
            runtime.Debounce?.Dispose();
            runtime.Debounce = null;
        }
        runtime.Poll?.Dispose();
        runtime.Poll = null;
        if (runtime.Watcher is not null)
        {
            runtime.Watcher.EnableRaisingEvents = false;
            runtime.Watcher.Dispose();
            runtime.Watcher = null;
        }
    }

    // ---------------- 同步周期 ----------------

    private static async Task<SyncCycleStats> RunCycleAsync(BindingRuntime runtime, CancellationToken ct)
    {
        var binding = runtime.Binding;
        if (binding.Paused) return SyncCycleStats.Empty;

        // 服务栈未运行：安静跳过（轮询 30s 一次，避免日志与通知刷屏）
        if (!await AyakaleafService.IsContainerRunningAsync(AyakaleafService.OverleafContainer).ConfigureAwait(false))
            return SyncCycleStats.Empty;

        ct.ThrowIfCancellationRequested();
        var pulled = 0;
        var pushed = 0;
        var conflicts = 0;
        var errors = 0;

        // 1) 会话登录（本地用户对应账号，与工作台同一账号体系）
        string sid;
        try
        {
            var userName = LocalUserSession.Instance.Current?.UserName ?? string.Empty;
            var account = await AyakaleafAccountService.GetOrCreateAccountAsync(userName).ConfigureAwait(false);
            sid = await AyakaleafAccountService.LoginAsync(account).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseError(runtime, "本地同步登录失败：" + ex.Message);
            return new SyncCycleStats(0, 0, 0, 1, ex.Message);
        }

        // 2) 拉取：下载项目 zip（路由打包前已 flush 文档缓存，内容必为最新）
        byte[] zipBytes;
        try
        {
            zipBytes = await DownloadProjectZipAsync(sid, binding.ProjectId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RaiseError(runtime, "下载项目内容失败：" + ex.Message);
            return new SyncCycleStats(0, 0, 0, 1, ex.Message);
        }

        var manifest = LoadManifest(binding);
        var remoteFiles = ExtractZipToTemp(zipBytes, out var tempDir);
        try
        {
            // 3) 本地扫描 + 三方比对 → 应用远端变更 / 收集推送候选
            var localFiles = ScanLocalFiles(binding.LocalPath);
            var pushCandidates = new List<string>(); // 相对路径

            foreach (var (relPath, remoteAbs) in remoteFiles)
            {
                var manifestEntry = manifest.Files.FirstOrDefault(f => f.Path == relPath);
                var localAbs = Path.Combine(binding.LocalPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                var hasLocal = File.Exists(localAbs);
                var localSha = hasLocal ? HashLocalFile(localAbs, relPath) : null;
                var remoteSha = HashFile(remoteAbs, relPath);

                if (manifestEntry is null)
                {
                    // 首见：绑定后新出现的远端文件
                    if (!hasLocal)
                    {
                        CopyFile(remoteAbs, localAbs);
                        AddManifestEntry(manifest, relPath, remoteSha);
                        pulled++;
                    }
                    else if (localSha == remoteSha)
                    {
                        AddManifestEntry(manifest, relPath, localSha);
                    }
                    else
                    {
                        // 绑定时本地目录已有同名文件且内容不同：按冲突保留双方版本（FR-017）
                        MakeConflictCopy(localAbs);
                        CopyFile(remoteAbs, localAbs);
                        AddManifestEntry(manifest, relPath, remoteSha);
                        conflicts++;
                        RaiseNotice(SyncNoticeKind.Conflict, runtime,
                            $"「{binding.ProjectName}」绑定目录中已存在 {relPath} 且与项目内容不同：已把本地原文件保存为冲突副本，项目版本覆盖同名文件。");
                    }
                    continue;
                }

                var localChanged = localSha is not null && localSha != manifestEntry.Sha256;
                var remoteChanged = remoteSha != manifestEntry.Sha256;

                if (remoteChanged && (localSha is null || localChanged))
                {
                    if (localSha is null)
                    {
                        // 本地已删除、远端有修改：远端版本落盘（删除不传播，修改优先）
                        CopyFile(remoteAbs, localAbs);
                        manifestEntry.Sha256 = remoteSha;
                        pulled++;
                    }
                    else
                    {
                        // 双端同改且内容不同：冲突 → 保留双方版本（FR-017）
                        MakeConflictCopy(localAbs);
                        CopyFile(remoteAbs, localAbs);
                        manifestEntry.Sha256 = remoteSha;
                        conflicts++;
                        RaiseNotice(SyncNoticeKind.Conflict, runtime,
                            $"「{relPath}」在本地与 Overleaf 两端都被修改：本地修改已保存为冲突副本，远端版本覆盖当前文件，请对照两份内容手工合并。");
                    }
                }
                else if (remoteChanged)
                {
                    // 远端改、本地未改：直接应用
                    CopyFile(remoteAbs, localAbs);
                    manifestEntry.Sha256 = remoteSha;
                    pulled++;
                }
                else if (localChanged)
                {
                    // 本地改、远端未改：推送候选
                    pushCandidates.Add(relPath);
                }
                // 双端均未改：无需处理
            }

            // 本地新增（远端与清单都没有）：推送候选
            foreach (var relPath in localFiles.Keys)
            {
                if (manifest.Files.Any(f => f.Path == relPath)) continue;
                if (remoteFiles.ContainsKey(relPath)) continue;
                pushCandidates.Add(relPath);
            }

            // 4) 推送
            if (pushCandidates.Count > 0)
            {
                var tree = await GetRemoteTreeAsync(binding.ProjectId, ct).ConfigureAwait(false);
                if (tree is null)
                {
                    RaiseError(runtime, "读取项目文件树失败，本轮推送已跳过（拉取结果已生效）。");
                    errors++;
                }
                else
                {
                    string? csrf = null;
                    foreach (var relPath in pushCandidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        var localAbs = Path.Combine(
                            binding.LocalPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                        try
                        {
                            if (IsDocFile(relPath))
                            {
                                if (tree.Docs.TryGetValue(relPath, out var docId))
                                {
                                    await PushDocAsync(binding.ProjectId, docId, localAbs, ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    // 远端不存在该文档（新建或远端已删）：走上传补建目录
                                    csrf ??= await GetSessionCsrfAsync(sid, ct).ConfigureAwait(false)
                                        ?? throw new InvalidOperationException("获取会话令牌失败");
                                    await UploadFileAsync(sid, csrf, binding.ProjectId,
                                        tree.RootFolderId, relPath, localAbs, ct).ConfigureAwait(false);
                                }
                                pushed++;
                                AddManifestEntry(manifest, relPath, HashLocalFile(localAbs, relPath));
                            }
                            else
                            {
                                if (tree.Files.ContainsKey(relPath) || tree.Docs.ContainsKey(relPath))
                                {
                                    // 已存在二进制文件的修改无法经上传接口替换（DuplicateNameError）：
                                    // 明确告知用户在工作台手动替换，绝不静默丢弃
                                    RaiseNotice(SyncNoticeKind.Info, runtime,
                                        $"二进制文件「{relPath}」的本地修改不支持自动同步，请在工作台项目页上传替换该文件。");
                                    errors++;
                                    continue;
                                }
                                csrf ??= await GetSessionCsrfAsync(sid, ct).ConfigureAwait(false)
                                    ?? throw new InvalidOperationException("获取会话令牌失败");
                                await UploadFileAsync(sid, csrf, binding.ProjectId,
                                    tree.RootFolderId, relPath, localAbs, ct).ConfigureAwait(false);
                                pushed++;
                                AddManifestEntry(manifest, relPath, HashLocalFile(localAbs, relPath));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LogWrapper.Warn(ex, $"[Ayakaleaf] 推送 {relPath} 失败（{binding.ProjectName}）");
                            RaiseError(runtime, $"推送「{relPath}」失败：" + ex.Message);
                            errors++;
                        }
                    }
                }
            }

            // 5) 清单与绑定时间落盘（仅在有实际动作时写配置，避免空转刷盘）
            manifest.ProjectId = binding.ProjectId;
            manifest.LastSyncAtUtc = DateTime.UtcNow;
            SaveManifest(binding, manifest);
            if (pulled + pushed + conflicts > 0)
            {
                binding.LastSyncAtUtc = DateTime.UtcNow;
                SaveBinding(binding);
                lock (RuntimeGate)
                {
                    if (Runtimes.TryGetValue(binding.BindingId, out var rt)) rt.Binding = binding;
                }
            }

            var stats = new SyncCycleStats(pulled, pushed, conflicts, errors,
                errors > 0 ? $"{errors} 个文件同步失败，详见日志" : null);
            if (pulled + pushed + conflicts + errors > 0)
                LogWrapper.Info(
                    $"[Ayakaleaf] 本地同步完成（{binding.ProjectName}）：拉取 {pulled}，推送 {pushed}，冲突 {conflicts}，失败 {errors}");
            return stats;
        }
        finally
        {
            try { tempDir?.Delete(recursive: true); }
            catch { /* 临时目录清理失败不影响同步 */ }
        }
    }

    // ---------------- 拉取通道 ----------------

    /// <summary>下载项目 zip。<paramref name="sid"/> 失效时自动重登一次。</summary>
    private static async Task<byte[]> DownloadProjectZipAsync(string sid, string projectId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{AyakaleafService.BaseUrl}/Project/{projectId}/download/zip");
            request.Headers.TryAddWithoutValidation("Cookie", "overleaf.sid=" + sid);
            request.Headers.TryAddWithoutValidation("Accept", "application/zip");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var buffer = new MemoryStream();
                await response.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);
                return buffer.ToArray();
            }

            // 会话失效（302 跳登录 / 401）：重登一次后重试
            if (attempt == 0 && ((int)response.StatusCode is 301 or 302 or 303 or 307 or 401 or 403))
            {
                var userName = LocalUserSession.Instance.Current?.UserName ?? string.Empty;
                var account = await AyakaleafAccountService.GetOrCreateAccountAsync(userName).ConfigureAwait(false);
                sid = await AyakaleafAccountService.LoginAsync(account).ConfigureAwait(false);
                continue;
            }
            throw new HttpRequestException($"服务端返回 HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>
    /// 解压项目 zip 到临时目录。zip 内文件位于压缩包根部（镜像内
    /// ProjectZipStreamManager 以实体路径直挂 archive，实证 2026-11）。
    /// 含路径穿越 / 非法字符的条目跳过并告警。
    /// </summary>
    private static Dictionary<string, string> ExtractZipToTemp(byte[] zipBytes, out DirectoryInfo? tempDir)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        tempDir = null;
        if (zipBytes.Length == 0) return files;

        tempDir = Directory.CreateTempSubdirectory("ayaka-sync-");
        using var zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue; // 目录条目
            var relPath = NormalizeRelativePath(entry.FullName);
            if (relPath is null)
            {
                LogWrapper.Warn($"[Ayakaleaf] 项目 zip 中存在非法路径条目，已跳过：{entry.FullName}");
                continue;
            }
            var target = Path.Combine(tempDir.FullName, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var target2 = File.Create(target);
            source.CopyTo(target2);
            files[relPath] = target;
        }
        return files;
    }

    // ---------------- 推送通道 ----------------

    /// <summary>远端文件树（mongo 只读遍历）。</summary>
    private sealed class RemoteTree
    {
        public string RootFolderId = "";
        /// <summary>相对路径 → 文档 ID（可经 doc-updater setDoc 更新）。</summary>
        public Dictionary<string, string> Docs = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>相对路径 → 文件 ID（二进制，仅上传新建，不可替换）。</summary>
        public Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 经 mongo 容器只读遍历项目 rootFolder 树，返回 路径 → ID 映射与根目录 ID。
    /// 输出行协议：<c>ROOT id</c> / <c>DOC 目录 名称 id</c> / <c>FILE 目录 名称 id</c>（TAB 分隔）。
    /// 项目不存在返回 null。
    /// </summary>
    private static async Task<RemoteTree?> GetRemoteTreeAsync(string projectId, CancellationToken ct)
    {
        // 单行 JS、内层只用单引号（wslc 命令行约定，与 TryGrantServerAdminAsync 同款）
        var walkJs =
            "var p=db.getSiblingDB('sharelatex').projects.findOne({_id:ObjectId('" + projectId + "')},{rootFolder:1});" +
            "if(!p){print('ERR_NOT_FOUND')}else{print('ROOT\\t'+p.rootFolder._id.toString());" +
            "(function walk(f,pre){var fp=pre?pre+'/'+f.name:f.name;" +
            "(f.docs||[]).forEach(function(d){print('DOC\\t'+fp+'\\t'+(d.name||'')+'\\t'+d._id.toString())});" +
            "(f.fileRefs||[]).forEach(function(x){print('FILE\\t'+fp+'\\t'+(x.name||'')+'\\t'+x._id.toString())});" +
            "(f.folders||[]).forEach(function(g){walk(g,fp)})})(p.rootFolder,'')}";

        var result = await WslcInterop.RunAsync(
            $"exec {AyakaleafService.MongoContainer} mongosh --quiet --eval \"{walkJs}\"", 60000, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException("查询项目文件树失败：" + result.ErrorText.Trim());

        var tree = new RemoteTree();
        foreach (var rawLine in result.Stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            switch (parts[0])
            {
                case "ERR_NOT_FOUND":
                    return null;
                case "ROOT" when parts.Length >= 2:
                    tree.RootFolderId = parts[1];
                    break;
                case "DOC" when parts.Length >= 4:
                {
                    var path = JoinRemotePath(parts[1], parts[2]);
                    if (path is not null) tree.Docs[path] = parts[3];
                    break;
                }
                case "FILE" when parts.Length >= 4:
                {
                    var path = JoinRemotePath(parts[1], parts[2]);
                    if (path is not null) tree.Files[path] = parts[3];
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(tree.RootFolderId))
            throw new InvalidOperationException("项目文件树解析失败（缺少根目录）。");
        return tree;
    }

    /// <summary>目录 + 文件名 → 相对路径；非法成分返回 null。</summary>
    private static string? JoinRemotePath(string directory, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (string.IsNullOrEmpty(directory)) return NormalizeRelativePath(name);
        return NormalizeRelativePath(directory + "/" + name);
    }

    /// <summary>
    /// 推送文本文档：负载 JSON 写入容器（wslc cp，规避命令行长度限制），
    /// 容器内 curl 调 doc-updater setDoc（与编辑器保存同通道，在线协作者实时收到更新），
    /// 成功后清理两侧临时文件。
    /// </summary>
    private static async Task PushDocAsync(string projectId, string docId, string localAbsPath, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(localAbsPath, ct).ConfigureAwait(false);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var payload = JsonSerializer.Serialize(new { lines, source = "ayaka-folder-sync" });

        var name = Guid.NewGuid().ToString("N");
        var localTemp = Path.Combine(Path.GetTempPath(), "ayaka-sync-" + name + ".json");
        var containerFile = $"{ContainerWorkDir}/{name}.json";
        try
        {
            await File.WriteAllTextAsync(localTemp, payload, ct).ConfigureAwait(false);
            // 负载经 wslc cp 送进容器（HTTP 端口仅容器内可达，负载不进命令行）
            var copy = await WslcInterop.CopyToContainerAsync(
                AyakaleafService.OverleafContainer, localTemp, containerFile, 120_000, ct).ConfigureAwait(false);
            if (!copy.IsSuccess)
                throw new InvalidOperationException("同步负载写入容器失败：" + copy.ErrorText.Trim());

            // 容器内 curl 调 doc-updater（setDoc：{lines, source}，无需版本号，服务端自行求差异）
            var curl = await WslcInterop.RunAsync(
                $"exec {AyakaleafService.OverleafContainer} sh -c " +
                "\"curl -sS -f -X POST http://127.0.0.1:3003/project/" + projectId + "/doc/" + docId +
                " -H 'Content-Type: application/json' --data @" + containerFile + "\"", 60000, ct)
                .ConfigureAwait(false);
            if (!curl.IsSuccess)
                throw new InvalidOperationException("文档写入服务失败：" + curl.ErrorText.Trim());
        }
        finally
        {
            try { File.Delete(localTemp); } catch { /* 忽略清理失败 */ }
            _ = WslcInterop.RunAsync($"exec {AyakaleafService.OverleafContainer} rm -f {containerFile}", 15000, default);
        }
    }

    /// <summary>
    /// 上传新文件（multipart：qqfile + name + relativePath，查询 folder_id 指向项目根目录；
    /// 镜像内 UploadsRouter.mjs / ProjectUploadController.uploadFile 实证 2026-11）。
    /// </summary>
    private static async Task UploadFileAsync(
        string sid, string csrf, string projectId, string rootFolderId, string relPath, string localAbsPath, CancellationToken ct)
    {
        await using var fileStream = new FileStream(localAbsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var form = new MultipartFormDataContent();
        var fileName = Path.GetFileName(relPath);
        var directory = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";
        // 服务端以表单字段 name 作为实体名（uploadFile 实证），multipart 文件名仅作占位：
        // 真名走 UTF-8 表单字段，规避 Content-Disposition 非 ASCII 文件名编码兼容性问题
        form.Add(new StringContent(fileName, Encoding.UTF8), "name");
        var safeFileField = "file" + Path.GetExtension(fileName);
        form.Add(fileContent, "qqfile", safeFileField);
        if (!string.IsNullOrEmpty(directory))
            form.Add(new StringContent(directory + "/" + fileName, Encoding.UTF8), "relativePath");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{AyakaleafService.BaseUrl}/Project/{projectId}/upload?folder_id={rootFolderId}")
        {
            Content = form,
        };
        request.Headers.TryAddWithoutValidation("Cookie", "overleaf.sid=" + sid);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)response.StatusCode == 422 && body.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("项目里已存在同名文件，上传被拒绝（可能已被其他协作者创建）");
            throw new HttpRequestException($"服务端返回 HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>取会话 CSRF 令牌（GET /user/settings 解析 ol-csrfToken；失败返回 null）。</summary>
    private static async Task<string?> GetSessionCsrfAsync(string sid, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, AyakaleafService.BaseUrl + "/user/settings");
        request.Headers.TryAddWithoutValidation("Cookie", "overleaf.sid=" + sid);
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var match = Regex.Match(body, "ol-csrfToken\" content=\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    // ---------------- 清单 ----------------

    private sealed class SyncManifest
    {
        public int Version { get; set; } = 1;
        public string ProjectId { get; set; } = "";
        public DateTime LastSyncAtUtc { get; set; }
        public List<ManifestEntry> Files { get; set; } = [];
    }

    private sealed class ManifestEntry
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string Kind { get; set; } = "doc";
    }

    private static string ManifestPath(SyncBinding binding) =>
        Path.Combine(binding.LocalPath, ManifestFileName);

    private static SyncManifest LoadManifest(SyncBinding binding)
    {
        try
        {
            var path = ManifestPath(binding);
            if (!File.Exists(path)) return new SyncManifest { ProjectId = binding.ProjectId };
            var manifest = JsonSerializer.Deserialize<SyncManifest>(File.ReadAllText(path));
            if (manifest is not null && manifest.Files is not null)
                return manifest;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, $"[Ayakaleaf] 同步清单读取失败，按空清单处理（{binding.ProjectName}）");
        }
        return new SyncManifest { ProjectId = binding.ProjectId };
    }

    private static void SaveManifest(SyncBinding binding, SyncManifest manifest)
    {
        try
        {
            var path = ManifestPath(binding);
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            // 先写临时文件再原子替换：同步中途断电/退出不会留下半个清单
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, $"[Ayakaleaf] 同步清单写入失败（{binding.ProjectName}）");
        }
    }

    private static void AddManifestEntry(SyncManifest manifest, string relPath, string sha256)
    {
        var entry = manifest.Files.FirstOrDefault(f => f.Path == relPath);
        if (entry is null)
        {
            entry = new ManifestEntry { Path = relPath };
            manifest.Files.Add(entry);
        }
        entry.Sha256 = sha256;
        entry.Size = 0;
        entry.Kind = IsDocFile(relPath) ? "doc" : "file";
    }

    // ---------------- 文件工具 ----------------

    /// <summary>本地扫描：绑定目录全部文件（清单 / 冲突副本 / 编译产物 / 系统杂项除外）。</summary>
    private static Dictionary<string, string> ScanLocalFiles(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rootLength = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relPath = NormalizeRelativePath(file[(rootLength + 1)..].Replace(Path.DirectorySeparatorChar, '/'));
                if (relPath is null || IsExcludedLocalFile(relPath)) continue;
                result[relPath] = file;
            }
        }
        catch (Exception ex)
        {
            // 目录被删除/不可访问：按空集处理，本轮只做拉取；监听失败已有独立告警
            LogWrapper.Warn($"[Ayakaleaf] 本地同步目录扫描失败（{root}）：{ex.Message}");
        }
        return result;
    }

    /// <summary>本地文件是否排除在同步之外（清单自身 / 冲突副本 / 编译产物 / 系统杂项）。</summary>
    internal static bool IsExcludedLocalFile(string relPath)
    {
        var fileName = Path.GetFileName(relPath);
        if (fileName.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)) return true;
        if (ConflictCopyRegex().IsMatch(fileName)) return true;
        if (fileName is "Thumbs.db" or "desktop.ini") return true;
        if (fileName.StartsWith("~$", StringComparison.Ordinal)) return true; // Office 锁定文件
        if (fileName.EndsWith(".synctex.gz", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".synctex(busy)", StringComparison.OrdinalIgnoreCase)) return true;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension is
            ".aux" or ".bbl" or ".blg" or ".fdb_latexmk" or ".fls" or ".lof" or ".log" or ".lot" or
            ".nav" or ".out" or ".snm" or ".toc" or ".vrb" or ".xdv";
    }

    /// <summary>文本文档判定（推送走 doc-updater setDoc；其余视为二进制走上传）。</summary>
    internal static bool IsDocFile(string relPath)
    {
        var extension = Path.GetExtension(relPath).ToLowerInvariant();
        return extension is
            ".tex" or ".sty" or ".cls" or ".bib" or ".bst" or
            ".md" or ".markdown" or ".txt" or ".csv" or
            ".json" or ".yaml" or ".yml" or ".toml" or ".xml" or
            ".html" or ".htm" or ".js" or ".ts" or ".css" or
            ".py" or ".r" or ".lua" or ".sh" or ".bat" or ".ps1" or ".texi" or ".ltx";
    }

    /// <summary>
    /// 计算同步用哈希：文本文档先统一换行为 LF 再哈希（远端文档恒为 LF，
    /// 本地 Windows 编辑器 CRLF 保存不会造成虚假差异）；二进制按原始字节。
    /// </summary>
    private static string HashFile(string absPath, string relPath)
    {
        if (IsDocFile(relPath))
        {
            try
            {
                var text = File.ReadAllText(absPath).Replace("\r\n", "\n");
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            }
            catch (DecoderFallbackException)
            {
                // 非法 UTF-8：按二进制处理
            }
        }
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absPath)));
    }

    private static string HashLocalFile(string absPath, string relPath) => HashFile(absPath, relPath);

    /// <summary>复制文件（保持字节一致；自动创建目标目录）。</summary>
    private static void CopyFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    /// <summary>
    /// 生成冲突副本：把当前本地文件复制为 <c>名称.conflict-YYYYMMDD-HHMMSS.扩展名</c>。
    /// 同名冲突副本已存在时追加序号。原文件保持不动（由调用方决定覆盖与否）。
    /// </summary>
    private static void MakeConflictCopy(string localAbsPath)
    {
        var directory = Path.GetDirectoryName(localAbsPath)!;
        var name = Path.GetFileNameWithoutExtension(localAbsPath);
        var extension = Path.GetExtension(localAbsPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(directory, $"{name}{ConflictInfix}{stamp}{extension}");
        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{name}{ConflictInfix}{stamp}-{i}{extension}");
        File.Copy(localAbsPath, candidate, overwrite: false);
    }

    /// <summary>相对路径规范化：统一 / 分隔、去首尾空白段；含 .. 或空段返回 null。</summary>
    internal static string? NormalizeRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var segments = raw.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0) return null;
        if (segments.Any(s => s is "." or "..")) return null;
        if (segments.Any(s => s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        return string.Join('/', segments);
    }

    private static string NormalizeLocalPath(string path) =>
        Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>ancestor 是否是 descendant 的严格祖先目录（大小写不敏感，Windows 文件系统）。</summary>
    private static bool IsAncestorPath(string ancestor, string descendant)
    {
        var a = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
        return descendant.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- 通知 ----------------

    private static void RaiseNotice(SyncNoticeKind kind, BindingRuntime runtime, string message)
    {
        var notice = new SyncNotice(kind, runtime.Binding.BindingId, runtime.Binding.ProjectName, message);
        LogWrapper.Info($"[Ayakaleaf][Sync] {kind}: {message}");
        NoticeReceived?.Invoke(notice);
    }

    /// <summary>错误通知（节流：同一绑定 10 分钟内不重复提示同类错误，避免后台轮询刷屏）。</summary>
    private static void RaiseError(BindingRuntime runtime, string message)
    {
        LogWrapper.Warn($"[Ayakaleaf][Sync] {runtime.Binding.ProjectName}：{message}");
        var now = DateTime.UtcNow;
        lock (runtime)
        {
            if (now - runtime.LastErrorNoticeUtc < ErrorNoticeThrottle &&
                runtime.LastErrorMessage == message)
                return;
            runtime.LastErrorNoticeUtc = now;
            runtime.LastErrorMessage = message;
        }
        NoticeReceived?.Invoke(new SyncNotice(
            SyncNoticeKind.Error, runtime.Binding.BindingId, runtime.Binding.ProjectName, message));
    }
}
