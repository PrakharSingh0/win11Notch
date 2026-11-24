using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Win11Notch.Services;

/// <summary>
/// Controls Windows System Media Transport Controls (SMTC) sessions.
/// Reads real-time track info, playback state, and album art from Spotify, Chrome, Edge, etc.
/// </summary>
public sealed class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession?        _currentSession;

    public string      Title     { get; private set; } = "No Media Playing";
    public string      Artist    { get; private set; } = string.Empty;
    public bool        IsPlaying { get; private set; }
    public bool        HasMedia  { get; private set; }
    public ImageSource? Thumbnail { get; private set; }

    public event Action? MediaChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                UpdateCurrentSession(_sessionManager.GetCurrentSession());
            }
        }
        catch { /* SMTC unavailable on some OS configurations */ }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateCurrentSession(sender.GetCurrentSession());
    }

    private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged   -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged   += OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _ = RefreshMediaInfoAsync();
        }
        else
        {
            Title     = "No Media Playing";
            Artist    = string.Empty;
            IsPlaying = false;
            HasMedia  = false;
            Thumbnail = null;
            MediaChanged?.Invoke();
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

    public async Task RefreshMediaInfoAsync()
    {
        if (_currentSession == null) return;

        try
        {
            var playbackInfo = _currentSession.GetPlaybackInfo();
            IsPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var props = await _currentSession.TryGetMediaPropertiesAsync();
            if (props != null && !string.IsNullOrWhiteSpace(props.Title))
            {
                Title    = props.Title;
                Artist   = props.Artist ?? string.Empty;
                HasMedia = true;

                if (props.Thumbnail != null)
                {
                    Thumbnail = await LoadThumbnailAsync(props.Thumbnail);
                }
                else
                {
                    Thumbnail = null;
                }
            }
            else
            {
                HasMedia = false;
            }
        }
        catch
        {
            HasMedia = false;
        }

        MediaChanged?.Invoke();
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference thumbnailRef)
    {
        try
        {
            using var stream = await thumbnailRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TryTogglePlayPauseAsync();
        }
    }

    public async Task NextTrackAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TrySkipNextAsync();
        }
    }

    public async Task PreviousTrackAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TrySkipPreviousAsync();
        }
    }

    public void Dispose()
    {
        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        if (_currentSession != null)
        {
            _currentSession.PlaybackInfoChanged   -= OnPlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        }
    }
}
