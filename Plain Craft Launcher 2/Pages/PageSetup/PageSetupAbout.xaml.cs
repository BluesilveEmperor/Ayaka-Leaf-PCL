using System.Windows;

namespace PCL;

public partial class PageSetupAbout
{
    private new bool isLoaded;

    public PageSetupAbout()
    {
        InitializeComponent();
        Loaded += PageSetupAbout_Loaded;
    }

    private void PageSetupAbout_Loaded(object sender, RoutedEventArgs e)
    {
        PanBack.ScrollToHome();

        if (isLoaded)
            return;
        isLoaded = true;

        TextAboutVersion.Text = "当前版本：" + ModBase.versionBaseName;
    }
}
