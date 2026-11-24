using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OFPlayer.WinUI.Core.Appearance;
using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Views;
using Windows.Storage.Pickers;

namespace OFPlayer.Community;

public sealed partial class MainWindow
{
    private static readonly (string Key, string LabelKey, string Glyph)[] SettingsCategories =
    {
        ("appearance", "settings.category.appearance", "\uE790"),
        ("playback", "settings.category.playback", "\uE768"),
        ("library", "settings.category.library", "\uE8F1"),
        ("about", "settings.category.about", "\uE946"),
    };

    private string activeSettingsCategory = "appearance";
    private bool backdropExpanded = true;
    private bool capsuleExpanded;
    private bool audioOutputExpanded = true;
    private bool audioPathExpanded = true;
    private bool musicFoldersExpanded = true;
    private bool lyricFoldersExpanded = true;
    private bool licensesExpanded;
    private bool settingsModelHooked;
    private bool refreshingSettingsData;
    private bool settingsRenderQueued;
    private readonly Dictionary<string, (Border Plate, FontIcon Icon, TextBlock Label, Border Indicator)> settingsTabVisuals = new(StringComparer.Ordinal);

    internal void OpenCapsuleSettings()
    {
        activeSettingsCategory = "appearance";
        capsuleExpanded = true;
        Activate();
        _ = ShowSettings(preserveScroll: false, focusCapsule: true);
    }

    private void EnsureSettingsModelHooked()
    {
        if (settingsModelHooked)
            return;
        settingsModelHooked = true;
        model.PropertyChanged += (_, args) =>
        {
            if (closing || refreshingSettingsData || SettingsPage.Visibility != Visibility.Visible)
                return;
            if (activeSettingsCategory == "playback" && args.PropertyName is nameof(MainViewModel.CurrentTrackDetail) or nameof(MainViewModel.AudioEngineStatus) or nameof(MainViewModel.AudioEngineRequestError))
            {
                ScheduleRenderSettingsCategory(preserveScroll: true);
            }
            else if (activeSettingsCategory == "library" && args.PropertyName is nameof(MainViewModel.ManualLibraryScanStatusText) or nameof(MainViewModel.IsManualLibraryScanRunning) or nameof(MainViewModel.ListeningStatsText) or nameof(MainViewModel.StorageMaintenanceStatus))
            {
                ScheduleRenderSettingsCategory(preserveScroll: true);
            }
            else if (activeSettingsCategory == "about" && args.PropertyName is nameof(MainViewModel.DiagnosticsStatusText))
            {
                ScheduleRenderSettingsCategory(preserveScroll: true);
            }
        };
    }

    private void ScheduleRenderSettingsCategory(bool preserveScroll = true, bool resetHeader = false)
    {
        if (closing)
            return;
        if (resetHeader)
        {
            SettingsHeader.Children.Clear();
            settingsTabVisuals.Clear();
        }
        if (settingsRenderQueued)
            return;
        settingsRenderQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            settingsRenderQueued = false;
            if (!closing && SettingsPage.Visibility == Visibility.Visible)
                RenderSettingsCategory(preserveScroll: preserveScroll);
        });
    }

    private async Task ShowSettings(bool preserveScroll = false, bool focusCapsule = false)
    {
        EnsureSettingsModelHooked();
        var pageChanged = SettingsPage.Visibility != Visibility.Visible;

        LibraryPage.Visibility = VideoPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        if (pageChanged && SettingsPage.IsLoaded)
            Motion.Show(SettingsPage, offsetY: 16, durationMs: Motion.SlowMs, restart: true);

        BuildNavigation();
        RenderSettingsCategory(preserveScroll, focusCapsule);

        if (pageChanged && !refreshingSettingsData)
        {
            refreshingSettingsData = true;
            try
            {
                await model.RefreshAudioEngineAsync(clearRequestError: false);
                await model.RefreshOutputDevicesAsync();
                await model.RefreshLyricsScanDirectoriesAsync();
                if (string.IsNullOrWhiteSpace(model.ListeningStatsText))
                    await model.LoadListeningStatsAsync();
                if (string.IsNullOrWhiteSpace(model.DiagnosticsStatusText))
                    await model.RefreshDiagnosticsStatusAsync();
            }
            finally
            {
                refreshingSettingsData = false;
            }
            if (!closing && SettingsPage.Visibility == Visibility.Visible)
                RenderSettingsCategory(preserveScroll: true, focusCapsule: focusCapsule);
        }
    }

    private void RenderSettingsCategory(bool preserveScroll = false, bool focusCapsule = false)
    {
        if (closing)
            return;
        var savedOffset = preserveScroll ? SettingsScroller.VerticalOffset : 0.0;
        BuildSettingsHeader();
        SettingsContent.Children.Clear();
        switch (activeSettingsCategory)
        {
            case "playback":
                BuildPlaybackCategory();
                break;
            case "library":
                BuildLibraryCategory();
                break;
            case "about":
                BuildAboutCategory();
                break;
            default:
                BuildAppearanceCategory();
                break;
        }

        if (focusCapsule)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!closing && SettingsPage.Visibility == Visibility.Visible)
                    SettingsScroller.ChangeView(null, SettingsScroller.ScrollableHeight, null, false);
            });
        }
        else if (preserveScroll && savedOffset > 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!closing && SettingsPage.Visibility == Visibility.Visible)
                    SettingsScroller.ChangeView(null, Math.Min(savedOffset, SettingsScroller.ScrollableHeight), null, true);
            });
        }
    }

    private void BuildSettingsHeader()
    {
        if (SettingsHeader.Children.Count > 0 && settingsTabVisuals.Count == SettingsCategories.Length)
        {
            UpdateSettingsHeaderSelection();
            return;
        }

        var t = Localization.Get;
        SettingsHeader.Children.Clear();
        settingsTabVisuals.Clear();
        SettingsHeader.Children.Add(new TextBlock
        {
            Text = t("settings.title"),
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
        });

        var navBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        foreach (var (key, labelKey, glyph) in SettingsCategories)
        {
            var categoryKey = key;
            var pillContent = new Grid
            {
                RowSpacing = 4,
                IsHitTestVisible = false,
            };
            pillContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pillContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Padding = new Thickness(12, 6, 12, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
            var label = new TextBlock
            {
                Text = t(labelKey),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(icon);
            row.Children.Add(label);
            pillContent.Children.Add(row);

            var indicator = new Border
            {
                Height = 3,
                Width = 20,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            OFPlayer_WinUI.WindowTheme.Brush(indicator, "Background", "AccentFillColorDefaultBrush");
            Grid.SetRow(indicator, 1);
            pillContent.Children.Add(indicator);

            var pillPlate = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Child = pillContent,
                IsHitTestVisible = false,
            };

            settingsTabVisuals[categoryKey] = (pillPlate, icon, label, indicator);

            var button = new Button
            {
                Padding = new Thickness(0),
                MinHeight = 36,
                MinWidth = 0,
                Style = (Style)Application.Current.Resources["QuietButtonStyle"],
                Content = pillPlate,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, $"SettingsTab_{categoryKey}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, t(labelKey));
            button.Click += (_, _) =>
            {
                if (activeSettingsCategory == categoryKey)
                    return;
                activeSettingsCategory = categoryKey;
                UpdateSettingsHeaderSelection();
                SettingsScroller.ChangeView(null, 0, null, true);
                RenderSettingsCategory(preserveScroll: false);
                if (SettingsContent.IsLoaded)
                    Motion.Show(SettingsContent, offsetY: 10, durationMs: Motion.FastMs, restart: true);
            };
            navBar.Children.Add(button);
        }

        SettingsHeader.Children.Add(navBar);
        UpdateSettingsHeaderSelection();
    }

    private void UpdateSettingsHeaderSelection()
    {
        foreach (var (key, (pillPlate, icon, label, indicator)) in settingsTabVisuals)
        {
            var isSelected = string.Equals(activeSettingsCategory, key, StringComparison.Ordinal);
            label.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            indicator.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            var fgKey = isSelected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush";
            OFPlayer_WinUI.WindowTheme.Brush(icon, "Foreground", fgKey);
            OFPlayer_WinUI.WindowTheme.Brush(label, "Foreground", fgKey);
            if (isSelected)
            {
                OFPlayer_WinUI.WindowTheme.Brushes(pillPlate,
                    ("Background", "CardBackgroundFillColorDefaultBrush"),
                    ("BorderBrush", "CardStrokeColorDefaultBrush"));
            }
            else
            {
                pillPlate.Style = null;
            }
        }
    }

    // ---- 外观 (Appearance) ----
    private void BuildAppearanceCategory()
    {
        var t = Localization.Get;

        var basicGroup = CreateSettingGroup();
        AddSelectRow(basicGroup, "\uE790", t("settings.appearance.theme"),
            new[] { t("settings.appearance.theme.system"), t("settings.appearance.theme.light"), t("settings.appearance.theme.dark") },
            Array.IndexOf(new[] { "system", "light", "dark" }, model.ThemePreference),
            async i => await model.SetThemeAsync(new[] { "system", "light", "dark" }[i]));

        AddSelectRow(basicGroup, "\uF2B7", t("settings.appearance.language"),
            new[] { t("settings.appearance.language.system"), "English", "简体中文", "日本語", "Français", "Deutsch" },
            Array.IndexOf(new[] { "system", "en", "zh-CN", "ja", "fr", "de" }, model.LanguagePreference),
            async i =>
            {
                await model.SetLanguageAsync(new[] { "system", "en", "zh-CN", "ja", "fr", "de" }[i]);
                BuildNavigation();
                ScheduleRenderSettingsCategory(preserveScroll: true, resetHeader: true);
            });

        AddSelectRow(basicGroup, "\uE740", t("settings.appearance.taskbar"),
            new[] { t("settings.appearance.taskbar.show"), t("settings.appearance.taskbar.hide") },
            model.ImmersiveTaskbarModePreference == "hide" ? 1 : 0,
            async i => await model.SetImmersiveTaskbarModeAsync(i == 1 ? "hide" : "show"),
            t("settings.appearance.taskbar.hint"));

        AddSelectRow(basicGroup, "\uE8FD", t("settings.appearance.smartPlaylists"),
            new[] { t("settings.appearance.smartPlaylists.expanded"), t("settings.appearance.smartPlaylists.collapsed") },
            model.SmartPlaylistsPreference == "collapsed" ? 1 : 0,
            async i => await model.SetSmartPlaylistsAsync(i == 1 ? "collapsed" : "expanded"),
            t("settings.appearance.smartPlaylists.hint"));

        // 层级卡片 1：内容区背景 (Content Backdrop)
        var backdropKinds = new[] { ContentBackdrop.Plain, ContentBackdrop.Cover, ContentBackdrop.Image };
        var backdropLabels = new[]
        {
            t("settings.appearance.backdrop.kind.plain"),
            t("settings.appearance.backdrop.kind.cover"),
            t("settings.appearance.backdrop.kind.image"),
        };
        var shownBackdrop = model.ShownContentBackdrop;
        var backdropIndex = Math.Max(0, Array.IndexOf(backdropKinds, shownBackdrop.Kind));
        var backdropGroup = CreateSettingExpander(
            "\uEB9F",
            t("settings.appearance.backdrop"),
            t("settings.appearance.backdrop.hint"),
            backdropLabels[backdropIndex],
            backdropExpanded,
            expanded => backdropExpanded = expanded);

        AddSelectRow(backdropGroup, null, t("settings.appearance.backdrop.kind"),
            backdropLabels,
            backdropIndex,
            async i =>
            {
                var kind = backdropKinds[i];
                if (kind == ContentBackdrop.Image && string.IsNullOrEmpty(model.ContentBackdrop.ImageFile))
                {
                    await PickBackdropImageAsync();
                    return;
                }
                await model.ChooseContentBackdropAsync(model.ContentBackdrop with { Kind = kind });
                ScheduleRenderSettingsCategory(preserveScroll: true);
            },
            shownBackdrop.Kind == ContentBackdrop.Cover ? t("settings.appearance.backdrop.kind.cover.hint") : null,
            indented: true);

        if (shownBackdrop.Kind == ContentBackdrop.Image || !string.IsNullOrEmpty(model.ContentBackdrop.ImageFile))
        {
            var imageHint = string.IsNullOrEmpty(model.ContentBackdrop.ImageFile)
                ? t("settings.appearance.backdrop.image.none")
                : model.ContentBackdrop.ImageFile;
            AddButtonRow(backdropGroup, null, t("settings.appearance.backdrop.image"),
                string.IsNullOrEmpty(model.ContentBackdrop.ImageFile) ? t("settings.appearance.backdrop.image.choose") : t("settings.appearance.backdrop.image.change"),
                PickBackdropImageAsync,
                imageHint,
                indented: true);
        }

        if (shownBackdrop.Kind != ContentBackdrop.Plain)
        {
            AddSliderRow(backdropGroup, null, t("settings.appearance.backdrop.strength"), 10, 100, model.ContentBackdrop.Strength,
                async v => await model.ChooseContentBackdropAsync(model.ContentBackdrop with { Strength = (int)v }),
                indented: true);
        }

        if (shownBackdrop.Kind == ContentBackdrop.Image)
        {
            AddSliderRow(backdropGroup, null, t("settings.appearance.backdrop.blur"), 0, 100, model.ContentBackdrop.Blur,
                async v => await model.ChooseContentBackdropAsync(model.ContentBackdrop with { Blur = (int)v }),
                indented: true);
        }

        AddSliderRow(backdropGroup, null, t("settings.appearance.backdrop.bar"), 0, 100, model.ContentBackdrop.BarClarity,
            async v => await model.ChooseContentBackdropAsync(model.ContentBackdrop with { BarClarity = (int)v }),
            t("settings.appearance.backdrop.bar.hint"),
            indented: true);

        // 层级卡片 2：歌词胶囊 (Lyric Capsule)
        var capsule = model.CapsuleAppearance;
        var capsuleThemes = new[] { "dark", "light", "cover" };
        var capsuleThemeLabels = new[]
        {
            t("settings.appearance.capsule.theme.dark"),
            t("settings.appearance.capsule.theme.light"),
            t("settings.appearance.capsule.theme.cover"),
        };
        var capsuleThemeIndex = Math.Max(0, Array.IndexOf(capsuleThemes, capsule.Theme));
        var openCapsuleButton = new Button
        {
            Content = t("tray.open"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        openCapsuleButton.Click += (_, _) => CapsuleWindow.ShowOrActivate();

        var capsuleGroup = CreateSettingExpander(
            "\uE8A5",
            t("settings.appearance.capsule"),
            t("settings.appearance.capsule.hint"),
            capsuleThemeLabels[capsuleThemeIndex],
            capsuleExpanded,
            expanded => capsuleExpanded = expanded,
            openCapsuleButton);

        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.theme"),
            capsuleThemeLabels,
            capsuleThemeIndex,
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Theme = capsuleThemes[i] }),
            indented: true);

        var capsuleAccents = new[] { "cover", "blue", "coral", "teal", "amber", "lavender" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.accent"),
            new[]
            {
                t("settings.appearance.capsule.accent.cover"),
                t("settings.appearance.capsule.accent.blue"),
                t("settings.appearance.capsule.accent.coral"),
                t("settings.appearance.capsule.accent.teal"),
                t("settings.appearance.capsule.accent.amber"),
                t("settings.appearance.capsule.accent.lavender"),
            },
            Math.Max(0, Array.IndexOf(capsuleAccents, capsule.Accent)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Accent = capsuleAccents[i] }),
            t("settings.appearance.capsule.accent.hint"),
            indented: true);

        AddSliderRow(capsuleGroup, null, t("settings.appearance.capsule.opacity"), 60, 100, capsule.Opacity,
            async v => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Opacity = (int)v }),
            indented: true);

        AddSwitchRow(capsuleGroup, null, t("settings.appearance.capsule.spin"), capsule.Spin,
            async value => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Spin = value }),
            t("settings.appearance.capsule.spin.hint"),
            indented: true);

        var capsuleFontSizes = new[] { "small", "medium", "large" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.fontSize"),
            new[] { t("settings.appearance.capsule.fontSize.small"), t("settings.appearance.capsule.fontSize.medium"), t("settings.appearance.capsule.fontSize.large") },
            Math.Max(0, Array.IndexOf(capsuleFontSizes, capsule.FontSize)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { FontSize = capsuleFontSizes[i] }),
            indented: true);

        var capsuleSecondLines = new[] { "meta", "next", "translation", "none" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.secondLine"),
            new[] { t("settings.appearance.capsule.secondLine.meta"), t("settings.appearance.capsule.secondLine.next"), t("settings.appearance.capsule.secondLine.translation"), t("settings.appearance.capsule.secondLine.none") },
            Math.Max(0, Array.IndexOf(capsuleSecondLines, capsule.SecondLine)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { SecondLine = capsuleSecondLines[i] }),
            t("settings.appearance.capsule.secondLine.hint"),
            indented: true);

        var capsuleAligns = new[] { "left", "center" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.align"),
            new[] { t("settings.appearance.capsule.align.left"), t("settings.appearance.capsule.align.center") },
            Math.Max(0, Array.IndexOf(capsuleAligns, capsule.Align)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Align = capsuleAligns[i] }),
            indented: true);

        var capsuleWaves = new[] { "echo", "spectrum", "off" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.wave"),
            new[] { t("settings.appearance.capsule.wave.echo"), t("settings.appearance.capsule.wave.spectrum"), t("settings.appearance.capsule.wave.off") },
            Math.Max(0, Array.IndexOf(capsuleWaves, capsule.Wave)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Wave = capsuleWaves[i] }),
            t("settings.appearance.capsule.wave.hint"),
            indented: true);

        var capsuleWidths = new[] { "line", "fixed" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.width"),
            new[] { t("settings.appearance.capsule.width.line"), t("settings.appearance.capsule.width.fixed") },
            Math.Max(0, Array.IndexOf(capsuleWidths, capsule.Width)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Width = capsuleWidths[i] }),
            t("settings.appearance.capsule.width.hint"),
            indented: true);

        var capsuleControls = new[] { "hover", "always" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.controls"),
            new[] { t("settings.appearance.capsule.controls.hover"), t("settings.appearance.capsule.controls.always") },
            Math.Max(0, Array.IndexOf(capsuleControls, capsule.Controls)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Controls = capsuleControls[i] }),
            indented: true);

        var capsulePaused = new[] { "keep", "mini" };
        AddSelectRow(capsuleGroup, null, t("settings.appearance.capsule.whenPaused"),
            new[] { t("settings.appearance.capsule.whenPaused.keep"), t("settings.appearance.capsule.whenPaused.mini") },
            Math.Max(0, Array.IndexOf(capsulePaused, capsule.WhenPaused)),
            async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { WhenPaused = capsulePaused[i] }),
            indented: true);

        AddButtonRow(capsuleGroup, null, t("settings.appearance.capsule.reset"), t("settings.appearance.capsule.reset.action"), () =>
        {
            CapsuleWindow.ResetPosition();
            return Task.CompletedTask;
        }, t("settings.appearance.capsule.reset.hint"), indented: true);
    }

    // ---- 播放 (Playback) ----
    private void BuildPlaybackCategory()
    {
        var t = Localization.Get;

        var behaviorGroup = CreateSettingGroup();
        AddSwitchRow(behaviorGroup, "\uE767", t("settings.playback.rememberVolume"), model.RememberVolume,
            async b => await model.SetRememberVolumeAsync(b));

        var closeOptions = new[] { "ask", "minimize", "exit" };
        AddSelectRow(behaviorGroup, "\uE8BB", t("settings.playback.close"),
            new[] { t("settings.playback.close.ask"), t("settings.playback.close.minimize"), t("settings.playback.close.exit") },
            Math.Max(0, Array.IndexOf(closeOptions, model.CloseBehaviorPreference)),
            async i => await model.SetCloseBehaviorAsync(closeOptions[i]));

        // 层级卡片 1：音频输出 (Audio Output)
        var engineStatus = model.AudioEngineStatus;
        var engineView = AudioOutputPresentation.Engine(
            engineStatus,
            model.IsConnected,
            togetherOwned: false,
            remotePlayback: model.IsRemoteMode,
            model.AudioEngineRequestError,
            MainViewModel.AudioText);

        var outputGroup = CreateSettingExpander(
            "\uE995",
            t("audio.output.title"),
            engineView.Status,
            null,
            audioOutputExpanded,
            expanded => audioOutputExpanded = expanded);

        var engineIds = new[] { "default", "xaudio2", "wasapi-exclusive" };
        var engineLabels = new[]
        {
            t("settings.playback.audioEngine.default"),
            t("settings.playback.audioEngine.xaudio2"),
            t("settings.playback.audioEngine.exclusive"),
        };
        var selectedEngine = engineStatus is null ? 0 : Math.Max(0, Array.IndexOf(engineIds, engineStatus.PreferredEngine));
        var engineHint = string.Join("\n", new[] { engineView.Help ?? t("settings.playback.audioEngine.exclusiveHint"), engineView.Notice }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        AddSelectRow(outputGroup, null, t("settings.playback.audioEngine"),
            engineLabels,
            selectedEngine,
            async i =>
            {
                await model.SetAudioEngineAsync(engineIds[i]);
                ScheduleRenderSettingsCategory(preserveScroll: true);
            },
            engineHint,
            indented: true,
            isEnabled: engineView.Selectable && !model.AudioEngineBusy);

        var deviceOptions = model.OutputDevices
            .Select(d => new AudioDeviceOption(d.Id, d.DisplayName, d.IsDefault))
            .ToList();
        var deviceView = AudioOutputPresentation.Devices(
            engineStatus?.PreferredEngine,
            deviceOptions,
            model.OutputPreferredDeviceId,
            model.OutputPrefersSystemDefault,
            model.OutputPreferredDeviceAvailable,
            model.OutputActiveDeviceName,
            MainViewModel.AudioText);

        AddSelectRow(outputGroup, null, t("settings.playback.output"),
            deviceView.Choices.Select(c => c.Label).ToArray(),
            deviceView.SelectedIndex,
            async i =>
            {
                if (i >= 0 && i < deviceView.Choices.Count)
                {
                    await model.SetOutputDeviceAsync(deviceView.Choices[i].Id);
                    ScheduleRenderSettingsCategory(preserveScroll: true);
                }
            },
            deviceView.Note,
            indented: true,
            isEnabled: deviceView.Selectable);

        AddButtonRow(outputGroup, null, t("player.output.restart"), t("player.output.restart"),
            async () =>
            {
                await model.RecoverOutputAsync();
                ScheduleRenderSettingsCategory(preserveScroll: true);
            },
            indented: true);

        // 层级卡片 2：音频链路 (Audio Signal Path)
        var pathView = AudioOutputPresentation.Path(model.AudioPathInput, Localization.Culture, MainViewModel.AudioText);
        var pathGroup = CreateSettingExpander(
            "\uE9D9",
            t("audio.path.title"),
            pathView.Summary,
            null,
            audioPathExpanded,
            expanded => audioPathExpanded = expanded);

        if (pathView.Available)
        {
            foreach (var stage in pathView.Stages)
            {
                AddInfoRow(pathGroup, null, stage.Label, stage.Value, indented: true);
            }
            if (pathView.Facts.Count > 0)
            {
                AddInfoRow(pathGroup, null, string.Join(" · ", pathView.Facts), null, indented: true, muted: true);
            }
            var diag = AudioOutputPresentation.Diagnostics(engineStatus?.ExclusiveDiagnostics, Localization.Culture, MainViewModel.AudioText);
            if (!string.IsNullOrWhiteSpace(diag))
            {
                AddInfoRow(pathGroup, null, diag, null, indented: true, muted: true);
            }
        }
        else
        {
            AddInfoRow(pathGroup, null, pathView.Summary, null, indented: true, muted: true);
        }
    }

    // ---- 曲库 (Library) ----
    private void BuildLibraryCategory()
    {
        var t = Localization.Get;

        var scanGroup = CreateSettingGroup();
        AddSwitchRow(scanGroup, "\uE8CB", t("settings.library.newTracksFirst"), model.NewTracksFirst,
            async b => await model.SetNewTracksFirstAsync(b));
        AddSwitchRow(scanGroup, "\uE895", t("settings.library.autoScan"), model.AutoScanOnLaunch,
            async b => await model.SetAutoScanOnLaunchAsync(b));

        // 层级卡片 1：音乐文件夹 (Music Folders)
        var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var scanNowButton = new Button
        {
            Content = t(model.IsManualLibraryScanRunning ? "settings.library.scanning" : "settings.library.scanNow"),
            IsEnabled = model.CanRunManualLibraryScan,
            VerticalAlignment = VerticalAlignment.Center,
        };
        scanNowButton.Click += async (_, _) => await model.RunManualLibraryScanAsync();
        var addMusicFolderButton = new Button
        {
            Content = t("settings.library.addFolder"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        addMusicFolderButton.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                await model.AddLibraryScanDirectoryAsync(folder.Path);
                ScheduleRenderSettingsCategory(preserveScroll: true);
            }
        };
        folderActions.Children.Add(scanNowButton);
        folderActions.Children.Add(addMusicFolderButton);

        var musicFoldersGroup = CreateSettingExpander(
            "\uE8B7",
            t("settings.library.folders"),
            t("settings.library.folders.note"),
            model.LibraryScanDirectories.Count.ToString("N0", Localization.Culture),
            musicFoldersExpanded,
            expanded => musicFoldersExpanded = expanded,
            folderActions);

        if (!string.IsNullOrWhiteSpace(model.ManualLibraryScanStatusText))
        {
            AddInfoRow(musicFoldersGroup, null, model.ManualLibraryScanStatusText, null, indented: true, muted: true);
        }
        if (model.LibraryScanDirectories.Count == 0)
        {
            AddInfoRow(musicFoldersGroup, null, t("settings.library.folders.note"), null, indented: true, muted: true);
        }
        else
        {
            foreach (var dir in model.LibraryScanDirectories)
            {
                var path = dir;
                AddButtonRow(musicFoldersGroup, "\uE8B7", path, t("settings.library.removeFolder"), async () =>
                {
                    await model.RemoveLibraryScanDirectoryAsync(path);
                    ScheduleRenderSettingsCategory(preserveScroll: true);
                }, indented: true);
            }
        }

        // 层级卡片 2：歌词文件夹 (Lyric Folders)
        var addLyricFolderButton = new Button
        {
            Content = t("settings.library.addFolder"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        addLyricFolderButton.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSingleFolderAsync() is { } folder)
            {
                await model.AddLyricsScanDirectoryAsync(folder.Path);
                ScheduleRenderSettingsCategory(preserveScroll: true);
            }
        };

        var lyricFoldersGroup = CreateSettingExpander(
            "\uE8A5",
            t("settings.library.lyricFolders"),
            t("settings.library.lyricFolders.note"),
            model.LyricsScanDirectories.Count.ToString("N0", Localization.Culture),
            lyricFoldersExpanded,
            expanded => lyricFoldersExpanded = expanded,
            addLyricFolderButton);

        if (model.LyricsScanDirectories.Count == 0)
        {
            AddInfoRow(lyricFoldersGroup, null, t("settings.library.lyricFolders.note"), null, indented: true, muted: true);
        }
        else
        {
            foreach (var dir in model.LyricsScanDirectories)
            {
                var path = dir;
                AddButtonRow(lyricFoldersGroup, "\uE8B7", path, t("settings.library.removeFolder"), async () =>
                {
                    await model.RemoveLyricsScanDirectoryAsync(path);
                    ScheduleRenderSettingsCategory(preserveScroll: true);
                }, indented: true);
            }
        }

        // 分组卡片 3：收听记录、存储空间与数据重置
        var maintenanceGroup = CreateSettingGroup();
        var statsHint = string.IsNullOrWhiteSpace(model.ListeningStatsText)
            ? t("settings.library.stats.empty")
            : model.ListeningStatsText;
        AddButtonRow(maintenanceGroup, "\uE9D9", t("settings.library.stats"), t("settings.library.stats.open"), async () =>
        {
            var dialog = new ListeningStatsDialog { XamlRoot = Root.XamlRoot };
            dialog.Initialize(model.LoadStatsSnapshotAsync);
            await dialog.ShowAsync();
        }, statsHint);

        var storageHint = string.IsNullOrWhiteSpace(model.StorageMaintenanceStatus)
            ? t("settings.library.storage.note")
            : $"{t("settings.library.storage.note")}\n{model.StorageMaintenanceStatus}";
        AddButtonRow(maintenanceGroup, "\uEDA2", t("settings.library.storage"), t("settings.library.storage.open"),
            async () => await new StorageAnalysisDialog(model) { XamlRoot = Root.XamlRoot }.ShowAsync(),
            storageHint);

        AddButtonRow(maintenanceGroup, "\uE74D", t("settings.library.data"), t("settings.about.reset"), async () =>
        {
            if (await Confirm(t("settings.library.reset.title"), t("settings.library.reset.body")))
            {
                await model.ResetAllDataAsync();
                ScheduleRenderSettingsCategory(preserveScroll: true, resetHeader: true);
            }
        }, t("settings.library.reset.note"));
    }

    // ---- 关于与隐私 (About & Privacy) ----
    private void BuildAboutCategory()
    {
        var t = Localization.Get;

        var aboutGroup = CreateSettingGroup();
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var appHeaderGrid = new Grid
        {
            Padding = new Thickness(16, 14, 16, 14),
            ColumnSpacing = 14,
        };
        appHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        appHeaderGrid.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/AppLogo.png")),
            Width = 36,
            Height = 36,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var appTitleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        appTitleStack.Children.Add(new TextBlock
        {
            Text = "OFPlayer Community",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });
        var versionText = new TextBlock
        {
            Text = $"{Localization.Format("settings.about.version", ("version", version))} · WinUI 本机音乐、视频与第三方媒体库",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        OFPlayer_WinUI.WindowTheme.Brush(versionText, "Foreground", "TextFillColorSecondaryBrush");
        appTitleStack.Children.Add(versionText);
        Grid.SetColumn(appTitleStack, 1);
        appHeaderGrid.Children.Add(appTitleStack);
        AddGroupElement(aboutGroup, appHeaderGrid);

        if (!string.IsNullOrWhiteSpace(model.DataRootText))
        {
            AddInfoRow(aboutGroup, "\uE8B7", model.DataRootText, null);
        }

        // 隐私与诊断分组
        var privacyGroup = CreateSettingGroup();
        AddInfoRow(privacyGroup, "\uE72E", t("settings.privacy.telemetry"), null,
            hint: "包含必需的最小每日活跃统计，不发送曲名、媒体路径或账号信息。");

        var diagHint = string.IsNullOrWhiteSpace(model.DiagnosticsStatusText)
            ? t("diagnostics.notReady")
            : model.DiagnosticsStatusText;
        AddInfoRow(privacyGroup, "\uE9D9", t("settings.privacy.diagnostics"), null, hint: diagHint);

        // 开源许可证折叠层级 (Licenses)
        var licenseFiles = GetBundledLicenseFiles();
        var licensesGroup = CreateSettingExpander(
            "\uE8A5",
            t("settings.category.licenses"),
            null,
            licenseFiles.Count > 0 ? licenseFiles.Count.ToString("N0", Localization.Culture) : null,
            licensesExpanded,
            expanded => licensesExpanded = expanded);

        foreach (var (name, filePath) in licenseFiles)
        {
            var licensePath = filePath;
            AddButtonRow(licensesGroup, null, name, t("inspector.title"), async () =>
            {
                await ShowLicenseDialogAsync(name, licensePath);
            }, Path.GetFileName(filePath), indented: true);
        }
    }

    private static IReadOnlyList<(string Name, string FilePath)> GetBundledLicenseFiles()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "licenses");
            if (!Directory.Exists(dir))
                return Array.Empty<(string, string)>();
            return Directory.GetFiles(dir, "*-LICENSE.txt")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(p);
                    var component = fileName.EndsWith("-LICENSE", StringComparison.OrdinalIgnoreCase)
                        ? fileName[..^"-LICENSE".Length]
                        : fileName;
                    return (component, p);
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    private async Task ShowLicenseDialogAsync(string componentName, string filePath)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath);
        }
        catch (Exception ex)
        {
            content = ex.Message;
        }

        var dialog = new ContentDialog
        {
            Title = componentName,
            CloseButtonText = Localization.Get("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                MaxWidth = 620,
                Content = new TextBlock
                {
                    Text = content,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
        };
        await dialog.ShowAsync();
    }

    private async Task PickBackdropImageAsync()
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            ScheduleRenderSettingsCategory(preserveScroll: true);
            return;
        }
        var imported = await model.ImportContentBackdropImageAsync(file.Path);
        if (!string.IsNullOrEmpty(imported))
            await model.ChooseContentBackdropAsync(model.ContentBackdrop with { Kind = ContentBackdrop.Image, ImageFile = imported });
        ScheduleRenderSettingsCategory(preserveScroll: true);
    }

    // ---- 分层分级卡片构建器 (Hierarchical Card & Expander Builders) ----

    private StackPanel CreateSettingGroup()
    {
        var rows = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = rows,
        };
        OFPlayer_WinUI.WindowTheme.Brushes(card,
            ("Background", "CardBackgroundFillColorDefaultBrush"),
            ("BorderBrush", "CardStrokeColorDefaultBrush"));
        SettingsContent.Children.Add(card);
        return rows;
    }

    private StackPanel CreateSettingExpander(
        string glyph,
        string title,
        string? subtitle,
        string? summaryText,
        bool initiallyExpanded,
        Action<bool> onExpandedChanged,
        FrameworkElement? headerAction = null)
    {
        var outerStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = outerStack,
        };
        OFPlayer_WinUI.WindowTheme.Brushes(card,
            ("Background", "CardBackgroundFillColorDefaultBrush"),
            ("BorderBrush", "CardStrokeColorDefaultBrush"));

        var headerRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnSpacing = 8,
            Padding = new Thickness(6, 6, 12, 6),
        };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerContent = new Grid
        {
            ColumnSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
        };
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        OFPlayer_WinUI.WindowTheme.Brush(icon, "Foreground", "TextFillColorSecondaryBrush");
        headerContent.Children.Add(icon);

        var titleStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        titleStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var sub = new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            OFPlayer_WinUI.WindowTheme.Brush(sub, "Foreground", "TextFillColorSecondaryBrush");
            titleStack.Children.Add(sub);
        }
        Grid.SetColumn(titleStack, 1);
        headerContent.Children.Add(titleStack);

        if (!string.IsNullOrWhiteSpace(summaryText))
        {
            var summary = new TextBlock
            {
                Text = summaryText,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            OFPlayer_WinUI.WindowTheme.Brush(summary, "Foreground", "TextFillColorSecondaryBrush");
            Grid.SetColumn(summary, 2);
            headerContent.Children.Add(summary);
        }

        var headerToggleButton = new Button
        {
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["QuietButtonStyle"],
            Content = headerContent,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(headerToggleButton, title);
        headerRow.Children.Add(headerToggleButton);

        if (headerAction is not null)
        {
            Grid.SetColumn(headerAction, 1);
            headerRow.Children.Add(headerAction);
        }

        var chevron = new FontIcon
        {
            Glyph = initiallyExpanded ? "\uE70E" : "\uE70D",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        OFPlayer_WinUI.WindowTheme.Brush(chevron, "Foreground", "TextFillColorSecondaryBrush");
        var chevronButton = new Button
        {
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["QuietButtonStyle"],
            Content = chevron,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chevronButton, title);
        Grid.SetColumn(chevronButton, 2);
        headerRow.Children.Add(chevronButton);

        var bodyRows = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var bodyContainer = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = initiallyExpanded ? Visibility.Visible : Visibility.Collapsed,
        };
        bodyContainer.Children.Add(bodyRows);

        var isExpanded = initiallyExpanded;
        void ToggleExpand()
        {
            isExpanded = !isExpanded;
            onExpandedChanged(isExpanded);
            chevron.Glyph = isExpanded ? "\uE70E" : "\uE70D";
            bodyContainer.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (isExpanded && bodyContainer.IsLoaded)
                Motion.Show(bodyContainer, offsetY: -6, durationMs: Motion.FastMs, restart: true);
        }

        headerToggleButton.Click += (_, _) => ToggleExpand();
        chevronButton.Click += (_, _) => ToggleExpand();

        outerStack.Children.Add(headerRow);
        outerStack.Children.Add(bodyContainer);
        SettingsContent.Children.Add(card);
        return bodyRows;
    }

    private static void AddGroupElement(StackPanel group, UIElement row, bool forceTopDivider = false)
    {
        if (group.Children.Count > 0 || forceTopDivider)
        {
            var divider = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
            OFPlayer_WinUI.WindowTheme.Brush(divider, "Background", "DividerStrokeColorDefaultBrush");
            group.Children.Add(divider);
        }
        group.Children.Add(row);
    }

    private static Grid CreateRowGrid(StackPanel group, string? glyph, string label, string? hint, bool indented, bool muted = false)
    {
        var grid = new Grid
        {
            Padding = indented ? new Thickness(48, 12, 16, 12) : new Thickness(16, 14, 16, 14),
            ColumnSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (!string.IsNullOrEmpty(glyph))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textColumn = 0;
        if (!string.IsNullOrEmpty(glyph))
        {
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
            };
            OFPlayer_WinUI.WindowTheme.Brush(icon, "Foreground", "TextFillColorSecondaryBrush");
            grid.Children.Add(icon);
            textColumn = 1;
        }

        if (string.IsNullOrWhiteSpace(hint))
        {
            var title = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            if (muted)
                OFPlayer_WinUI.WindowTheme.Brush(title, "Foreground", "TextFillColorSecondaryBrush");
            Grid.SetColumn(title, textColumn);
            grid.Children.Add(title);
        }
        else
        {
            var stack = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var title = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
            };
            if (muted)
                OFPlayer_WinUI.WindowTheme.Brush(title, "Foreground", "TextFillColorSecondaryBrush");
            var sub = new TextBlock
            {
                Text = hint,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            OFPlayer_WinUI.WindowTheme.Brush(sub, "Foreground", "TextFillColorSecondaryBrush");
            stack.Children.Add(title);
            stack.Children.Add(sub);
            Grid.SetColumn(stack, textColumn);
            grid.Children.Add(stack);
        }

        AddGroupElement(group, grid, forceTopDivider: indented);
        return grid;
    }

    private static void AddSelectRow(
        StackPanel group,
        string? glyph,
        string label,
        string[] values,
        int index,
        Func<int, Task> action,
        string? hint = null,
        bool indented = false,
        bool isEnabled = true)
    {
        var row = CreateRowGrid(group, glyph, label, hint, indented);
        var currentIndex = Math.Max(0, index);
        var combo = new ComboBox
        {
            ItemsSource = values,
            SelectedIndex = currentIndex,
            MinWidth = 200,
            MaxWidth = 360,
            IsEnabled = isEnabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(combo, row.ColumnDefinitions.Count - 1);
        row.Children.Add(combo);
        combo.SelectionChanged += async (_, _) =>
        {
            if (!combo.IsLoaded || combo.SelectedIndex < 0 || combo.SelectedIndex == currentIndex)
                return;
            currentIndex = combo.SelectedIndex;
            await action(currentIndex);
        };
    }

    private static void AddSwitchRow(
        StackPanel group,
        string? glyph,
        string label,
        bool value,
        Func<bool, Task> action,
        string? hint = null,
        bool indented = false)
    {
        var row = CreateRowGrid(group, glyph, label, hint, indented);
        var currentValue = value;
        var toggle = new ToggleSwitch
        {
            IsOn = currentValue,
            OnContent = Localization.Get("settings.general.on"),
            OffContent = Localization.Get("settings.general.off"),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(toggle, row.ColumnDefinitions.Count - 1);
        row.Children.Add(toggle);
        toggle.Toggled += async (_, _) =>
        {
            if (!toggle.IsLoaded || toggle.IsOn == currentValue)
                return;
            currentValue = toggle.IsOn;
            await action(currentValue);
        };
    }

    private static void AddSliderRow(
        StackPanel group,
        string? glyph,
        string label,
        double min,
        double max,
        double value,
        Func<double, Task> action,
        string? hint = null,
        bool indented = false)
    {
        var row = CreateRowGrid(group, glyph, label, hint, indented);
        var currentValue = (int)Math.Round(value);
        var rightStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueLabel = new TextBlock
        {
            Text = currentValue.ToString(Localization.Culture),
            Width = 32,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        OFPlayer_WinUI.WindowTheme.Brush(valueLabel, "Foreground", "TextFillColorSecondaryBrush");
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = currentValue,
            Width = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += async (_, e) =>
        {
            if (!slider.IsLoaded)
                return;
            var next = (int)Math.Round(e.NewValue);
            if (next == currentValue)
                return;
            currentValue = next;
            valueLabel.Text = next.ToString(Localization.Culture);
            await action(next);
        };
        rightStack.Children.Add(slider);
        rightStack.Children.Add(valueLabel);
        Grid.SetColumn(rightStack, row.ColumnDefinitions.Count - 1);
        row.Children.Add(rightStack);
    }

    private static void AddButtonRow(
        StackPanel group,
        string? glyph,
        string label,
        string buttonText,
        Func<Task> action,
        string? hint = null,
        bool indented = false)
    {
        var row = CreateRowGrid(group, glyph, label, hint, indented);
        var button = new Button
        {
            Content = buttonText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(button, row.ColumnDefinitions.Count - 1);
        row.Children.Add(button);
        button.Click += async (_, _) => await action();
    }

    private static void AddInfoRow(
        StackPanel group,
        string? glyph,
        string label,
        string? rightValue,
        string? hint = null,
        bool indented = false,
        bool muted = false)
    {
        var row = CreateRowGrid(group, glyph, label, hint, indented, muted);
        if (!string.IsNullOrWhiteSpace(rightValue))
        {
            var valueText = new TextBlock
            {
                Text = rightValue,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            };
            OFPlayer_WinUI.WindowTheme.Brush(valueText, "Foreground", "TextFillColorSecondaryBrush");
            Grid.SetColumn(valueText, row.ColumnDefinitions.Count - 1);
            row.Children.Add(valueText);
        }
    }
}
