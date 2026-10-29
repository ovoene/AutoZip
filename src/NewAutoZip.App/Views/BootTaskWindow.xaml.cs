using System.Windows;

namespace NewAutoZip.App.Views;

/// <summary>
/// 注册开机自启时问 Windows 密码的那个小窗。
///
/// 只负责"问密码、显示结果"：真正的注册由构造时传进来的 <c>register</c> 去做，
/// 它返回 null 表示成功，否则返回一句给用户看的原因 —— 这时窗口不关，
/// 就地显示原因，让用户改了密码再点一次（输错密码是最常见的失败，关掉重开太折腾）。
/// </summary>
public partial class BootTaskWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Func<string, string?> _register;
    private bool _busy;

    internal BootTaskWindow(string account, string exePath, Func<string, string?> register)
    {
        _register = register;

        InitializeComponent();

        AccountText.Text = account;
        ExeText.Text = exePath;

        Loaded += (_, _) => PasswordInput.Focus();
    }

    private async void OnRegister(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        string password = PasswordInput.Password ?? string.Empty;

        if (password.Length == 0)
        {
            ShowError("请填这个账户的 Windows 登录密码。Windows 不允许空密码账户在无人登录时运行任务。");
            return;
        }

        SetBusy(true);

        string? error;

        try
        {
            // 注册是一次跨进程的 COM 调用，慢的时候要一两秒 —— 别卡住窗口。
            error = await Task.Run(() => _register(password)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        if (error is null)
        {
            // 成功后不在界面上多留一秒密码。
            PasswordInput.Password = string.Empty;
            DialogResult = true;
            return;
        }

        ShowError(error);
        PasswordInput.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            DialogResult = false;
        }
    }

    /// <summary>注册进行到一半时不让关：关了也停不下那次 COM 调用，只会让结果无处可报。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RegisterButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;
        RegisterButton.Content = busy ? "正在注册…" : "注册";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
