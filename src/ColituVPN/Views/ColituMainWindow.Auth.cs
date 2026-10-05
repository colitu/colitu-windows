using System.Net.Mail;
using System.Windows.Controls;
using v2rayN.Services;

namespace v2rayN.Views;

public partial class ColituMainWindow
{
    private const int MinPasswordLength = 10;
    private bool _registerMode;
    private bool _passwordVisible;
    private bool _authBusy;

    private void ShowAuth()
    {
        _refresh.Stop();
        ShowView(AuthView);
        PasswordBox.Clear();
        PasswordPlainBox.Clear();
        PasswordRepeatBox.Clear();
        HideAuthError();
        ApplyAuthMode();
        Dispatcher.BeginInvoke(() => (string.IsNullOrWhiteSpace(EmailBox.Text) ? (Control)EmailBox : PasswordBox).Focus(), DispatcherPriority.Input);
    }

    private void AuthTab_Checked(object sender, RoutedEventArgs e)
    {
        if (AuthRegisterTab == null)
        {
            return;
        }
        _registerMode = AuthRegisterTab.IsChecked == true;
        if (IsLoaded)
        {
            HideAuthError();
            ApplyAuthMode();
            FadeIn(AuthCard, 8);
        }
    }

    private void ApplyAuthMode()
    {
        if (AuthTitle == null)
        {
            return;
        }
        AuthTitle.Text = Loc.I[_registerMode ? "auth.registerTitle" : "auth.loginTitle"];
        AuthSubtitle.Text = Loc.I[_registerMode ? "auth.registerSub" : "auth.loginSub"];
        AuthSubmitText.Text = Loc.I[_registerMode ? "auth.submitRegister" : "auth.submitLogin"];
        RegisterFields.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        ForgotButton.Visibility = _registerMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPassword_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;
        if (_passwordVisible)
        {
            PasswordPlainBox.Text = PasswordBox.Password;
            PasswordPlainBox.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordPlainBox.Focus();
            PasswordPlainBox.CaretIndex = PasswordPlainBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordPlainBox.Text;
            // A hidden TextBox would keep the password in plain text for nothing.
            PasswordPlainBox.Clear();
            PasswordBox.Visibility = Visibility.Visible;
            PasswordPlainBox.Visibility = Visibility.Collapsed;
            PasswordBox.Focus();
        }
        ShowPasswordButton.Opacity = _passwordVisible ? 1 : 0.8;
    }

    private string CurrentPassword => _passwordVisible ? PasswordPlainBox.Text : PasswordBox.Password;

    private async void AuthField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitAuthAsync();
        }
    }

    private async void AuthSubmit_Click(object sender, RoutedEventArgs e) => await SubmitAuthAsync();

    private async Task SubmitAuthAsync()
    {
        if (_authBusy)
        {
            return;
        }

        var email = EmailBox.Text.Trim();
        var password = CurrentPassword;
        var error = ValidateAuth(email, password);
        if (error != null)
        {
            ShowAuthError(error);
            return;
        }

        SetAuthBusy(true);
        HideAuthError();
        try
        {
            var result = _registerMode
                ? await _auth.RegisterAsync("", email, password)
                : await _auth.LoginAsync(email, password);
            if (!result.Success)
            {
                ShowAuthError(result.Error ?? Loc.I["err.generic"]);
                return;
            }

            PasswordBox.Clear();
            PasswordPlainBox.Clear();
            PasswordRepeatBox.Clear();
            if (result.RequiresEmailVerification)
            {
                ShowVerify(codeJustSent: true);
                return;
            }
            await EnterAppAsync(offline: false);
            if (_registerMode && _auth.CurrentSubscription?.Active == true)
            {
                ShowToast(PlanTitle(_auth.CurrentSubscription) + " · " + PlanDetailText(_auth.CurrentSubscription));
            }
        }
        catch (Exception ex)
        {
            ShowAuthError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
        }
        finally
        {
            SetAuthBusy(false);
        }
    }

    private string? ValidateAuth(string email, string password)
    {
        if (!IsEmail(email))
        {
            return Loc.I["auth.err.email"];
        }
        if (password.Length < MinPasswordLength && _registerMode)
        {
            return Loc.I["auth.err.password"];
        }
        if (password.Length == 0)
        {
            return Loc.I["auth.err.password"];
        }
        if (_registerMode && PasswordRepeatBox.Password != password)
        {
            return Loc.I["auth.err.mismatch"];
        }
        if (_registerMode && TermsCheck.IsChecked != true)
        {
            return Loc.I["auth.err.terms"];
        }
        return null;
    }

    private static bool IsEmail(string value)
    {
        if (value.Length is < 3 or > 254 || !value.Contains('@'))
        {
            return false;
        }
        try
        {
            return new MailAddress(value).Address == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void SetAuthBusy(bool busy)
    {
        _authBusy = busy;
        AuthSubmitButton.IsEnabled = !busy;
        AuthSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        EmailBox.IsEnabled = PasswordBox.IsEnabled = PasswordPlainBox.IsEnabled = PasswordRepeatBox.IsEnabled = !busy;
        AuthLoginTab.IsEnabled = AuthRegisterTab.IsEnabled = !busy;
        ForgotButton.IsEnabled = !busy;
    }

    private void ShowAuthError(string message)
    {
        AuthErrorText.Text = message;
        AuthErrorBox.Visibility = Visibility.Visible;
        FadeIn(AuthErrorBox, 6);
    }

    private void HideAuthError() => AuthErrorBox.Visibility = Visibility.Collapsed;

    private void Forgot_Click(object sender, RoutedEventArgs e) => ShowReset(EmailBox.Text.Trim());

    private void TermsLink_Click(object sender, RoutedEventArgs e) => OpenUrl(LocalizedPath("/legal/terms"));

    private bool _signingOut;

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_signingOut)
        {
            return;
        }
        _signingOut = true;
        try
        {
            await _vpn.ForgetAccountAsync();
        }
        catch (Exception ex)
        {
            // Disconnecting failed: sign out all the same, a half signed-out app is worse.
            Logging.SaveLog("ColituMainWindow.SignOut", ex);
        }
        try
        {
            ClearAccountViews();
            await _auth.LogoutAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SignOut", ex);
        }
        finally
        {
            _signingOut = false;
            AuthLoginTab.IsChecked = true;
            ShowAuth();
        }
    }
}
