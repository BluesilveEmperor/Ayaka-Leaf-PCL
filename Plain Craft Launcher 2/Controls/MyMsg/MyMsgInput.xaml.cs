using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PCL.Core.UI.Controls;

using PCL.Core.App.Localization;
namespace PCL;

public partial class MyMsgInput
{
    private readonly ModMain.MyMsgBoxConverter myConverter;
    private readonly int uuid = ModBase.GetUuid();

    /// <summary>
    ///     密码模式下遮蔽输入框是否可用。不可用时必须禁止提交，不能退回明文输入。
    /// </summary>
    private bool passwordInputAvailable = true;

    public MyMsgInput(ModMain.MyMsgBoxConverter converter)
    {
        myConverter = converter;
        try
        {
            InitializeComponent();
            AppendUniqueNameSuffix(Btn1);
            AppendUniqueNameSuffix(Btn2);
            LabTitle.Text = converter.Title;
            LabText.Text = converter.Text;
            PanText.Visibility = string.IsNullOrEmpty(converter.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (converter.IsPassword)
            {
                // 密码模式：隐藏明文输入框，改用遮蔽输入框，且不设置任何提示文本
                TextArea.Visibility = Visibility.Collapsed;
                var passwordArea = PasswordArea;
                passwordInputAvailable = passwordArea is not null;
                if (passwordArea is not null)
                {
                    passwordArea.Visibility = Visibility.Visible;
                    passwordArea.Password = converter.Content as string ?? "";
                }
            }
            else
            {
                TextArea.Text = (string)converter.Content;
                TextArea.HintText = converter.HintText;
            }

            TextArea.ValidateRules = converter.ValidateRules;
            ConfigurePrimaryButton(converter.Button1, converter.IsWarn);
            ConfigureSecondaryButton(converter.Button2);
            ShapeLine.StrokeThickness = ModBase.GetWPFSize(1d);
            if (converter.IsPassword && !passwordInputAvailable)
                ApplyPasswordFallback("密码输入框不可用，已禁止提交");
        }

        catch (Exception ex)
        {
            if (converter.IsPassword)
                ApplyPasswordFallback("密码输入弹窗初始化失败，已禁止提交");
            ModBase.Log(
                ex,
                "输入弹窗初始化失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }

        Loaded += Load;
    }

    /// <summary>
    ///     密码模式的安全兜底：隐藏明文输入框并禁用输入与提交，避免敏感内容回显。
    /// </summary>
    private void ApplyPasswordFallback(string reason)
    {
        passwordInputAvailable = false;
        try
        {
            if (TextArea is not null)
            {
                TextArea.Visibility = Visibility.Collapsed;
                TextArea.IsEnabled = false;
            }

            if (PasswordArea is not null)
                PasswordArea.IsEnabled = false;

            if (Btn1 is not null)
                Btn1.IsEnabled = false;
            ModBase.Log("[Control] " + reason, ModBase.LogLevel.Hint);
        }

        catch (Exception ex)
        {
            ModBase.Log(ex, "密码输入弹窗安全兜底失败", ModBase.LogLevel.Hint);
        }
    }

    private void AppendUniqueNameSuffix(FrameworkElement element)
    {
        element.Name += ModBase.GetUuid();
    }

    private void ConfigurePrimaryButton(string text, bool isWarn)
    {
        Btn1.Text = text;
        if (isWarn)
        {
            Btn1.ColorType = MyButton.ColorState.Red;
            LabTitle.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushRedLight");
        }
    }

    private void ConfigureSecondaryButton(string text)
    {
        Btn2.Text = text;
        Btn2.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Load(object sender, EventArgs e)
    {
        try
        {
            // UI 初始化
            if (Btn2.IsVisible && !(Btn1.ColorType == MyButton.ColorState.Red))
                Btn1.ColorType = MyButton.ColorState.Highlight;
            if (myConverter.IsPassword)
            {
                // 密码模式聚焦遮蔽输入框
                PasswordArea?.Focus();
            }
            else
            {
                TextArea.Focus();
                TextArea.SelectionStart = TextArea.Text.Length;
            }
            // 动画
            Opacity = 0d;
            ModAnimation.AniStart(
                ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground, BlurBorder.BackgroundProperty,
                    (myConverter.IsWarn
                        ? new ModBase.MyColor(140d, 80d, 0d, 0d)
                        : new ModBase.MyColor(90d, 0d, 0d, 0d)) - ModMain.frmMain.PanMsgBackground.Background, 200),
                "PanMsgBackground Background");
            ModAnimation.AniStart(
                new[]
                {
                    ModAnimation.AaOpacity(this, 1d, 120, 60),
                    ModAnimation.AaDouble(i => TransformPos.Y += (double)i,
                        -TransformPos.Y, 300, 60, new ModAnimation.AniEaseOutBack(ModAnimation.AniEasePower.Weak)),
                    ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i,
                        -TransformRotate.Angle, 300, 60,
                        new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak))
                }, "MyMsgBox " + uuid);
            // 记录日志
            ModBase.Log("[Control] 输入弹窗：" + LabTitle.Text);
        }

        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "输入弹窗加载失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }

    private void Close()
    {
        // 结束线程阻塞
        myConverter.WaitFrame.Continue = false;
        ComponentDispatcher.PopModal();
        // 动画
        ModAnimation.AniStart(new[]
        {
            ModAnimation.AaCode(() =>
            {
                if (!ModMain.WaitingMyMsgBox.Any())
                    ModAnimation.AniStart(ModAnimation.AaColor(ModMain.frmMain.PanMsgBackground,
                        BlurBorder.BackgroundProperty,
                        new ModBase.MyColor(0d, 0d, 0d, 0d) - ModMain.frmMain.PanMsgBackground.Background, 200,
                        ease: new ModAnimation.AniEaseOutFluent(ModAnimation.AniEasePower.Weak)));
            }, 30),
            ModAnimation.AaOpacity(this, -Opacity, 80, 20),
            ModAnimation.AaDouble(i => TransformPos.Y += (double)i, 20d - TransformPos.Y,
                150, 0, new ModAnimation.AniEaseOutFluent()),
            ModAnimation.AaDouble(i => TransformRotate.Angle += (double)i,
                6d - TransformRotate.Angle, 150, 0, new ModAnimation.AniEaseInFluent(ModAnimation.AniEasePower.Weak)),
            ModAnimation.AaCode(() => ((Grid)Parent).Children.Remove(this), after: true)
        }, "MyMsgBox " + uuid);
    }

    public void Btn1_Click(object sender, MouseButtonEventArgs e)
    {
        string text;
        if (myConverter.IsPassword)
        {
            // 密码模式跳过明文本框校验，直接提交遮蔽输入框中的原文
            if (myConverter.IsExited || !passwordInputAvailable)
                return;
            text = PasswordArea?.Password ?? "";
        }
        else
        {
            TextArea.Validate(); // #5773
            if (myConverter.IsExited || !TextArea.IsValidated)
                return;
            text = TextArea.Text;
        }

        myConverter.IsExited = true;
        myConverter.Result = text;
        Close();
    }

    public void Btn2_Click(object sender, MouseButtonEventArgs e)
    {
        if (myConverter.IsExited)
            return;
        myConverter.IsExited = true;
        myConverter.Result = null;
        Close();
    }

    private void TextCaption_ValidateChanged(object sender, EventArgs e)
    {
        Btn1.IsEnabled = TextArea.IsValidated;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                if (e.GetPosition(ShapeLine).Y <= 2d)
                    ModMain.frmMain.DragMove();
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                "拖拽移动失败",
                ModBase.LogLevel.Hint,
                userSummary: Lang.Text("Application.Control.MessageBox.Error.OperationFailed"));
        }
    }
}
