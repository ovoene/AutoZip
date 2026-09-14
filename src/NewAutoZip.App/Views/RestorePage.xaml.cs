using System.IO;
using Microsoft.Win32;
using NewAutoZip.App.ViewModels;

namespace NewAutoZip.App.Views;

/// <summary>
/// 恢复页。
///
/// 只有"选目录"这一件事留在 code-behind 里：文件对话框要一个窗口句柄，
/// 而 ViewModel 里不该出现任何 <c>System.Windows</c> 的东西
/// —— 设置页的几个"浏览…"按钮也是这么做的。
/// </summary>
public partial class RestorePage : System.Windows.Controls.UserControl
{
    public RestorePage() => InitializeComponent();

    private MainViewModel? Model => DataContext as MainViewModel;

    private void OnPickTarget(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Model is not { } model) { return; }

        OpenFolderDialog dialog = new()
        {
            Title = "选择解压到哪个目录",
            Multiselect = false,
        };

        // 已填的路径若还存在就从那里开始，省得每次从"此电脑"翻起。
        if (!string.IsNullOrWhiteSpace(model.RestoreTarget) && Directory.Exists(model.RestoreTarget))
        {
            dialog.InitialDirectory = model.RestoreTarget;
        }

        if (dialog.ShowDialog() == true)
        {
            model.RestoreTarget = dialog.FolderName;
        }
    }
}
