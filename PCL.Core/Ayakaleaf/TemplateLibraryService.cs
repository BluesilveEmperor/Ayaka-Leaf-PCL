using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.Logging;

namespace PCL.Core.Ayakaleaf;

/// <summary>内置模板分类（对应 plan R9：<c>exam / thesis / contest / journal</c>）。</summary>
public enum TemplateCategory
{
    /// <summary>中学试卷。</summary>
    Exam,

    /// <summary>本科毕设。</summary>
    Thesis,

    /// <summary>竞赛报告。</summary>
    Contest,

    /// <summary>国内期刊投稿。</summary>
    Journal,
}

/// <summary>
/// 内置中文模板元数据（US10 / FR-015，对应 plan R9 TemplateInfo）。
/// 模板包以版本化 zip 资源随启动器分发：<c>Resources/ayakaleaf/templates/&lt;TemplateId&gt;.zip</c>
/// （zip 内含 <c>template.json</c>、<c>preview.png</c> 与 <c>content/</c> 内容树）。
/// </summary>
/// <param name="TemplateId">模板标识（与资源 zip 文件名一致，如 <c>thesis</c>）。</param>
/// <param name="Category">场景分类。</param>
/// <param name="Title">展示名称（如「本科毕设」）。</param>
/// <param name="Description">场景与用法说明。</param>
/// <param name="Version">模板内容版本（SemVer，随资源包更新递增）。</param>
/// <param name="Entry">入口文件（项目内相对路径，如 <c>main.tex</c>）。</param>
/// <param name="Engine">推荐编译引擎（如 <c>xelatex</c>）。</param>
/// <param name="MinImageVariant">所需最低镜像变体（<c>full</c>=任意变体可编译）。</param>
public sealed record TemplateInfo(
    string TemplateId,
    TemplateCategory Category,
    string Title,
    string Description,
    string Version,
    string Entry,
    string Engine,
    string MinImageVariant)
{
    /// <summary>模板内容包的 WPF 打包资源路径（<c>&lt;Resource Include="Resources\**" /&gt;</c> 自动嵌入）。</summary>
    public string PackageResourcePath => $"Resources/ayakaleaf/templates/{TemplateId}.zip";
}

/// <summary>一键新建结果状态。</summary>
public enum TemplateCreateStatus
{
    /// <summary>创建成功。</summary>
    Created,

    /// <summary>创建失败（账号、网络或服务端任一环节出错）。</summary>
    Failed,
}

/// <summary>
/// 一键新建结果。<paramref name="ProjectId"/> 为 Overleaf 项目标识，
/// 拼接 <c>/project/&lt;ProjectId&gt;</c> 即工作台路径；
/// <paramref name="Account"/>/<paramref name="SessionId"/> 为创建过程登录所得凭据，
/// 供调用方直接打开工作台窗口。
/// </summary>
/// <param name="Status">结果状态。</param>
/// <param name="ProjectId">项目标识（仅 <see cref="TemplateCreateStatus.Created"/> 时有值）。</param>
/// <param name="Error">失败原因（仅 <see cref="TemplateCreateStatus.Failed"/> 时有值）。</param>
/// <param name="Account">登录所用账号（仅 Created 时有值）。</param>
/// <param name="SessionId">登录所得 <c>overleaf.sid</c>（仅 Created 时有值）。</param>
public sealed record TemplateCreateResult(
    TemplateCreateStatus Status,
    string? ProjectId,
    string? Error,
    AyakaleafAccount? Account,
    string? SessionId)
{
    /// <summary>创建成功。</summary>
    public static TemplateCreateResult Created(
        AyakaleafAccount account, string sessionId, string projectId) =>
        new(TemplateCreateStatus.Created, projectId, null, account, sessionId);

    /// <summary>创建失败。</summary>
    public static TemplateCreateResult Fail(string error) =>
        new(TemplateCreateStatus.Failed, null, error, null, null);
}

/// <summary>
/// 内置中文模板库服务（US10 / FR-015）：模板索引、预览图与「一键新建」。
/// <para>
/// 分发：模板包（<c>template.json</c> + <c>preview.png</c> + <c>content/</c>）以版本化
/// zip 随启动器嵌入 <c>Resources/ayakaleaf/templates/</c>，后续可经在线更新通道替换；
/// 「一键新建」仅读取资源创建新项目，不覆盖既有项目（FR-015 / AC3）。
/// </para>
/// <para>
/// 创建链路（镜像内 <c>web/app/src/router.mjs</c> 与
/// <c>Features/Uploads/UploadsRouter.mjs</c>、<c>ProjectUploadController.mjs</c>，2026-11 实证）：
/// 本地账号登录 → 取会话 CSRF（<c>GET /user/settings</c> 解析 <c>ol-csrfToken</c>）→
/// <c>POST /project/new/upload</c>（multipart，文件字段 <c>qqfile</c>，
/// 表单字段 <c>name</c> 携带「项目名.zip」；<c>X-CSRF-TOKEN</c> 请求头过 csurf，
/// 会话经 <c>overleaf.sid</c> Cookie 鉴权）→ 响应 <c>{ success, project_id }</c>。
/// 服务端按官方导入流程解压全目录、识别根文档并生成唯一项目名，与手动「上传项目」行为一致。
/// </para>
/// <para>
/// 本服务持有独立 HttpClient（不走系统代理、不共享账号服务的 Cookie 容器），
/// 会话 Cookie 按请求手动附加，避免与登录流程的会话状态互串。
/// </para>
/// </summary>
public static class TemplateLibraryService
{
    /// <summary>上传超时：服务端解压导入可能耗时数秒，上限对齐 <c>projectUploadTimeout</c> 默认值（120s）。</summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(120);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>模板清单（代码级注册表，与资源 zip 一一对应；资源缺失时对应条目加载失败并报错）。</summary>
    private static readonly IReadOnlyList<TemplateInfo> Templates = new[]
    {
        new TemplateInfo("exam", TemplateCategory.Exam, "中学试卷",
            "适用于中学各学科单元测验、期中期末试卷排版，内置选择、填空、解答三种题型版式与考生信息栏，替换题目即可使用。",
            "1.0.0", "main.tex", "xelatex", "full"),
        new TemplateInfo("thesis", TemplateCategory.Thesis, "本科毕设",
            "面向本科毕业设计（论文）撰写：封面、摘要、目录、分章正文、GB/T 7714 参考文献与致谢一应俱全，章节文件独立存放便于协作。",
            "1.0.0", "main.tex", "xelatex", "full"),
        new TemplateInfo("contest", TemplateCategory.Contest, "竞赛报告",
            "数学建模等学科竞赛报告模板：独立摘要页、问题重述、模型假设、符号说明、模型建立与求解、模型评价与附录代码，符合通行评审版式。",
            "1.0.0", "main.tex", "xelatex", "full"),
        new TemplateInfo("journal", TemplateCategory.Journal, "国内期刊投稿",
            "国内科技期刊通用双栏版式：通栏标题作者摘要、关键词与中图分类号齐备，正文五号双栏，参考文献按 GB/T 7714 著录，投稿前按目标期刊简则微调即可。",
            "1.0.0", "main.tex", "xelatex", "full"),
    };

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false, // 本地服务，绝不能走系统代理
            UseCookies = false, // 会话 Cookie 按请求手动附加，避免多会话互串
            AllowAutoRedirect = false, // 会话失效时的 /login 跳转按失败处理，不跟随
        };
        return new HttpClient(handler) { Timeout = UploadTimeout };
    }

    /// <summary>取全部内置模板（US10 AC1：按场景分类的模板列表）。</summary>
    public static IReadOnlyList<TemplateInfo> GetTemplates() => Templates;

    /// <summary>按标识取模板；不存在时返回 null。</summary>
    public static TemplateInfo? GetTemplate(string templateId) =>
        Templates.FirstOrDefault(t =>
            string.Equals(t.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 读取模板预览图（zip 内 <c>preview.png</c>）。
    /// </summary>
    /// <returns>PNG 字节；资源缺失或格式非法时返回 null（页面显示占位）。</returns>
    public static byte[]? LoadPreview(TemplateInfo template)
    {
        using var zip = OpenPackage(template, out _);
        var entry = zip?.GetEntry("preview.png");
        if (entry is null)
            return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 一键新建：把模板 <c>content/</c> 树经服务端项目创建入口写入新项目（FR-015）。
    /// 全链路（登录 → CSRF → 上传导入）任一环节失败均不抛异常，以状态结果返回。
    /// </summary>
    /// <param name="template">目标模板。</param>
    /// <param name="localUserName">本地用户名（账号服务据此取/建账号）。</param>
    /// <param name="projectName">项目显示名；缺省取模板标题。服务端重名时自动加序号。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<TemplateCreateResult> CreateProjectFromTemplateAsync(
        TemplateInfo template,
        string localUserName,
        string? projectName = null,
        CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(projectName) ? template.Title : projectName.Trim();

        // 1) 打包内容树（在异步等待前完成，资源访问失败即快速失败）
        byte[] uploadZip;
        try
        {
            uploadZip = BuildUploadZip(template, out var entryCount);
            if (entryCount == 0)
                return TemplateCreateResult.Fail($"模板 {template.Title} 的内容包为空，无法创建项目。");
        }
        catch (Exception ex)
        {
            return TemplateCreateResult.Fail($"读取模板 {template.Title} 资源失败：" + ex.Message);
        }

        // 2) 账号与登录（沿用账号服务，保证与工作台同一账号体系）
        AyakaleafAccount account;
        string sessionId;
        try
        {
            account = await AyakaleafAccountService
                .GetOrCreateAccountAsync(localUserName).ConfigureAwait(false);
            sessionId = await AyakaleafAccountService.LoginAsync(account).ConfigureAwait(false);
        }
        catch (AyakaleafAccountException ex)
        {
            return TemplateCreateResult.Fail("登录 Ayakaleaf 服务失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            return TemplateCreateResult.Fail("登录 Ayakaleaf 服务失败：" + ex.Message);
        }

        // 3) 会话 CSRF（与登录页令牌不同：csurf 令牌与会话绑定，须带会话 Cookie 获取）
        string? csrf;
        try
        {
            csrf = await GetSessionCsrfTokenAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return TemplateCreateResult.Fail("获取会话令牌失败：" + ex.Message);
        }
        if (csrf is null)
            return TemplateCreateResult.Fail("获取会话令牌失败：服务尚未就绪或会话已失效。");

        // 4) 上传导入：POST /project/new/upload（multipart：name + qqfile）
        try
        {
            return await UploadAndCreateAsync(account, sessionId, csrf, template, displayName, uploadZip, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TemplateCreateResult.Fail("创建项目请求失败：" + ex.Message);
        }
    }

    /// <summary>打开模板包 zip（WPF 打包资源）。</summary>
    private static ZipArchive? OpenPackage(TemplateInfo template, out byte[] zipBytes)
    {
        Stream? stream = null;
        try
        {
            // <Resource Include="Resources\**" /> 嵌入主程序集，pack URI 相对应用程序解析
            stream = Application.GetResourceStream(
                new Uri($"pack://application:,,,/{template.PackageResourcePath}", UriKind.Absolute))?.Stream;
            if (stream is null)
            {
                zipBytes = Array.Empty<byte>();
                return null;
            }
            using (stream)
            {
                var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                zipBytes = buffer.ToArray();
            }
            return new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        }
        catch
        {
            stream?.Dispose();
            zipBytes = Array.Empty<byte>();
            return null;
        }
    }

    /// <summary>
    /// 把模板包内 <c>content/</c> 树重打包为上传 zip（条目前缀剥离，<c>main.tex</c> 落在 zip 根）。
    /// 上传后服务端解压即得项目目录树；<c>template.json</c>/<c>preview.png</c> 等元数据不进项目。
    /// </summary>
    private static byte[] BuildUploadZip(TemplateInfo template, out int entryCount)
    {
        using var package = OpenPackage(template, out _);
        if (package is null)
            throw new InvalidOperationException($"未找到模板资源 {template.PackageResourcePath}。");

        var buffer = new MemoryStream();
        using (var upload = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            entryCount = 0;
            foreach (var entry in package.Entries)
            {
                if (!entry.FullName.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var targetName = entry.FullName["content/".Length..];
                if (string.IsNullOrEmpty(targetName) || entry.FullName.EndsWith('/'))
                    continue; // 目录条目：ZipArchive 按文件条目路径自动建目录
                var newEntry = upload.CreateEntry(targetName, CompressionLevel.Optimal);
                using var source = entry.Open();
                using var target = newEntry.Open();
                source.CopyTo(target);
                entryCount++;
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 取会话 CSRF 令牌：<c>GET /user/settings</c>（带会话 Cookie）解析 <c>ol-csrfToken</c>。
    /// 非 200（含会话失效跳登录页）返回 null。与账号服务同名私有实现策略一致，
    /// 但本服务不经共享 Cookie 容器，改为手动附加 <c>overleaf.sid</c>。
    /// </summary>
    private static async Task<string?> GetSessionCsrfTokenAsync(string sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, AyakaleafService.BaseUrl + "/user/settings");
        request.Headers.TryAddWithoutValidation("Cookie", "overleaf.sid=" + sessionId);
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var match = Regex.Match(body, "ol-csrfToken\" content=\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>上传模板内容并创建项目；解析响应 <c>{ success, project_id }</c>。</summary>
    private static async Task<TemplateCreateResult> UploadAndCreateAsync(
        AyakaleafAccount account,
        string sessionId,
        string csrf,
        TemplateInfo template,
        string displayName,
        byte[] uploadZip,
        CancellationToken ct)
    {
        // 文件字段固定 ASCII 文件名（multipart 头编码稳妥），展示名经 name 表单字段（UTF-8）传递
        using var fileContent = new ByteArrayContent(uploadZip);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(displayName + ".zip", Encoding.UTF8), "name");
        form.Add(fileContent, "qqfile", "template.zip");

        using var request = new HttpRequestMessage(
            HttpMethod.Post, AyakaleafService.BaseUrl + "/project/new/upload")
        {
            Content = form,
        };
        request.Headers.TryAddWithoutValidation("Cookie", "overleaf.sid=" + sessionId);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TemplateCreateResult.Fail("无法连接 Ayakaleaf 服务：" + ex.Message);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return TemplateCreateResult.Fail(
                    $"服务端拒绝创建项目（HTTP {statusCode}），请稍后重试。");

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return TemplateCreateResult.Fail("读取服务端响应失败：" + ex.Message);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var projectId = root.TryGetProperty("project_id", out var idElement) &&
                                idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(projectId))
                    return TemplateCreateResult.Fail("服务端响应缺少项目标识，创建未完成。");
                LogWrapper.Info($"[Ayakaleaf] 模板创建项目成功：{template.TemplateId} → {projectId}");
                return TemplateCreateResult.Created(account, sessionId, projectId!);
            }
            catch (JsonException)
            {
                return TemplateCreateResult.Fail("服务端响应解析失败，创建结果未知。");
            }
        }
    }
}
