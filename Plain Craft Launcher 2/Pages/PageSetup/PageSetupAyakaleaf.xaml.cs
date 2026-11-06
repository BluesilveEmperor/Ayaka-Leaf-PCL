using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Localization;
using PCL.Core.Ayakaleaf;

namespace PCL;

public partial class PageSetupAyakaleaf
{
    private new bool isLoaded;

    public PageSetupAyakaleaf()
    {
        InitializeComponent();
        Loaded += PageSetupAyakaleaf_Loaded;
        Loaded += (_, _) => Reload();
    }

    private void PageSetupAyakaleaf_Loaded(object sender, RoutedEventArgs e)
    {
        PanBack.ScrollToHome();

        // 非重复加载部分
        if (isLoaded)
            return;
        isLoaded = true;

        ModAnimation.AniControlEnabled += 1;
        Reload();
        ModAnimation.AniControlEnabled -= 1;
    }

    /// <summary>
    ///     从配置刷新全部控件状态。
    /// </summary>
    public void Reload()
    {
        var cfg = Config.Ayakaleaf;

        // Git-Bridge / MCP
        CheckGitBridge.SetChecked(cfg.EnableGitBridge, false);

        // 沙箱编译 / Pandoc：WSL 容器环境不支持同级容器，仅作置灰展示，无可保存状态

        // AI 助手
        CheckAi.SetChecked(cfg.EnableAi, false);
        TextAiBaseUrl.Text = cfg.AiBaseUrl;
        TextAiApiKey.Text = cfg.AiApiKey;
        TextAiModel.Text = cfg.AiModel;
        TextAiImageModel.Text = cfg.AiImageModel;
        TextAiPdfModel.Text = cfg.AiPdfModel;
        TextAiQuota.Text = cfg.AiTokenQuota.ToString();

        // 模板画廊
        CheckTemplate.SetChecked(cfg.EnableTemplateGallery, false);
        TextTemplateCategories.Text = cfg.TemplateCategories;
        TextTemplateUserId.Text = cfg.TemplatesUserId;

        // Git 双向同步
        CheckGithub.SetChecked(cfg.EnableGithubSync, false);
        TextGithubUrl.Text = cfg.GithubSyncUrl;
        TextGithubClientId.Text = cfg.GithubClientId;
        TextGithubClientSecret.Text = cfg.GithubClientSecret;
        CheckGitlab.SetChecked(cfg.EnableGitlabSync, false);
        TextGitlabUrl.Text = cfg.GitlabSyncUrl;
        TextGitlabClientId.Text = cfg.GitlabClientId;
        TextGitlabClientSecret.Text = cfg.GitlabClientSecret;
        CheckGitea.SetChecked(cfg.EnableGiteaSync, false);
        TextGiteaUrl.Text = cfg.GiteaSyncUrl;
        TextGiteaClientId.Text = cfg.GiteaClientId;
        TextGiteaClientSecret.Text = cfg.GiteaClientSecret;

        // 文献引用
        CheckZotero.SetChecked(cfg.EnableZotero, false);
        TextZoteroClientId.Text = cfg.ZoteroClientId;
        TextZoteroClientSecret.Text = cfg.ZoteroClientSecret;
        CheckMendeley.SetChecked(cfg.EnableMendeley, false);
        TextMendeleyClientId.Text = cfg.MendeleyClientId;
        TextMendeleyClientSecret.Text = cfg.MendeleyClientSecret;

        // 注册与安全
        var regRadio = cfg.RegistrationMode switch
        {
            AyakaleafRegistrationMode.Open => RadioRegOpen,
            AyakaleafRegistrationMode.DomainList => RadioRegDomainList,
            _ => RadioRegClosed
        };
        regRadio.SetChecked(true, false);
        TextRegistrationDomains.Text = cfg.RegistrationDomains;
        CheckRecaptcha.SetChecked(cfg.EnableRecaptcha, false);
        TextRecaptchaSiteKey.Text = cfg.RecaptchaSiteKey;
        TextRecaptchaSecretKey.Text = cfg.RecaptchaSecretKey;
        CheckHibp.SetChecked(cfg.EnableHibp, false);

        // 品牌化
        TextAppName.Text = cfg.AppName;
        TextNavTitle.Text = cfg.NavTitle;
        TextHeaderImageUrl.Text = cfg.HeaderImageUrl;
        TextHeaderImageUrlLight.Text = cfg.HeaderImageUrlLight;
        TextHeaderImageUrlDark.Text = cfg.HeaderImageUrlDark;

        // 高级
        TextOverleafImage.Text = cfg.OverleafImage;
        TextAdvancedEnv.Text = cfg.AdvancedEnv;

        // 界面语言（18 语言复选列表）
        BuildLanguageList();
    }

    /// <summary>
    ///     按镜像内置语言列表构建 18 语言复选列表：
    ///     en 固定勾选且禁用（绑定主端口），其余语言勾选状态取自 EnabledLanguages。
    /// </summary>
    private void BuildLanguageList()
    {
        PanLanguages.Children.Clear();
        var enabled = AyakaleafService.GetEnabledLanguages();
        foreach (var lang in AyakaleafService.SupportedLanguages)
        {
            var box = new MyCheckBox
            {
                Text = $"{lang.Name}（{lang.NativeName}）",
                Height = 22,
                Tag = lang.Code,
            };
            if (lang.Code == "en")
            {
                // en 固定启用：默认语言绑定主端口，页脚/工作台语言切换的兜底目标
                box.SetChecked(true, false);
                box.IsEnabled = false;
            }
            else
            {
                box.SetChecked(enabled.Contains(lang.Code), false);
                box.Change += LanguageCheckBoxChange;
            }
            PanLanguages.Children.Add(box);
        }
    }

    /// <summary>
    ///     语言复选状态变化：按列表顺序重建 EnabledLanguages（en 固定首位）。
    ///     端口分配随语言列表变化，重启服务后容器会按新端口映射自动重建。
    /// </summary>
    private void LanguageCheckBoxChange(object senderRaw, bool user)
    {
        if (!user || ModAnimation.AniControlEnabled != 0)
            return;
        var codes = new List<string>();
        foreach (var box in PanLanguages.Children.OfType<MyCheckBox>())
        {
            if (box.Tag is not string code || code == "en") continue;
            if (box.Checked == true) codes.Add(code);
        }
        ConfigService.TrySetValue("AyakaleafEnabledLanguages",
            codes.Count > 0 ? "en," + string.Join(",", codes) : "en");
        HintService.Hint(Lang.Text("Setup.Ayakaleaf.RestartHint"), log: false);
    }

    private void CheckBoxChange(object senderRaw, bool user)
    {
        var sender = (MyCheckBox)senderRaw;
        if (ModAnimation.AniControlEnabled != 0)
            return;
        ConfigService.TrySetValue(sender.Tag?.ToString() ?? "", sender.Checked ?? false);
        if (user)
            HintService.Hint(Lang.Text("Setup.Ayakaleaf.RestartHint"), log: false);
    }

    private void RadioBoxChange(object senderRaw, ModBase.RouteEventArgs e)
    {
        var sender = (MyRadioBox)senderRaw;
        var gotCfg = sender.Tag?.ToString()?.Split("/") ?? System.Array.Empty<string>();
        if (ModAnimation.AniControlEnabled != 0 || gotCfg.Length < 2)
            return;
        ConfigService.TrySetValue(gotCfg[0], int.Parse(gotCfg[1]));
        HintService.Hint(Lang.Text("Setup.Ayakaleaf.RestartHint"), log: false);
    }

    private void TextLostFocus(object sender, RoutedEventArgs e)
    {
        if (ModAnimation.AniControlEnabled != 0)
            return;
        var box = (MyTextBox)sender;
        var tag = box.Tag?.ToString();
        if (string.IsNullOrEmpty(tag))
            return;
        if (tag == "AyakaleafAiTokenQuota")
        {
            ConfigService.TrySetValue(tag, int.TryParse(box.Text.Trim(), out var quota) ? quota : 0);
            return;
        }
        ConfigService.TrySetValue(tag, box.Text);
    }
}
