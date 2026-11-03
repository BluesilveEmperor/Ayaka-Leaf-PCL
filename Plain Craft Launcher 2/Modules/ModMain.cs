using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FluentValidation;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using PCL.Core.App;
using PCL.Core.App.Configuration;
using PCL.Core.App.Localization;
using PCL.Core.UI;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils.Secret;

namespace PCL;

public static class ModMain
{
    public static FormMain? frmMain;
    public static SplashScreen? frmStart;
    public static PageServiceLeft? frmServiceLeft;
    public static PageServiceRight? frmServiceRight;
    public static PageMirror? frmMirror;
    public static PageBackup? frmBackup;
    public static PageSpeedLeft? frmSpeedLeft;
    public static PageSpeedRight? frmSpeedRight;
    public static PageSetupLeft? frmSetupLeft;
    public static PageSetupUI? frmSetupUI;
    public static PageSetupUpdate? frmSetupUpdate;
    public static PageSetupAbout? frmSetupAbout;
    public static PageSetupLog? frmSetupLog;
    public static PageSetupFeedback? frmSetupFeedback;
    public static PageSetupLauncherLanguage? frmSetupLauncherLanguage;
    public static PageSetupLauncherMisc? frmSetupLauncherMisc;

    public static MySlider? dragControl = null;
    private static int timer4Count;
    private static int timer150Count;

    /// <summary>
    ///     等待显示的弹窗。
    /// </summary>
    public static List<MyMsgBoxConverter> WaitingMyMsgBox { get; } = [];

    private static void TimerMain()
    {
        try
        {
            #region 每 50ms 执行一次的代码

            HintService.Tick();
            MyMsgBoxTick();
            frmMain!.DragTick();
            ModLoader.LoaderTaskbarProgressRefresh();
        }

        #endregion

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "短程主时钟执行异常",
                ModBase.LogLevel.Critical,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }

        timer4Count += 1;
        if (timer4Count == 4)
        {
            timer4Count = 0;
            try
            {
                #region 每 250ms 执行一次的代码
            }

            #endregion

            catch (Exception ex)
            {
                ModBase.Log(ex, "中程主时钟执行异常");
            }
        }

        timer150Count += 1;
        if (timer150Count == 150)
        {
            timer150Count = 0;
            try
            {
                #region 每 7.5s 执行一次的代码

                // 以未知原因窗口被丢到一边去的修复（Top、Left = -25600），还有 #745
                ModBase.RunInUi(() =>
                {
                    if (!frmMain.Hidden)
                    {
                        if (frmMain.Top < -9000) frmMain.Top = 100d;
                        if (frmMain.Left < -9000) frmMain.Left = 100d;
                    }
                }); // 窗口拉至最大时 Left = -18.8
            }

            #endregion

            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "长程主时钟执行异常",
                    ModBase.LogLevel.Critical,
                    userSummary: Lang.Text("Main.Error.OperationFailed"));
            }
        }
    }

    public static void TimerMainStart()
    {
        ModBase.RunInNewThread(() =>
        {
            try
            {
                while (true)
                {
                    ModBase.RunInUiWait(TimerMain);
                    Thread.Sleep((int)Math.Round(50d * 0.98d));
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "程序主时钟出错",
                    ModBase.LogLevel.Feedback,
                    userSummary: Lang.Text("Main.Error.OperationFailed"));
            }
        }, "Timer Main");
    }

    #region 弹窗

    /// <summary>
    ///     存储弹窗信息的转换器。
    /// </summary>
    public class MyMsgBoxConverter
    {
        // 设置轮询 Url
        public string Button1 = "";

        /// <summary>
        ///     点击第一个按钮将执行该方法，不关闭弹窗。
        /// </summary>
        public Action Button1Action;

        public string Button2 = "";

        /// <summary>
        ///     点击第二个按钮将执行该方法，不关闭弹窗。
        /// </summary>
        public Action Button2Action;

        public string Button3 = "";

        /// <summary>
        ///     点击第三个按钮将执行该方法，不关闭弹窗。
        /// </summary>
        public Action Button3Action;

        /// <summary>
        ///     输入模式：文本框的文本。
        ///     选择模式：需要放进去的 List(Of MyListItem)。
        /// </summary>
        public object Content;

        public bool ForceWait;

        /// <summary>
        ///     选择模式：是否允许勾选多个选项
        /// </summary>
        public bool MultiSelect;

        /// <summary>
        ///     有多个按钮时，是否给第一个按钮加高亮。
        /// </summary>
        public bool HighLight;

        /// <summary>
        ///     输入模式：提示文本。
        /// </summary>
        public string HintText = "";

        /// <summary>
        ///     弹窗是否已经关闭。
        /// </summary>
        public bool IsExited = false;

        public bool IsWarn;

        /// <summary>
        ///     输入模式：输入的文本。若点击了 非 第一个按钮，则为 Nothing。
        ///     选择模式：点击的按钮编号，从 1 开始。
        /// </summary>
        public object Result;

        public string Text;
        public string Title;
        public MyMsgBoxType Type;

        /// <summary>
        ///     输入模式：输入验证规则。
        /// </summary>
        public Collection<IValidator<string>> ValidateRules;

        public DispatcherFrame WaitFrame = new(true);
    }

    public enum MyMsgBoxType
    {
        Text,
        Select,
        Input,
        Markdown
    }

    private static string GetDefaultDialogTitle() => Lang.Text("Common.Dialog.Title");

    private static string GetDefaultConfirmText() => Lang.Text("Common.Action.Confirm");

    private static string GetDefaultCancelText() => Lang.Text("Common.Action.Cancel");

    /// <summary>
    ///     显示弹窗，返回点击按钮的编号（从 1 开始）。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="caption">弹窗的内容。</param>
    /// <param name="button1">显示的第一个按钮，默认为“确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="button3">显示的第三个按钮，默认为空。</param>
    /// <param name="button1Action">点击第一个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button2Action">点击第二个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button3Action">点击第三个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int MyMsgBox(string caption, string? title = null, string? button1 = null, string? button2 = "",
        string? button3 = "", bool isWarn = false, bool highLight = true, bool forceWait = false,
        Action button1Action = null, Action button2Action = null, Action button3Action = null)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        button3 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Text, Button1 = button1, Button2 = button2, Button3 = button3, Text = caption,
            IsWarn = isWarn, Title = title, HighLight = highLight, ForceWait = true, Button1Action = button1Action,
            Button2Action = button2Action, Button3Action = button3Action
        };
        WaitingMyMsgBox.Add(converter);
        if (ModBase.RunInUi())
            // 若为 UI 线程，立即执行弹窗刻， 避免快速（连点器）点击时多次弹窗
            MyMsgBoxTick();
        if (button2.Length > 0 || forceWait)
        {
            // 若有多个按钮则开始等待
            if (frmMain is null || (frmMain.PanMsg is null && ModBase.RunInUi()))
            {
                // 主窗体尚未加载，用老土的弹窗来替代
                WaitingMyMsgBox.Remove(converter);
                if (button2.Length > 0)
                {
                    var rawResult = Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)(button3.Length > 0 ? MsgBoxStyle.YesNoCancel : MsgBoxStyle.YesNo) +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    switch (rawResult)
                    {
                        case MsgBoxResult.Yes:
                        {
                            converter.Result = 1;
                            break;
                        }
                        case MsgBoxResult.No:
                        {
                            converter.Result = 2;
                            break;
                        }
                        case MsgBoxResult.Cancel:
                        {
                            converter.Result = 3;
                            break;
                        }
                    }
                }
                else
                {
                    Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)MsgBoxStyle.OkOnly +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    converter.Result = 1;
                }

                ModBase.Log("[Control] 主窗体加载完成前出现意料外的等待弹窗：" + button1 + "," + button2 + "," + button3,
                    ModBase.LogLevel.Debug);
            }
            else
            {
                try
                {
                    frmMain.DragStop();
                    ComponentDispatcher.PushModal();
                    Dispatcher.PushFrame(converter.WaitFrame);
                }
                finally
                {
                    ComponentDispatcher.PopModal();
                }
            }

            ModBase.Log($"[Control] 普通弹框返回：{converter.Result ?? "null"}");
            return (int)converter.Result;
        }

        // 不进行等待，直接返回
        return 1;
    }

    /// <summary>
    ///     显示弹窗，返回点击按钮的编号（从 1 开始）。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="caption">弹窗的内容。</param>
    /// <param name="button1">显示的第一个按钮，默认为“确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="button3">显示的第三个按钮，默认为空。</param>
    /// <param name="button1Action">点击第一个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button2Action">点击第二个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="button3Action">点击第三个按钮将执行该方法，不关闭弹窗。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int MyMsgBoxMarkdown(string caption, string? title = null, string? button1 = null, string? button2 = "",
        string? button3 = "", bool isWarn = false, bool highLight = true, bool forceWait = false,
        Action button1Action = null, Action button2Action = null, Action button3Action = null)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        button3 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Markdown, Button1 = button1, Button2 = button2, Button3 = button3, Text = caption,
            IsWarn = isWarn, Title = title, HighLight = highLight, ForceWait = true, Button1Action = button1Action,
            Button2Action = button2Action, Button3Action = button3Action
        };
        WaitingMyMsgBox.Add(converter);
        if (ModBase.RunInUi())
            // 若为 UI 线程，立即执行弹窗刻， 避免快速（连点器）点击时多次弹窗
            MyMsgBoxTick();
        if (button2.Length > 0 || forceWait)
        {
            // 若有多个按钮则开始等待
            if (frmMain is null || (frmMain.PanMsg is null && ModBase.RunInUi()))
            {
                // 主窗体尚未加载，用老土的弹窗来替代
                WaitingMyMsgBox.Remove(converter);
                if (button2.Length > 0)
                {
                    var rawResult = Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)(button3.Length > 0 ? MsgBoxStyle.YesNoCancel : MsgBoxStyle.YesNo) +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    switch (rawResult)
                    {
                        case MsgBoxResult.Yes:
                        {
                            converter.Result = 1;
                            break;
                        }
                        case MsgBoxResult.No:
                        {
                            converter.Result = 2;
                            break;
                        }
                        case MsgBoxResult.Cancel:
                        {
                            converter.Result = 3;
                            break;
                        }
                    }
                }
                else
                {
                    Interaction.MsgBox(caption,
                        (MsgBoxStyle)((int)MsgBoxStyle.OkOnly +
                                      (int)(isWarn ? MsgBoxStyle.Critical : MsgBoxStyle.Question)), title);
                    converter.Result = 1;
                }

                ModBase.Log("[Control] 主窗体加载完成前出现意料外的等待弹窗：" + button1 + "," + button2 + "," + button3,
                    ModBase.LogLevel.Debug);
            }
            else
            {
                try
                {
                    frmMain.DragStop();
                    ComponentDispatcher.PushModal();
                    Dispatcher.PushFrame(converter.WaitFrame);
                }
                finally
                {
                    ComponentDispatcher.PopModal();
                }
            }

            ModBase.Log($"[Control] 普通弹框返回：{converter.Result ?? "null"}");
            return (int)converter.Result;
        }

        // 不进行等待，直接返回
        return 1;
    }

    /// <summary>
    ///     显示输入框并返回输入的文本。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="validateRules">文本框的输入检测。</param>
    /// <param name="text">弹窗的介绍文本。</param>
    /// <param name="defaultInput">文本框的默认内容。</param>
    /// <param name="hintText">文本框的提示内容。</param>
    /// <param name="button1">显示的第一个按钮，默认为“确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为“取消”。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static string MyMsgBoxInput(string title, string text = "", string defaultInput = "",
        Collection<IValidator<string>>? validateRules = null, string hintText = "", string? button1 = null,
        string? button2 = null, bool isWarn = false)
    {
        button1 ??= GetDefaultConfirmText();
        button2 ??= GetDefaultCancelText();
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Text = text, HintText = hintText, Type = MyMsgBoxType.Input,
            ValidateRules = validateRules ?? [], Button1 = button1, Button2 = button2,
            Content = defaultInput, IsWarn = isWarn, Title = title
        };
        WaitingMyMsgBox.Add(converter);
        // 虽然我也不知道这是啥但是能用就成了 :)
        try
        {
            frmMain?.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }

        ModBase.Log($"[Control] 输入弹框返回：{converter.Result}");
        return converter.Result?.ToString();
    }

    /// <summary>
    ///     显示选择框并返回选择的第几项（从 0 开始）。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="button1">显示的第一个按钮，默认为 “确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static int? MyMsgBoxSelect(List<IMyRadio> selections, string? title = null, string? button1 = null,
        string? button2 = "", bool isWarn = false)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Select, Button1 = button1, Button2 = button2, Content = selections, IsWarn = isWarn,
            Title = title
        };
        WaitingMyMsgBox.Add(converter);
        // 虽然我也不知道这是啥但是能用就成了 :)
        try
        {
            if (frmMain is not null)
                frmMain.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }

        ModBase.Log($"[Control] 选择弹框返回：{converter.Result ?? "null"}");
        return (int?)converter.Result;
    }

    /// <summary>
    ///     显示多选选择框并返回勾选的所有项索引（从 0 开始）。若点击第二个按钮，则返回 Nothing。
    /// </summary>
    /// <param name="selections">需要展示的可勾选列表项。</param>
    /// <param name="title">弹窗的标题。</param>
    /// <param name="button1">显示的第一个按钮，默认为 “确定”。</param>
    /// <param name="button2">显示的第二个按钮，默认为空。</param>
    /// <param name="isWarn">是否为警告弹窗，若为 True，弹窗配色和背景会变为红色。</param>
    public static List<int>? MyMsgBoxMultiSelect(List<MyListItem> selections, string? title = null,
        string? button1 = null, string? button2 = "", bool isWarn = false)
    {
        title ??= GetDefaultDialogTitle();
        button1 ??= GetDefaultConfirmText();
        button2 ??= "";
        // 将弹窗列入队列
        var converter = new MyMsgBoxConverter
        {
            Type = MyMsgBoxType.Select, MultiSelect = true, Button1 = button1, Button2 = button2, Content = selections,
            IsWarn = isWarn, Title = title
        };
        WaitingMyMsgBox.Add(converter);
        // 虽然我也不知道这是啥但是能用就成了 :)
        try
        {
            if (frmMain is not null)
                frmMain.DragStop();
            ComponentDispatcher.PushModal();
            Dispatcher.PushFrame(converter.WaitFrame);
        }
        finally
        {
            ComponentDispatcher.PopModal();
        }

        ModBase.Log($"[Control] 多选弹框返回：{converter.Result ?? "null"}");
        return (List<int>?)converter.Result;
    }


    public static void MyMsgBoxTick()
    {
        try
        {
            if (frmMain is null || frmMain.PanMsg is null || frmMain.WindowState == WindowState.Minimized)
                return;
            if (frmMain.PanMsg.Children.Count > 0)
            {
                // 弹窗中
                frmMain.PanMsgBackground.Visibility = Visibility.Visible;
            }
            else if (WaitingMyMsgBox.Any())
            {
                // 没有弹窗，显示一个等待的弹窗
                frmMain.PanMsgBackground.Visibility = Visibility.Visible;
                switch (WaitingMyMsgBox[0].Type)
                {
                    case MyMsgBoxType.Input:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgInput(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Select:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgSelect(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Text:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgText(WaitingMyMsgBox[0]));
                        break;
                    }
                    case MyMsgBoxType.Markdown:
                    {
                        frmMain.PanMsg.Children.Add(new MyMsgMarkdown(WaitingMyMsgBox[0]));
                        break;
                    }
                }

                WaitingMyMsgBox.RemoveAt(0);
            }
            // 没有弹窗，没有等待的弹窗
            else if (!(frmMain.PanMsgBackground.Visibility == Visibility.Collapsed))
            {
                frmMain.PanMsgBackground.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "处理等待中的弹窗失败",
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }

    public static void MsgBoxWrapper_OnShow(string message, string caption, ICollection<MsgBoxButtonInfo> buttons,
        MsgBoxTheme theme, bool block, ref int result)
    {
        var btnText1 = buttons.Count < 1 ? GetDefaultConfirmText() : buttons.ElementAt(0).Context;
        var btnAct1 = (Action)(buttons.Count < 1 ? (object)null : buttons.ElementAt(0).OnClick);
        var btnText2 = buttons.Count < 2 ? GetDefaultCancelText() : buttons.ElementAt(1).Context;
        var btnAct2 = (Action)(buttons.Count < 2 ? (object)null : buttons.ElementAt(1).OnClick);
        var btnText3 = buttons.Count < 3 ? "" : buttons.ElementAt(2).Context;
        var btnAct3 = (Action)(buttons.Count < 3 ? (object)null : buttons.ElementAt(2).OnClick);

        var isWarn = theme == MsgBoxTheme.Warning || theme == MsgBoxTheme.Error;

        result = MyMsgBox(message, caption, btnText1, btnText2, btnText3, isWarn, forceWait: block,
            button1Action: btnAct1, button2Action: btnAct2, button3Action: btnAct3);
    }

    #endregion


    #region 系统

    /// <summary>
    ///     把某个 PCL 窗口拖到最前面。
    /// </summary>
    public static void ShowWindowToTop(nint handle)
    {
        try
        {
            PostMessage(handle, 400 * 16 + 2, 0L, 0L);
            SetForegroundWindow(handle); // 不在这里放不行，神秘 WinAPI，建议别动
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "设置窗口置顶失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Main.Error.OperationFailed"));
        }
    }

    [DllImport("user32", EntryPoint = "FindWindowA")]
    public static extern nint FindWindow(string className, string windowName);

    [DllImport("user32")]
    public static extern int SetForegroundWindow(nint hWnd);

    [DllImport("user32", EntryPoint = "PostMessageA")]
    private static extern bool PostMessage(nint hWnd, uint msg, long wParam, long lParam);

    /// <summary>
    ///     将特定程序设置为使用高性能显卡启动。
    ///     如果失败，则抛出异常。
    /// </summary>
    public static void SetGPUPreference(string executeable, bool wantHighPerformance = true)
    {
        const string GPU_PERFERENCE_REG_KEY = @"Software\Microsoft\DirectX\UserGpuPreferences";
        const string GPU_PERFERENCE_REG_VALUE_HIGH = "GpuPreference=2;";
        const string GPU_PERFERENCE_REG_VALUE_DEFAULT = "GpuPreference=0;";
        // Const GPU_PERFERENCE_REG_VALUE_POWER_SAVING As String = "GpuPreference=1;"

        var isCurrentHighPerformance = false;
        // 查看现有设置
        // 就知道 My.Computer，改个注册表 Microsoft.Win32.Registry 几年前的 API 了不用，还在这 My.Computer 都 5202 年了 My 你大爷
        using (var readOnlyKey = Registry.CurrentUser.OpenSubKey(GPU_PERFERENCE_REG_KEY, false))
        {
            if (readOnlyKey is not null)
            {
                var currentValue = readOnlyKey.GetValue(executeable);
                if (GPU_PERFERENCE_REG_VALUE_HIGH == (currentValue?.ToString() ?? "")) isCurrentHighPerformance = true;
            }
            else
            {
                // 创建父级键
                ModBase.Log("[System] 需要创建显卡设置的父级键");
                Registry.CurrentUser.CreateSubKey(GPU_PERFERENCE_REG_KEY);
            }
        }

        ModBase.Log($"[System] 当前程序 ({executeable}) 的显卡设置为高性能: {isCurrentHighPerformance}");
        if (isCurrentHighPerformance ^ wantHighPerformance)
            // 写入新设置
            using (var writeKey = Registry.CurrentUser.OpenSubKey(GPU_PERFERENCE_REG_KEY, true))
            {
                writeKey.SetValue(executeable,
                    wantHighPerformance ? GPU_PERFERENCE_REG_VALUE_HIGH : GPU_PERFERENCE_REG_VALUE_DEFAULT);
                ModBase.Log($"[System] 已调整程序 ({executeable}) 显卡设置: {wantHighPerformance}");
            }
    }

    /// <summary>
    /// 对替换标记进行处理。会对替换内容使用 EscapeHandler 进行转义。
    /// /// </summary>
    public static string ArgumentReplace(string text, Func<string, string> escapeHandler = null, bool replaceTime = true) 
    {
    // 预处理
    if (text is null) return null;
    
    Func<string, string> replacer = (s) =>
    {
        if (s is null) return "";
        if (escapeHandler is null) return s;
        if (s.Contains(":\\")) s = ModBase.ShortenPath(s);
        return escapeHandler(s);
    };
    
    // 基础
    text = text.Replace("{pcl_version}", replacer(ModBase.versionBaseName));
    text = text.Replace("{pcl_version_code}", replacer(ModBase.versionCode.ToString()));
    text = text.Replace("{pcl_version_branch}", replacer(ModBase.versionBranchName));
    text = text.Replace("{pcl_branch}", replacer(ModBase.versionBranchName));
    text = text.Replace("{identify}", replacer(Identify.LauncherId));
    text = text.Replace("{path}", replacer(Basics.ExecutableDirectory));
    text = text.Replace("{path_with_name}", replacer(Basics.ExecutableName));
    text = text.Replace("{path_temp}", replacer(ModBase.pathTemp));
    
    // 时间
    if (replaceTime) // 在窗口标题中，时间会被后续动态替换，所以此时不应该替换
    {
        text = text.Replace("{date}", replacer(Lang.Date(DateTime.Now, "d")));
        text = text.Replace("{time}", replacer(Lang.Date(DateTime.Now, "T")));
    }
    
    // 高级
    text = ModBase.RegexReplaceEach(text, @"\{setup:([a-zA-Z0-9]+)\}", m =>
    {
        if (ConfigService.TryGetConfigItemNoType(m.Groups[1].Value, out var item) && item.Source != ConfigSource.SharedEncrypt)
            return replacer(item.GetValueNoType(null)?.ToString() ?? "");
        return replacer("");
    });
    text = ModBase.RegexReplaceEach(text, @"\{varible:([^:\}]+)(?::([^\}]+))?\}", m => replacer(CustomEvent.GetCustomVariable(m.Groups[1].Value, m.Groups[2].Value)));
    text = ModBase.RegexReplaceEach(text, @"\{variable:([^:\}]+)(?::([^\}]+))?\}", m => replacer(CustomEvent.GetCustomVariable(m.Groups[1].Value, m.Groups[2].Value)));
    
    return text;
}
    #endregion

    #region 任务缓存

    private static bool isTaskTempCleared;
    private static bool isTaskTempClearing;

    /// <summary>
    ///     尝试清理任务缓存文件夹。
    ///     在整次运行中只会实际清理一次。
    /// </summary>
    public static void TryClearTaskTemp()
    {
        if (!isTaskTempCleared)
        {
            isTaskTempCleared = true;
            isTaskTempClearing = true;
            try
            {
                ModBase.Log("[System] 开始清理任务缓存文件夹");
                ModBase.DeleteDirectory(Path.Combine(SystemPaths.DriveLetter, "ProgramData", "PCL", "TaskTemp"));
                ModBase.DeleteDirectory($@"{ModBase.pathTemp}TaskTemp\");
                ModBase.Log("[System] 已清理任务缓存文件夹");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "清理任务缓存文件夹失败");
            }
            finally
            {
                isTaskTempClearing = false;
            }
        }
        else if (isTaskTempClearing)
        {
            // 等待另一个清理步骤完成
            while (isTaskTempClearing)
                Thread.Sleep(1);
        }
    }

    /// <summary>
    ///     申请一个可用于任务缓存的临时文件夹，以 \ 结尾。这些文件夹无需进行后续清理。
    ///     若所有缓存位置均没有权限，会抛出异常。
    /// </summary>
    /// <param name="requireNonSpace">是否要求路径不包含空格。</param>
    public static string RequestTaskTempFolder(bool requireNonSpace = false)
    {
        TryClearTaskTemp();
        string resultFolder;
        do
        {
            try
            {
                resultFolder = $@"{ModBase.pathTemp}TaskTemp\{ModBase.GetUuid()}-{RandomUtils.NextInt(0, 1000000)}\";
                if (requireNonSpace && resultFolder.Contains(" "))
                    break; // 带空格
                Directory.CreateDirectory(resultFolder);
                ModBase.CheckPermissionWithException(resultFolder);
                return resultFolder;
            }
            catch
            {
            }
        } while (false);

        // 使用备用路径
        resultFolder =
            Path.Combine(SystemPaths.DriveLetter, "ProgramData", "PCL", "TaskTemp", $"{ModBase.GetUuid()}-{RandomUtils.NextInt(0, 1000000)}");
        Directory.CreateDirectory(resultFolder);
        ModBase.CheckPermission(resultFolder);
        return resultFolder;
    }

    #endregion
    
    public static void RaiseCustomEvent(DependencyObject control)
    {
        // 收集事件列表
        var events = CustomEventService.GetEvents(control).ToList();
        var eventType = CustomEventService.GetEventType(control);
        if (eventType != EventType.None)
            events.Add(new CustomEvent(eventType, CustomEventService.GetEventData(control)));

        if (!events.Any()) return;

        ModBase.RunInNewThread(() =>
            {
                foreach (var e in events)
                    e.Raise();
            }, $"执行自定义事件 {ModBase.GetUuid()}");
    }
}
