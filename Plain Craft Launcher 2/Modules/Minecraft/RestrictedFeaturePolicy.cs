using PCL.Core.App.Region;
using PCL.Core.App.Localization;
using PCL.Core.UI;

namespace PCL;

internal static class RestrictedFeaturePolicy
{
    // 本地修改：短路 #3673 引入的区域门禁，恢复 Minecraft 下载/安装/补全功能（仅个人本地使用，请勿分发）
    internal static RegionAssessment Assess() => new RegionAssessment(RegionClassification.Other, "local-bypass");

    internal static bool IsRestricted => Assess().IsRestricted;

    internal static bool Check(string feature, bool showHint = true)
    {
        var assessment = Assess();
        if (!assessment.IsRestricted) return true;

        ModBase.Log($"[RegionPolicy] 已阻止功能 {feature}，判定原因：{assessment.Reason}");
        if (showHint)
        {
            HintService.Hint(Lang.Text(assessment.Classification == RegionClassification.Unknown
                ? "Region.Feature.Unknown"
                : "Region.Feature.Restricted"), HintType.Error);
        }
        return false;
    }

    internal static void ThrowIfRestricted(string feature)
    {
        if (Check(feature, false)) return;
        throw new ModBase.CancelledException();
    }
}
