using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using v2rayN.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace v2rayN.Views;

/// <summary>
/// Live support: the conversation list, the chat thread with attachments and
/// the unread badge on the floating launcher. The thread refreshes every few
/// seconds while it is on screen; the badge checks in the background.
/// </summary>
public partial class ColituMainWindow
{
    private readonly ColituSupportService _support = ColituSupportService.Instance;
    private readonly Dictionary<string, BitmapImage> _supportImages = [];
    private readonly List<string> _supportNewFiles = [];
    private readonly List<string> _supportReplyFiles = [];
    private List<ColituSupportConversation> _supportConversations = [];
    private DispatcherTimer? _supportUnreadTimer;
    private DispatcherTimer? _supportThreadTimer;
    private ColituSupportConversation? _supportCurrent;
    private string? _supportSignature;
    private int _supportUnread = -1;
    private bool _supportBusy;
    private bool _supportSelecting;
    private bool _supportPolling;

    // ── Polling and the badge ──────────────────────────────────────────────
    private void StartSupportPolling()
    {
        _supportUnreadTimer ??= NewTimer(TimeSpan.FromSeconds(45), async () => await CheckSupportUnreadAsync());
        _supportUnreadTimer.Start();
        SupportLauncher.Visibility = Visibility.Visible;
        _ = CheckSupportUnreadAsync();
    }

    private void StopSupportPolling()
    {
        _supportUnreadTimer?.Stop();
        _supportThreadTimer?.Stop();
        _supportUnread = -1;
        _supportConversations = [];
        _supportCurrent = null;
        _supportSignature = null;
        _supportNewFiles.Clear();
        _supportReplyFiles.Clear();
        _supportImages.Clear();
        SupportBadge.Visibility = Visibility.Collapsed;
        SupportLauncher.Visibility = Visibility.Collapsed;
        SupportList.ItemsSource = null;
        SupportMessages.Children.Clear();
        ShowSupportPane(SupportPlaceholder);
    }

    /// <summary>The thread refreshes quickly only while the support page shows it.</summary>
    private void UpdateSupportPolling()
    {
        _supportThreadTimer ??= NewTimer(TimeSpan.FromSeconds(5), async () => await PollSupportThreadAsync());
        if (_page == "support" && _supportCurrent != null && SupportThread.Visibility == Visibility.Visible)
        {
            _supportThreadTimer.Start();
        }
        else
        {
            _supportThreadTimer.Stop();
        }
    }

    private DispatcherTimer NewTimer(TimeSpan interval, Func<Task> tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += async (_, _) => await tick();
        return timer;
    }

    private async Task CheckSupportUnreadAsync()
    {
        if (!_auth.HasSession || AppView.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            var unread = await _support.UnreadAsync();
            if (_supportUnread >= 0 && unread > _supportUnread)
            {
                NotifySupportReply();
            }
            _supportUnread = unread;
            ApplySupportBadge();
            if (_page == "support" && unread > 0)
            {
                await LoadSupportListAsync(showSpinner: false);
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SUPPORT_UNAVAILABLE")
        {
            // Support switched off in the panel: hide the launcher until it returns.
            SupportLauncher.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.CheckSupportUnreadAsync", ex);
        }
    }

    private void NotifySupportReply()
    {
        var hidden = !IsVisible || WindowState == WindowState.Minimized;
        if (hidden)
        {
            TrayIcon.ShowNotification("Colitu VPN", Loc.I["support.newReply"]);
        }
        else if (_page != "support")
        {
            ShowToast(Loc.I["support.newReply"]);
            PulseSupportLauncher();
        }
    }

    private void ApplySupportBadge()
    {
        var count = Math.Max(0, _supportUnread);
        SupportBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SupportBadgeText.Text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
    }

    private void PulseSupportLauncher()
    {
        var pulse = new DoubleAnimation(1, 1.14, TimeSpan.FromMilliseconds(220)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), EasingFunction = Ease };
        SupportLauncherScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        SupportLauncherScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private void SupportLauncher_Click(object sender, RoutedEventArgs e) => Navigate("support");

    private void SupportHelp_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl($"https://docs.colitu.com/{Loc.I.Language}");
    }

    // ── Page and list ──────────────────────────────────────────────────────
    private async Task OpenSupportAsync()
    {
        await LoadSupportListAsync(showSpinner: _supportConversations.Count == 0);
        if (_supportCurrent != null && _supportConversations.Any(item => item.Id == _supportCurrent.Id))
        {
            await OpenSupportThreadAsync(_supportCurrent.Id);
        }
        else if (_supportConversations.Count > 0)
        {
            await OpenSupportThreadAsync(_supportConversations[0].Id);
        }
        else if (SupportNewForm.Visibility != Visibility.Visible)
        {
            ShowNewSupportForm();
        }
    }

    private async Task LoadSupportListAsync(bool showSpinner)
    {
        if (showSpinner)
        {
            SupportListSpinner.Visibility = Visibility.Visible;
            SupportListEmpty.Visibility = Visibility.Collapsed;
        }
        try
        {
            _supportConversations = (await _support.ListAsync())
                .OrderByDescending(item => item.LastMessageAt)
                .ToList();
            _supportUnread = _supportConversations.Sum(item => item.Unread);
            ApplySupportBadge();
            RenderSupportList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadSupportListAsync", ex);
            if (showSpinner)
            {
                ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
            }
        }
        finally
        {
            SupportListSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderSupportList()
    {
        if (SupportList == null)
        {
            return;
        }
        var rows = _supportConversations.Select(ColituSupportRow.From).ToList();
        _supportSelecting = true;
        try
        {
            SupportList.ItemsSource = rows;
            SupportList.SelectedItem = rows.FirstOrDefault(row => row.Id == _supportCurrent?.Id && SupportThread.Visibility == Visibility.Visible);
        }
        finally
        {
            _supportSelecting = false;
        }
        SupportListEmpty.Visibility = rows.Count == 0 && SupportListSpinner.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (_supportCurrent != null && SupportThread.Visibility == Visibility.Visible)
        {
            ApplySupportThreadHeader(_supportConversations.FirstOrDefault(item => item.Id == _supportCurrent.Id) ?? _supportCurrent);
        }
    }

    private async void SupportList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_supportSelecting || SupportList.SelectedItem is not ColituSupportRow row)
        {
            return;
        }
        await OpenSupportThreadAsync(row.Id);
    }

    private void ShowSupportPane(FrameworkElement pane)
    {
        foreach (var candidate in new FrameworkElement[] { SupportPlaceholder, SupportNewForm, SupportThread })
        {
            candidate.Visibility = candidate == pane ? Visibility.Visible : Visibility.Collapsed;
        }
        FadeIn(pane, 8);
        UpdateSupportPolling();
    }

    // ── New request ────────────────────────────────────────────────────────
    private void SupportNew_Click(object sender, RoutedEventArgs e) => ShowNewSupportForm();

    private void ShowNewSupportForm()
    {
        _supportSelecting = true;
        SupportList.SelectedItem = null;
        _supportSelecting = false;
        SupportSubjectBox.Clear();
        SupportMessageBox.Clear();
        _supportNewFiles.Clear();
        RenderFileChips(SupportNewFiles, _supportNewFiles);
        SupportDiagnostics.IsChecked = true;
        SupportErrorBox.Visibility = Visibility.Collapsed;
        ShowSupportPane(SupportNewForm);
        Dispatcher.BeginInvoke(() => SupportSubjectBox.Focus(), DispatcherPriority.Input);
    }

    private async void SupportCancelNew_Click(object sender, RoutedEventArgs e)
    {
        if (_supportCurrent != null)
        {
            await OpenSupportThreadAsync(_supportCurrent.Id);
        }
        else
        {
            ShowSupportPane(SupportPlaceholder);
        }
    }

    private void SupportAttachNew_Click(object sender, RoutedEventArgs e) => PickSupportFiles(_supportNewFiles, SupportNewFiles);

    private async void SupportCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_supportBusy)
        {
            return;
        }

        var subject = SupportSubjectBox.Text.Trim();
        var message = SupportMessageBox.Text.Trim();
        if (subject.Length == 0 || message.Length == 0)
        {
            ShowSupportError(Loc.I["support.err.subject"]);
            return;
        }

        SetSupportBusy(true, SupportCreateButton, SupportCreateSpinner);
        SupportErrorBox.Visibility = Visibility.Collapsed;
        try
        {
            var diagnostics = SupportDiagnostics.IsChecked == true
                ? await Task.Run(ColituSupportService.CollectDiagnostics)
                : null;
            var files = _supportNewFiles.ToList();
            var created = await _support.CreateAsync(subject, message, files, diagnostics);
            _supportNewFiles.Clear();
            ShowToast(Loc.I["support.sent"]);
            await LoadSupportListAsync(showSpinner: false);
            if (created != null)
            {
                await OpenSupportThreadAsync(created.Id);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SupportCreate", ex);
            ShowSupportError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
        }
        finally
        {
            SetSupportBusy(false, SupportCreateButton, SupportCreateSpinner);
        }
    }

    private void ShowSupportError(string message)
    {
        SupportErrorText.Text = message;
        SupportErrorBox.Visibility = Visibility.Visible;
        FadeIn(SupportErrorBox, 6);
    }

    // ── Thread ─────────────────────────────────────────────────────────────
    private async Task OpenSupportThreadAsync(string id)
    {
        var known = _supportConversations.FirstOrDefault(item => item.Id == id);
        if (_supportCurrent?.Id != id)
        {
            _supportSignature = null;
            SupportMessages.Children.Clear();
            _supportReplyFiles.Clear();
            RenderFileChips(SupportReplyFiles, _supportReplyFiles);
            SupportReplyBox.Clear();
        }
        _supportCurrent = known ?? _supportCurrent ?? new ColituSupportConversation { Id = id };
        if (known != null)
        {
            ApplySupportThreadHeader(known);
        }
        if (SupportThread.Visibility != Visibility.Visible)
        {
            ShowSupportPane(SupportThread);
        }
        await PollSupportThreadAsync(forceScroll: true);

        // Opening the thread marks the replies read on the panel.
        if (known is { Unread: > 0 })
        {
            known.Unread = 0;
            _supportUnread = _supportConversations.Sum(item => item.Unread);
            ApplySupportBadge();
            RenderSupportList();
        }
        UpdateSupportPolling();
    }

    private async Task PollSupportThreadAsync(bool forceScroll = false)
    {
        var current = _supportCurrent;
        if (current == null || _supportPolling)
        {
            return;
        }

        _supportPolling = true;
        try
        {
            var thread = await _support.ThreadAsync(current.Id);
            if (_supportCurrent?.Id != current.Id)
            {
                return;
            }
            if (thread.Conversation != null)
            {
                _supportCurrent = thread.Conversation;
                ApplySupportThreadHeader(thread.Conversation);
            }
            var messages = thread.Messages ?? [];
            var signature = $"{messages.Count}:{messages.LastOrDefault()?.Id}";
            if (signature == _supportSignature && !forceScroll)
            {
                return;
            }
            var nearBottom = SupportScroll.VerticalOffset >= SupportScroll.ScrollableHeight - 60;
            var grew = _supportSignature != null && signature != _supportSignature;
            _supportSignature = signature;
            RenderSupportMessages(messages);
            if (forceScroll || nearBottom || grew)
            {
                _ = Dispatcher.BeginInvoke(() => SupportScroll.ScrollToEnd(), DispatcherPriority.Loaded);
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SUPPORT_CONVERSATION_NOT_FOUND")
        {
            _supportCurrent = null;
            ShowSupportPane(SupportPlaceholder);
            await LoadSupportListAsync(showSpinner: false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.PollSupportThreadAsync", ex);
        }
        finally
        {
            _supportPolling = false;
        }
    }

    private void ApplySupportThreadHeader(ColituSupportConversation conversation)
    {
        SupportThreadTitle.Text = string.IsNullOrWhiteSpace(conversation.Subject) ? Loc.I["support.title"] : conversation.Subject;
        var (text, background, foreground) = ColituSupportRow.StatusLook(conversation.Status);
        SupportThreadStatusText.Text = text;
        SupportThreadStatusText.Foreground = foreground;
        SupportThreadStatus.Background = background;
        var closed = conversation.Status == "closed";
        SupportComposer.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
        SupportReplyFiles.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
        SupportClosedNote.Visibility = closed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSupportMessages(IReadOnlyList<ColituSupportMessage> messages)
    {
        SupportMessages.Children.Clear();
        DateTime? lastDay = null;
        foreach (var message in messages)
        {
            var local = message.CreatedAt.ToLocalTime();
            if (lastDay != local.Date)
            {
                lastDay = local.Date;
                SupportMessages.Children.Add(new TextBlock
                {
                    Text = local.ToString("D", Loc.I.Culture),
                    Margin = new Thickness(0, 10, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11.5,
                    Foreground = (Brush)FindResource("DimBrush")
                });
            }
            SupportMessages.Children.Add(BuildBubble(message, local));
        }
    }

    private FrameworkElement BuildBubble(ColituSupportMessage message, DateTimeOffset local)
    {
        var mine = message.Sender == "user";
        var body = new StackPanel();
        if (!mine)
        {
            var name = message.Sender == "bot" ? "Colitu Bot" : Loc.I["support.team"];
            if (!string.IsNullOrWhiteSpace(message.AdminName) && message.Sender == "admin")
            {
                name += " · " + message.AdminName;
            }
            body.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("LilacBrush"), Margin = new Thickness(0, 0, 0, 5) });
        }
        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            var text = new TextBox
            {
                Text = message.Body,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = mine ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("TextBrush"),
                FontSize = 14,
                Padding = new Thickness(0),
                FocusVisualStyle = null,
                Cursor = Cursors.IBeam,
                SelectionBrush = (Brush)FindResource("VioletBrush")
            };
            text.Template = PlainTextTemplate();
            body.Children.Add(text);
        }
        foreach (var attachment in message.Attachments ?? [])
        {
            body.Children.Add(attachment.IsImage ? BuildImageAttachment(attachment) : BuildFileAttachment(attachment, mine));
        }
        body.Children.Add(new TextBlock
        {
            Text = local.ToString("t", Loc.I.Culture),
            FontSize = 10.5,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = mine ? new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x0A, 0x14)) : (Brush)FindResource("DimBrush")
        });

        var bubble = new Border
        {
            Child = body,
            MaxWidth = 460,
            Padding = new Thickness(15, 11, 15, 9),
            Margin = new Thickness(mine ? 80 : 0, 0, mine ? 0 : 80, 10),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            CornerRadius = mine ? new CornerRadius(18, 18, 6, 18) : new CornerRadius(18, 18, 18, 6),
            Background = mine ? (Brush)FindResource("ActionBrush") : (Brush)FindResource("Surface2Brush"),
            BorderBrush = mine ? null : (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(mine ? 0 : 1)
        };
        return bubble;
    }

    private static ControlTemplate? _plainTextTemplate;

    /// <summary>A selectable, borderless text box so users can copy what support wrote.</summary>
    private static ControlTemplate PlainTextTemplate()
    {
        if (_plainTextTemplate != null)
        {
            return _plainTextTemplate;
        }
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(FocusableProperty, false);
        _plainTextTemplate = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        return _plainTextTemplate;
    }

    private FrameworkElement BuildImageAttachment(ColituSupportAttachment attachment)
    {
        var image = new Image { MaxWidth = 300, MaxHeight = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var frame = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            MinWidth = 120,
            MinHeight = 80,
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Child = image,
            ToolTip = attachment.FileName
        };
        frame.MouseLeftButtonUp += async (_, _) => await OpenSupportAttachmentAsync(attachment);
        _ = LoadSupportImageAsync(attachment.Id, image, frame);
        return frame;
    }

    private async Task LoadSupportImageAsync(string id, Image target, Border frame)
    {
        try
        {
            if (!_supportImages.TryGetValue(id, out var bitmap))
            {
                var bytes = await _support.DownloadBytesAsync(id);
                bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 600;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                _supportImages[id] = bitmap;
            }
            target.Source = bitmap;
            frame.MinWidth = frame.MinHeight = 0;
            frame.Background = null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadSupportImageAsync", ex);
        }
    }

    private FrameworkElement BuildFileAttachment(ColituSupportAttachment attachment, bool mine)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new ShapePath
        {
            Data = (Geometry)FindResource("IconFile"),
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Stroke = mine ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("LilacBrush"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{attachment.FileName}  ·  {FormatSize(attachment.Size)}",
            FontSize = 12.5,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = mine ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("TextBrush")
        });
        var chip = new Border
        {
            Child = row,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 7, 12, 7),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(mine ? Color.FromArgb(0x26, 0x0B, 0x0A, 0x14) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF))
        };
        chip.MouseLeftButtonUp += async (_, _) => await OpenSupportAttachmentAsync(attachment);
        return chip;
    }

    private async Task OpenSupportAttachmentAsync(ColituSupportAttachment attachment)
    {
        try
        {
            var path = await _support.DownloadAsync(attachment);
            // Open with Explorer selecting the file: nothing downloaded is executed directly.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
        }
    }

    // ── Reply ──────────────────────────────────────────────────────────────
    private void SupportAttachReply_Click(object sender, RoutedEventArgs e) => PickSupportFiles(_supportReplyFiles, SupportReplyFiles);

    private async void SupportReply_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter starts a new line.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await SendSupportReplyAsync();
        }
    }

    private async void SupportSend_Click(object sender, RoutedEventArgs e) => await SendSupportReplyAsync();

    private async Task SendSupportReplyAsync()
    {
        var current = _supportCurrent;
        var body = SupportReplyBox.Text.Trim();
        if (_supportBusy || current == null || (body.Length == 0 && _supportReplyFiles.Count == 0))
        {
            return;
        }

        SetSupportBusy(true, SupportSendButton, SupportSendSpinner);
        SupportSendIcon.Visibility = Visibility.Collapsed;
        try
        {
            await _support.ReplyAsync(current.Id, body, _supportReplyFiles.ToList());
            SupportReplyBox.Clear();
            _supportReplyFiles.Clear();
            RenderFileChips(SupportReplyFiles, _supportReplyFiles);
            await PollSupportThreadAsync(forceScroll: true);
            await LoadSupportListAsync(showSpinner: false);
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SUPPORT_CONVERSATION_CLOSED")
        {
            ShowToast(ex.Message, true);
            await PollSupportThreadAsync();
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
        }
        finally
        {
            SetSupportBusy(false, SupportSendButton, SupportSendSpinner);
            SupportSendIcon.Visibility = Visibility.Visible;
            SupportReplyBox.Focus();
        }
    }

    // ── Files ──────────────────────────────────────────────────────────────
    private void PickSupportFiles(List<string> files, Panel chips)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = Loc.I["support.attach"],
            Filter = "Images, PDF, text, archives|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.pdf;*.txt;*.log;*.zip;*.json;*.gz"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        foreach (var file in dialog.FileNames)
        {
            if (files.Count >= ColituSupportService.MaxFilesPerMessage)
            {
                ShowToast(Loc.I["support.err.files"], true);
                break;
            }
            if (ColituSupportService.CheckFile(file) is { } problem)
            {
                ShowToast($"{System.IO.Path.GetFileName(file)}: {problem}", true);
                continue;
            }
            if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }
        RenderFileChips(chips, files);
    }

    private void RenderFileChips(Panel host, List<string> files)
    {
        host.Children.Clear();
        foreach (var file in files)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = $"{System.IO.Path.GetFileName(file)}  ·  {FormatSize(new FileInfo(file).Length)}",
                FontSize = 12.5,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var remove = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Width = 22,
                Height = 22,
                Margin = new Thickness(6, 0, 0, 0),
                Content = new ShapePath { Data = (Geometry)FindResource("IconX"), Style = (Style)FindResource("Stroke"), Width = 9, Height = 9 }
            };
            var captured = file;
            remove.Click += (_, _) =>
            {
                files.Remove(captured);
                RenderFileChips(host, files);
            };
            row.Children.Add(remove);
            host.Children.Add(new Border
            {
                Child = row,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 4, 4, 4),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1)
            });
        }
    }

    private void SetSupportBusy(bool busy, Button button, FrameworkElement spinner)
    {
        _supportBusy = busy;
        button.IsEnabled = !busy;
        spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1 << 20 => $"{bytes / 1048576.0:0.#} MB",
            >= 1 << 10 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B"
        };
    }
}

/// <summary>One conversation in the support list.</summary>
public sealed class ColituSupportRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public string TimeText { get; init; } = "";
    public int Unread { get; init; }
    public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string StatusText { get; init; } = "";
    public Brush? StatusBackground { get; init; }
    public Brush? StatusForeground { get; init; }

    public static ColituSupportRow From(ColituSupportConversation conversation)
    {
        var (text, background, foreground) = StatusLook(conversation.Status);
        var local = conversation.LastMessageAt.ToLocalTime();
        return new ColituSupportRow
        {
            Id = conversation.Id,
            Title = string.IsNullOrWhiteSpace(conversation.Subject) ? Loc.I["support.title"] : conversation.Subject!,
            Preview = (conversation.LastMessage ?? "").ReplaceLineEndings(" "),
            TimeText = local.Date == DateTime.Today ? local.ToString("t", Loc.I.Culture) : local.ToString("d MMM", Loc.I.Culture),
            Unread = conversation.Unread,
            StatusText = text,
            StatusBackground = background,
            StatusForeground = foreground
        };
    }

    public static (string Text, Brush Background, Brush Foreground) StatusLook(string? status)
    {
        return status switch
        {
            "open" => (Loc.I["support.status.open"], Tint(0x29, 0x9F, 0x8C, 0xFF), Tint(0xFF, 0xC4, 0xB5, 0xFD)),
            "resolved" => (Loc.I["support.status.resolved"], Tint(0x26, 0x5E, 0xE0, 0xA0), Tint(0xFF, 0x5E, 0xE0, 0xA0)),
            "closed" => (Loc.I["support.status.closed"], Tint(0x1A, 0xFF, 0xFF, 0xFF), Tint(0xFF, 0x9A, 0x9A, 0xAB)),
            _ => (Loc.I["support.status.waiting"], Tint(0x26, 0xF5, 0xC3, 0x6B), Tint(0xFF, 0xF5, 0xC3, 0x6B))
        };
    }

    private static SolidColorBrush Tint(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
