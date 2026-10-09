using System.Windows.Controls;
using System.Windows.Media;
using v2rayN.Services;

namespace v2rayN.Views;

/// <summary>
/// The notice banner at the top of the home cards: the first notice from the panel
/// that this user has not closed. Fetched with the periodic data refresh, at most
/// every 15 minutes.
/// </summary>
public partial class ColituMainWindow
{
    private List<ColituNotice> _notices = [];
    private ColituNotice? _shownNotice;
    private DateTimeOffset _noticesFetchedAt = DateTimeOffset.MinValue;
    private string? _noticesLanguage;
    private bool _noticesFetching;
    private int _noticesEpoch;

    /// <summary>Asks the panel for notices when the last answer is old or in another language.</summary>
    private async Task RefreshNoticesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var language = Loc.I.Language;
        if (_noticesFetching
            || (language == _noticesLanguage && now - _noticesFetchedAt < ColituNotices.MinFetchInterval))
        {
            return;
        }
        _noticesFetching = true;
        _noticesFetchedAt = now;
        _noticesLanguage = language;
        var epoch = _noticesEpoch;
        try
        {
            var fetched = await ColituNoticeService.Instance.FetchAsync(language);
            if (fetched == null)
            {
                // Failed: try again with the next data refresh.
                _noticesFetchedAt = DateTimeOffset.MinValue;
                return;
            }
            if (epoch != _noticesEpoch)
            {
                return;
            }
            _notices = fetched;
            ShowNextNotice();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RefreshNoticesAsync", ex);
        }
        finally
        {
            _noticesFetching = false;
        }
    }

    /// <summary>Shows the first notice not closed yet, or hides the banner.</summary>
    private void ShowNextNotice()
    {
        if (NoticeBanner == null)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var store = ColituNoticeStore.Instance;
        var next = ColituNotices.PickNext(_notices, store.IsDismissed, now);
        var changed = next?.Id != _shownNotice?.Id;
        _shownNotice = next;
        if (next == null)
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            return;
        }

        NoticeTitle.Text = next.Title;
        NoticeTitle.Visibility = next.Title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeBody.Text = next.Body;
        NoticeBody.Visibility = next.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeAction.Content = next.Button ?? "";
        NoticeAction.Visibility = next.HasButton ? Visibility.Visible : Visibility.Collapsed;
        ApplyNoticeLevel(next.Level);
        NoticeBanner.Visibility = Visibility.Visible;
        if (changed)
        {
            FadeIn(NoticeBanner, 8);
        }
        if (store.MarkSeen(next.Id, now))
        {
            _ = ColituNoticeService.Instance.SendEventAsync(next.Id, ColituNotices.EventSeen);
        }
    }

    // Critical red, warning amber, promo the accent violet, info neutral (same tints as the trial and error boxes).
    private void ApplyNoticeLevel(string level)
    {
        var (fill, edge) = level switch
        {
            ColituNotices.LevelCritical => (Color.FromArgb(0x1A, 0xFF, 0x6B, 0x7A), Color.FromArgb(0x59, 0xFF, 0x6B, 0x7A)),
            ColituNotices.LevelWarning => (Color.FromArgb(0x14, 0xF5, 0xC3, 0x6B), Color.FromArgb(0x59, 0xF5, 0xC3, 0x6B)),
            ColituNotices.LevelPromo => (Color.FromArgb(0x14, 0x9F, 0x8C, 0xFF), Color.FromArgb(0x59, 0x9F, 0x8C, 0xFF)),
            _ => (Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF))
        };
        NoticeBanner.Background = new SolidColorBrush(fill);
        NoticeBanner.BorderBrush = new SolidColorBrush(edge);
    }

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if (_shownNotice is not { HasButton: true } notice)
        {
            return;
        }
        OpenUrl(notice.Url!);
        _ = ColituNoticeService.Instance.SendEventAsync(notice.Id, ColituNotices.EventClicked);
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e)
    {
        if (_shownNotice is not { } notice)
        {
            return;
        }
        ColituNoticeStore.Instance.MarkDismissed(notice.Id, DateTimeOffset.UtcNow);
        _ = ColituNoticeService.Instance.SendEventAsync(notice.Id, ColituNotices.EventDismissed);
        ShowNextNotice();
    }

    /// <summary>Sign-out: the next account must not see this one's notices.</summary>
    private void ClearNotices()
    {
        _noticesEpoch++;
        _notices = [];
        _shownNotice = null;
        _noticesFetchedAt = DateTimeOffset.MinValue;
        if (NoticeBanner != null)
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
        }
    }
}
