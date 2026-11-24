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

                if (loadedThumb == null)
                {
                    // Multi-stage retry for delayed cover art from Chromium, Edge, Spotify
                    string capturedTitle = Title;
                    string capturedArtist = Artist;
                    _ = Task.Run(async () =>
                    {
                        int[] delays = { 250, 600, 1200 };
                        foreach (int delay in delays)
                        {
                            await Task.Delay(delay);
                            if (Title != capturedTitle) break; // Track changed
                            try
                            {
                                var s = _currentSession ?? GetBestSession();
                                if (s != null)
                                {
                                    var p = await s.TryGetMediaPropertiesAsync();
                                    if (p?.Thumbnail != null)
                                    {
                                        var retryThumb = await LoadThumbnailAsync(p.Thumbnail);
                                        if (retryThumb != null && Title == capturedTitle)
                                        {
                                            Thumbnail = retryThumb;
                                            NotifyMediaChanged();
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    });
                }
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
            if (stream == null || stream.Size == 0) return null;

            // Strategy 1: WinRT native BitmapDecoder — handles WebP (YouTube/Chrome), AVIF, JPEG, PNG seamlessly
            try
            {
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var pixelData = await decoder.GetPixelDataAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    new Windows.Graphics.Imaging.BitmapTransform(),
                    Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                    Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

                byte[] pixels = pixelData.DetachPixelData();
                int width = (int)decoder.PixelWidth;
                int height = (int)decoder.PixelHeight;
                int stride = width * 4;

                if (Application.Current?.Dispatcher != null && width > 0 && height > 0)
                {
                    return await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var bsource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                        bsource.Freeze();
                        return (ImageSource)bsource;
                    });
                }
            }
            catch
            {
                // Strategy 2: Fallback to standard WIC BitmapImage if WinRT decoder is unavailable
                stream.Seek(0);
                using var netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms);

                if (ms.Length > 0 && Application.Current?.Dispatcher != null)
                {
                    var bytes = ms.ToArray();
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
                // Deterministic vibrant palette based on track hash
                int hash = Math.Abs((title + artist).GetHashCode());
                var (c1, c2) = (hash % 6) switch
                {
                    0 => (Color.FromRgb(0x8A, 0x23, 0x87), Color.FromRgb(0xE9, 0x40, 0x57)), // Sunset Neon
                    1 => (Color.FromRgb(0x00, 0x7C, 0xF0), Color.FromRgb(0x00, 0xDF, 0xD8)), // Electric Cyan
                    2 => (Color.FromRgb(0x79, 0x28, 0xCA), Color.FromRgb(0xFF, 0x00, 0x80)), // Cyber Magenta
                    3 => (Color.FromRgb(0x11, 0x99, 0x8E), Color.FromRgb(0x38, 0xEF, 0x7D)), // Emerald Aurora
                    4 => (Color.FromRgb(0xF8, 0x57, 0xA6), Color.FromRgb(0xFF, 0x58, 0x58)), // Ruby Glow
                    _ => (Color.FromRgb(0x4E, 0x54, 0xC8), Color.FromRgb(0x8F, 0x94, 0xFB))  // Deep Indigo
                };

                // Rich gradient cover art background
                var bgBrush = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
                dc.DrawRoundedRectangle(bgBrush, null, new Rect(0, 0, 100, 100), 12, 12);

                // Vinyl groove rings for genuine album record look
                var groovePen = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1.2);
                dc.DrawEllipse(null, groovePen, new Point(50, 50), 38, 38);
                dc.DrawEllipse(null, groovePen, new Point(50, 50), 28, 28);
                dc.DrawEllipse(null, groovePen, new Point(50, 50), 18, 18);

                // Center disc hub
                var centerBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00));
                var centerPen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1.0);
                dc.DrawEllipse(centerBrush, centerPen, new Point(50, 50), 14, 14);

                // Icon in center
                var ft = new FormattedText(
                    "🎵",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Emoji"),
                    14,
                    Brushes.White,
                    1.0);
                dc.DrawText(ft, new Point(50 - ft.Width / 2.0, 50 - ft.Height / 2.0));

                // Diagonal specular glass gloss
                var glossBrush = new LinearGradientBrush(
                    Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                    new Point(0, 0),
                    new Point(1, 0.7));
                dc.DrawRoundedRectangle(glossBrush, null, new Rect(0, 0, 100, 50), 12, 12);

                // Crisp 1px specular border
                var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)), 1.0);
                dc.DrawRoundedRectangle(null, borderPen, new Rect(0.5, 0.5, 99, 99), 12, 12);
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
