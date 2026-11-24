using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Win11Notch.Services;

/// <summary>
/// Controls Windows System Media Transport Controls (SMTC) sessions.
/// Reads real-time track info, playback state, album art, and timeline progress.
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession?        _currentSession;

    public string      Title             { get; private set; } = "No Media Playing";
    public string      Artist            { get; private set; } = string.Empty;
    public bool        IsPlaying         { get; private set; }
    public bool        HasMedia          { get; private set; }
    public ImageSource? Thumbnail        { get; private set; }
    public TimeSpan    Position          { get; private set; } = TimeSpan.Zero;
    public TimeSpan    Duration          { get; private set; } = TimeSpan.Zero;
    public double      ProgressFraction  => Duration.TotalSeconds > 0 ? Math.Clamp(Position.TotalSeconds / Duration.TotalSeconds, 0.0, 1.0) : 0.0;
    public string      PositionText      => Position.ToString(@"mm\:ss");
    public string      DurationText      => Duration.ToString(@"mm\:ss");

    public event Action? MediaChanged;
    public event Action? TimelineChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                _sessionManager.SessionsChanged       += OnSessionsChanged;
                UpdateCurrentSession(GetBestSession());
            }
        }
        catch { /* SMTC unavailable on some OS configurations */ }
    }

    private GlobalSystemMediaTransportControlsSession? GetBestSession()
    {
        try
        {
            return _sessionManager?.GetCurrentSession() ?? _sessionManager?.GetSessions().FirstOrDefault();
        }
        catch { return null; }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        UpdateCurrentSession(GetBestSession());
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateCurrentSession(GetBestSession());
    }

    private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged   -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged   += OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            _ = RefreshMediaInfoAsync();
        }
        else
        {
            Title     = "No Media Playing";
            Artist    = string.Empty;
            IsPlaying = false;
            HasMedia  = false;
            Thumbnail = null;
            Position  = TimeSpan.Zero;
            Duration  = TimeSpan.Zero;
            NotifyMediaChanged();
        }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshMediaInfoAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshMediaInfoAsync();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        UpdateTimeline();
    }

    public async Task RefreshMediaInfoAsync()
    {
        var session = _currentSession ?? GetBestSession();
        if (session == null)
        {
            HasMedia = false;
            NotifyMediaChanged();
            return;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            IsPlaying = playbackInfo != null &&
                        playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props != null && !string.IsNullOrWhiteSpace(props.Title))
            {
                Title    = props.Title;
                Artist   = props.Artist ?? string.Empty;
                HasMedia = true;

                ImageSource? loadedThumb = null;
                if (props.Thumbnail != null)
                {
                    loadedThumb = await LoadThumbnailAsync(props.Thumbnail);
                }

                Thumbnail = loadedThumb ?? GenerateFallbackThumbnail(Title, Artist);
            }
            else
            {
                HasMedia = false;
            }

            UpdateTimeline();
        }
        catch
        {
            HasMedia = false;
        }

        NotifyMediaChanged();
    }

    public void UpdateTimeline()
    {
        var session = _currentSession ?? GetBestSession();
        if (session == null) return;

        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                var dur = timeline.EndTime - timeline.StartTime;
                Duration = dur > TimeSpan.Zero ? dur : TimeSpan.Zero;

                var pos = timeline.Position;
                if (IsPlaying && timeline.LastUpdatedTime != default)
                {
                    var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                    if (elapsed > TimeSpan.Zero)
                    {
                        pos += elapsed;
                    }
                }

                if (Duration > TimeSpan.Zero && pos > Duration)
                {
                    pos = Duration;
                }
                else if (pos < TimeSpan.Zero)
                {
                    pos = TimeSpan.Zero;
                }

                Position = pos;
                NotifyTimelineChanged();
            }
        }
        catch { /* best effort */ }
    }

    public async Task SeekToFractionAsync(double fraction)
    {
        var session = _currentSession ?? GetBestSession();
        if (session != null && Duration.TotalSeconds > 0)
        {
            long targetTicks = (long)(Duration.Ticks * Math.Clamp(fraction, 0.0, 1.0));
            await session.TryChangePlaybackPositionAsync(targetTicks);
            Position = TimeSpan.FromTicks(targetTicks);
            NotifyTimelineChanged();
        }
    }

    private void NotifyMediaChanged()
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() => MediaChanged?.Invoke());
        }
        else
        {
            MediaChanged?.Invoke();
        }
    }

    private void NotifyTimelineChanged()
    {
        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.InvokeAsync(() => TimelineChanged?.Invoke());
        }
        else
        {
            TimelineChanged?.Invoke();
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference thumbnailRef)
    {
        try
        {
            using var stream = await thumbnailRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return null;

            if (Application.Current?.Dispatcher != null)
            {
                return await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                        bitmap.CacheOption   = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource  = new MemoryStream(bytes);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return (ImageSource)bitmap;
                    }
                    catch
                    {
                        return null;
                    }
                });
            }
        }
        catch { /* thumbnail stream unavailable or unreadable */ }
        return null;
    }

    public static ImageSource GenerateFallbackThumbnail(string title, string artist)
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Dark-slate gradient background (#282E38 to #14161C)
                var grad = new LinearGradientBrush(
                    Color.FromRgb(0x28, 0x2E, 0x38),
                    Color.FromRgb(0x14, 0x16, 0x1C),
                    new Point(0, 0),
                    new Point(1, 1));
                dc.DrawRoundedRectangle(grad, null, new Rect(0, 0, 100, 100), 12, 12);

                // Specular border (#30FFFFFF)
                var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1.0);
                dc.DrawRoundedRectangle(null, borderPen, new Rect(0.5, 0.5, 99, 99), 12, 12);

                // Music note icon or letter initial
                string initial = !string.IsNullOrWhiteSpace(title) && char.IsLetterOrDigit(title[0])
                    ? title[0].ToString().ToUpperInvariant()
                    : "🎵";

                var ft = new FormattedText(
                    initial,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    initial.Length == 1 && initial != "🎵" ? 36 : 28,
                    new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                    1.0);

                double tx = (100 - ft.Width) / 2.0;
                double ty = (100 - ft.Height) / 2.0;
                dc.DrawText(ft, new Point(tx, ty));
            }

            var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
        catch
        {
            return null!;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _currentSession ?? GetBestSession();
        if (session != null)
        {
            await session.TryTogglePlayPauseAsync();
            await RefreshMediaInfoAsync();
        }
    }

    public async Task NextTrackAsync()
    {
        var session = _currentSession ?? GetBestSession();
        if (session != null)
        {
            await session.TrySkipNextAsync();
            await Task.Delay(200);
            await RefreshMediaInfoAsync();
        }
    }

    public async Task PreviousTrackAsync()
    {
        var session = _currentSession ?? GetBestSession();
        if (session != null)
        {
            await session.TrySkipPreviousAsync();
            await Task.Delay(200);
            await RefreshMediaInfoAsync();
        }
    }

    public void Dispose()
    {
        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _sessionManager.SessionsChanged       -= OnSessionsChanged;
        }
        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged   -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
    }
}
