using v2rayN.Services;

namespace v2rayN.Views;

/// <summary>
/// "Forgot password": the address gets a six-digit code, and the code plus a
/// new password sign this computer in. The panel ends every other session.
/// </summary>
public partial class ColituMainWindow
{
    private DispatcherTimer? _resetTimer;
    private int _resetSeconds;
    private bool _resetCodeSent;
    private bool _resetBusy;

    private void ShowReset(string email)
    {
        _refresh.Stop();
        ShowView(ResetView);
        ResetEmailBox.Text = email;
        ResetCodeBox.Clear();
        ResetPasswordBox.Clear();
        ResetRepeatBox.Clear();
        _resetCodeSent = false;
        HideResetMessages();
        ApplyResetState();
        Dispatcher.BeginInvoke(() => ResetEmailBox.Focus(), DispatcherPriority.Input);
    }

    private void ApplyResetState()
    {
        ResetSubtitle.Text = Loc.I[_resetCodeSent ? "reset.codeSub" : "reset.sub"];
        ResetSubmitText.Text = Loc.I[_resetCodeSent ? "reset.submit" : "reset.send"];
        var codeStep = _resetCodeSent ? Visibility.Visible : Visibility.Collapsed;
        ResetCodeFields.Visibility = ResetResendButton.Visibility = ResetChangeEmailButton.Visibility = ResetNote.Visibility = codeStep;
        ResetEmailBox.IsEnabled = !_resetCodeSent && !_resetBusy;
        UpdateResetResendText();
    }

    private async void ResetField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitResetAsync();
        }
    }

    private async void ResetSubmit_Click(object sender, RoutedEventArgs e) => await SubmitResetAsync();

    private async Task SubmitResetAsync()
    {
        if (_resetCodeSent)
        {
            await ChangePasswordWithCodeAsync();
        }
        else
        {
            await SendResetCodeAsync();
        }
    }

    private async void ResetResend_Click(object sender, RoutedEventArgs e)
    {
        if (_resetSeconds <= 0)
        {
            await SendResetCodeAsync();
        }
    }

    private async Task SendResetCodeAsync()
    {
        if (_resetBusy)
        {
            return;
        }
        var email = ResetEmailBox.Text.Trim();
        if (!IsEmail(email))
        {
            ShowResetError(Loc.I["auth.err.email"]);
            return;
        }
        SetResetBusy(true);
        HideResetMessages();
        try
        {
            await _auth.RequestPasswordResetAsync(email);
            _resetCodeSent = true;
            StartResetCooldown();
            ApplyResetState();
            ShowResetInfo(Loc.I.Format("reset.sent", ("email", email)));
            _ = Dispatcher.BeginInvoke(() => ResetCodeBox.Focus(), DispatcherPriority.Input);
        }
        catch (ColituApiException ex)
        {
            ShowResetError(ex.Message);
            if (ex.ErrorCode is "VERIFICATION_RATE_LIMITED")
            {
                _resetCodeSent = true;
                StartResetCooldown();
                ApplyResetState();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SendResetCode", ex);
            ShowResetError(Loc.I["err.network"]);
        }
        finally
        {
            SetResetBusy(false);
        }
    }

    private async Task ChangePasswordWithCodeAsync()
    {
        if (_resetBusy)
        {
            return;
        }
        var code = new string(ResetCodeBox.Text.Where(char.IsAsciiDigit).ToArray());
        var password = ResetPasswordBox.Password;
        var error = code.Length != 6 ? Loc.I["verify.err.length"]
            : password.Length < MinPasswordLength ? Loc.I["auth.err.password"]
            : ResetRepeatBox.Password != password ? Loc.I["auth.err.mismatch"]
            : null;
        if (error != null)
        {
            ShowResetError(error);
            return;
        }
        SetResetBusy(true);
        HideResetMessages();
        try
        {
            var result = await _auth.ResetPasswordAsync(ResetEmailBox.Text.Trim(), code, password);
            if (!result.Success)
            {
                ShowResetError(result.Error ?? Loc.I["err.generic"]);
                return;
            }
            ResetPasswordBox.Clear();
            ResetRepeatBox.Clear();
            ResetCodeBox.Clear();
            _resetTimer?.Stop();
            EmailBox.Text = ResetEmailBox.Text.Trim();
            if (result.RequiresEmailVerification)
            {
                ShowVerify(codeJustSent: true);
                return;
            }
            await EnterAppAsync(offline: false);
            ShowToast(Loc.I["reset.done"]);
        }
        catch (Exception ex)
        {
            ShowResetError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
        }
        finally
        {
            SetResetBusy(false);
        }
    }

    private void ResetChangeEmail_Click(object sender, RoutedEventArgs e)
    {
        _resetCodeSent = false;
        ResetCodeBox.Clear();
        HideResetMessages();
        ApplyResetState();
        ResetEmailBox.Focus();
    }

    private void ResetBack_Click(object sender, RoutedEventArgs e)
    {
        _resetTimer?.Stop();
        if (IsEmail(ResetEmailBox.Text.Trim()))
        {
            EmailBox.Text = ResetEmailBox.Text.Trim();
        }
        AuthLoginTab.IsChecked = true;
        ShowAuth();
    }

    private void StartResetCooldown()
    {
        _resetSeconds = ResendCooldownSeconds;
        if (_resetTimer == null)
        {
            _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resetTimer.Tick += (_, _) =>
            {
                if (--_resetSeconds <= 0)
                {
                    _resetTimer.Stop();
                }
                UpdateResetResendText();
            };
        }
        _resetTimer.Start();
        UpdateResetResendText();
    }

    private void UpdateResetResendText()
    {
        ResetResendText.Text = _resetSeconds > 0 ? Loc.I.Format("verify.resendIn", ("n", _resetSeconds)) : Loc.I["verify.resend"];
        ResetResendButton.IsEnabled = _resetSeconds <= 0 && !_resetBusy;
    }

    private void SetResetBusy(bool busy)
    {
        _resetBusy = busy;
        ResetSubmitButton.IsEnabled = !busy;
        ResetSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ResetCodeBox.IsEnabled = ResetPasswordBox.IsEnabled = ResetRepeatBox.IsEnabled = !busy;
        ResetEmailBox.IsEnabled = !busy && !_resetCodeSent;
        UpdateResetResendText();
    }

    private void ShowResetError(string message)
    {
        ResetInfoBox.Visibility = Visibility.Collapsed;
        ResetErrorText.Text = message;
        ResetErrorBox.Visibility = Visibility.Visible;
        FadeIn(ResetErrorBox, 6);
    }

    private void ShowResetInfo(string message)
    {
        ResetInfoText.Text = message;
        ResetInfoBox.Visibility = Visibility.Visible;
        FadeIn(ResetInfoBox, 6);
    }

    private void HideResetMessages()
    {
        ResetErrorBox.Visibility = Visibility.Collapsed;
        ResetInfoBox.Visibility = Visibility.Collapsed;
    }
}
