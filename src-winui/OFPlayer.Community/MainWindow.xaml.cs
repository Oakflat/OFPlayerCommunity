using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using System.ComponentModel;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.Views;

namespace OFPlayer.Community;
public sealed partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private SystemMediaSession? media;
    private bool navigating, closing, closeReady, videoMode;
    internal new static MainWindow? Current { get; private set; }
    internal nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public MainWindow()
    {
        Current = this;
        model = new MainViewModel(action =>
        {
            if (DispatcherQueue.HasThreadAccess)
                action();
            else
                DispatcherQueue.TryEnqueue(() => action());
        });
        InitializeComponent();
        Title = "OFPlayer Community";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/AppIcon.ico"));
        try
        {
            SystemBackdrop = new ThinDesktopAcrylicBackdrop();
        }
        catch
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        }
        ModeSwitch.PlaceChosen += place =>
        {
            if (place == SidebarPlace.Video)
                VideoClicked(this, new());
            else
                ShowLibrary();
        };
        model.NavigationChanged += BuildNavigation;
        model.PropertyChanged += ModelChanged;
        model.LibraryActivated += async id =>
        {
            model.CloseRemote();
            ShowLibrary();
            await model.SelectLibraryAsync(id);
        };
        Sources.NewLibraryRequested += async (_, _) =>
        {
            if (await AskName("新建曲库") is { } n)
                await model.CreateLibraryAsync(n);
        };
        Sources.AddRemoteRequested += async (_, _) => await AddRemote();
        Sources.LibraryContextMenuFactory = LibraryMenu;
        Sources.RemoteConnectionSelected += async (_, c) =>
        {
            ShowLibrary();
            await model.OpenRemoteConnectionAsync(c);
        };
        Sources.RemoveRemoteRequested += async (_, c) =>
        {
            if (await Confirm("移除媒体库", $"移除 {c.Name} 的连接？服务器上的文件不会删除。"))
                await model.DeleteRemoteConnectionAsync(c);
        };
        PlaybackBar.QueueRequested += async (_, _) =>
        {
            ShowLibrary();
            await model.SelectViewAsync("current-queue");
        };
        PlaybackBar.DetailsRequested += async (_, _) =>
        {
            var d = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = Localization.Get("player.details"),
                Content = new TrackInspectorView
                {
                    Embedded = true
                },
                CloseButtonText = Localization.Get("common.close")
            };
            await d.ShowAsync();
        };
        PlaybackBar.ImmersiveRequested += (_, _) => ShowImmersive();
        Root.Loaded += async (_, _) =>
        {
            ApplySidebarWidth();
            ApplyNavigationStrings();
            Localization.LanguageChanged += ApplyNavigationStrings;
            UpdateTitleBarRegions();
            UpdatePlaybackBarInset();
            EnsureImmersiveOverlay();
            await model.ConnectAsync();
            BuildNavigation();
            ApplyTheme();
            ApplyContentBackdrop();
            InitializeVideo();
            InitializeTray();
            media = new SystemMediaSession(Hwnd, DispatcherQueue, model);
            _ = PreloadWebSurfacesAsync();
        };
        AppWindow.Closing += Closing;
        AppWindow.Changed += OnAppWindowChanged;
        bool sized = false;
        Activated += (_, e) =>
        {
            if (sized || e.WindowActivationState == WindowActivationState.Deactivated)
                return;
            sized = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (closing)
                    return;
                var scale = Root.XamlRoot?.RasterizationScale is > 0 and var s ? s : 1.0;
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                var width = Math.Min((int)Math.Round(1280 * scale), (int)(area.Width * 0.92));
                var height = Math.Min((int)Math.Round(850 * scale), (int)(area.Height * 0.92));
                AppWindow.Resize(new(width, height));
            });
        };
        Key(Windows.System.VirtualKey.Space, Windows.System.VirtualKeyModifiers.None, ToggleCurrentPlayback);
        Key(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Control, async () => { if (videoMode) await StepVideo(-1); else await model.PlayPreviousAsync(); });
        Key(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Control, async () => { if (videoMode) await StepVideo(1); else await model.PlayNextAsync(); });
        Key(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None, () =>
        {
            CloseImmersive();
            return Task.CompletedTask;
        });
    }

    void Key(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Func<Task> action)
    {
        var a = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        a.Invoked += async (_, e) =>
        {
            if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or AutoSuggestBox || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count > 0)
                return;
            e.Handled = true;
            await action();
        };
        Root.KeyboardAccelerators.Add(a);
    }

    void ApplyTheme()
    {
        Root.RequestedTheme = model.ThemePreference switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
        ApplyContentBackdrop();
    }

    void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActiveCollectionRef) or nameof(MainViewModel.IsRemoteMode)
            or nameof(MainViewModel.SmartPlaylistsPreference) or nameof(MainViewModel.IsConnected))
            BuildNavigation();
        if (AffectsContentBackdrop(e.PropertyName))
            ApplyContentBackdrop();
        trayMenu?.Update(CurrentTrayState());
        if (e.PropertyName == nameof(MainViewModel.PendingMissingTracksReview))
        {
            var review = model.PendingMissingTracksReview;
            MissingNotice.Message = review is null ? "" : $"发现 {review.MissingTotal} 首无法找到文件的歌曲。";
            MissingNotice.IsOpen = review is not null;
        }
        if (e.PropertyName == nameof(MainViewModel.ThemePreference))
            ApplyTheme();
        if (e.PropertyName is nameof(MainViewModel.ImmersiveTaskbarHidden) or nameof(MainViewModel.ImmersiveTaskbarModePreference))
        {
            immersive?.SetTaskbarHidden(model.ImmersiveTaskbarHidden);
            ApplyImmersivePresenter();
        }
        if (e.PropertyName == nameof(MainViewModel.StatusMessage))
        {
            Notice.Message = model.StatusMessage;
            Notice.Severity = model.StatusKind == StatusKind.Error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
            Notice.IsOpen = model.StatusMessage.Length > 0;
        }
    }

    void ShowLibrary()
    {
        var changed = LibraryPage.Visibility != Visibility.Visible;
        videoMode = false;
        video?.Stop();
        ModeSwitch.SelectedPlace = SidebarPlace.Music;
        LibraryPage.Visibility = Visibility.Visible;
        SettingsPage.Visibility = VideoPage.Visibility = Visibility.Collapsed;
        PlaybackBar.Visibility = Visibility.Visible;
        Sources.Visibility = Visibility.Visible;
        UpdatePlaybackBarInset();
        if (changed && LibraryPage.IsLoaded) Motion.Show(LibraryPage, offsetY: 16, durationMs: Motion.SlowMs, restart: true);
        BuildNavigation();
    }

    void MusicClicked(object sender, RoutedEventArgs e) => ShowLibrary();
    async void VideoClicked(object sender, RoutedEventArgs e)
    {
        var changed = VideoPage.Visibility != Visibility.Visible;
        await model.SuspendForVideoAsync();
        videoMode = true;
        ModeSwitch.SelectedPlace = SidebarPlace.Video;
        LibraryPage.Visibility = SettingsPage.Visibility = Visibility.Collapsed;
        VideoPage.Visibility = Visibility.Visible;
        PlaybackBar.Visibility = Visibility.Collapsed;
        Sources.Visibility = Visibility.Collapsed;
        UpdatePlaybackBarInset();
        if (changed && VideoPage.IsLoaded)
            Motion.Show(VideoPage, offsetY: 16, durationMs: Motion.SlowMs, restart: true);
        BuildNavigation();
        RefreshVideoUI();
    }

    private MenuFlyout LibraryMenu(string id)
    {
        var library = model.Libraries.First(l => l.Id == id);
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "重命名曲库" };
        rename.Click += async (_, _) => { if (await AskName("重命名曲库", library.Name) is { } name) await model.RenameLibraryAsync(id, name); };
        menu.Items.Add(rename);
        foreach (var (label, delta) in new[] { ("上移", -1), ("下移", 1) })
        {
            var move = new MenuFlyoutItem { Text = label };
            move.Click += async (_, _) => await model.MoveLibraryAsync(id, delta);
            menu.Items.Add(move);
        }
        var remove = new MenuFlyoutItem { Text = "删除曲库" };
        remove.Click += async (_, _) => { if (await Confirm("删除曲库", $"删除 {library.Name} 的曲库记录？媒体原文件会保留。")) await model.DeleteLibraryAsync(id, library.Name); };
        menu.Items.Add(remove);
        return menu;
    }

    void PlaybackBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePlaybackBarInset();

    void UpdatePlaybackBarInset()
    {
        if (LibraryPage is null || PlaybackBar is null)
            return;
        LibraryPage.SetPlaybackBarInset(PlaybackBar.Visibility == Visibility.Visible ? PlaybackBar.ActualHeight : 0);
    }

    void ShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ShellClip is not null) ShellClip.Rect = new(0, 0, e.NewSize.Width, e.NewSize.Height);
        if (SidebarToggle is not null) UpdateTitleBarRegions();
        UpdatePlaybackBarInset();
    }

    private async void ReviewMissingClicked(object sender, RoutedEventArgs args)
    {
        if (model.PendingMissingTracksReview is not { } review)
            return;
        var list = new ListView
        {
            ItemsSource = review.Tracks.Select(t => t.Title + "\n" + t.SourcePath).ToArray(),
            MaxHeight = 360
        };
        var choice = await new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "失效歌曲",
            Content = list,
            PrimaryButtonText = "从曲库移除",
            CloseButtonText = "保留记录",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
        if (choice == ContentDialogResult.Primary)
            await model.ConfirmMissingTracksCleanupAsync(review);
        else
            model.KeepMissingTracks(review);
    }

    internal async Task<string?> AskName(string title, string value = "")
    {
        var box = new TextBox
        {
            Text = value,
            MinWidth = 300,
            MaxLength = 200
        };
        var d = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await d.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    internal async Task<bool> Confirm(string title, string text) => await new ContentDialog
    {
        XamlRoot = Root.XamlRoot,
        Title = title,
        Content = text,
        PrimaryButtonText = "确认",
        CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Close
    }.ShowAsync() == ContentDialogResult.Primary;

    async Task AddRemote()
    {
        var name = new TextBox { Header = "名称" };
        var provider = new ComboBox
        {
            Header = "类型",
            ItemsSource = new[] { "WebDAV", "Navidrome / Subsonic" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var endpoint = new TextBox { Header = "服务器地址", PlaceholderText = "https://" };
        var path = new TextBox { Header = "目录（可选）" };
        var user = new TextBox { Header = "用户名" };
        var password = new PasswordBox { Header = "密码" };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        foreach (var c in new Control[] { name, provider, endpoint, path, user, password })
            panel.Children.Add(c);
        var d = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "添加网络媒体库",
            Content = panel,
            PrimaryButtonText = "连接并保存",
            CloseButtonText = "取消"
        };
        d.PrimaryButtonClick += async (_, args) =>
        {
            var def = args.GetDeferral();
            try
            {
                var draft = new RemoteConnectionDraft(name.Text, provider.SelectedIndex == 0 ? "webdav" : "subsonic", endpoint.Text, path.Text, user.Text, password.Password);
                var test = await model.TestRemoteConnectionAsync(draft);
                if (!test.Ok)
                {
                    args.Cancel = true;
                    endpoint.Description = test.Message;
                }
                else if (!await model.SaveRemoteConnectionAsync(draft))
                    args.Cancel = true;
            }
            finally
            {
                def.Complete();
            }
        };
        await d.ShowAsync();
    }
}
