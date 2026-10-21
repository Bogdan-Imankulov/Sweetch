using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Sweetch.Core;
using Sweetch.Core.Backends;
using static Sweetch.Core.Loc;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Sweetch.App.UI;

/// <summary>
/// Trim with a player: play with sound, scrub, step frame by frame and mark start/end.
/// Produces the same "start"/"end" values as the plain parameter dialog, so "repeat last" keeps working.
/// </summary>
internal sealed class TrimWindow : Window, IParameterWindow
{
    private static LibVLC? _vlc;

    private readonly Media _media;
    private readonly MediaPlayer _player;
    private readonly VideoView? _video;
    private readonly Slider _timeline;
    private readonly TextBlock _clock = Controls.Text("", 13, wrap: false);
    private readonly TextBox _start = Controls.TextField("0:00", 20);
    private readonly TextBox _end = Controls.TextField("", 20);
    private readonly TextBlock _playGlyph;
    private readonly Image _still = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Hidden };
    private readonly string _path;
    private readonly string? _ffmpeg;
    private readonly string _stills = OutputPaths.TempDirectory();
    private readonly SemaphoreSlim _stillGate = new(1);
    private int _stillWanted;

    private long _length;
    private long _position;
    private double _frameMs = 40;
    private bool _syncing;

    public TrimWindow(Operation op, IReadOnlyList<FileItem> items, ToolLocator tools)
    {
        Title = op.Label;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Theme.Refresh();
        Background = Theme.Page;
        FontFamily = Theme.UiFont;
        Foreground = Theme.Text;

        _path = System.IO.Path.GetFullPath(items[0].Path);
        _ffmpeg = tools.Ffmpeg;
        _vlc ??= CreateVlc();
        _media = new Media(_vlc, new Uri(_path));
        _player = new MediaPlayer(_media);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 22), Width = 720 };
        stack.Children.Add(new TextBlock
        {
            Text = items.Count == 1 ? items[0].Name : Items(items.Count),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (Formats.Video.Contains(items[0].Format))
        {
            // VideoView hosts a native window, so nothing can be drawn on top of it: the still replaces it instead.
            _video = new VideoView { Background = Brushes.Black };
            _video.Loaded += (_, _) => _video.MediaPlayer = _player;
            var screen = new Grid { Height = 405, Background = Brushes.Black };
            screen.Children.Add(_video);
            screen.Children.Add(_still);
            stack.Children.Add(screen);
        }
        else
        {
            stack.Children.Add(new Border
            {
                Height = 96,
                CornerRadius = new CornerRadius(8),
                Background = Theme.Card,
                Child = new TextBlock { Text = Glyphs.Audio, FontFamily = Theme.Icons, FontSize = 36, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        }

        _timeline = new Slider { Minimum = 0, Maximum = 1, IsSelectionRangeEnabled = true, Margin = new Thickness(0, 12, 0, 0), Focusable = false };
        _timeline.ValueChanged += (_, e) =>
        {
            if (!_syncing)
                Seek((long)e.NewValue, still: !_player.IsPlaying);
        };
        stack.Children.Add(_timeline);

        var transport = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        var back = Controls.Button("", () => Step(-1), glyph: Glyphs.Previous);
        var play = Controls.Button("", TogglePlay, glyph: Glyphs.Play);
        var forward = Controls.Button("", () => Step(1), glyph: Glyphs.Next);
        _playGlyph = (TextBlock)((Panel)play.Child).Children[0];
        foreach (var b in new[] { back, play, forward })
        {
            b.Margin = new Thickness(0, 0, 6, 0);
            transport.Children.Add(b);
        }
        _clock.VerticalAlignment = VerticalAlignment.Center;
        _clock.Margin = new Thickness(10, 0, 0, 0);
        transport.Children.Add(_clock);
        stack.Children.Add(transport);

        var marks = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        marks.Children.Add(Field(T("Start", "Начало"), _start, T("[  Set start  (I)", "[  Начало здесь  (I)"), () => _start.Text = Mark(end: false)));
        marks.Children.Add(Field(T("End", "Конец"), _end, T("Set end  (O)  ]", "Конец здесь  (O)  ]"), () => _end.Text = Mark(end: true)));
        stack.Children.Add(marks);
        _start.TextChanged += (_, _) => ShowSelection();
        _end.TextChanged += (_, _) => ShowSelection();
        stack.Children.Add(new TextBlock
        {
            Text = T("Space — play/pause · ←/→ — frame · Shift+←/→ — second · I/O — start/end · empty end = until the end",
                "Пробел — пауза · ←/→ — кадр · Shift+←/→ — секунда · I/O — начало/конец · пустой конец = до конца файла"),
            Opacity = 0.65,
            FontSize = 11.5,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var ok = Controls.Button(T("Run", "Выполнить"), () => DialogResult = true, primary: true);
        ok.Margin = new Thickness(0, 0, 8, 0);
        var cancel = Controls.Button(T("Cancel", "Отмена"), () => DialogResult = false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        Content = stack;

        PreviewKeyDown += OnKey;
        _player.TimeChanged += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            // While paused our own position rules: late events from playback must not undo a frame step.
            if (_player.IsPlaying)
                Show(e.Time);
        });
        _player.LengthChanged += (_, e) => Dispatcher.BeginInvoke(() => SetLength(e.Length));
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _playGlyph.Text = Glyphs.Pause;
            ShowVideo(true);
        });
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => _playGlyph.Text = Glyphs.Play);
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _playGlyph.Text = Glyphs.Play);
        Loaded += async (_, _) =>
        {
            ForegroundHelper.Force(this);
            // LibVLC's own frame rate is an estimate (24.75 for a 25 fps file); ffmpeg reads the stream's.
            if (_video is not null && tools.Ffmpeg is { } ffmpeg && await FfmpegBackend.FrameRateAsync(ffmpeg, items[0].Path) is { } fps)
                _frameMs = 1000 / fps;
            await _media.Parse(MediaParseOptions.ParseLocal);
            SetLength(_media.Duration);
            if (_video is null)
                return;
            // Show the first frame: LibVLC creates the picture only once playback starts
            // (":start-paused" leaves it black), so play muted and pause as soon as it appears.
            _player.Mute = true;
            _player.Vout += OnFirstPicture;
            _player.Play();
        };
        Closed += (_, _) =>
        {
            // Stopping LibVLC can block for a moment; never on the UI thread.
            var player = _player;
            var media = _media;
            var video = _video;
            video?.Dispose();
            var stills = _stills;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                player.Stop();
                player.Dispose();
                media.Dispose();
                OutputPaths.TryDelete(stills);
            });
        };
    }

    public IReadOnlyDictionary<string, string> Values => new Dictionary<string, string> { ["start"] = _start.Text, ["end"] = _end.Text };

    // For the self-test.
    internal long Length => _length;
    internal long Position => _position;
    internal double FrameMs => _frameMs;

    private static LibVLC CreateVlc()
    {
        LibVLCSharp.Shared.Core.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        return new LibVLC("--no-video-title-show", "--no-osd", "--no-sub-autodetect-file", "--no-stats", "--quiet");
    }

    private static StackPanel Field(string label, TextBox box, string button, Action set)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        panel.Children.Add(Controls.FieldLabel(label, 0));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        box.Width = 150;
        row.Children.Add(box);
        var b = Controls.Button(button, set);
        b.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(b);
        panel.Children.Add(row);
        return panel;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox && e.Key is not (Key.Enter or Key.Escape))
            return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(shift ? -1000 / _frameMs : -1); break;
            case Key.Right: Step(shift ? 1000 / _frameMs : 1); break;
            case Key.I: _start.Text = Mark(end: false); break;
            case Key.O: _end.Text = Mark(end: true); break;
            case Key.Enter: DialogResult = true; break;
            case Key.Escape: DialogResult = false; break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnFirstPicture(object? sender, MediaPlayerVoutEventArgs e)
    {
        _player.Vout -= OnFirstPicture;
        Dispatcher.BeginInvoke(() =>
        {
            _player.SetPause(true);
            Seek(0, still: false);
            _player.Mute = false;
        });
    }

    internal void TogglePlay()
    {
        if (_player.State == VLCState.Ended)
        {
            _player.Stop();
            _player.Play();
        }
        else if (_player.IsPlaying)
            _player.SetPause(true);
        else
            _player.Play();
    }

    /// <summary>
    /// Moves by whole frames (paused), seeking so that backward steps work too. Seeks land just inside the
    /// frame (LibVLC shows the frame at or before the time); marks use the frame's exact start (ffmpeg keeps
    /// frames at or after -ss), so the cut starts on the frame that was on screen.
    /// </summary>
    internal void Step(double frames)
    {
        _player.SetPause(true);
        Seek((long)Math.Ceiling((FrameIndex() + Math.Round(frames)) * _frameMs), still: true);
    }

    /// <summary>
    /// LibVLC lands a few frames off when seeking while paused, so a paused position is shown as a frame
    /// decoded by ffmpeg — the same decoder and the same -ss rule the cut uses, so what you see is where it cuts.
    /// </summary>
    private async void ShowStill()
    {
        if (_video is null || _ffmpeg is null)
            return;
        var wanted = ++_stillWanted;
        await _stillGate.WaitAsync();
        try
        {
            if (wanted != _stillWanted)
                return; // a newer frame was asked for meanwhile (key held down)
            var at = Math.Floor(FrameIndex() * _frameMs) / 1000;
            var file = System.IO.Path.Combine(_stills, $"{wanted}.bmp");
            await ProcessRunner.RunAsync(_ffmpeg, ["-hide_banner", "-nostdin", "-y", "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", _path, "-frames:v", "1", "-vf", "scale=-2:'min(720,ih)'", "-c:v", "bmp", file], default);
            if (wanted != _stillWanted || _player.IsPlaying || !System.IO.File.Exists(file))
                return;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            _still.Source = bitmap;
            ShowVideo(false);
            OutputPaths.TryDelete(file);
        }
        catch (ConversionException)
        {
            // No still (e.g. past the last frame): LibVLC's picture stays.
        }
        finally
        {
            _stillGate.Release();
        }
    }

    private void ShowVideo(bool video)
    {
        if (_video is null)
            return;
        _video.Visibility = video ? Visibility.Visible : Visibility.Hidden;
        _still.Visibility = video ? Visibility.Hidden : Visibility.Visible;
    }

    private long FrameIndex() => (long)Math.Floor(_position / _frameMs + 1e-6);

    /// <summary>Start of the frame on screen; for the end, the start of the next one, so the visible frame is kept.</summary>
    private string Mark(bool end) => FfmpegBackend.FormatTime(Math.Floor((FrameIndex() + (end ? 1 : 0)) * _frameMs) / 1000.0);

    private void Seek(long ms, bool still)
    {
        ms = Math.Clamp(ms, 0, Math.Max(0, _length - 1));
        if (_player.State is VLCState.Ended or VLCState.Stopped)
            _player.Play();
        _player.Time = ms;
        Show(ms);
        if (still)
            ShowStill();
    }

    private void Show(long ms)
    {
        _position = ms;
        _syncing = true;
        _timeline.Value = ms;
        _syncing = false;
        _clock.Text = $"{FfmpegBackend.FormatTime(ms / 1000.0)} / {FfmpegBackend.FormatTime(_length / 1000.0)}";
    }

    private void SetLength(long ms)
    {
        if (ms <= 0)
            return;
        _length = ms;
        _timeline.Maximum = ms;
        _timeline.SmallChange = _frameMs;
        _timeline.LargeChange = 1000;
        ShowSelection();
        Show(_position);
    }

    private void ShowSelection()
    {
        _timeline.SelectionStart = TryMs(_start.Text) ?? 0;
        _timeline.SelectionEnd = TryMs(_end.Text) ?? _length;
    }

    private static long? TryMs(string text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : (long)(FfmpegBackend.ParseTime(text) * 1000);
        }
        catch (ConversionException)
        {
            return null;
        }
    }
}
