﻿using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.Composition;
using Windows.UI.Xaml.Hosting;
using Plugin.MediaManager.Abstractions;
using Plugin.MediaManager.Abstractions.EventArguments;
using Plugin.MediaManager.Abstractions.Implementations;

namespace Plugin.MediaManager
{
    public class VideoPlayerImplementation : IVideoPlayer
    {
        private readonly MediaPlayer _player;
        private readonly Timer _playProgressTimer;
        private MediaSource _currentMediaSource;
        private TaskCompletionSource<bool> _loadMediaTaskCompletionSource = new TaskCompletionSource<bool>();
        private MediaPlayerStatus _status;
        private IMediaFile _currentMediaFile;

        public VideoPlayerImplementation()
        {
            _player = new MediaPlayer();

            _playProgressTimer = new Timer(state =>
            {
                if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    var progress = _player.PlaybackSession.Position.TotalSeconds/
                                   _player.PlaybackSession.NaturalDuration.TotalSeconds;
                    if (double.IsNaN(progress))
                        progress = 0;
                    PlayingChanged?.Invoke(this, new PlayingChangedEventArgs(progress, _player.PlaybackSession.Position));
                }
            }, null, 0, int.MaxValue);

            _player.MediaFailed += (sender, args) =>
            {
                _status = MediaPlayerStatus.Failed;
                _playProgressTimer.Change(0, int.MaxValue);
                MediaFailed?.Invoke(this, new MediaFailedEventArgs(args.ErrorMessage, args.ExtendedErrorCode));
            };

            _player.PlaybackSession.PlaybackStateChanged += (sender, args) =>
            {
                switch (sender.PlaybackState)
                {
                    case MediaPlaybackState.None:
                        _playProgressTimer.Change(0, int.MaxValue);
                        break;
                    case MediaPlaybackState.Opening:
                        Status = MediaPlayerStatus.Loading;
                        _playProgressTimer.Change(0, int.MaxValue);
                        break;
                    case MediaPlaybackState.Buffering:
                        Status = MediaPlayerStatus.Buffering;
                        _playProgressTimer.Change(0, int.MaxValue);
                        break;
                    case MediaPlaybackState.Playing:
                        if ((sender.PlaybackRate <= 0) && (sender.Position == TimeSpan.Zero))
                        {
                            Status = MediaPlayerStatus.Stopped;
                        }
                        else
                        {
                            Status = MediaPlayerStatus.Playing;
                            _playProgressTimer.Change(0, 50);
                        }
                        break;
                    case MediaPlaybackState.Paused:
                        Status = MediaPlayerStatus.Paused;
                        _playProgressTimer.Change(0, int.MaxValue);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            };

            _player.MediaEnded += (sender, args) => { MediaFinished?.Invoke(this, new MediaFinishedEventArgs(_currentMediaFile)); };
            _player.PlaybackSession.BufferingProgressChanged += (sender, args) =>
            {
                var bufferedTime =
                    TimeSpan.FromSeconds(_player.PlaybackSession.BufferingProgress*
                                         _player.PlaybackSession.NaturalDuration.TotalSeconds);
                BufferingChanged?.Invoke(this,
                    new BufferingChangedEventArgs(_player.PlaybackSession.BufferingProgress, bufferedTime));
            };

            _player.PlaybackSession.SeekCompleted += (sender, args) => { };
            _player.MediaOpened += (sender, args) => { _loadMediaTaskCompletionSource.SetResult(true); };
        }

        public MediaPlayerStatus Status
        {
            get { return _status; }
            private set
            {
                _status = value;
                StatusChanged?.Invoke(this, new StatusChangedEventArgs(_status));
            }
        }

        public event StatusChangedEventHandler StatusChanged;
        public event PlayingChangedEventHandler PlayingChanged;
        public event BufferingChangedEventHandler BufferingChanged;
        public event MediaFinishedEventHandler MediaFinished;
        public event MediaFailedEventHandler MediaFailed;

        public TimeSpan Buffered
        {
            get
            {
                if (_player == null) return TimeSpan.Zero;
                return
                    TimeSpan.FromMilliseconds(_player.PlaybackSession.BufferingProgress*
                                              _player.PlaybackSession.NaturalDuration.TotalMilliseconds);
            }
        }

        public TimeSpan Duration => _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
        public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

        public Task Pause()
        {
            if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused)
                _player.Play();
            else
                _player.Pause();
            return Task.CompletedTask;
        }

        public async Task PlayPause()
        {
            if ((_status == MediaPlayerStatus.Paused) || (_status == MediaPlayerStatus.Stopped))
                await Play();
            else
                await Pause();
        }

        public async Task Play(IMediaFile mediaFile)
        {
            _currentMediaFile = mediaFile;
            await Play(mediaFile.Url, mediaFile.Type);
        }

        public async Task Play(string url, MediaFileType fileType)
        {
            _loadMediaTaskCompletionSource = new TaskCompletionSource<bool>();
            try
            {
                if (_currentMediaSource != null)
                {
                    _currentMediaSource.StateChanged -= MediaSourceOnStateChanged;
                    _currentMediaSource.OpenOperationCompleted -= MediaSourceOnOpenOperationCompleted;
                }
                // Todo: sync this with the playback queue
                var mediaPlaybackList = new MediaPlaybackList();
                _currentMediaSource = await CreateMediaSource(url, fileType);
                _currentMediaSource.StateChanged += MediaSourceOnStateChanged;
                _currentMediaSource.OpenOperationCompleted += MediaSourceOnOpenOperationCompleted;
                var item = new MediaPlaybackItem(_currentMediaSource);
                mediaPlaybackList.Items.Add(item);
                _player.Source = mediaPlaybackList;
                _player.Play();
            }
            catch (Exception)
            {
                Debug.WriteLine("Unable to open url: " + url);
            }
        }

        public async Task Seek(TimeSpan position)
        {
            _player.PlaybackSession.Position = position;
            await Task.CompletedTask;
        }

        public Task Stop()
        {
            _player.PlaybackSession.PlaybackRate = 0;
            _player.PlaybackSession.Position = TimeSpan.Zero;
            Status = MediaPlayerStatus.Stopped;
            return Task.CompletedTask;
        }

        public IVideoSurface RenderSurface { get; private set; }

        public void SetVideoSurface(IVideoSurface videoSurface)
        {
            if (!(videoSurface is VideoSurface))
                throw new ArgumentException("Not a valid video surface");

            var canvas = (VideoSurface) videoSurface;
            RenderSurface = canvas;
            _player.SetSurfaceSize(new Size(canvas.ActualWidth, canvas.ActualHeight));

            var compositor = ElementCompositionPreview.GetElementVisual(canvas).Compositor;
            var surface = _player.GetSurface(compositor);

            var spriteVisual = compositor.CreateSpriteVisual();
            spriteVisual.Size =
                new Vector2((float) canvas.ActualWidth, (float) canvas.ActualHeight);

            CompositionBrush brush = compositor.CreateSurfaceBrush(surface.CompositionSurface);
            spriteVisual.Brush = brush;

            var container = compositor.CreateContainerVisual();
            container.Children.InsertAtTop(spriteVisual);

            ElementCompositionPreview.SetElementChildVisual(canvas, container);
        }

        private async Task<MediaSource> CreateMediaSource(string url, MediaFileType fileType)
        {
            switch (fileType)
            {
                case MediaFileType.AudioUrl:
                case MediaFileType.VideoUrl:
                    return MediaSource.CreateFromUri(new Uri(url));
                case MediaFileType.AudioFile:
                case MediaFileType.VideoFile:
                    return MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(url));
                case MediaFileType.Other:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fileType), fileType, null);
            }
            return MediaSource.CreateFromUri(new Uri(url));
        }

        private void MediaSourceOnOpenOperationCompleted(MediaSource sender,
            MediaSourceOpenOperationCompletedEventArgs args)
        {
            if (args.Error != null)
                MediaFailed?.Invoke(this, new MediaFailedEventArgs(args.Error.ToString(), args.Error.ExtendedError));
        }

        private void MediaSourceOnStateChanged(MediaSource sender, MediaSourceStateChangedEventArgs args)
        {
            switch (args.NewState)
            {
                case MediaSourceState.Initial:
                    Status = MediaPlayerStatus.Loading;
                    break;
                case MediaSourceState.Opening:
                    Status = MediaPlayerStatus.Loading;
                    break;
                case MediaSourceState.Failed:
                    Status = MediaPlayerStatus.Failed;
                    break;
                case MediaSourceState.Closed:
                    Status = MediaPlayerStatus.Stopped;
                    break;
                case MediaSourceState.Opened:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private Task Play()
        {
            _player.PlaybackSession.PlaybackRate = 1;
            _player.Play();
            return Task.CompletedTask;
        }
    }
}