using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OFPlayer.WindowsMedia;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.Views;
using Windows.Storage.Pickers;

namespace OFPlayer.Community;

public sealed record VideoRow(string Path, double Position = 0, double Duration = 0)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string DirectoryName => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed record SubtitleChoice(int Index, string Name);
public sealed record AudioTrackChoice(int Index, string Name);
public sealed record VideoRateChoice(double Rate, string Label);

public sealed partial class MainWindow
{
    private static readonly string[] SupportedVideoExtensions =
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts"
    };

    private static readonly VideoRateChoice[] VideoRateChoices =
    {
        new(0.25, "0.25x"),
        new(0.5, "0.5x"),
        new(0.75, "0.75x"),
        new(1.0, "1.0x"),
        new(1.25, "1.25x"),
        new(1.5, "1.5x"),
        new(2.0, "2.0x"),
        new(3.0, "3.0x"),
        new(4.0, "4.0x"),
    };

    private WindowsVideoBackend? video;
    private List<VideoRow> videos = [];
    private ImmersiveOverlayView? immersive;
    private bool fullScreen;
    private DispatcherTimer? videoTimer;
    private DispatcherTimer? videoProgressTimer;
    private bool updatingVideoControls;
    private double? pendingRepeatStart;
    private CancellationTokenSource? frameCaptureCts;

    private void InitializeVideo()
    {
        video = new WindowsVideoBackend(action => DispatcherQueue.TryEnqueue(() => action()));
        video.SetVolume(0.8);
        video.PlayerChanged += p => VideoSurface.SetMediaPlayer(p);
        video.StateChanged += OnVideoStateChanged;

        updatingVideoControls = true;
        try
        {
            VideoRate.ItemsSource = VideoRateChoices;
            VideoRate.SelectedIndex = Array.FindIndex(VideoRateChoices, r => Math.Abs(r.Rate - 1.0) < 0.001);
            VideoVolumeSlider.Value = 80;
        }
        finally
        {
            updatingVideoControls = false;
        }

        _ = LoadVideos();

        videoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        videoTimer.Tick += async (_, _) =>
        {
            if (videoMode && video?.Snapshot.Loaded == true)
                await SaveVideos();
        };
        videoTimer.Start();

        videoProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        videoProgressTimer.Tick += (_, _) =>
        {
            if (!videoMode || video is null || !video.Snapshot.Loaded)
                return;
            video.Refresh();
        };
        videoProgressTimer.Start();
    }

    private void OnVideoStateChanged(VideoSnapshot state)
    {
        if (closing)
            return;
        UpdateVideoControlsFromSnapshot(state);
    }

    internal void RefreshVideoUI()
    {
        if (video is null)
            return;
        RenderVideoList();
        UpdateVideoControlsFromSnapshot(video.Snapshot);
    }

    private void UpdateVideoControlsFromSnapshot(VideoSnapshot state)
    {
        updatingVideoControls = true;
        try
        {
            var countText = $"{videos.Count} 个本地视频";
            if (state.Loaded && state.Width > 0 && state.Height > 0)
                countText += $" · {state.Width}×{state.Height} · {state.Decoder}";
            else if (state.Loaded && !string.IsNullOrWhiteSpace(state.Decoder))
                countText += $" · {state.Decoder}";
            VideoCountLabel.Text = countText;

            VideoLoadingRing.IsActive = state.Phase is "opening" or "reconnecting";
            VideoEmptyOverlay.Visibility = state.Loaded || state.Phase is "opening" or "reconnecting"
                ? Visibility.Collapsed
                : Visibility.Visible;

            PlayAllVideosButton.IsEnabled = videos.Count > 0;
            ClearVideosButton.IsEnabled = videos.Count > 0;

            if (!string.IsNullOrWhiteSpace(state.Error))
            {
                VideoNotice.Severity = InfoBarSeverity.Error;
                VideoNotice.Message = state.Error;
                VideoNotice.IsOpen = true;
            }
            else if (!string.IsNullOrWhiteSpace(state.RecoveryMessage))
            {
                VideoNotice.Severity = InfoBarSeverity.Informational;
                VideoNotice.Message = state.RecoveryMessage;
                VideoNotice.IsOpen = true;
            }

            // Stage status badges (A-B repeat, speed, rotation, subtitle delay)
            var badges = new List<string>();
            if (state.RepeatStart is { } a && state.RepeatEnd is { } b)
                badges.Add($"A–B 循环 {FormatVideoTime(a)}–{FormatVideoTime(b)}");
            else if (pendingRepeatStart is { } pa)
                badges.Add($"A 点已设 {FormatVideoTime(pa)}（待设 B 点）");
            if (Math.Abs(state.Rate - 1.0) > 0.01)
                badges.Add($"倍速 {state.Rate:0.##}x");
            if (state.Rotation != 0)
                badges.Add($"旋转 {state.Rotation}°");
            if (Math.Abs(state.SubtitleDelay) >= 0.05)
                badges.Add($"字幕 {(state.SubtitleDelay >= 0 ? "+" : "")}{state.SubtitleDelay:0.0}s");

            if (badges.Count > 0 && state.Loaded)
            {
                VideoStageBadgeText.Text = string.Join(" · ", badges);
                VideoStageBadge.Visibility = Visibility.Visible;
            }
            else
            {
                VideoStageBadge.Visibility = Visibility.Collapsed;
            }

            // Subtitle tracks
            var subtitleChoices = new[] { new SubtitleChoice(-1, "关闭字幕") }
                .Concat(state.Tracks.Where(t => t.Kind == "subtitle")
                    .Select(t => new SubtitleChoice(t.Index, string.IsNullOrWhiteSpace(t.Label) ? $"字幕 #{t.Index + 1}" : t.Label)))
                .ToArray();
            var selectedSubTrack = state.Tracks.FirstOrDefault(t => t.Kind == "subtitle" && t.Selected);
            var targetSubIndex = selectedSubTrack is null
                ? 0
                : Math.Max(0, Array.FindIndex(subtitleChoices, c => c.Index == selectedSubTrack.Index));

            var currentSubChoices = SubtitleTracks.ItemsSource as SubtitleChoice[];
            if (currentSubChoices is null || !currentSubChoices.SequenceEqual(subtitleChoices))
                SubtitleTracks.ItemsSource = subtitleChoices;
            if (SubtitleTracks.SelectedIndex != targetSubIndex)
                SubtitleTracks.SelectedIndex = targetSubIndex;
            SubtitleTracks.IsEnabled = state.Loaded;
            AddSubtitleButton.IsEnabled = state.Loaded && state.CanAddExternalSubtitle;
            SubtitleSettingsButton.IsEnabled = state.Loaded;

            // Audio tracks
            var audioChoices = state.Tracks.Where(t => t.Kind == "audio")
                .Select(t => new AudioTrackChoice(t.Index, string.IsNullOrWhiteSpace(t.Label)
                    ? (string.IsNullOrWhiteSpace(t.Language) ? $"音轨 #{t.Index + 1}" : $"音轨 #{t.Index + 1} ({t.Language})")
                    : t.Label))
                .ToArray();
            if (audioChoices.Length > 1)
            {
                var currentAudio = AudioTracks.ItemsSource as AudioTrackChoice[];
                if (currentAudio is null || !currentAudio.SequenceEqual(audioChoices))
                    AudioTracks.ItemsSource = audioChoices;
                var selectedAudio = state.Tracks.FirstOrDefault(t => t.Kind == "audio" && t.Selected);
                var targetAudioIdx = selectedAudio is null
                    ? 0
                    : Math.Max(0, Array.FindIndex(audioChoices, c => c.Index == selectedAudio.Index));
                if (AudioTracks.SelectedIndex != targetAudioIdx)
                    AudioTracks.SelectedIndex = targetAudioIdx;
                AudioTracks.Visibility = Visibility.Visible;
            }
            else
            {
                AudioTracks.Visibility = Visibility.Collapsed;
            }

            // Playback rate
            var rateIdx = Array.FindIndex(VideoRateChoices, r => Math.Abs(r.Rate - state.Rate) < 0.02);
            if (rateIdx >= 0 && VideoRate.SelectedIndex != rateIdx)
                VideoRate.SelectedIndex = rateIdx;
            VideoRate.IsEnabled = state.Loaded;
            VideoToolsButton.IsEnabled = state.Loaded;
            VideoCaptureButton.IsEnabled = state.Loaded && !string.IsNullOrWhiteSpace(state.Path);

            // Bottom playback bar
            if (state.Loaded && !string.IsNullOrWhiteSpace(state.Path))
            {
                VideoNowPlayingTitle.Text = System.IO.Path.GetFileName(state.Path);
                var subParts = new List<string> { "本地视频" };
                if (state.Width > 0 && state.Height > 0)
                    subParts.Add($"{state.Width}×{state.Height}");
                if (!string.IsNullOrWhiteSpace(state.Decoder))
                    subParts.Add(state.Decoder);
                VideoNowPlayingSub.Text = string.Join(" · ", subParts);
            }
            else
            {
                VideoNowPlayingTitle.Text = "选一部本地视频开始播放";
                VideoNowPlayingSub.Text = "本地视频";
            }

            VideoPlayPauseIcon.Symbol = state.Loaded && !state.Paused ? Symbol.Pause : Symbol.Play;
            VideoPlayPauseButton.IsEnabled = state.Loaded || videos.Count > 0;
            VideoPrevButton.IsEnabled = videos.Count > 1;
            VideoNextButton.IsEnabled = videos.Count > 1;

            OFPlayer_WinUI.WindowTheme.Brush(
                VideoLoopIcon,
                "Foreground",
                state.Loop ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");

            VideoVolumeIcon.Glyph = state.Muted || state.Volume <= 0.001
                ? "\uE74F"
                : state.Volume < 0.5 ? "\uE993" : "\uE767";
            var volumePercent = Math.Round(state.Volume * 100);
            if (Math.Abs(VideoVolumeSlider.Value - volumePercent) >= 1)
                VideoVolumeSlider.Value = volumePercent;

            var duration = Math.Max(0, state.Duration);
            var position = Math.Clamp(state.Position, 0, duration > 0 ? duration : Math.Max(0, state.Position));
            VideoPositionLeftText.Text = FormatVideoTime(position);
            VideoDurationRightText.Text = FormatVideoTime(duration);
            VideoSeekSlider.IsEnabled = state.Loaded && state.CanSeek && duration > 0;
            VideoSeekSlider.Maximum = duration > 0 ? duration : 1;
            if (Math.Abs(VideoSeekSlider.Value - position) >= 0.25)
                VideoSeekSlider.Value = position;
        }
        finally
        {
            updatingVideoControls = false;
        }
    }

    private static string FormatVideoTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return "0:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    async Task LoadVideos()
    {
        var prefs = await model.CommunityCallAsync("preferences.load");
        if (prefs.TryGetProperty("video", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            videos = array.EnumerateArray()
                .Where(v => v.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                .Select(v => new VideoRow(
                    v.GetProperty("path").GetString()!,
                    v.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number ? pos.GetDouble() : 0,
                    v.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : 0))
                .ToList();
        }
        RenderVideoList();
        if (video is not null)
            UpdateVideoControlsFromSnapshot(video.Snapshot);
    }

    async Task SaveVideos()
    {
        if (video is null)
            return;
        var state = video.Snapshot;
        if (!string.IsNullOrWhiteSpace(state.Path))
        {
            var i = videos.FindIndex(v => string.Equals(v.Path, state.Path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                var updated = videos[i] with
                {
                    Position = state.Position,
                    Duration = state.Duration > 0 ? state.Duration : videos[i].Duration
                };
                if (videos[i] != updated)
                {
                    videos[i] = updated;
                    RenderVideoList();
                }
            }
        }
        await model.CommunityCallAsync("preferences.patch", new
        {
            video = videos.Select(v => new { path = v.Path, position = v.Position, duration = v.Duration }).ToArray()
        });
    }

    private void RenderVideoList()
    {
        var activePath = video?.Snapshot.Path ?? "";
        VideoListHeaderLabel.Text = videos.Count == 0 ? "本地视频列表（暂无视频）" : $"本地视频列表 · {videos.Count} 部";
        var items = new List<ListViewItem>(videos.Count);

        foreach (var row in videos)
        {
            var currentRow = row;
            var isPlaying = !string.IsNullOrWhiteSpace(activePath)
                && string.Equals(currentRow.Path, activePath, StringComparison.OrdinalIgnoreCase);

            var grid = new Grid
            {
                ColumnSpacing = 12,
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = isPlaying ? "\uE768" : "\uE173",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            OFPlayer_WinUI.WindowTheme.Brush(
                icon,
                "Foreground",
                isPlaying ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            grid.Children.Add(icon);

            var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var nameText = new TextBlock
            {
                Text = currentRow.Name,
                FontWeight = isPlaying ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (isPlaying)
                OFPlayer_WinUI.WindowTheme.Brush(nameText, "Foreground", "AccentTextFillColorPrimaryBrush");
            textStack.Children.Add(nameText);

            if (!string.IsNullOrWhiteSpace(currentRow.DirectoryName))
            {
                var dirText = new TextBlock
                {
                    Text = currentRow.DirectoryName,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                OFPlayer_WinUI.WindowTheme.Brush(dirText, "Foreground", "TextFillColorSecondaryBrush");
                textStack.Children.Add(dirText);
            }
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            var progressLabel = currentRow.Duration > 0
                ? (currentRow.Position > 1 ? $"{FormatVideoTime(currentRow.Position)} / {FormatVideoTime(currentRow.Duration)}" : FormatVideoTime(currentRow.Duration))
                : (currentRow.Position > 1 ? $"上次看到 {FormatVideoTime(currentRow.Position)}" : "");
            if (!string.IsNullOrWhiteSpace(progressLabel))
            {
                var progressText = new TextBlock
                {
                    Text = progressLabel,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                };
                OFPlayer_WinUI.WindowTheme.Brush(progressText, "Foreground", "TextFillColorSecondaryBrush");
                Grid.SetColumn(progressText, 2);
                grid.Children.Add(progressText);
            }

            var removeBtn = new Button
            {
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["QuietButtonStyle"],
                Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
            };
            ToolTipService.SetToolTip(removeBtn, "从列表移除");
            removeBtn.Click += async (_, _) => await RemoveVideoRowAsync(currentRow);
            Grid.SetColumn(removeBtn, 3);
            grid.Children.Add(removeBtn);

            var menu = new MenuFlyout();
            var playItem = new MenuFlyoutItem { Text = "播放", Icon = new FontIcon { Glyph = "\uE768" } };
            playItem.Click += async (_, _) => await OpenVideo(currentRow);
            menu.Items.Add(playItem);

            var explorerItem = new MenuFlyoutItem { Text = "在文件资源管理器中显示", Icon = new FontIcon { Glyph = "\uE8B7" } };
            explorerItem.Click += (_, _) =>
            {
                if (File.Exists(currentRow.Path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{currentRow.Path}\"") { UseShellExecute = true });
            };
            menu.Items.Add(explorerItem);

            menu.Items.Add(new MenuFlyoutSeparator());
            var removeItem = new MenuFlyoutItem { Text = "从列表移除", Icon = new FontIcon { Glyph = "\uE74D" } };
            removeItem.Click += async (_, _) => await RemoveVideoRowAsync(currentRow);
            menu.Items.Add(removeItem);

            var listItem = new ListViewItem
            {
                Tag = currentRow,
                Content = grid,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ContextFlyout = menu,
            };
            items.Add(listItem);
        }

        VideoFiles.ItemsSource = items;
        var selectedItem = items.FirstOrDefault(i => i.Tag is VideoRow r && string.Equals(r.Path, activePath, StringComparison.OrdinalIgnoreCase));
        if (selectedItem is not null)
            VideoFiles.SelectedItem = selectedItem;
    }

    private async Task RemoveVideoRowAsync(VideoRow row)
    {
        if (video is not null && string.Equals(video.Snapshot.Path, row.Path, StringComparison.OrdinalIgnoreCase))
            video.Stop();
        videos.RemoveAll(v => string.Equals(v.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        RenderVideoList();
        await SaveVideos();
        if (video is not null)
            UpdateVideoControlsFromSnapshot(video.Snapshot);
    }

    async void AddVideoClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in SupportedVideoExtensions)
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
            return;
        VideoRow? firstAdded = null;
        foreach (var f in files)
        {
            if (!videos.Any(v => string.Equals(v.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new VideoRow(f.Path);
                videos.Add(row);
                firstAdded ??= row;
            }
        }
        RenderVideoList();
        await SaveVideos();
        if (video is not null && !video.Snapshot.Loaded && firstAdded is not null)
            await OpenVideo(firstAdded);
        else if (video is not null)
            UpdateVideoControlsFromSnapshot(video.Snapshot);
    }

    async void AddVideoFolderClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSingleFolderAsync() is not { } folder)
            return;

        var added = 0;
        VideoRow? firstAdded = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories))
            {
                var ext = System.IO.Path.GetExtension(file);
                if (!SupportedVideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (videos.Any(v => string.Equals(v.Path, file, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var row = new VideoRow(file);
                videos.Add(row);
                firstAdded ??= row;
                added++;
            }
        }
        catch (Exception ex)
        {
            FailureText.Capture(ex, "Scan video folder");
        }

        RenderVideoList();
        await SaveVideos();
        if (added > 0 && video is not null && !video.Snapshot.Loaded && firstAdded is not null)
            await OpenVideo(firstAdded);
        else if (video is not null)
            UpdateVideoControlsFromSnapshot(video.Snapshot);
    }

    async void PlayAllVideosClicked(object sender, RoutedEventArgs e)
    {
        if (videos.Count == 0)
            return;
        var target = (VideoFiles.SelectedItem as ListViewItem)?.Tag as VideoRow ?? videos[0];
        await OpenVideo(target);
    }

    async void ClearVideosClicked(object sender, RoutedEventArgs e)
    {
        if (videos.Count == 0)
            return;
        if (!await Confirm("清空视频列表", "将清空本地视频播放列表记录，磁盘上的原视频文件不会受影响。"))
            return;
        video?.Stop();
        videos.Clear();
        RenderVideoList();
        await SaveVideos();
        if (video is not null)
            UpdateVideoControlsFromSnapshot(video.Snapshot);
    }

    async void VideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = (VideoFiles.SelectedItem as ListViewItem)?.Tag as VideoRow
            ?? VideoFiles.SelectedItem as VideoRow;
        if (video is null || row is null)
            return;
        await OpenVideo(row);
    }

    private async Task OpenVideo(VideoRow row)
    {
        if (video is null)
            return;
        try
        {
            pendingRepeatStart = null;
            await SaveVideos();
            await model.SuspendForVideoAsync();
            await video.OpenAsync(row.Path);
            if (row.Position > 0 && row.Position < video.Snapshot.Duration - 3)
                video.Seek(row.Position);
            RenderVideoList();
            UpdateVideoControlsFromSnapshot(video.Snapshot);
        }
        catch (Exception error)
        {
            FailureText.Capture(error, "Open local video");
        }
    }

    private Task ToggleCurrentPlayback()
    {
        if (!videoMode)
            return model.TogglePlaybackAsync();
        if (video?.Snapshot.Loaded == true)
            video.SetPaused(!video.Snapshot.Paused);
        return Task.CompletedTask;
    }

    private Task StepVideo(int direction)
    {
        if (video is null || videos.Count == 0)
            return Task.CompletedTask;
        var index = videos.FindIndex(v => string.Equals(v.Path, video.Snapshot.Path, StringComparison.OrdinalIgnoreCase));
        return OpenVideo(videos[(Math.Max(0, index) + direction + videos.Count) % videos.Count]);
    }

    async void VideoPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        if (video is null)
            return;
        if (video.Snapshot.Loaded)
        {
            video.SetPaused(!video.Snapshot.Paused);
        }
        else if (videos.Count > 0)
        {
            var target = (VideoFiles.SelectedItem as ListViewItem)?.Tag as VideoRow ?? videos[0];
            await OpenVideo(target);
        }
    }

    async void VideoPrevClicked(object sender, RoutedEventArgs e) => await StepVideo(-1);
    async void VideoNextClicked(object sender, RoutedEventArgs e) => await StepVideo(1);

    void VideoLoopClicked(object sender, RoutedEventArgs e)
    {
        if (video is null)
            return;
        video.SetLoop(!video.Snapshot.Loop);
    }

    void VideoMuteClicked(object sender, RoutedEventArgs e)
    {
        if (video is null)
            return;
        video.SetMuted(!video.Snapshot.Muted);
    }

    void VideoVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (updatingVideoControls || video is null || !VideoVolumeSlider.IsLoaded)
            return;
        var vol = Math.Clamp(e.NewValue / 100.0, 0, 1);
        if (video.Snapshot.Muted && vol > 0)
            video.SetMuted(false);
        video.SetVolume(vol);
    }

    void VideoSeekChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (updatingVideoControls || video is null || !VideoSeekSlider.IsLoaded)
            return;
        var snap = video.Snapshot;
        if (!snap.Loaded || !snap.CanSeek || Math.Abs(e.NewValue - snap.Position) < 0.6)
            return;
        try
        {
            video.Seek(e.NewValue);
        }
        catch (Exception ex)
        {
            ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
        }
    }

    void VideoStageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (video?.Snapshot.Loaded == true)
            video.SetPaused(!video.Snapshot.Paused);
    }

    void VideoStageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        VideoFullScreenClicked(sender, new RoutedEventArgs());
    }

    async void AddSubtitleClicked(object sender, RoutedEventArgs e)
    {
        if (video is null || !video.Snapshot.Loaded)
            return;
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".srt", ".vtt" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSingleFileAsync() is { } file)
        {
            try
            {
                await video.AddSubtitleAsync(file.Path);
                ShowVideoNotice($"已加载外挂字幕：{file.Name}", InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    void SubtitleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVideoControls || video is null || !video.Snapshot.Loaded)
            return;
        if (SubtitleTracks.SelectedItem is SubtitleChoice choice)
        {
            try
            {
                video.SelectSubtitle(choice.Index);
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        }
    }

    void AudioTrackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVideoControls || video is null || !video.Snapshot.Loaded)
            return;
        if (AudioTracks.SelectedItem is AudioTrackChoice choice)
        {
            try
            {
                video.SelectAudio(choice.Index);
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        }
    }

    void VideoRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVideoControls || video is null || !video.Snapshot.Loaded)
            return;
        if (VideoRate.SelectedItem is VideoRateChoice choice)
        {
            try
            {
                video.SetRate(choice.Rate);
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
                UpdateVideoControlsFromSnapshot(video.Snapshot);
            }
        }
    }

    // ---- 字幕设置面板 (Subtitle Delay, Appearance, Cue Search & Alignment, Export) ----
    void SubtitleSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (video is null || !video.Snapshot.Loaded)
            return;
        var snap = video.Snapshot;
        var panel = new StackPanel { Spacing = 12, Width = 360 };

        // 1. 字幕时间偏移与上下句跳转
        panel.Children.Add(new TextBlock { Text = "字幕时间同步", FontWeight = FontWeights.SemiBold });
        var delayLabel = new TextBlock
        {
            Text = $"当前偏移：{(snap.SubtitleDelay >= 0 ? "+" : "")}{snap.SubtitleDelay:0.1} 秒",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        OFPlayer_WinUI.WindowTheme.Brush(delayLabel, "Foreground", "TextFillColorSecondaryBrush");
        panel.Children.Add(delayLabel);

        var delayButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (text, delta) in new[] { ("-0.5s", -0.5), ("-0.1s", -0.1), ("+0.1s", 0.1), ("+0.5s", 0.5) })
        {
            var d = delta;
            var btn = new Button { Content = text, MinWidth = 58 };
            btn.Click += (_, _) =>
            {
                try
                {
                    var next = Math.Round(video.Snapshot.SubtitleDelay + d, 2);
                    video.SetSubtitleDelay(next);
                    delayLabel.Text = $"当前偏移：{(next >= 0 ? "+" : "")}{next:0.1} 秒";
                }
                catch (Exception ex)
                {
                    ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
                }
            };
            delayButtons.Children.Add(btn);
        }
        var resetDelayBtn = new Button { Content = "归零" };
        resetDelayBtn.Click += (_, _) =>
        {
            try
            {
                video.SetSubtitleDelay(0);
                delayLabel.Text = "当前偏移：+0.0 秒";
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        };
        delayButtons.Children.Add(resetDelayBtn);
        panel.Children.Add(delayButtons);

        var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var prevSubBtn = new Button { Content = "上一句字幕" };
        prevSubBtn.Click += (_, _) =>
        {
            if (video.GetAdjacentSubtitleLine(video.Snapshot.Position, forward: false) is { } line)
                video.Seek(line.Position);
        };
        var nextSubBtn = new Button { Content = "下一句字幕" };
        nextSubBtn.Click += (_, _) =>
        {
            if (video.GetAdjacentSubtitleLine(video.Snapshot.Position, forward: true) is { } line)
                video.Seek(line.Position);
        };
        var exportSubBtn = new Button { Content = "导出字幕…" };
        exportSubBtn.Click += async (_, _) => await ExportCalibratedSubtitleAsync();
        jumpRow.Children.Add(prevSubBtn);
        jumpRow.Children.Add(nextSubBtn);
        jumpRow.Children.Add(exportSubBtn);
        panel.Children.Add(jumpRow);

        // 2. 外挂字幕外观设置
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 } });
        panel.Children.Add(new TextBlock { Text = "外挂文字字幕外观", FontWeight = FontWeights.SemiBold });
        var styleNotice = new TextBlock
        {
            Text = snap.SubtitleStyleNotice,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        OFPlayer_WinUI.WindowTheme.Brush(styleNotice, "Foreground", "TextFillColorSecondaryBrush");
        panel.Children.Add(styleNotice);

        var currentAppearance = snap.SubtitleAppearance ?? new VideoSubtitleAppearance();
        var scaleSlider = new Slider
        {
            Header = "字号缩放 (%)",
            Minimum = 50,
            Maximum = 250,
            StepFrequency = 10,
            Value = Math.Round(currentAppearance.FontScale * 100),
            IsEnabled = snap.CanStyleSubtitle,
        };
        var posSlider = new Slider
        {
            Header = "垂直位置 (%)",
            Minimum = 10,
            Maximum = 95,
            StepFrequency = 5,
            Value = Math.Round(currentAppearance.VerticalPosition),
            IsEnabled = snap.CanStyleSubtitle,
        };
        var colorCombo = new ComboBox
        {
            Header = "字幕颜色",
            ItemsSource = new[] { "白色", "黄色" },
            SelectedIndex = currentAppearance.Color == "yellow" ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = snap.CanStyleSubtitle,
        };

        void ApplyStyleFromControls()
        {
            if (!video.Snapshot.CanStyleSubtitle)
                return;
            try
            {
                var scale = Math.Clamp(scaleSlider.Value / 100.0, 0.5, 2.5);
                var color = colorCombo.SelectedIndex == 1 ? "yellow" : "white";
                var vpos = Math.Clamp(posSlider.Value, 10, 95);
                video.SetSubtitleAppearance(scale, color, vpos);
                styleNotice.Text = video.Snapshot.SubtitleStyleNotice;
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        }

        scaleSlider.ValueChanged += (_, _) => { if (scaleSlider.IsLoaded) ApplyStyleFromControls(); };
        posSlider.ValueChanged += (_, _) => { if (posSlider.IsLoaded) ApplyStyleFromControls(); };
        colorCombo.SelectionChanged += (_, _) => { if (colorCombo.IsLoaded) ApplyStyleFromControls(); };

        var resetStyleBtn = new Button
        {
            Content = "恢复默认字幕外观",
            IsEnabled = snap.CanStyleSubtitle,
        };
        resetStyleBtn.Click += (_, _) =>
        {
            try
            {
                video.ResetSubtitleAppearance();
                styleNotice.Text = video.Snapshot.SubtitleStyleNotice;
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        };

        panel.Children.Add(scaleSlider);
        panel.Children.Add(posSlider);
        panel.Children.Add(colorCombo);
        panel.Children.Add(resetStyleBtn);

        // 3. 字幕台词搜索与对时
        var lines = video.GetSubtitleLines(null, 200);
        if (lines.Count > 0)
        {
            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) { Opacity = 0.25 } });
            panel.Children.Add(new TextBlock { Text = "字幕台词与快速对时", FontWeight = FontWeights.SemiBold });
            var searchBox = new TextBox { PlaceholderText = "搜索字幕台词…" };
            var lineList = new ListView { MaxHeight = 150, SelectionMode = ListViewSelectionMode.Single };

            void PopulateLines(string? query)
            {
                var matched = video.GetSubtitleLines(query, 200);
                lineList.ItemsSource = matched.Select(l => new ListViewItem
                {
                    Tag = l,
                    Content = $"[{FormatVideoTime(l.Position)}] {l.Text.Replace('\n', ' ')}",
                }).ToList();
            }
            PopulateLines(null);
            searchBox.TextChanged += (_, _) => PopulateLines(searchBox.Text);

            var lineActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var seekLineBtn = new Button { Content = "跳转到此句" };
            seekLineBtn.Click += (_, _) =>
            {
                if ((lineList.SelectedItem as ListViewItem)?.Tag is VideoSubtitleLine line)
                    video.Seek(line.Position);
            };
            var alignLineBtn = new Button { Content = "对齐到当前时间" };
            alignLineBtn.Click += (_, _) =>
            {
                if ((lineList.SelectedItem as ListViewItem)?.Tag is VideoSubtitleLine line)
                {
                    try
                    {
                        video.AlignSubtitleLine(line.Index, video.Snapshot.Position);
                        var next = video.Snapshot.SubtitleDelay;
                        delayLabel.Text = $"当前偏移：{(next >= 0 ? "+" : "")}{next:0.1} 秒";
                        PopulateLines(searchBox.Text);
                    }
                    catch (Exception ex)
                    {
                        ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
                    }
                }
            };
            lineActions.Children.Add(seekLineBtn);
            lineActions.Children.Add(alignLineBtn);

            panel.Children.Add(searchBox);
            panel.Children.Add(lineList);
            panel.Children.Add(lineActions);
        }

        var flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            }
        };
        flyout.ShowAt(SubtitleSettingsButton);
    }

    private async Task ExportCalibratedSubtitleAsync()
    {
        if (video is null)
            return;
        try
        {
            var export = video.CaptureSubtitleExport();
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(export.SubtitlePath) + ".calibrated.srt",
            };
            picker.FileTypeChoices.Add("SubRip 字幕 (.srt)", new List<string> { ".srt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSaveFileAsync() is not { } file)
                return;

            var sb = new StringBuilder();
            var idx = 1;
            foreach (var cue in export.Cues)
            {
                if (cue.End <= TimeSpan.Zero)
                    continue;
                var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;
                sb.AppendLine(idx++.ToString());
                sb.AppendLine($"{FormatSrtTimestamp(start)} --> {FormatSrtTimestamp(cue.End)}");
                sb.AppendLine(cue.Text);
                sb.AppendLine();
            }
            await File.WriteAllTextAsync(file.Path, sb.ToString(), Encoding.UTF8);
            ShowVideoNotice($"已导出校准字幕：{file.Name}", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
        }
    }

    private static string FormatSrtTimestamp(TimeSpan ts)
        => $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";

    // ---- 音频输出与声道平衡面板 (Audio Output & Balance) ----
    async void AudioSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (video is null)
            return;
        var outputs = await video.GetAudioOutputsAsync();
        var snap = video.Snapshot;
        var panel = new StackPanel { Spacing = 12, Width = 300 };

        panel.Children.Add(new TextBlock { Text = "视频音频输出与声道", FontWeight = FontWeights.SemiBold });

        var deviceCombo = new ComboBox
        {
            Header = "输出设备",
            ItemsSource = outputs.Select(o => o.Name).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(outputs, o => o.Id == snap.AudioDeviceId)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        deviceCombo.SelectionChanged += async (_, _) =>
        {
            if (!deviceCombo.IsLoaded || deviceCombo.SelectedIndex < 0 || deviceCombo.SelectedIndex >= outputs.Length)
                return;
            try
            {
                await video.SetAudioOutputAsync(outputs[deviceCombo.SelectedIndex].Id);
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        };
        panel.Children.Add(deviceCombo);

        var balanceSlider = new Slider
        {
            Header = "左右声道平衡 (-100 左 ~ +100 右)",
            Minimum = -100,
            Maximum = 100,
            StepFrequency = 5,
            Value = Math.Round(snap.AudioBalance * 100),
        };
        balanceSlider.ValueChanged += (_, _) =>
        {
            if (!balanceSlider.IsLoaded)
                return;
            video.SetAudioBalance(Math.Clamp(balanceSlider.Value / 100.0, -1, 1));
        };
        var resetBalanceBtn = new Button { Content = "声道居中" };
        resetBalanceBtn.Click += (_, _) =>
        {
            balanceSlider.Value = 0;
            video.SetAudioBalance(0);
        };
        panel.Children.Add(balanceSlider);
        panel.Children.Add(resetBalanceBtn);

        new Flyout { Content = panel }.ShowAt(AudioSettingsButton);
    }

    // ---- 画面旋转、逐帧定位与 A–B 循环面板 (Picture, Frame Step & A-B Repeat) ----
    void VideoToolsClicked(object sender, RoutedEventArgs e)
    {
        if (video is null || !video.Snapshot.Loaded)
            return;
        var snap = video.Snapshot;
        var panel = new StackPanel { Spacing = 12, Width = 320 };

        // 1. 逐帧定位
        panel.Children.Add(new TextBlock { Text = "逐帧定位", FontWeight = FontWeights.SemiBold });
        var stepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var stepBackBtn = new Button { Content = "上一帧" };
        stepBackBtn.Click += (_, _) =>
        {
            try { video.StepFrame(backward: true); }
            catch (Exception ex) { ShowVideoNotice(ex.Message, InfoBarSeverity.Warning); }
        };
        var stepForwardBtn = new Button { Content = "下一帧" };
        stepForwardBtn.Click += (_, _) =>
        {
            try { video.StepFrame(backward: false); }
            catch (Exception ex) { ShowVideoNotice(ex.Message, InfoBarSeverity.Warning); }
        };
        stepRow.Children.Add(stepBackBtn);
        stepRow.Children.Add(stepForwardBtn);
        panel.Children.Add(stepRow);

        // 2. 画面旋转
        panel.Children.Add(new TextBlock { Text = "画面旋转", FontWeight = FontWeights.SemiBold });
        var rotateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var deg in new[] { 0, 90, 180, 270 })
        {
            var angle = deg;
            var btn = new Button { Content = $"{angle}°", MinWidth = 62 };
            btn.Click += (_, _) =>
            {
                try { video.SetRotation(angle); }
                catch (Exception ex) { ShowVideoNotice(ex.Message, InfoBarSeverity.Warning); }
            };
            rotateRow.Children.Add(btn);
        }
        panel.Children.Add(rotateRow);

        // 3. A-B 区间循环
        panel.Children.Add(new TextBlock { Text = "A–B 区间循环", FontWeight = FontWeights.SemiBold });
        var abStatus = new TextBlock
        {
            Text = snap.RepeatStart is { } a && snap.RepeatEnd is { } b
                ? $"当前区间：{FormatVideoTime(a)} – {FormatVideoTime(b)}"
                : pendingRepeatStart is { } pa
                    ? $"已设 A 点：{FormatVideoTime(pa)}（请移动到终点后设 B 点）"
                    : "未设置区间循环",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        OFPlayer_WinUI.WindowTheme.Brush(abStatus, "Foreground", "TextFillColorSecondaryBrush");
        panel.Children.Add(abStatus);

        var abRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var setABtn = new Button { Content = "设当前为 A 点" };
        setABtn.Click += (_, _) =>
        {
            var pos = video.Snapshot.Position;
            pendingRepeatStart = pos;
            try
            {
                if (video.Snapshot.RepeatEnd is { } end && end > pos)
                {
                    video.SetABRepeat(pos, end);
                    abStatus.Text = $"当前区间：{FormatVideoTime(pos)} – {FormatVideoTime(end)}";
                }
                else
                {
                    video.SetABRepeat(null, null);
                    pendingRepeatStart = pos;
                    UpdateVideoControlsFromSnapshot(video.Snapshot);
                    abStatus.Text = $"已设 A 点：{FormatVideoTime(pos)}（请移动到终点后设 B 点）";
                }
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        };
        var setBBtn = new Button { Content = "设当前为 B 点" };
        setBBtn.Click += (_, _) =>
        {
            var start = video.Snapshot.RepeatStart ?? pendingRepeatStart ?? 0;
            var end = video.Snapshot.Position;
            try
            {
                video.SetABRepeat(start, end);
                pendingRepeatStart = null;
                abStatus.Text = $"当前区间：{FormatVideoTime(start)} – {FormatVideoTime(end)}";
            }
            catch (Exception ex)
            {
                ShowVideoNotice(ex.Message, InfoBarSeverity.Warning);
            }
        };
        var clearAbBtn = new Button { Content = "清除" };
        clearAbBtn.Click += (_, _) =>
        {
            pendingRepeatStart = null;
            video.SetABRepeat(null, null);
            abStatus.Text = "未设置区间循环";
        };
        abRow.Children.Add(setABtn);
        abRow.Children.Add(setBBtn);
        abRow.Children.Add(clearAbBtn);
        panel.Children.Add(abRow);

        new Flyout { Content = panel }.ShowAt(VideoToolsButton);
    }

    // ---- 视频截图复制与保存 (Frame Capture & Clipboard) ----
    async void CopyVideoFrameClicked(object sender, RoutedEventArgs e)
    {
        if (video is null || !video.Snapshot.Loaded || string.IsNullOrWhiteSpace(video.Snapshot.Path))
            return;
        var snap = video.Snapshot;
        var generation = snap.Generation;
        var width = snap.Width > 0 ? (int)snap.Width : 1920;
        var height = snap.Height > 0 ? (int)snap.Height : 1080;

        frameCaptureCts?.Cancel();
        var cts = frameCaptureCts = new CancellationTokenSource();
        try
        {
            var png = await WindowsVideoFrameCapture.PngAsync(snap.Path, snap.Position, width, height, cts.Token);
            var copied = await WindowsVideoFrameClipboard.CopyPngAsync(
                png,
                () => !closing && video is not null && video.Snapshot.Generation == generation,
                cts.Token);
            if (copied)
                ShowVideoNotice("已将当前画面复制到剪贴板。", InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowVideoNotice(ex.Message, InfoBarSeverity.Error);
        }
    }

    async void SaveVideoFrameClicked(object sender, RoutedEventArgs e)
    {
        if (video is null || !video.Snapshot.Loaded || string.IsNullOrWhiteSpace(video.Snapshot.Path))
            return;
        var snap = video.Snapshot;
        var width = snap.Width > 0 ? (int)snap.Width : 1920;
        var height = snap.Height > 0 ? (int)snap.Height : 1080;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(snap.Path);
        var timeTag = TimeSpan.FromSeconds(Math.Max(0, snap.Position));

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"{baseName}_{timeTag.Minutes:D2}m{timeTag.Seconds:D2}s.png",
        };
        picker.FileTypeChoices.Add("PNG 图片 (.png)", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSaveFileAsync() is not { } file)
            return;

        frameCaptureCts?.Cancel();
        var cts = frameCaptureCts = new CancellationTokenSource();
        try
        {
            var png = await WindowsVideoFrameCapture.PngAsync(snap.Path, snap.Position, width, height, cts.Token);
            await File.WriteAllBytesAsync(file.Path, png, cts.Token);
            ShowVideoNotice($"截图已保存：{file.Name}", InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowVideoNotice(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowVideoNotice(string message, InfoBarSeverity severity)
    {
        VideoNotice.Severity = severity;
        VideoNotice.Message = message;
        VideoNotice.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    void VideoFullScreenClicked(object sender, RoutedEventArgs e)
    {
        fullScreen = !fullScreen;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        if (fullScreen)
        {
            if (immersive is not null)
                immersive.Visibility = Visibility.Collapsed;
            VideoStageGrid.Children.Remove(VideoSurface);
            VideoSurface.AreTransportControlsEnabled = true;
            ImmersiveHost.Children.Add(VideoSurface);
            ImmersiveHost.Visibility = Visibility.Visible;
        }
        else
        {
            ImmersiveHost.Children.Remove(VideoSurface);
            VideoSurface.AreTransportControlsEnabled = false;
            VideoStageGrid.Children.Insert(0, VideoSurface);
            ImmersiveHost.Visibility = Visibility.Collapsed;
        }
    }

    private ImmersiveOverlayView EnsureImmersiveOverlay()
    {
        if (immersive is not null)
            return immersive;
        immersive = new()
        {
            Visibility = Visibility.Collapsed
        };
        immersive.CloseRequested += (_, _) => CloseImmersive();
        immersive.ToggleTaskbarRequested += async (_, _) =>
        {
            var nextMode = model.ImmersiveTaskbarHidden ? "show" : "hide";
            await model.SetImmersiveTaskbarModeAsync(nextMode);
        };
        ImmersiveHost.Children.Add(immersive);
        return immersive;
    }

    private async Task PreloadWebSurfacesAsync()
    {
        try
        {
            await Task.Delay(600);
            if (closing)
                return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (closing)
                    return;
                var overlay = EnsureImmersiveOverlay();
                overlay.SetTaskbarHidden(model.ImmersiveTaskbarHidden);
                overlay.Preload();
                CapsuleWindow.Preload();
                UpdateBackgroundPresentationState();
            });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"web surface preload failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private void ApplyImmersivePresenter()
    {
        if (fullScreen)
            return;
        var isImmersiveOpen = immersive is { Visibility: Visibility.Visible } && ImmersiveHost.Visibility == Visibility.Visible;
        var targetKind = isImmersiveOpen && model.ImmersiveTaskbarHidden
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default;
        if (AppWindow.Presenter.Kind != targetKind)
            AppWindow.SetPresenter(targetKind);
    }

    void ShowImmersive()
    {
        var overlay = EnsureImmersiveOverlay();
        overlay.SetTaskbarHidden(model.ImmersiveTaskbarHidden);
        overlay.Visibility = Visibility.Visible;
        ImmersiveHost.Visibility = Visibility.Visible;
        ApplyImmersivePresenter();
        overlay.Start();
        overlay.NotifyWebEnter();
    }

    void CloseImmersive()
    {
        if (fullScreen)
        {
            VideoFullScreenClicked(this, new());
            return;
        }

        if (immersive is not null)
            immersive.Visibility = Visibility.Collapsed;
        ImmersiveHost.Visibility = Visibility.Collapsed;
        immersive?.NotifyWebExit();
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.Default)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
    }
}
