using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ffmpegplayer;

internal sealed class MainForm : Form
{
    private const int WM_SETREDRAW = 0x000B;
    private const string DefaultMediaFileName = "go1080p25.mp4";
    private const string DefaultMediaRootPath = @"C:\casparcg\_media";
    private const string DefaultDeckLinkDeviceName = "DeckLink SDI 4K";
    private const string DefaultDeckLinkModeCode = "Hi50";
    private const string SettingsFolderName = "DeckLinkPlayer";
    private const string SettingsFileName = "settings.txt";
    private const uint PdhFormatDouble = 0x00000200;
    private const int PdhSuccess = 0;
    private const double CpuSmoothingFactor = 0.35;
    private const int FixedClientWidth = 1282;
    private const int RootPadding = 18;
    private const int HeaderRowHeight = 62;
    private const int ActionRowHeight = 110;
    private const int RemainingTimeRowHeight = 28;
    private const int CurrentTimeRowHeight = 28;
    private const int SourcePanelHeight = AppPreviewAreaHeight + ActionRowHeight;
    private const int ToggleRowHeight = 44;
    private const int AppPreviewWidth = 640;
    private const int AppPreviewHeight = 360;
    private const int AppPreviewAreaHeight = AppPreviewHeight + RemainingTimeRowHeight + CurrentTimeRowHeight;
    private const int AppAudioMeterColumnWidth = 34;
    private const int AppAudioMeterPanelWidth = AppAudioMeterColumnWidth * 2;
    private const int AppPreviewPanelWidth = AppPreviewWidth + AppAudioMeterPanelWidth;
    private const int PreviewColumnWidth = AppPreviewPanelWidth + 18;
    private const int SourceColumnWidth = 520;
    private const int SeekGroupWidth = AppPreviewPanelWidth;
    private const int SeekPositiveGroupGap = 8;
    private const int TransportSpanWidth = SourceColumnWidth + PreviewColumnWidth;
    private const int DetailsPanelHeight = 320;
    private const int SettingsAreaVerticalPadding = 12;
    private const int CollapsedSettingsAreaHeight =
        AppPreviewAreaHeight + ActionRowHeight + ToggleRowHeight + SettingsAreaVerticalPadding;
    private const int ExpandedSettingsAreaHeight = CollapsedSettingsAreaHeight + DetailsPanelHeight;
    private const int CollapsedClientHeight = RootPadding * 2 + HeaderRowHeight + CollapsedSettingsAreaHeight;
    private const int ExpandedClientHeight = RootPadding * 2 + HeaderRowHeight + ExpandedSettingsAreaHeight;
    // In-process FFmpeg decoding can crash the whole GUI with native access violations.
    // Keep the implementation available for an isolated helper process later, but do not call it here.
    private const bool EnableInProcessNativeSeekPreview = false;

    private readonly FfmpegDeckLink _deckLink = new();
    private readonly DeckLinkSdkPlayer _sdkPlayer = new();
    private readonly TextBox _inputPathBox = new();
    private readonly TextBox _mediaRootPathBox = new();
    private readonly TextBox _mediaSearchBox = new();
    private readonly TreeView _mediaTree = new();
    private readonly DataGridView _mediaGrid = new();
    private readonly ComboBox _deviceBox = new();
    private readonly ComboBox _modeBox = new();
    private readonly TextBox _videoSizeBox = new();
    private readonly TextBox _frameRateBox = new();
    private readonly TextBox _pixelFormatBox = new();
    private readonly NumericUpDown _audioChannelsBox = new();
    private readonly NumericUpDown _prerollBox = new();
    private readonly ComboBox _duplexBox = new();
    private readonly ComboBox _linkBox = new();
    private readonly ComboBox _levelABox = new();
    private readonly Button _stopButton = new();
    private readonly Button _pauseResumeButton = new();
    private readonly Button _refreshMediaButton = new();
    private readonly Button _browseMediaRootButton = new();
    private readonly Button _clearMediaSearchButton = new();
    private readonly Button _toggleSettingsButton = new();
    private readonly Button _toggleLogButton = new();
    private readonly CheckBox _previewOnlyCheckBox = new();
    private readonly CheckBox _pcAudioCheckBox = new();
    private readonly Button _refreshDevicesButton = new();
    private readonly Button _refreshModesButton = new();
    private readonly Button _seekBackOneSecondButton = new();
    private readonly Button _seekBackTenFramesButton = new();
    private readonly Button _seekBackFiveFramesButton = new();
    private readonly Button _seekBackOneFrameButton = new();
    private readonly Button _seekForwardOneFrameButton = new();
    private readonly Button _seekForwardFiveFramesButton = new();
    private readonly Button _seekForwardTenFramesButton = new();
    private readonly Button _seekForwardOneSecondButton = new();
    private readonly TextBox _logBox = new();
    private readonly PictureBox _appPreviewBox = new();
    private readonly AudioMeterBar _leftAudioMeter = new();
    private readonly AudioMeterBar _rightAudioMeter = new();
    private readonly Label _statusLabel = new();
    private readonly Label _cpuUsageLabel = new();
    private readonly Label _durationLabel = new();
    private readonly Label _currentTimeLabel = new();
    private readonly Label _positionStartLabel = new();
    private readonly Label _positionEndLabel = new();
    private readonly TrackBar _positionBar = new();
    private readonly System.Windows.Forms.Timer _mediaSearchTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _durationProbeTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _playbackPositionTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _scrubSeekTimer = new() { Interval = 140 };
    private readonly System.Windows.Forms.Timer _cpuUsageTimer = new() { Interval = 1000 };

    private TableLayoutPanel? _settingsSplit;
    private TableLayoutPanel? _detailsPanelLayout;
    private Control? _outputSettingsPanel;
    private Control? _logPanel;
    private CancellationTokenSource? _playbackCancellation;
    private CancellationTokenSource? _mediaSearchCancellation;
    private CancellationTokenSource? _durationProbeCancellation;
    private CancellationTokenSource? _mediaGridMetadataCancellation;
    private PlaybackPauseController? _playbackPauseController;
    private TaskCompletionSource? _playbackStoppedSignal;
    private NativeFfmpegFrameDecoder? _nativeSeekDecoder;
    private NativeDeckLinkPreviewOutput? _nativeSeekOutput;
    private NativeDeckLinkPreviewOutput? _scrubPreviewOutput;
    private PreviewFrameHelperClient? _scrubPreviewHelper;
    private CancellationTokenSource? _scrubPreviewDecodeCancellation;
    private Task? _scrubPreviewStartTask;
    private TimeSpan? _selectedMediaDuration;
    private TimeSpan? _playbackDuration;
    private DateTime? _playbackStartedAt;
    private DateTime? _playbackPausedAt;
    private TimeSpan _playbackPausedDuration;
    private TimeSpan _selectedStartOffset;
    private TimeSpan _playbackStartOffset;
    private TimeSpan? _pendingSeekOffset;
    private string? _selectedDurationPath;
    private string? _playbackPath;
    private string? _savedDeviceName;
    private string? _savedModeCode;
    private string? _nativeSeekPath;
    private string? _nativeSeekModeCode;
    private string? _nativeSeekDisabledPath;
    private string? _scrubPreviewPath;
    private string? _scrubPreviewModeCode;
    private string? _scrubPreviewHelperDisabledPath;
    private string _mediaRootPath = DefaultMediaRootPath;
    private string? _selectedMediaFolderPath;
    private TimeSpan? _pendingScrubPreviewOffset;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _settingsVisible;
    private bool _logVisible;
    private bool _nativeSeekPreviewMode;
    private bool _scrubPreviewMode;
    private bool _scrubPreviewLoopRunning;
    private bool _scrubPreviewReturnPaused;
    private bool _switchingPlayback;
    private bool _isDraggingSeek;
    private bool _seekQueueRunning;
    private bool _pendingSeekShouldRemainPaused;
    private bool _activeSeekShouldRemainPaused;
    private bool _loadingSettings;
    private bool _selectedDurationUnavailable;
    private bool _playbackDurationUnavailable;
    private bool _playbackIsStillImage;
    private bool _playbackIsTestPattern;
    private int _appPreviewFramePending;
    private ulong _lastSystemIdleTime;
    private ulong _lastSystemKernelTime;
    private ulong _lastSystemUserTime;
    private bool _hasSystemCpuSample;
    private IntPtr _cpuPdhQuery;
    private IntPtr _cpuPdhCounter;
    private bool _hasPdhCpuCounter;
    private double? _smoothedCpuPercent;

    public MainForm()
    {
        Text = GetExecutableWindowTitle();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        SetFixedClientHeight(CollapsedClientHeight);
        BackColor = Color.FromArgb(22, 25, 29);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();

        _loadingSettings = true;
        BuildUi();

        _pixelFormatBox.Text = FfmpegDeckLink.DefaultPixelFormat;
        _audioChannelsBox.Value = FfmpegDeckLink.DefaultAudioChannels;
        _prerollBox.Value = (decimal)FfmpegDeckLink.DefaultPrerollSeconds;
        _duplexBox.SelectedItem = "unset";
        _linkBox.SelectedItem = "single";
        _levelABox.SelectedItem = "true";
        LoadAppSettings();
        _inputPathBox.Text = FindDefaultMediaPath();
        LoadMediaTree();
        _durationProbeTimer.Tick += DurationProbeTimer_Tick;
        _playbackPositionTimer.Tick += (_, _) => UpdateDurationLabel();
        _scrubSeekTimer.Tick += ScrubSeekTimer_Tick;
        InitializeCpuUsageSampling();
        _cpuUsageTimer.Tick += (_, _) => UpdateCpuUsageLabel();
        _cpuUsageTimer.Start();
        ScheduleDurationProbe();

        Shown += async (_, _) => await RefreshDevicesAsync();
        FormClosing += (_, _) =>
        {
            _playbackCancellation?.Cancel();
            _mediaSearchCancellation?.Cancel();
            _durationProbeCancellation?.Cancel();
            _mediaGridMetadataCancellation?.Cancel();
            _scrubSeekTimer.Stop();
            _cpuUsageTimer.Stop();
            DisposeCpuUsageSampling();
            ExitNativeSeekPreviewMode(setStopped: false);
            ExitScrubPreviewMode(holdForReplacement: false, setStopped: false);
            DisposeAppPreviewImage();
            DeckLinkSdkPlayer.ReleaseHeldVideoOutput();
            SaveAppSettings();
        };
    }

    private static string GetExecutableWindowTitle()
    {
        var executableName = Path.GetFileName(Application.ExecutablePath);
        return string.IsNullOrWhiteSpace(executableName) ? "DeckLink Player" : executableName;
    }

    private static string GetSettingsFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            SettingsFolderName,
            SettingsFileName);
    }

    private void LoadAppSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = ReadAppSettings();
            _savedDeviceName = GetSetting(settings, "DeckLinkDevice");
            _savedModeCode = GetSetting(settings, "DeckLinkModeCode");
            var mediaRootPath = GetSetting(settings, "MediaRootPath");
            if (mediaRootPath is not null)
            {
                _mediaRootPath = mediaRootPath;
                _mediaRootPathBox.Text = mediaRootPath;
            }

            SetTextSetting(_videoSizeBox, settings, "VideoSize");
            SetTextSetting(_frameRateBox, settings, "FrameRate");
            SetTextSetting(_pixelFormatBox, settings, "PixelFormat");
            SetNumericSetting(_audioChannelsBox, settings, "AudioChannels");
            SetNumericSetting(_prerollBox, settings, "PrerollSeconds");
            SelectComboValue(_duplexBox, GetSetting(settings, "Duplex"));
            SelectComboValue(_linkBox, GetSetting(settings, "Link"));
            SelectComboValue(_levelABox, GetSetting(settings, "LevelA"));

            if (TryGetBoolSetting(settings, "PreviewOnly", out var previewOnly))
            {
                _previewOnlyCheckBox.Checked = previewOnly;
            }

            if (TryGetBoolSetting(settings, "PcAudio", out var pcAudio))
            {
                _pcAudioCheckBox.Checked = pcAudio;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Settings load skipped: {ex.Message}");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveAppSettings()
    {
        if (_loadingSettings || IsDisposed)
        {
            return;
        }

        try
        {
            if (_deviceBox.SelectedItem is not null)
            {
                _savedDeviceName = _deviceBox.SelectedItem.ToString();
            }

            if (_modeBox.SelectedItem is DeckLinkMode selectedMode)
            {
                _savedModeCode = selectedMode.Code;
            }

            var settingsPath = GetSettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

            File.WriteAllLines(
                settingsPath,
                [
                    $"DeckLinkDevice={_savedDeviceName ?? string.Empty}",
                    $"DeckLinkModeCode={_savedModeCode ?? string.Empty}",
                    $"MediaRootPath={_mediaRootPath}",
                    $"VideoSize={_videoSizeBox.Text.Trim()}",
                    $"FrameRate={_frameRateBox.Text.Trim()}",
                    $"PixelFormat={_pixelFormatBox.Text.Trim()}",
                    $"AudioChannels={_audioChannelsBox.Value.ToString(CultureInfo.InvariantCulture)}",
                    $"PrerollSeconds={_prerollBox.Value.ToString(CultureInfo.InvariantCulture)}",
                    $"Duplex={_duplexBox.SelectedItem?.ToString() ?? string.Empty}",
                    $"Link={_linkBox.SelectedItem?.ToString() ?? string.Empty}",
                    $"LevelA={_levelABox.SelectedItem?.ToString() ?? string.Empty}",
                    $"PreviewOnly={_previewOnlyCheckBox.Checked.ToString(CultureInfo.InvariantCulture)}",
                    $"PcAudio={_pcAudioCheckBox.Checked.ToString(CultureInfo.InvariantCulture)}",
                ]);
        }
        catch (Exception ex)
        {
            AppendLog($"Settings save skipped: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ReadAppSettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settingsPath = GetSettingsFilePath();
        if (!File.Exists(settingsPath))
        {
            return settings;
        }

        foreach (var line in File.ReadLines(settingsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return settings;
    }

    private static string? GetSetting(Dictionary<string, string> settings, string key)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool TryGetBoolSetting(Dictionary<string, string> settings, string key, out bool value)
    {
        value = false;
        return settings.TryGetValue(key, out var rawValue) &&
            bool.TryParse(rawValue, out value);
    }

    private static void SetTextSetting(TextBox textBox, Dictionary<string, string> settings, string key)
    {
        var value = GetSetting(settings, key);
        if (value is not null)
        {
            textBox.Text = value;
        }
    }

    private static void SetNumericSetting(NumericUpDown numericUpDown, Dictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var rawValue) ||
            !decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        numericUpDown.Value = Math.Clamp(value, numericUpDown.Minimum, numericUpDown.Maximum);
    }

    private static void SelectComboValue(ComboBox comboBox, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            if (string.Equals(comboBox.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void SetFixedClientHeight(int clientHeight)
    {
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        ClientSize = new Size(FixedClientWidth, clientHeight);
        MinimumSize = Size;
        MaximumSize = Size;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(RootPadding),
            BackColor = BackColor,
        };
        EnableDoubleBuffering(root);

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderRowHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSettingsArea(), 0, 1);
        SetSettingsVisible(false);
        SetLogVisible(false);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2, 0, 2, 0) };

        var title = new Label
        {
            Text = "DecklinkPlayer",
            AutoSize = false,
            Size = new Size(292, 38),
            Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(239, 244, 248),
            Location = new Point(0, 4),
        };

        _statusLabel.Text = "Ready";
        _statusLabel.AutoSize = false;
        _statusLabel.Size = new Size(520, 18);
        _statusLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _statusLabel.ForeColor = Color.FromArgb(130, 210, 164);
        _statusLabel.Location = new Point(304, 18);

        _cpuUsageLabel.Text = "PC CPU 0%";
        _cpuUsageLabel.AutoSize = false;
        _cpuUsageLabel.Width = 230;
        _cpuUsageLabel.Font = new Font("Segoe UI Semibold", 20F, FontStyle.Bold, GraphicsUnit.Point);
        _cpuUsageLabel.ForeColor = Color.FromArgb(239, 244, 248);
        _cpuUsageLabel.TextAlign = ContentAlignment.MiddleRight;
        _cpuUsageLabel.Dock = DockStyle.Right;
        _cpuUsageLabel.Padding = new Padding(0, 5, 0, 0);

        panel.Controls.Add(title);
        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(_cpuUsageLabel);
        return panel;
    }

    private Control BuildSettingsArea()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = BackColor,
            Padding = new Padding(0, 4, 0, 8),
        };
        EnableDoubleBuffering(area);
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SourceColumnWidth));
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PreviewColumnWidth));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, AppPreviewAreaHeight));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, ActionRowHeight));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, ToggleRowHeight));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _settingsSplit = area;

        var transport = BuildActionBar();
        transport.Margin = new Padding(0);
        transport.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        var toggles = BuildToggleBar();

        var sourcePanel = BuildSourcePanel();
        area.Controls.Add(sourcePanel, 0, 0);
        area.SetRowSpan(sourcePanel, 2);
        area.Controls.Add(BuildAppPreviewPanel(), 1, 0);
        area.Controls.Add(transport, 1, 1);
        area.Controls.Add(toggles, 0, 2);
        area.SetColumnSpan(toggles, 2);
        var details = BuildDetailsPanel();
        area.Controls.Add(details, 0, 3);
        area.SetColumnSpan(details, 2);
        return area;
    }

    private Control BuildSourcePanel()
    {
        var panel = BuildSection("Source");
        panel.Dock = DockStyle.None;
        panel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        panel.Size = new Size(SourceColumnWidth - panel.Margin.Horizontal, SourcePanelHeight);

        _inputPathBox.PlaceholderText = "Media file to play";
        _inputPathBox.TextChanged += (_, _) => ScheduleDurationProbe();
        _mediaRootPathBox.Text = _mediaRootPath;
        _mediaRootPathBox.ReadOnly = true;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = panel.BackColor,
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _mediaSearchBox.PlaceholderText = "Search media";
        _mediaSearchBox.TextChanged += (_, _) => ScheduleMediaSearch();
        _mediaSearchTimer.Tick -= MediaSearchTimer_Tick;
        _mediaSearchTimer.Tick += MediaSearchTimer_Tick;
        _clearMediaSearchButton.Text = "Clear";
        StyleButton(_clearMediaSearchButton, Color.FromArgb(52, 67, 82));
        _clearMediaSearchButton.Click += (_, _) =>
        {
            _mediaSearchBox.Clear();
            _mediaSearchTimer.Stop();
            ShowSelectedFolderFiles();
        };
        content.Controls.Add(BuildInputRow("Search", _mediaSearchBox, _clearMediaSearchButton), 0, 0);

        _refreshMediaButton.Text = "Refresh";
        StyleButton(_refreshMediaButton, Color.FromArgb(52, 67, 82));
        _refreshMediaButton.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_mediaSearchBox.Text))
            {
                LoadMediaTree();
            }
            else
            {
                await ApplyMediaSearchAsync(_mediaSearchBox.Text.Trim());
            }
        };
        _browseMediaRootButton.Text = "Browse";
        StyleButton(_browseMediaRootButton, Color.FromArgb(63, 96, 135));
        _browseMediaRootButton.Click += (_, _) => BrowseMediaRoot();
        content.Controls.Add(BuildLibraryRow(), 0, 1);

        StyleMediaTree();
        StyleMediaGrid();
        content.Controls.Add(BuildMediaBrowserPanel(panel.BackColor), 0, 2);
        panel.Controls.Add(content);

        return panel;
    }

    private Control BuildMediaBrowserPanel(Color backColor)
    {
        var browser = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = backColor,
        };
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _mediaTree.Margin = new Padding(0, 0, 8, 0);
        _mediaGrid.Margin = new Padding(0);

        browser.Controls.Add(_mediaTree, 0, 0);
        browser.Controls.Add(_mediaGrid, 1, 0);
        return browser;
    }

    private Control BuildOutputPanel()
    {
        var panel = BuildSection("DeckLink Output");
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = panel.BackColor,
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));

        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;

        _refreshDevicesButton.Text = "Refresh";
        StyleButton(_refreshDevicesButton, Color.FromArgb(52, 67, 82));
        _refreshDevicesButton.Click += async (_, _) => await RefreshDevicesAsync();

        _refreshModesButton.Text = "Modes";
        StyleButton(_refreshModesButton, Color.FromArgb(52, 67, 82));
        _refreshModesButton.Click += async (_, _) => await RefreshModesAsync();

        _deviceBox.SelectedIndexChanged += async (_, _) =>
        {
            SaveAppSettings();
            await RefreshModesAsync();
        };
        _modeBox.SelectedIndexChanged += (_, _) =>
        {
            ApplySelectedModeToFields();
            SaveAppSettings();
        };

        content.Controls.Add(BuildInputRow("Device", _deviceBox, _refreshDevicesButton), 0, 0);
        content.Controls.Add(BuildInputRow("Mode", _modeBox, _refreshModesButton), 0, 1);

        var outputGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 2,
            Height = 92,
            Padding = new Padding(0, 8, 0, 0),
        };

        outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        outputGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _videoSizeBox.PlaceholderText = "1920x1080";
        _frameRateBox.PlaceholderText = "25000/1000";
        _audioChannelsBox.Minimum = 2;
        _audioChannelsBox.Maximum = 16;
        _audioChannelsBox.Increment = 2;
        _prerollBox.Minimum = 0;
        _prerollBox.Maximum = 5;
        _prerollBox.DecimalPlaces = 1;
        _prerollBox.Increment = 0.1M;
        _duplexBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _duplexBox.Items.AddRange(["unset", "half", "full"]);
        _linkBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _linkBox.Items.AddRange(["unset", "single", "dual", "quad"]);
        _levelABox.DropDownStyle = ComboBoxStyle.DropDownList;
        _levelABox.Items.AddRange(["unset", "true", "false"]);
        RegisterDeckLinkSettingsPersistenceEvents();

        AddGridField(outputGrid, "Size", _videoSizeBox, 0, 0);
        AddGridField(outputGrid, "Rate", _frameRateBox, 2, 0);
        AddGridField(outputGrid, "Audio", _audioChannelsBox, 0, 1);
        AddGridField(outputGrid, "Preroll", _prerollBox, 2, 1);

        content.Controls.Add(outputGrid, 0, 2);

        var tailGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 2,
            Height = 84,
        };
        tailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        tailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        tailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddGridField(tailGrid, "Pixel", _pixelFormatBox, 0, 0);
        AddGridField(tailGrid, "Duplex", _duplexBox, 2, 0);
        AddGridField(tailGrid, "Link", _linkBox, 0, 1);
        AddGridField(tailGrid, "Level A", _levelABox, 2, 1);
        content.Controls.Add(tailGrid, 0, 3);

        panel.Controls.Add(content);
        return panel;
    }

    private void RegisterDeckLinkSettingsPersistenceEvents()
    {
        _videoSizeBox.TextChanged += (_, _) => SaveAppSettings();
        _frameRateBox.TextChanged += (_, _) => SaveAppSettings();
        _pixelFormatBox.TextChanged += (_, _) => SaveAppSettings();
        _audioChannelsBox.ValueChanged += (_, _) => SaveAppSettings();
        _prerollBox.ValueChanged += (_, _) => SaveAppSettings();
        _duplexBox.SelectedIndexChanged += (_, _) => SaveAppSettings();
        _linkBox.SelectedIndexChanged += (_, _) => SaveAppSettings();
        _levelABox.SelectedIndexChanged += (_, _) => SaveAppSettings();
    }

    private Control BuildDetailsPanel()
    {
        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
            Padding = new Padding(0, 8, 0, 0),
            Visible = false,
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TransportSpanWidth / 2));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TransportSpanWidth - TransportSpanWidth / 2));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, DetailsPanelHeight));
        _detailsPanelLayout = details;

        _outputSettingsPanel = BuildOutputPanel();
        _logPanel = BuildLogPanel();
        _logPanel.Margin = new Padding(0);

        details.Controls.Add(_outputSettingsPanel, 0, 0);
        details.Controls.Add(_logPanel, 1, 0);
        return details;
    }

    private Control BuildAppPreviewPanel()
    {
        var container = new Panel
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Size = new Size(AppPreviewPanelWidth, AppPreviewAreaHeight),
            Margin = new Padding(0, 0, 18, 0),
            Padding = new Padding(0),
            BackColor = Color.FromArgb(30, 35, 40),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = container.BackColor,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppAudioMeterColumnWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppPreviewWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, AppAudioMeterColumnWidth));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, RemainingTimeRowHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, AppPreviewHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, CurrentTimeRowHeight));

        _durationLabel.AutoSize = false;
        _durationLabel.Dock = DockStyle.Fill;
        _durationLabel.Margin = new Padding(0);
        _durationLabel.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
        _durationLabel.ForeColor = Color.FromArgb(236, 241, 244);
        _durationLabel.BackColor = Color.FromArgb(30, 35, 40);
        _durationLabel.TextAlign = ContentAlignment.MiddleCenter;
        _durationLabel.Text = "--";
        layout.Controls.Add(_durationLabel, 1, 0);

        ConfigurePreviewAudioMeter(_leftAudioMeter);
        ConfigurePreviewAudioMeter(_rightAudioMeter);
        layout.Controls.Add(_leftAudioMeter, 0, 1);

        var previewSurface = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Black,
        };

        _appPreviewBox.Dock = DockStyle.Fill;
        _appPreviewBox.BackColor = Color.Black;
        _appPreviewBox.BorderStyle = BorderStyle.FixedSingle;
        _appPreviewBox.SizeMode = PictureBoxSizeMode.Zoom;
        previewSurface.Controls.Add(_appPreviewBox);

        _currentTimeLabel.AutoSize = false;
        _currentTimeLabel.Dock = DockStyle.Fill;
        _currentTimeLabel.Margin = new Padding(0);
        _currentTimeLabel.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point);
        _currentTimeLabel.ForeColor = Color.FromArgb(236, 241, 244);
        _currentTimeLabel.BackColor = Color.FromArgb(30, 35, 40);
        _currentTimeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _currentTimeLabel.Text = "--";

        layout.Controls.Add(previewSurface, 1, 1);
        layout.Controls.Add(_currentTimeLabel, 1, 2);
        layout.Controls.Add(_rightAudioMeter, 2, 1);

        container.Controls.Add(layout);
        ResetAudioMeters();
        return container;
    }

    private static void ConfigurePreviewAudioMeter(AudioMeterBar meter)
    {
        meter.Dock = DockStyle.Fill;
        meter.Margin = new Padding(0);
    }

    private Control BuildPositionRow()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _positionStartLabel.Text = "0";
        _positionStartLabel.AutoSize = false;
        _positionStartLabel.Dock = DockStyle.Fill;
        _positionStartLabel.TextAlign = ContentAlignment.MiddleLeft;
        _positionStartLabel.ForeColor = Color.FromArgb(172, 184, 192);

        _positionEndLabel.Text = "--";
        _positionEndLabel.AutoSize = false;
        _positionEndLabel.Dock = DockStyle.Fill;
        _positionEndLabel.TextAlign = ContentAlignment.MiddleRight;
        _positionEndLabel.ForeColor = Color.FromArgb(172, 184, 192);

        _positionBar.Dock = DockStyle.Fill;
        _positionBar.Minimum = 0;
        _positionBar.Maximum = 1;
        _positionBar.Value = 0;
        _positionBar.TickStyle = TickStyle.None;
        _positionBar.Enabled = false;
        _positionBar.MouseDown += PositionBar_MouseDown;
        _positionBar.MouseUp += PositionBar_MouseUp;
        _positionBar.Scroll += (_, _) =>
        {
            PreviewSeekBarPosition();
            if (_isDraggingSeek && _scrubPreviewMode)
            {
                ScheduleScrubSeek();
            }
        };
        _positionBar.KeyUp += PositionBar_KeyUp;

        panel.Controls.Add(_positionStartLabel, 0, 0);
        panel.Controls.Add(_positionBar, 1, 0);
        panel.Controls.Add(_positionEndLabel, 2, 0);
        return panel;
    }

    private void ConfigureSeekStepButton(Button button, string text, Func<Task> action)
    {
        button.Text = text;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, Color.FromArgb(52, 67, 82));
        button.Width = 58;
        button.Height = 26;
        button.Enabled = false;
        button.Click += async (_, _) => await RunSeekSafelyAsync(action);
    }

    private Control BuildActionBar()
    {
        var panel = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Size = new Size(SeekGroupWidth, ActionRowHeight),
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 5, 0, 5),
            BackColor = BackColor,
        };

        _stopButton.Text = "Stop";
        StyleButton(_stopButton, Color.FromArgb(149, 64, 58));
        _stopButton.Width = 72;
        ConfigureTransportButton(_stopButton);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopPlayback();

        _pauseResumeButton.Text = "Pause";
        StyleButton(_pauseResumeButton, Color.FromArgb(183, 126, 46));
        _pauseResumeButton.Width = 82;
        ConfigureTransportButton(_pauseResumeButton);
        _pauseResumeButton.Enabled = false;
        _pauseResumeButton.Click += async (_, _) => await TogglePauseResumePlaybackAsync();

        ConfigureSeekStepButton(_seekBackOneSecondButton, "-1 sec", () => SeekRelativeAsync(TimeSpan.FromSeconds(-1)));
        ConfigureSeekStepButton(_seekForwardOneSecondButton, "+1 sec", () => SeekRelativeAsync(TimeSpan.FromSeconds(1)));
        ConfigureSeekStepButton(_seekBackTenFramesButton, "-10 fr", () => SeekRelativeFramesAsync(-10));
        ConfigureSeekStepButton(_seekForwardTenFramesButton, "+10 fr", () => SeekRelativeFramesAsync(10));
        ConfigureSeekStepButton(_seekBackFiveFramesButton, "-5 fr", () => SeekRelativeFramesAsync(-5));
        ConfigureSeekStepButton(_seekForwardFiveFramesButton, "+5 fr", () => SeekRelativeFramesAsync(5));
        ConfigureSeekStepButton(_seekBackOneFrameButton, "-1 fr", () => SeekRelativeFramesAsync(-1));
        ConfigureSeekStepButton(_seekForwardOneFrameButton, "+1 fr", () => SeekRelativeFramesAsync(1));

        panel.Controls.Add(BuildSeekActionGroup(
            SeekGroupWidth,
            new Control[]
            {
                _seekBackOneSecondButton,
                _seekBackTenFramesButton,
                _seekBackFiveFramesButton,
                _seekBackOneFrameButton,
            },
            new Control[]
            {
                _stopButton,
                _pauseResumeButton,
            },
            new Control[]
            {
                _seekForwardOneFrameButton,
                _seekForwardFiveFramesButton,
                _seekForwardTenFramesButton,
                _seekForwardOneSecondButton,
            }));
        return panel;
    }

    private Control BuildToggleBar()
    {
        var panel = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Size = new Size(SeekGroupWidth, ToggleRowHeight),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0),
            BackColor = BackColor,
        };

        _toggleSettingsButton.Text = "Show Settings";
        _toggleSettingsButton.Width = 122;
        _toggleSettingsButton.Margin = new Padding(0, 0, 6, 0);
        StyleButton(_toggleSettingsButton, Color.FromArgb(52, 67, 82));
        _toggleSettingsButton.Click += (_, _) => SetSettingsVisible(!_settingsVisible);

        _toggleLogButton.Text = "Show Log";
        _toggleLogButton.Width = 100;
        _toggleLogButton.Margin = new Padding(0, 0, 6, 0);
        StyleButton(_toggleLogButton, Color.FromArgb(52, 67, 82));
        _toggleLogButton.Click += (_, _) => SetLogVisible(!_logVisible);

        _previewOnlyCheckBox.Text = "Preview only";
        _previewOnlyCheckBox.Checked = false;
        _previewOnlyCheckBox.AutoSize = false;
        _previewOnlyCheckBox.Size = new Size(128, 34);
        _previewOnlyCheckBox.Margin = new Padding(0);
        _previewOnlyCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _previewOnlyCheckBox.ForeColor = Color.FromArgb(224, 232, 236);
        _previewOnlyCheckBox.CheckedChanged += (_, _) =>
        {
            SetStatus(PreviewOnlyMode ? "Preview only" : "Ready", PreviewOnlyMode
                ? Color.FromArgb(232, 181, 105)
                : Color.FromArgb(130, 210, 164));
            SaveAppSettings();
        };

        panel.Controls.Add(_toggleSettingsButton);
        panel.Controls.Add(_toggleLogButton);
        panel.Controls.Add(_previewOnlyCheckBox);

        _pcAudioCheckBox.Text = "PC audio";
        _pcAudioCheckBox.Checked = false;
        _pcAudioCheckBox.AutoSize = false;
        _pcAudioCheckBox.Size = new Size(104, 34);
        _pcAudioCheckBox.Margin = new Padding(0);
        _pcAudioCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _pcAudioCheckBox.ForeColor = Color.FromArgb(224, 232, 236);
        _pcAudioCheckBox.CheckedChanged += (_, _) => SaveAppSettings();
        panel.Controls.Add(_pcAudioCheckBox);
        return panel;
    }

    private Control BuildActionGroup(int height, params Control[] controls)
    {
        var width = controls.Sum(control => control.Width + control.Margin.Horizontal) + 22;
        var panel = new Panel
        {
            Width = width,
            Height = height,
            Margin = new Padding(0, 0, 14, 0),
            BackColor = Color.FromArgb(30, 35, 40),
            Padding = new Padding(8, 3, 8, 5),
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Location = new Point(8, 27),
            Size = new Size(width - 16, 38),
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = panel.BackColor,
        };

        buttonPanel.Controls.AddRange(controls);
        panel.Controls.Add(buttonPanel);
        return panel;
    }

    private Control BuildSeekActionGroup(int width, Control[] negativeControls, Control[] transportControls, Control[] positiveControls)
    {
        var leftWidth = MeasureControlsWidth(negativeControls);
        var transportWidth = MeasureControlsWidth(transportControls);
        var rightWidth = MeasureControlsWidth(positiveControls);
        var panel = new Panel
        {
            Width = width,
            Height = 92,
            Margin = new Padding(0),
            BackColor = Color.FromArgb(30, 35, 40),
            Padding = new Padding(8, 3, 8, 5),
        };

        var seekPanel = BuildPositionRow();
        seekPanel.Location = new Point(8, 5);
        seekPanel.Size = new Size(width - 16, 39);
        seekPanel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

        var buttonPanel = new Panel
        {
            Location = new Point(8, 58),
            Size = new Size(width - 16, 31),
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = panel.BackColor,
        };

        var leftPanel = BuildSeekButtonPanel(negativeControls, leftWidth);
        leftPanel.Location = new Point(0, 0);

        var transportPanel = BuildSeekButtonPanel(transportControls, transportWidth);
        transportPanel.Location = new Point(leftWidth, 0);

        var rightPanel = BuildSeekButtonPanel(positiveControls, rightWidth);
        rightPanel.Location = new Point(leftWidth + transportWidth, 0);

        var controlsWidth = leftWidth + transportWidth + SeekPositiveGroupGap + rightWidth;
        var centeredX = Math.Max(0, (buttonPanel.Width - controlsWidth) / 2);
        leftPanel.Location = new Point(centeredX, 0);
        transportPanel.Location = new Point(centeredX + leftWidth, 0);
        rightPanel.Location = new Point(centeredX + leftWidth + transportWidth + SeekPositiveGroupGap, 0);

        buttonPanel.Controls.Add(leftPanel);
        buttonPanel.Controls.Add(transportPanel);
        buttonPanel.Controls.Add(rightPanel);
        panel.Controls.Add(seekPanel);
        panel.Controls.Add(buttonPanel);
        return panel;
    }

    private static int MeasureControlsWidth(IEnumerable<Control> controls)
    {
        return controls.Sum(control => control.Width + control.Margin.Horizontal);
    }

    private static FlowLayoutPanel BuildSeekButtonPanel(Control[] controls, int width)
    {
        var panel = new FlowLayoutPanel
        {
            Size = new Size(width, 31),
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0),
            WrapContents = false,
            BackColor = Color.FromArgb(30, 35, 40),
        };

        panel.Controls.AddRange(controls);
        return panel;
    }

    private static void ConfigureTransportButton(Button button)
    {
        button.Height = 31;
        button.Margin = new Padding(0);
        button.Padding = new Padding(0);
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.UseCompatibleTextRendering = true;
    }

    private Control BuildLogPanel()
    {
        var panel = BuildSection("Playback Log");
        _logPanel = panel;
        panel.Padding = new Padding(14, 44, 14, 14);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Both;
        _logBox.WordWrap = false;
        _logBox.ReadOnly = true;
        _logBox.BackColor = Color.FromArgb(13, 16, 19);
        _logBox.ForeColor = Color.FromArgb(216, 224, 229);
        _logBox.BorderStyle = BorderStyle.FixedSingle;
        _logBox.Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        panel.Controls.Add(_logBox);

        return panel;
    }

    private void SetSettingsVisible(bool visible)
    {
        _settingsVisible = visible;
        _toggleSettingsButton.Text = visible ? "Hide Settings" : "Show Settings";

        RunWithoutRedraw(() =>
        {
            if (_outputSettingsPanel is not null)
            {
                _outputSettingsPanel.Visible = visible;
            }

            UpdateDetailsPanelVisibility();
        });
    }

    private void SetLogVisible(bool visible)
    {
        _logVisible = visible;
        _toggleLogButton.Text = visible ? "Hide Log" : "Show Log";

        RunWithoutRedraw(() =>
        {
            if (_logPanel is not null)
            {
                _logPanel.Visible = visible;
            }

            UpdateDetailsPanelVisibility();
        });
    }

    private void UpdateDetailsPanelVisibility()
    {
        var anyVisible = _settingsVisible || _logVisible;

        if (_detailsPanelLayout is not null && _detailsPanelLayout.ColumnStyles.Count >= 2)
        {
            _detailsPanelLayout.Visible = anyVisible;
            _detailsPanelLayout.SuspendLayout();
            try
            {
                _detailsPanelLayout.ColumnStyles[0].SizeType = SizeType.Absolute;
                _detailsPanelLayout.ColumnStyles[1].SizeType = SizeType.Absolute;

                if (_settingsVisible && _logVisible)
                {
                    _detailsPanelLayout.ColumnStyles[0].Width = TransportSpanWidth / 2;
                    _detailsPanelLayout.ColumnStyles[1].Width = TransportSpanWidth - TransportSpanWidth / 2;
                }
                else if (_settingsVisible)
                {
                    _detailsPanelLayout.ColumnStyles[0].Width = TransportSpanWidth;
                    _detailsPanelLayout.ColumnStyles[1].Width = 0;
                }
                else if (_logVisible)
                {
                    _detailsPanelLayout.ColumnStyles[0].Width = 0;
                    _detailsPanelLayout.ColumnStyles[1].Width = TransportSpanWidth;
                }
                else
                {
                    _detailsPanelLayout.ColumnStyles[0].Width = 0;
                    _detailsPanelLayout.ColumnStyles[1].Width = 0;
                }
            }
            finally
            {
                _detailsPanelLayout.ResumeLayout(performLayout: true);
            }
        }

        if (_settingsSplit is null || _settingsSplit.RowStyles.Count < 4)
        {
            return;
        }

        var detailsRow = _settingsSplit.RowStyles[3];
        detailsRow.SizeType = SizeType.Absolute;
        detailsRow.Height = anyVisible ? DetailsPanelHeight : 0;
        _settingsSplit.PerformLayout();
        SetFixedClientHeight(anyVisible ? ExpandedClientHeight : CollapsedClientHeight);
    }

    private void RunWithoutRedraw(Action action)
    {
        if (!IsHandleCreated)
        {
            action();
            return;
        }

        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        SuspendLayout();

        try
        {
            action();
        }
        finally
        {
            _detailsPanelLayout?.PerformLayout();
            _settingsSplit?.PerformLayout();
            ResumeLayout(performLayout: true);
            SendMessage(Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            Invalidate(invalidateChildren: true);
            Update();
        }
    }

    private static void EnableDoubleBuffering(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true, null);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private bool HasDeckLinkDevice => _deviceBox.SelectedItem is not null;

    private bool PreviewOnlyMode => _previewOnlyCheckBox.Checked || !HasDeckLinkDevice;

    private bool PcAudioMode => _pcAudioCheckBox.Checked;

    private string PlaybackTargetName => PreviewOnlyMode ? "preview" : "playout";

    private Panel BuildSection(string title)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 35, 40),
            Padding = new Padding(14, 40, 14, 14),
            Margin = new Padding(0, 0, 12, 0),
        };

        var label = new Label
        {
            Text = title,
            AutoSize = false,
            Size = new Size(220, 22),
            ForeColor = Color.FromArgb(236, 241, 244),
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
            Location = new Point(14, 12),
        };

        panel.Controls.Add(label);
        return panel;
    }

    private Control BuildInputRow(string labelText, Control input, Control? button)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = button is null ? 2 : 3,
            RowCount = 1,
            Height = 43,
            Padding = new Padding(0, 3, 0, 3),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (button is not null)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        }

        var label = BuildLabel(labelText);
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(StyleInput(input), 1, 0);
        if (button is not null)
        {
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(8, 0, 0, 0);
            row.Controls.Add(button, 2, 0);
        }

        return row;
    }

    private Control BuildLibraryRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 1,
            Height = 43,
            Padding = new Padding(0, 3, 0, 3),
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));

        row.Controls.Add(BuildLabel("Library"), 0, 0);
        row.Controls.Add(StyleInput(_mediaRootPathBox), 1, 0);

        _browseMediaRootButton.Dock = DockStyle.Fill;
        _browseMediaRootButton.Margin = new Padding(8, 0, 0, 0);
        row.Controls.Add(_browseMediaRootButton, 2, 0);

        _refreshMediaButton.Dock = DockStyle.Fill;
        _refreshMediaButton.Margin = new Padding(8, 0, 0, 0);
        row.Controls.Add(_refreshMediaButton, 3, 0);
        return row;
    }

    private static void AddGridField(TableLayoutPanel grid, string labelText, Control input, int column, int row)
    {
        grid.Controls.Add(BuildLabel(labelText), column, row);
        grid.Controls.Add(StyleInput(input), column + 1, row);
    }

    private static Label BuildLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(166, 179, 190),
        };
    }

    private static Control StyleInput(Control input)
    {
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 0, 0, 6);
        input.BackColor = Color.FromArgb(20, 24, 28);
        input.ForeColor = Color.FromArgb(232, 237, 240);

        if (input is TextBox textBox)
        {
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (input is ComboBox comboBox)
        {
            comboBox.FlatStyle = FlatStyle.Flat;
        }

        return input;
    }

    private Button BuildButton(string text)
    {
        var button = new Button { Text = text };
        StyleButton(button, Color.FromArgb(52, 67, 82));
        return button;
    }

    private static void StyleButton(Button button, Color backColor)
    {
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = backColor;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(0);
        button.TextAlign = ContentAlignment.MiddleCenter;
    }

    private static void StyleCheckBox(CheckBox checkBox)
    {
        checkBox.AutoSize = true;
        checkBox.ForeColor = Color.FromArgb(224, 232, 236);
        checkBox.Margin = new Padding(0, 4, 22, 0);
    }

    private void StyleMediaTree()
    {
        _mediaTree.Dock = DockStyle.Fill;
        _mediaTree.BackColor = Color.FromArgb(13, 16, 19);
        _mediaTree.ForeColor = Color.FromArgb(226, 234, 238);
        _mediaTree.BorderStyle = BorderStyle.FixedSingle;
        _mediaTree.HideSelection = false;
        _mediaTree.FullRowSelect = true;
        _mediaTree.ShowNodeToolTips = true;
        _mediaTree.BeforeExpand -= MediaTree_BeforeExpand;
        _mediaTree.AfterSelect -= MediaTree_AfterSelect;
        _mediaTree.BeforeExpand += MediaTree_BeforeExpand;
        _mediaTree.AfterSelect += MediaTree_AfterSelect;
    }

    private void StyleMediaGrid()
    {
        _mediaGrid.Dock = DockStyle.Fill;
        _mediaGrid.BackgroundColor = Color.FromArgb(13, 16, 19);
        _mediaGrid.GridColor = Color.FromArgb(54, 61, 68);
        _mediaGrid.BorderStyle = BorderStyle.FixedSingle;
        _mediaGrid.AllowUserToAddRows = false;
        _mediaGrid.AllowUserToDeleteRows = false;
        _mediaGrid.AllowUserToResizeRows = false;
        _mediaGrid.ReadOnly = true;
        _mediaGrid.MultiSelect = false;
        _mediaGrid.RowHeadersVisible = false;
        _mediaGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _mediaGrid.AutoGenerateColumns = false;
        _mediaGrid.EnableHeadersVisualStyles = false;
        _mediaGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _mediaGrid.ColumnHeadersHeight = 25;
        _mediaGrid.RowTemplate.Height = 24;
        _mediaGrid.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        _mediaGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 44, 50);
        _mediaGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(236, 241, 244);
        _mediaGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 44, 50);
        _mediaGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(236, 241, 244);
        _mediaGrid.DefaultCellStyle.BackColor = Color.FromArgb(17, 20, 24);
        _mediaGrid.DefaultCellStyle.ForeColor = Color.FromArgb(226, 234, 238);
        _mediaGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 116, 190);
        _mediaGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        _mediaGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(29, 34, 39);

        _mediaGrid.Columns.Clear();
        _mediaGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FileName",
            HeaderText = "File_Name",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 68,
        });
        _mediaGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Duration",
            HeaderText = "Duration",
            Width = 72,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _mediaGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "Size",
            Width = 72,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        _mediaGrid.SelectionChanged -= MediaGrid_SelectionChanged;
        _mediaGrid.CellDoubleClick -= MediaGrid_CellDoubleClick;
        _mediaGrid.KeyDown -= MediaGrid_KeyDown;
        _mediaGrid.SelectionChanged += MediaGrid_SelectionChanged;
        _mediaGrid.CellDoubleClick += MediaGrid_CellDoubleClick;
        _mediaGrid.KeyDown += MediaGrid_KeyDown;
    }

    private void LoadMediaTree()
    {
        _mediaSearchCancellation?.Cancel();
        _mediaGridMetadataCancellation?.Cancel();
        var mediaRootPath = _mediaRootPath;
        _mediaRootPathBox.Text = mediaRootPath;
        _mediaTree.BeginUpdate();
        try
        {
            _mediaTree.Nodes.Clear();
            ClearMediaGrid();
            if (!Directory.Exists(mediaRootPath))
            {
                _mediaTree.Nodes.Add(new TreeNode($"{mediaRootPath} not found"));
                SetStatus("Media folder missing", Color.FromArgb(232, 181, 105));
                return;
            }

            var root = CreateDirectoryNode(mediaRootPath, mediaRootPath);
            _mediaTree.Nodes.Add(root);
            LoadDirectoryChildren(root);
            root.Expand();
            _mediaTree.SelectedNode = root;
            ShowFolderFiles(mediaRootPath);
        }
        catch (Exception ex)
        {
            AppendLog($"Media browser error: {ex.Message}");
            SetStatus("Media browser error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            _mediaTree.EndUpdate();
        }
    }

    private void BrowseMediaRoot()
    {
        using var dialog = new MediaRootDialog(_mediaRootPath, DefaultMediaRootPath);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        ChangeMediaRoot(dialog.SelectedPath);
    }

    private void ChangeMediaRoot(string path)
    {
        _mediaSearchCancellation?.Cancel();
        _mediaSearchTimer.Stop();
        _mediaRootPath = path;
        _mediaRootPathBox.Text = path;
        _mediaSearchBox.Clear();
        LoadMediaTree();
        SaveAppSettings();
        AppendLog($"Media library changed to {path}");
    }

    private void ScheduleMediaSearch()
    {
        _mediaSearchTimer.Stop();
        _mediaSearchTimer.Start();
    }

    private async void MediaSearchTimer_Tick(object? sender, EventArgs e)
    {
        _mediaSearchTimer.Stop();
        await ApplyMediaSearchAsync(_mediaSearchBox.Text.Trim());
    }

    private async Task ApplyMediaSearchAsync(string searchText)
    {
        _mediaSearchCancellation?.Cancel();

        if (string.IsNullOrWhiteSpace(searchText))
        {
            ShowSelectedFolderFiles();
            return;
        }

        var mediaRootPath = _mediaRootPath;
        if (!Directory.Exists(mediaRootPath))
        {
            ShowMediaSearchResults(searchText, []);
            SetStatus("Media folder missing", Color.FromArgb(232, 181, 105));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _mediaSearchCancellation = cancellation;

        try
        {
            SetStatus($"Searching media: {searchText}", Color.FromArgb(126, 188, 226));
            var results = await Task.Run(
                () => FindMediaFiles(mediaRootPath, searchText, maxResults: 300, cancellation.Token),
                cancellation.Token);

            if (cancellation.IsCancellationRequested || !string.Equals(_mediaSearchBox.Text.Trim(), searchText, StringComparison.Ordinal))
            {
                return;
            }

            ShowMediaSearchResults(searchText, results);
        }
        catch (OperationCanceledException)
        {
            // Expected while the user keeps typing.
        }
        catch (Exception ex)
        {
            AppendLog($"Media search error: {ex.Message}");
            SetStatus("Media search error", Color.FromArgb(229, 113, 105));
        }
    }

    private void ShowMediaSearchResults(string searchText, IReadOnlyList<string> results)
    {
        ShowMediaFiles(results, $"No matches for {searchText}");
        SetStatus($"{results.Count} media match(es)", results.Count > 0
            ? Color.FromArgb(130, 210, 164)
            : Color.FromArgb(232, 181, 105));
    }

    private void MediaTree_BeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        var node = e.Node;
        if (node is not null && node.Tag is string path && Directory.Exists(path))
        {
            LoadDirectoryChildren(node);
        }
    }

    private void MediaTree_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is string path && Directory.Exists(path))
        {
            ShowFolderFiles(path);
        }
    }

    private void ShowSelectedFolderFiles()
    {
        var folderPath = _mediaTree.SelectedNode?.Tag as string ?? _selectedMediaFolderPath ?? _mediaRootPath;
        if (Directory.Exists(folderPath))
        {
            ShowFolderFiles(folderPath);
            return;
        }

        ClearMediaGrid();
    }

    private void ShowFolderFiles(string folderPath)
    {
        _selectedMediaFolderPath = folderPath;
        if (!Directory.Exists(folderPath))
        {
            ClearMediaGrid();
            SetStatus("Media folder missing", Color.FromArgb(232, 181, 105));
            return;
        }

        try
        {
            var files = Directory
                .EnumerateFiles(folderPath)
                .Where(IsSupportedMediaFile)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ShowMediaFiles(files, "No media files in this folder");
            SetStatus($"{files.Count} file(s) in {Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}", files.Count > 0
                ? Color.FromArgb(130, 210, 164)
                : Color.FromArgb(232, 181, 105));
        }
        catch (UnauthorizedAccessException)
        {
            ClearMediaGrid();
            SetStatus("Folder access denied", Color.FromArgb(232, 181, 105));
        }
        catch (IOException ex)
        {
            ClearMediaGrid();
            AppendLog($"Folder load error: {ex.Message}");
            SetStatus("Folder load error", Color.FromArgb(229, 113, 105));
        }
    }

    private void ShowMediaFiles(IReadOnlyList<string> files, string emptyMessage)
    {
        _mediaGridMetadataCancellation?.Cancel();
        _mediaGrid.Rows.Clear();

        if (files.Count == 0)
        {
            var emptyRow = _mediaGrid.Rows[_mediaGrid.Rows.Add(emptyMessage, string.Empty, string.Empty)];
            emptyRow.DefaultCellStyle.ForeColor = Color.FromArgb(166, 179, 190);
            return;
        }

        _mediaGrid.SuspendLayout();
        try
        {
            foreach (var file in files)
            {
                var durationText = IsImageFile(file) ? "Still" : "--";
                var rowIndex = _mediaGrid.Rows.Add(GetMediaDisplayPath(file), durationText, GetFileSizeText(file));
                var row = _mediaGrid.Rows[rowIndex];
                row.Tag = file;
                row.Cells[0].ToolTipText = file;
            }
        }
        finally
        {
            _mediaGrid.ResumeLayout();
        }

        if (_mediaGrid.Rows.Count > 0)
        {
            _mediaGrid.CurrentCell = _mediaGrid.Rows[0].Cells[0];
            _mediaGrid.Rows[0].Selected = true;
            if (_mediaGrid.Rows[0].Tag is string path)
            {
                _ = SelectMediaPath(path);
            }
        }

        StartMediaGridMetadataProbe(files);
    }

    private void ClearMediaGrid()
    {
        _mediaGridMetadataCancellation?.Cancel();
        _mediaGrid.Rows.Clear();
    }

    private void MediaGrid_SelectionChanged(object? sender, EventArgs e)
    {
        var path = GetSelectedMediaGridPath();
        if (path is not null)
        {
            _ = SelectMediaPath(path);
        }
    }

    private async void MediaGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0)
        {
            await PlaySelectedMediaGridAsync();
        }
    }

    private async void MediaGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            await PlaySelectedMediaGridAsync();
        }
    }

    private string? GetSelectedMediaGridPath()
    {
        if (_mediaGrid.CurrentRow?.Tag is string currentPath && File.Exists(currentPath))
        {
            return currentPath;
        }

        if (_mediaGrid.SelectedRows.Count > 0 &&
            _mediaGrid.SelectedRows[0].Tag is string selectedPath &&
            File.Exists(selectedPath))
        {
            return selectedPath;
        }

        return null;
    }

    private bool SelectMediaPath(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (!string.Equals(_inputPathBox.Text.Trim(), path, StringComparison.OrdinalIgnoreCase))
        {
            _selectedStartOffset = TimeSpan.Zero;
            _nativeSeekDisabledPath = null;
            _scrubPreviewHelperDisabledPath = null;
        }

        _inputPathBox.Text = path;
        SetStatus(_isPlaying ? $"Next: {Path.GetFileName(path)}" : $"Selected {Path.GetFileName(path)}", Color.FromArgb(130, 210, 164));
        return true;
    }

    private async void StartMediaGridMetadataProbe(IReadOnlyList<string> files)
    {
        _mediaGridMetadataCancellation?.Cancel();
        if (files.Count == 0)
        {
            return;
        }

        var ffprobePath = GetFfprobePath();
        if (!File.Exists(ffprobePath))
        {
            AppendLog($"Media grid metadata skipped: ffprobe.exe not found next to {Path.GetFileName(GetFfmpegPath())}.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _mediaGridMetadataCancellation = cancellation;

        try
        {
            foreach (var file in files)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!File.Exists(file))
                {
                    continue;
                }

                var metadata = await ProbeMediaGridItemAsync(ffprobePath, file, cancellation.Token);
                UpdateMediaGridMetadata(file, metadata.Duration, metadata.Size, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the operator picks another folder or search text.
        }
        catch (Exception ex)
        {
            AppendLog($"Media grid metadata stopped: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_mediaGridMetadataCancellation, cancellation))
            {
                _mediaGridMetadataCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<(string Duration, string Size)> ProbeMediaGridItemAsync(
        string ffprobePath,
        string path,
        CancellationToken cancellationToken)
    {
        var durationText = IsImageFile(path) ? "Still" : "--";
        var sizeText = GetFileSizeText(path);

        var result = await _deckLink.RunProcessAsync(
            ffprobePath,
            [
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=width,height:format=duration",
                "-of",
                "default=noprint_wrappers=1",
                path,
            ],
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
        {
            return (durationText, sizeText);
        }

        int? width = null;
        int? height = null;
        double? seconds = null;
        foreach (var rawLine in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            if (string.Equals(key, "width", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth) &&
                parsedWidth > 0)
            {
                width = parsedWidth;
            }
            else if (string.Equals(key, "height", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHeight) &&
                parsedHeight > 0)
            {
                height = parsedHeight;
            }
            else if (string.Equals(key, "duration", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds) &&
                parsedSeconds > 0 &&
                !double.IsNaN(parsedSeconds) &&
                !double.IsInfinity(parsedSeconds))
            {
                seconds = parsedSeconds;
            }
        }

        if (!IsImageFile(path) && seconds.HasValue)
        {
            durationText = FormatGridDuration(TimeSpan.FromSeconds(seconds.Value));
        }

        if (width.HasValue && height.HasValue)
        {
            sizeText = $"{width.Value}x{height.Value}";
        }

        return (durationText, sizeText);
    }

    private void UpdateMediaGridMetadata(string path, string duration, string size, CancellationToken cancellationToken)
    {
        if (IsDisposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => UpdateMediaGridMetadata(path, duration, size, cancellationToken));
            }
            catch
            {
                // The form may be closing while metadata probing is being cancelled.
            }

            return;
        }

        foreach (DataGridViewRow row in _mediaGrid.Rows)
        {
            if (row.Tag is string rowPath &&
                string.Equals(rowPath, path, StringComparison.OrdinalIgnoreCase))
            {
                row.Cells["Duration"].Value = duration;
                row.Cells["Size"].Value = size;
                return;
            }
        }
    }

    private static string FormatGridDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string GetFileSizeText(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length >= 1024L * 1024 * 1024)
            {
                return $"{length / (1024d * 1024 * 1024):0.##} GB";
            }

            if (length >= 1024L * 1024)
            {
                return $"{length / (1024d * 1024):0.##} MB";
            }

            if (length >= 1024)
            {
                return $"{length / 1024d:0.##} KB";
            }

            return $"{length} B";
        }
        catch
        {
            return "--";
        }
    }

    private void ScheduleDurationProbe()
    {
        _durationProbeTimer.Stop();
        _durationProbeTimer.Start();
        UpdateDurationLabel();
    }

    private async void DurationProbeTimer_Tick(object? sender, EventArgs e)
    {
        _durationProbeTimer.Stop();
        await RefreshSelectedDurationAsync(_inputPathBox.Text.Trim());
    }

    private async Task RefreshSelectedDurationAsync(string path)
    {
        _durationProbeCancellation?.Cancel();

        if (!string.Equals(_selectedDurationPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _selectedDurationPath = path;
            _selectedMediaDuration = null;
            _selectedDurationUnavailable = false;
            _selectedStartOffset = TimeSpan.Zero;
            _nativeSeekDisabledPath = null;
            _scrubPreviewHelperDisabledPath = null;
        }

        UpdateDurationLabel();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || IsImageFile(path))
        {
            _selectedDurationUnavailable = false;
            UpdateDurationLabel();
            return;
        }

        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _durationProbeCancellation = cancellation;

        try
        {
            var duration = await ProbeMediaDurationAsync(path, cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !string.Equals(_inputPathBox.Text.Trim(), path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedDurationPath = path;
            _selectedMediaDuration = duration;
            _selectedDurationUnavailable = duration is null;

            if (string.Equals(_playbackPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _playbackDuration = duration;
                _playbackDurationUnavailable = duration is null;
            }

            UpdateDurationLabel();
        }
        catch (OperationCanceledException)
        {
            // Expected when the user selects another file quickly.
        }
        catch (Exception ex)
        {
            if (string.Equals(_inputPathBox.Text.Trim(), path, StringComparison.OrdinalIgnoreCase))
            {
                _selectedDurationPath = path;
                _selectedMediaDuration = null;
                _selectedDurationUnavailable = true;
                AppendLog($"Duration unavailable: {ex.Message}");
                UpdateDurationLabel();
            }
        }
        finally
        {
            if (ReferenceEquals(_durationProbeCancellation, cancellation))
            {
                _durationProbeCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<TimeSpan?> ProbeMediaDurationAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobePath = GetFfprobePath();
        if (!File.Exists(ffprobePath))
        {
            throw new InvalidOperationException($"ffprobe.exe not found next to {Path.GetFileName(GetFfmpegPath())}.");
        }

        var result = await _deckLink.RunProcessAsync(
            ffprobePath,
            [
                "-v",
                "error",
                "-show_entries",
                "format=duration",
                "-of",
                "default=noprint_wrappers=1:nokey=1",
                path,
            ],
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var text = result.StandardOutput.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0 &&
            !double.IsNaN(seconds) &&
            !double.IsInfinity(seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidOperationException(result.StandardError.Trim());
        }

        return null;
    }

    private void StartPlaybackClock(PlayRequest request, bool startPaused)
    {
        _playbackStartedAt = DateTime.UtcNow;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackStartOffset = TimeSpan.Zero;
        _playbackPath = request.UseTestPattern ? null : request.InputPath;
        _playbackIsStillImage = !request.UseTestPattern && IsImageFile(request.InputPath);
        _playbackIsTestPattern = request.UseTestPattern;
        _playbackDuration = null;
        _playbackDurationUnavailable = false;

        if (_playbackPath is not null &&
            string.Equals(_selectedDurationPath, _playbackPath, StringComparison.OrdinalIgnoreCase))
        {
            _playbackDuration = _selectedMediaDuration;
            _playbackDurationUnavailable = _selectedDurationUnavailable;
        }

        _playbackStartOffset = ClampSeekOffset(request.StartOffset, _playbackDuration);
        if (_playbackPath is not null)
        {
            _selectedStartOffset = _playbackStartOffset;
        }

        _isPaused = startPaused;
        _playbackPausedAt = startPaused ? DateTime.UtcNow : null;
        _pauseResumeButton.Text = startPaused ? "Resume" : "Pause";
        if (startPaused)
        {
            _playbackPositionTimer.Stop();
        }
        else
        {
            _playbackPositionTimer.Start();
        }

        UpdateDurationLabel();
    }

    private void StopPlaybackClock()
    {
        _playbackPositionTimer.Stop();
        _playbackPauseController?.Resume();
        _playbackPauseController = null;
        _isPaused = false;
        _pauseResumeButton.Text = "Pause";
        _playbackStartedAt = null;
        _playbackPausedAt = null;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackStartOffset = TimeSpan.Zero;
        _playbackPath = null;
        _playbackDuration = null;
        _playbackDurationUnavailable = false;
        _playbackIsStillImage = false;
        _playbackIsTestPattern = false;
        ResetAudioMeters();
    }

    private void UpdateDurationLabel()
    {
        if (_isPlaying && _playbackStartedAt.HasValue)
        {
            UpdatePlaybackDurationLabel();
            return;
        }

        var path = _inputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetRemainingDisplay("--");
            SetCurrentTimeDisplay("--");
            ResetPositionBar();
            return;
        }

        if (IsImageFile(path))
        {
            SetRemainingDisplay("LIVE");
            SetCurrentTimeDisplay("LIVE");
            ResetPositionBar("live");
            return;
        }

        if (string.Equals(_selectedDurationPath, path, StringComparison.OrdinalIgnoreCase) &&
            _selectedMediaDuration.HasValue)
        {
            var duration = _selectedMediaDuration.Value;
            _selectedStartOffset = ClampSeekOffset(_selectedStartOffset, duration);
            var remaining = duration - _selectedStartOffset;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            SetRemainingDisplay(remaining);
            SetCurrentTimeDisplay(_selectedStartOffset);
            UpdatePositionBar(duration, _selectedStartOffset);
            return;
        }

        SetRemainingDisplay(_selectedDurationUnavailable &&
            string.Equals(_selectedDurationPath, path, StringComparison.OrdinalIgnoreCase)
                ? "UNAVAILABLE"
                : "READING");
        SetCurrentTimeDisplay("--");
        ResetPositionBar();
    }

    private void UpdatePlaybackDurationLabel()
    {
        var clockNow = _playbackPausedAt ?? DateTime.UtcNow;
        var elapsedSinceDecoderStart = clockNow - _playbackStartedAt!.Value - _playbackPausedDuration;
        if (elapsedSinceDecoderStart < TimeSpan.Zero)
        {
            elapsedSinceDecoderStart = TimeSpan.Zero;
        }

        if (_playbackIsTestPattern)
        {
            SetRemainingDisplay("LIVE");
            SetCurrentTimeDisplay("LIVE");
            ResetPositionBar("live");
            return;
        }

        if (_playbackIsStillImage)
        {
            SetRemainingDisplay("LIVE");
            SetCurrentTimeDisplay("LIVE");
            ResetPositionBar("live");
            return;
        }

        if (!_playbackDuration.HasValue)
        {
            SetRemainingDisplay(_playbackDurationUnavailable ? "UNAVAILABLE" : "READING");
            SetCurrentTimeDisplay("--");
            ResetPositionBar();
            return;
        }

        var duration = _playbackDuration.Value;
        var position = _playbackStartOffset + elapsedSinceDecoderStart;
        position = position > duration
            ? duration
            : position;
        if (position > duration)
        {
            position = duration;
        }

        var remaining = duration - position;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        SetRemainingDisplay(remaining);
        SetCurrentTimeDisplay(position);
        UpdatePositionBar(duration, position);
    }

    private void SetRemainingDisplay(TimeSpan remaining)
    {
        SetRemainingDisplay(FormatClock(remaining, roundUp: true));
    }

    private void SetRemainingDisplay(string text)
    {
        _durationLabel.Text = text;
    }

    private void SetCurrentTimeDisplay(TimeSpan position)
    {
        SetCurrentTimeDisplay(FormatClock(position));
    }

    private void SetCurrentTimeDisplay(string text)
    {
        _currentTimeLabel.Text = text;
    }

    private async void PositionBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (!_positionBar.Enabled)
        {
            return;
        }

        _isDraggingSeek = true;
        SetPositionBarValueFromMouse(e.X);
        PreviewSeekBarPosition();
        if (CanUseScrubPreview())
        {
            _scrubPreviewStartTask = BeginScrubPreviewAsync(GetPositionBarTime());
            await RunSeekSafelyAsync(() => _scrubPreviewStartTask ?? Task.CompletedTask);
            _scrubPreviewStartTask = null;
        }
    }

    private async void PositionBar_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_positionBar.Enabled)
        {
            _isDraggingSeek = false;
            return;
        }

        SetPositionBarValueFromMouse(e.X);
        _isDraggingSeek = false;
        _scrubSeekTimer.Stop();
        if (_scrubPreviewStartTask is not null)
        {
            await RunSeekSafelyAsync(() => _scrubPreviewStartTask ?? Task.CompletedTask);
            _scrubPreviewStartTask = null;
        }

        if (_scrubPreviewMode)
        {
            await RunSeekSafelyAsync(() => FinishScrubPreviewAsync(GetPositionBarTime()));
            return;
        }

        await RunSeekSafelyAsync(SeekToPositionBarValueAsync);
    }

    private async void PositionBar_KeyUp(object? sender, KeyEventArgs e)
    {
        if (!_positionBar.Enabled)
        {
            return;
        }

        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown)
        {
            await RunSeekSafelyAsync(SeekToPositionBarValueAsync);
        }
    }

    private void ScheduleScrubSeek()
    {
        if (!_scrubPreviewMode)
        {
            return;
        }

        _scrubSeekTimer.Stop();
        _scrubSeekTimer.Start();
    }

    private async void ScrubSeekTimer_Tick(object? sender, EventArgs e)
    {
        _scrubSeekTimer.Stop();
        if (_scrubPreviewMode)
        {
            await RunSeekSafelyAsync(() => QueueScrubPreviewFrameAsync(GetPositionBarTime()));
        }
    }

    private bool CanUseScrubPreview()
    {
        var path = _inputPathBox.Text.Trim();
        return _isPlaying &&
            !_nativeSeekPreviewMode &&
            !_scrubPreviewMode &&
            !_playbackIsStillImage &&
            !_playbackIsTestPattern &&
            File.Exists(path) &&
            !IsImageFile(path);
    }

    private async Task BeginScrubPreviewAsync(TimeSpan target)
    {
        var duration = GetCurrentSeekDuration();
        if (!duration.HasValue)
        {
            return;
        }

        target = ClampSeekOffset(target, duration.Value);
        _selectedStartOffset = target;
        UpdatePositionBar(duration.Value, target);

        if (!_scrubPreviewMode)
        {
            _scrubPreviewReturnPaused = _isPaused;
            AppendLog("Starting NLE-style scrub preview...");
            SetStatus("Starting scrub preview", Color.FromArgb(126, 188, 226));

            var stoppedTask = _playbackStoppedSignal?.Task;
            if (!PreviewOnlyMode)
            {
                PreserveVideoOutputForReplacement();
            }

            StopPlayback();
            if (stoppedTask is not null)
            {
                await stoppedTask;
            }

            _scrubPreviewMode = true;
            _isPaused = true;
            SetPlaying(true);
            ResetAudioMeters();
            _pauseResumeButton.Text = "Resume";
            SetStatus("Scrub preview", Color.FromArgb(232, 181, 105));
        }

        await QueueScrubPreviewFrameAsync(target);
    }

    private async Task FinishScrubPreviewAsync(TimeSpan target)
    {
        var duration = GetCurrentSeekDuration();
        if (duration.HasValue)
        {
            target = ClampSeekOffset(target, duration.Value);
        }

        _selectedStartOffset = target;
        _pendingScrubPreviewOffset = null;
        _scrubPreviewDecodeCancellation?.Cancel();

        var shouldRemainPaused = _scrubPreviewReturnPaused;
        AppendLog(shouldRemainPaused
            ? $"Leaving scrub preview paused at {FormatClock(target)}..."
            : $"Leaving scrub preview and playing from {FormatClock(target)}...");

        ExitScrubPreviewMode(holdForReplacement: !PreviewOnlyMode, setStopped: true);
        await StartPlaybackAsync(dryRun: false, startOffset: target, startPaused: shouldRemainPaused);
    }

    private async Task QueueScrubPreviewFrameAsync(TimeSpan target)
    {
        if (!_scrubPreviewMode)
        {
            return;
        }

        var duration = GetCurrentSeekDuration();
        if (duration.HasValue)
        {
            target = ClampSeekOffset(target, duration.Value);
            _selectedStartOffset = target;
            UpdatePositionBar(duration.Value, target);
        }

        _pendingScrubPreviewOffset = target;
        if (_scrubPreviewLoopRunning)
        {
            _scrubPreviewDecodeCancellation?.Cancel();
            return;
        }

        _scrubPreviewLoopRunning = true;
        try
        {
            while (_scrubPreviewMode && _pendingScrubPreviewOffset.HasValue)
            {
                var nextTarget = _pendingScrubPreviewOffset.Value;
                _pendingScrubPreviewOffset = null;
                await DisplayScrubPreviewFrameAsync(nextTarget);
            }
        }
        finally
        {
            _scrubPreviewLoopRunning = false;
        }
    }

    private async Task DisplayScrubPreviewFrameAsync(TimeSpan target)
    {
        var request = BuildRequest(false, target);
        var selectedMode = _modeBox.SelectedItem as DeckLinkMode;
        request = _deckLink.ApplyModeDefaults(request, selectedMode);
        var size = ParseVideoSize(request.VideoSize)
            ?? throw new InvalidOperationException("Choose a valid DeckLink output size before scrubbing.");

        if (!PreviewOnlyMode)
        {
            EnsureScrubPreviewOutput(request);
        }

        UpdateScrubPreviewClock(request, target);

        var decodeTimedOut = false;
        using var decodeCancellation = new CancellationTokenSource();
        using var timeoutTimer = new System.Threading.Timer(
            _ =>
            {
                decodeTimedOut = true;
                decodeCancellation.Cancel();
            },
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);
        var previousCancellation = _scrubPreviewDecodeCancellation;
        _scrubPreviewDecodeCancellation = decodeCancellation;
        previousCancellation?.Cancel();

        try
        {
            var frame = await DecodeScrubPreviewFrameAsync(request, size.Width, size.Height, target, decodeCancellation.Token);
            if (!decodeCancellation.IsCancellationRequested && _scrubPreviewMode)
            {
                DisplayDecodedScrubPreviewFrame(frame, size.Width, size.Height, target);
            }
        }
        catch (OperationCanceledException)
        {
            if (decodeTimedOut && _scrubPreviewMode)
            {
                _scrubPreviewHelperDisabledPath = request.InputPath;
                DisposeScrubPreviewHelper();
                AppendLog("Persistent preview helper timed out; using one-shot ffmpeg preview for this file.");
                try
                {
                    using var fallbackCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var fallbackFrame = await DecodeScrubPreviewFrameWithFfmpegAsync(
                        request,
                        size.Width,
                        size.Height,
                        fallbackCancellation.Token);
                    DisplayDecodedScrubPreviewFrame(fallbackFrame, size.Width, size.Height, target);
                }
                catch (Exception ex)
                {
                    AppendLog($"Fallback scrub preview frame unavailable: {ex.Message}");
                }
            }

            // Expected when the user keeps dragging and a newer frame is requested.
        }
        catch (Exception ex)
        {
            AppendLog($"Scrub preview frame unavailable: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scrubPreviewDecodeCancellation, decodeCancellation))
            {
                _scrubPreviewDecodeCancellation = null;
            }
        }
    }

    private void DisplayDecodedScrubPreviewFrame(byte[] frame, int width, int height, TimeSpan target)
    {
        if (!_scrubPreviewMode)
        {
            return;
        }

        if (!PreviewOnlyMode)
        {
            _scrubPreviewOutput!.DisplayFrame(frame);
        }

        UpdateAppPreviewFrame(frame, width, height);
        SetStatus($"Scrub {FormatClock(target)}", Color.FromArgb(232, 181, 105));
        UpdateDurationLabel();
    }

    private void UpdateAppPreviewFrame(byte[] uyvyFrame, int width, int height)
    {
        if (IsDisposed || width <= 0 || height <= 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _appPreviewFramePending, 1) == 1)
        {
            return;
        }

        try
        {
            var bitmap = CreatePreviewBitmap(uyvyFrame, width, height, AppPreviewWidth, AppPreviewHeight);
            SetAppPreviewImage(bitmap);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _appPreviewFramePending, 0);
            AppendLog($"App preview frame skipped: {ex.Message}");
        }
    }

    private void SetAppPreviewImage(Bitmap bitmap)
    {
        if (IsDisposed)
        {
            bitmap.Dispose();
            Interlocked.Exchange(ref _appPreviewFramePending, 0);
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetAppPreviewImage(bitmap));
            }
            catch
            {
                bitmap.Dispose();
                Interlocked.Exchange(ref _appPreviewFramePending, 0);
            }

            return;
        }

        var previous = _appPreviewBox.Image;
        _appPreviewBox.Image = bitmap;
        previous?.Dispose();
        Interlocked.Exchange(ref _appPreviewFramePending, 0);
    }

    private void DisposeAppPreviewImage()
    {
        var image = _appPreviewBox.Image;
        _appPreviewBox.Image = null;
        image?.Dispose();
        Interlocked.Exchange(ref _appPreviewFramePending, 0);
    }

    private void UpdateAudioMeters(double leftDbfs, double rightDbfs)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => UpdateAudioMeters(leftDbfs, rightDbfs));
            }
            catch
            {
                // The form may be closing while the audio decoder is shutting down.
            }

            return;
        }

        SetAudioMeter(_leftAudioMeter, leftDbfs);
        SetAudioMeter(_rightAudioMeter, rightDbfs);
    }

    private void ResetAudioMeters()
    {
        UpdateAudioMeters(-90, -90);
    }

    private static void SetAudioMeter(AudioMeterBar meter, double dbfs)
    {
        meter.Dbfs = dbfs;
    }

    private static Bitmap CreatePreviewBitmap(byte[] uyvyFrame, int sourceWidth, int sourceHeight, int previewWidth, int previewHeight)
    {
        if (uyvyFrame.Length < checked(sourceWidth * sourceHeight * 2))
        {
            throw new InvalidOperationException("UYVY preview frame is smaller than expected.");
        }

        var bitmap = new Bitmap(previewWidth, previewHeight, PixelFormat.Format24bppRgb);
        var bounds = new Rectangle(0, 0, previewWidth, previewHeight);
        var data = bitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var rgb = new byte[data.Stride * previewHeight];
            for (var y = 0; y < previewHeight; y++)
            {
                var sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / previewHeight);
                var rowOffset = y * data.Stride;
                for (var x = 0; x < previewWidth; x++)
                {
                    var sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / previewWidth);
                    var pairX = sourceX & ~1;
                    var sourceOffset = (sourceY * sourceWidth + pairX) * 2;
                    var u = uyvyFrame[sourceOffset];
                    var v = uyvyFrame[sourceOffset + 2];
                    var yValue = uyvyFrame[sourceOffset + (sourceX == pairX ? 1 : 3)];
                    ConvertYuvToRgb(yValue, u, v, out var r, out var g, out var b);

                    var destination = rowOffset + x * 3;
                    rgb[destination] = b;
                    rgb[destination + 1] = g;
                    rgb[destination + 2] = r;
                }
            }

            Marshal.Copy(rgb, 0, data.Scan0, rgb.Length);
        }
        catch
        {
            bitmap.UnlockBits(data);
            bitmap.Dispose();
            throw;
        }

        bitmap.UnlockBits(data);
        return bitmap;
    }

    private static void ConvertYuvToRgb(byte yValue, byte uValue, byte vValue, out byte r, out byte g, out byte b)
    {
        var c = yValue - 16;
        var d = uValue - 128;
        var e = vValue - 128;
        r = ClampToByte((298 * c + 409 * e + 128) >> 8);
        g = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
        b = ClampToByte((298 * c + 516 * d + 128) >> 8);
    }

    private static byte ClampToByte(int value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }

    private void EnsureScrubPreviewOutput(PlayRequest request)
    {
        var modeCode = request.FormatCode ?? string.Empty;
        if (_scrubPreviewOutput is not null &&
            string.Equals(_scrubPreviewPath, request.InputPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_scrubPreviewModeCode, modeCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _scrubPreviewOutput?.Dispose();
        _scrubPreviewOutput = new NativeDeckLinkPreviewOutput(request);
        _scrubPreviewPath = request.InputPath;
        _scrubPreviewModeCode = modeCode;
    }

    private void UpdateScrubPreviewClock(PlayRequest request, TimeSpan target)
    {
        _selectedStartOffset = target;
        _playbackStartOffset = target;
        _playbackDuration = _selectedMediaDuration;
        _playbackStartedAt = DateTime.UtcNow;
        _playbackPausedAt = _playbackStartedAt;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackPath = request.InputPath;
        _playbackIsStillImage = false;
        _playbackIsTestPattern = false;
        _isPaused = true;
    }

    private async Task<byte[]> DecodeScrubPreviewFrameAsync(
        PlayRequest request,
        int width,
        int height,
        TimeSpan target,
        CancellationToken cancellationToken)
    {
        if (NativeFfmpegFrameDecoder.IsAvailable() &&
            !string.Equals(_scrubPreviewHelperDisabledPath, request.InputPath, StringComparison.OrdinalIgnoreCase))
        {
            EnsureScrubPreviewHelper(request, width, height);
            try
            {
                return await _scrubPreviewHelper!.DecodeFrameAsync(target, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _scrubPreviewHelperDisabledPath = request.InputPath;
                DisposeScrubPreviewHelper();
                AppendLog($"Persistent preview helper disabled for this file: {ex.Message}");
            }
        }

        return await DecodeScrubPreviewFrameWithFfmpegAsync(request, width, height, cancellationToken);
    }

    private void EnsureScrubPreviewHelper(PlayRequest request, int width, int height)
    {
        if (_scrubPreviewHelper is not null &&
            _scrubPreviewHelper.Matches(request.InputPath, width, height))
        {
            return;
        }

        DisposeScrubPreviewHelper();
        _scrubPreviewHelper = new PreviewFrameHelperClient(request.InputPath, width, height, AppendLog);
    }

    private void DisposeScrubPreviewHelper()
    {
        _scrubPreviewHelper?.Dispose();
        _scrubPreviewHelper = null;
    }

    private async Task<byte[]> DecodeScrubPreviewFrameWithFfmpegAsync(
        PlayRequest request,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var frameBytes = checked(width * height * 2);
        var buffer = new byte[frameBytes];
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in BuildScrubPreviewArguments(request, width, height))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        using var cancellationRegistration = cancellationToken.Register(() => TryKillProcess(process));
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await process.StandardOutput.BaseStream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"ffmpeg preview exited with code {process.ExitCode}."
                : stderr.Trim());
        }

        if (offset < frameBytes)
        {
            throw new InvalidOperationException($"ffmpeg preview returned {offset} of {frameBytes} bytes.");
        }

        return buffer;
    }

    private static IReadOnlyList<string> BuildScrubPreviewArguments(PlayRequest request, int width, int height)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "error",
        };

        if (request.StartOffset > TimeSpan.Zero)
        {
            args.Add("-ss");
            args.Add(FfmpegDeckLink.FormatFfmpegTimestamp(request.StartOffset));
            args.Add("-noaccurate_seek");
        }

        args.Add("-i");
        args.Add(request.InputPath);
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-an");
        args.Add("-vf");
        args.Add("format=uyvy422");
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-s");
        args.Add($"{width}x{height}");
        args.Add("-pix_fmt");
        args.Add("uyvy422");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("pipe:1");
        return args;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort when cancelling a stale preview frame.
        }
    }

    private void SetPositionBarValueFromMouse(int x)
    {
        if (_positionBar.Width <= 0)
        {
            return;
        }

        var ratio = Math.Clamp((double)x / _positionBar.Width, 0, 1);
        var value = (int)Math.Round(_positionBar.Minimum + ratio * (_positionBar.Maximum - _positionBar.Minimum));
        _positionBar.Value = Math.Clamp(value, _positionBar.Minimum, _positionBar.Maximum);
    }

    private void PreviewSeekBarPosition()
    {
        var duration = GetCurrentSeekDuration();
        if (!duration.HasValue)
        {
            return;
        }

        var target = ClampSeekOffset(GetPositionBarTime(), duration.Value);
        if (!_isPlaying)
        {
            _selectedStartOffset = target;
        }

        var remaining = duration.Value - target;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        SetRemainingDisplay(remaining);
        SetCurrentTimeDisplay(target);
    }

    private async Task SeekToPositionBarValueAsync()
    {
        await SeekToOffsetAsync(GetPositionBarTime());
    }

    private async Task SeekRelativeAsync(TimeSpan delta)
    {
        await SeekToOffsetAsync(GetCurrentSeekPosition() + delta);
    }

    private async Task SeekRelativeFramesAsync(int frames)
    {
        await SeekRelativeAsync(TimeSpan.FromTicks(GetFrameDuration().Ticks * frames));
    }

    private async Task SeekToOffsetAsync(TimeSpan target)
    {
        var duration = GetCurrentSeekDuration();
        if (!duration.HasValue)
        {
            return;
        }

        target = ClampSeekOffset(target, duration.Value);
        _selectedStartOffset = target;
        UpdatePositionBar(duration.Value, target);

        if (!_isPlaying && !_seekQueueRunning)
        {
            UpdateDurationLabel();
            SetStatus($"Start set to {FormatClock(target)}", Color.FromArgb(130, 210, 164));
            return;
        }

        if (_playbackIsStillImage || _playbackIsTestPattern)
        {
            UpdateDurationLabel();
            return;
        }

        var path = _inputPathBox.Text.Trim();
        if (EnableInProcessNativeSeekPreview &&
            _isPaused &&
            NativeFfmpegFrameDecoder.IsAvailable() &&
            !string.Equals(_nativeSeekDisabledPath, path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await PreviewNativeSeekFrameAsync(target);
                return;
            }
            catch (Exception ex)
            {
                _nativeSeekDisabledPath = path;
                AppendLog($"Native seek preview disabled for this file: {ex.Message}");
                if (_nativeSeekPreviewMode)
                {
                    ExitNativeSeekPreviewMode(setStopped: true);
                }

                if (!_isPlaying)
                {
                    _ = StartPlaybackAsync(dryRun: false, startOffset: target, startPaused: true);
                    return;
                }
            }
        }

        _pendingSeekOffset = target;
        _pendingSeekShouldRemainPaused = _isPaused || (_seekQueueRunning && _activeSeekShouldRemainPaused);

        if (!_seekQueueRunning)
        {
            await ProcessPendingSeekAsync();
        }
    }

    private async Task RunSeekSafelyAsync(Func<Task> seekAction)
    {
        try
        {
            await seekAction();
        }
        catch (Exception ex)
        {
            _scrubSeekTimer.Stop();
            _isDraggingSeek = false;
            AppendLog($"Seek error: {ex.Message}");
            SetStatus("Seek error", Color.FromArgb(229, 113, 105));
            UpdateDurationLabel();
        }
    }

    private async Task PreviewNativeSeekFrameAsync(TimeSpan target)
    {
        var request = BuildRequest(false, target);
        var selectedMode = _modeBox.SelectedItem as DeckLinkMode;
        request = _deckLink.ApplyModeDefaults(request, selectedMode);

        var size = ParseVideoSize(request.VideoSize)
            ?? throw new InvalidOperationException("Choose a valid DeckLink output size before native seek.");

        var stoppedTask = _playbackStoppedSignal?.Task;
        if (!_nativeSeekPreviewMode && _isPlaying)
        {
            AppendLog("Entering native seek preview...");
            StopPlayback();
            if (stoppedTask is not null)
            {
                await stoppedTask;
            }

            _nativeSeekPreviewMode = true;
            SetPlaying(true);
            _isPaused = true;
            _pauseResumeButton.Text = "Resume";
            SetStatus("Paused seek preview", Color.FromArgb(232, 181, 105));
        }

        EnsureNativeSeekPreview(request, size.Width, size.Height);

        _selectedStartOffset = target;
        _playbackStartOffset = target;
        _playbackDuration = _selectedMediaDuration;
        _playbackStartedAt = DateTime.UtcNow;
        _playbackPausedAt = _playbackStartedAt;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackPath = request.InputPath;
        _playbackIsStillImage = false;
        _playbackIsTestPattern = false;
        _isPaused = true;

        var frame = await Task.Run(() => _nativeSeekDecoder!.DecodeFrame(target));
        _nativeSeekOutput!.DisplayFrame(frame);
        SetStatus($"Preview {FormatClock(target)}", Color.FromArgb(232, 181, 105));
        UpdateDurationLabel();
    }

    private void EnsureNativeSeekPreview(PlayRequest request, int width, int height)
    {
        var modeCode = request.FormatCode ?? string.Empty;
        if (_nativeSeekDecoder is not null &&
            _nativeSeekOutput is not null &&
            string.Equals(_nativeSeekPath, request.InputPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_nativeSeekModeCode, modeCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeNativeSeekPreviewObjects();
        _nativeSeekPath = request.InputPath;
        _nativeSeekModeCode = modeCode;
        _nativeSeekDecoder = new NativeFfmpegFrameDecoder(request.InputPath, width, height);
        _nativeSeekOutput = new NativeDeckLinkPreviewOutput(request);
    }

    private async Task ResumeFromNativeSeekPreviewAsync()
    {
        var startOffset = _selectedStartOffset;
        AppendLog($"Resuming playout from native preview at {FormatClock(startOffset)}...");
        ExitNativeSeekPreviewMode(setStopped: true);
        await StartPlaybackAsync(dryRun: false, startOffset: startOffset);
    }

    private void ExitNativeSeekPreviewMode(bool setStopped)
    {
        _scrubSeekTimer.Stop();
        DisposeNativeSeekPreviewObjects();
        _nativeSeekPreviewMode = false;
        _nativeSeekPath = null;
        _nativeSeekModeCode = null;
        _pendingSeekOffset = null;
        _seekQueueRunning = false;
        _activeSeekShouldRemainPaused = false;
        if (setStopped)
        {
            StopPlaybackClock();
            SetPlaying(false);
            SetStatus("Stopped", Color.FromArgb(130, 210, 164));
            UpdateDurationLabel();
        }
    }

    private void DisposeNativeSeekPreviewObjects()
    {
        _nativeSeekOutput?.Dispose();
        _nativeSeekOutput = null;
        _nativeSeekDecoder?.Dispose();
        _nativeSeekDecoder = null;
    }

    private void ExitScrubPreviewMode(bool holdForReplacement, bool setStopped)
    {
        _scrubSeekTimer.Stop();
        _pendingScrubPreviewOffset = null;
        _scrubPreviewDecodeCancellation?.Cancel();
        _scrubPreviewDecodeCancellation = null;

        if (holdForReplacement)
        {
            _scrubPreviewOutput?.HoldForReplacement();
        }
        else
        {
            _scrubPreviewOutput?.Dispose();
        }

        _scrubPreviewOutput = null;
        _scrubPreviewStartTask = null;
        DisposeScrubPreviewHelper();
        _scrubPreviewPath = null;
        _scrubPreviewModeCode = null;
        _scrubPreviewMode = false;
        _scrubPreviewLoopRunning = false;
        _scrubPreviewReturnPaused = false;
        if (setStopped)
        {
            StopPlaybackClock();
            SetPlaying(false);
            SetStatus("Stopped", Color.FromArgb(130, 210, 164));
            UpdateDurationLabel();
        }
    }

    private async Task ProcessPendingSeekAsync()
    {
        if (_seekQueueRunning)
        {
            return;
        }

        _seekQueueRunning = true;
        try
        {
            while (_pendingSeekOffset.HasValue)
            {
                var target = _pendingSeekOffset.Value;
                var shouldRemainPaused = _pendingSeekShouldRemainPaused;
                _pendingSeekOffset = null;
                _pendingSeekShouldRemainPaused = false;
                _activeSeekShouldRemainPaused = shouldRemainPaused;

                await PerformSeekAsync(target, shouldRemainPaused);
            }
        }
        finally
        {
            _activeSeekShouldRemainPaused = false;
            _seekQueueRunning = false;
        }
    }

    private async Task PerformSeekAsync(TimeSpan target, bool shouldRemainPaused)
    {
        _switchingPlayback = true;
        try
        {
            var targetName = PlaybackTargetName;
            AppendLog($"Seeking {targetName} to {FormatClock(target)}...");
            SetStatus($"Seeking to {FormatClock(target)}", Color.FromArgb(126, 188, 226));

            var stoppedTask = _playbackStoppedSignal?.Task;
            PreserveVideoOutputForReplacement();
            StopPlayback();
            if (stoppedTask is not null)
            {
                await stoppedTask;
            }

            AppendLog(shouldRemainPaused
                ? $"Starting paused {targetName} from {FormatClock(target)}..."
                : $"Starting {targetName} from {FormatClock(target)}...");
            _ = StartPlaybackAsync(dryRun: false, startOffset: target, startPaused: shouldRemainPaused);
        }
        finally
        {
            _switchingPlayback = false;
        }
    }

    private TimeSpan? GetCurrentSeekDuration()
    {
        return _isPlaying
            ? _playbackDuration
            : _selectedMediaDuration;
    }

    private TimeSpan GetPositionBarTime()
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(_positionBar.Value, _positionBar.Minimum, _positionBar.Maximum));
    }

    private TimeSpan GetCurrentSeekPosition()
    {
        if (!_isPlaying || !_playbackStartedAt.HasValue)
        {
            return _selectedStartOffset;
        }

        if (_nativeSeekPreviewMode)
        {
            return _selectedStartOffset;
        }

        if (_scrubPreviewMode)
        {
            return _selectedStartOffset;
        }

        var clockNow = _playbackPausedAt ?? DateTime.UtcNow;
        var elapsedSinceDecoderStart = clockNow - _playbackStartedAt.Value - _playbackPausedDuration;
        if (elapsedSinceDecoderStart < TimeSpan.Zero)
        {
            elapsedSinceDecoderStart = TimeSpan.Zero;
        }

        return ClampSeekOffset(_playbackStartOffset + elapsedSinceDecoderStart, _playbackDuration);
    }

    private TimeSpan GetFrameDuration()
    {
        var rate = ParseFrameRate(_frameRateBox.Text);
        if (rate <= 0)
        {
            rate = 25;
        }

        return TimeSpan.FromSeconds(1 / rate);
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static (int Width, int Height)? ParseVideoSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('x', 'X');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    private static TimeSpan ClampSeekOffset(TimeSpan offset, TimeSpan? duration)
    {
        if (offset < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration.HasValue && duration.Value > TimeSpan.Zero && offset > duration.Value)
        {
            return duration.Value;
        }

        return offset;
    }

    private void ResetPositionBar(string endText = "--")
    {
        SetSeekControlsEnabled(false);
        if (_positionBar.Maximum != 1)
        {
            _positionBar.Value = 0;
            _positionBar.Maximum = 1;
        }

        _positionBar.Value = 0;
        _positionStartLabel.Text = "0";
        _positionEndLabel.Text = endText;
        SetCurrentTimeDisplay(string.Equals(endText, "live", StringComparison.OrdinalIgnoreCase) ? "LIVE" : "--");
    }

    private void UpdatePositionBar(TimeSpan duration, TimeSpan position)
    {
        if (duration <= TimeSpan.Zero)
        {
            ResetPositionBar();
            return;
        }

        SetSeekControlsEnabled(!_playbackIsStillImage && !_playbackIsTestPattern);
        var max = Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds));
        if (_positionBar.Maximum != max)
        {
            _positionBar.Value = 0;
            _positionBar.Maximum = max;
            _positionBar.TickFrequency = Math.Max(1, max / 10);
        }

        var value = Math.Clamp((int)Math.Floor(position.TotalMilliseconds), 0, max);
        if (!_isDraggingSeek && _positionBar.Value != value)
        {
            _positionBar.Value = value;
        }

        _positionStartLabel.Text = "0";
        _positionEndLabel.Text = FormatClock(duration, roundUp: true);
        SetCurrentTimeDisplay(position);
    }

    private void SetSeekControlsEnabled(bool enabled)
    {
        _positionBar.Enabled = enabled;
        _seekBackOneSecondButton.Enabled = enabled;
        _seekBackTenFramesButton.Enabled = enabled;
        _seekBackFiveFramesButton.Enabled = enabled;
        _seekBackOneFrameButton.Enabled = enabled;
        _seekForwardOneFrameButton.Enabled = enabled;
        _seekForwardFiveFramesButton.Enabled = enabled;
        _seekForwardTenFramesButton.Enabled = enabled;
        _seekForwardOneSecondButton.Enabled = enabled;
    }

    private static string FormatClock(TimeSpan value, bool roundUp = false)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var seconds = roundUp
            ? Math.Ceiling(value.TotalSeconds)
            : Math.Floor(value.TotalSeconds);
        value = TimeSpan.FromSeconds(seconds);
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private async Task PlaySelectedMediaGridAsync()
    {
        var path = GetSelectedMediaGridPath();
        if (path is null || !SelectMediaPath(path))
        {
            if (!_isPlaying && File.Exists(_inputPathBox.Text.Trim()))
            {
                await StartPlaybackAsync(dryRun: false);
            }

            return;
        }

        if (!_isPlaying)
        {
            await StartPlaybackAsync(dryRun: false);
            return;
        }

        if (_switchingPlayback)
        {
            return;
        }

        _switchingPlayback = true;
        try
        {
            var targetName = PlaybackTargetName;
            var nextFile = Path.GetFileName(_inputPathBox.Text);
            AppendLog($"Switching {targetName} to {nextFile}...");
            SetStatus($"Switching to {nextFile}", Color.FromArgb(126, 188, 226));

            var stoppedTask = _playbackStoppedSignal?.Task;
            PreserveVideoOutputForReplacement();
            StopPlayback();
            if (stoppedTask is not null)
            {
                await stoppedTask;
            }

            nextFile = Path.GetFileName(_inputPathBox.Text);
            AppendLog($"Starting switched {targetName}: {nextFile}...");
            _ = StartPlaybackAsync(dryRun: false);
        }
        finally
        {
            _switchingPlayback = false;
        }
    }

    private static TreeNode CreateDirectoryNode(string path, string text)
    {
        var node = new TreeNode(text) { Tag = path, ToolTipText = path };
        node.Nodes.Add(new TreeNode("Loading..."));
        return node;
    }

    private static void LoadDirectoryChildren(TreeNode node)
    {
        if (node.Tag is not string path || !Directory.Exists(path) || !IsPlaceholderOrEmpty(node))
        {
            return;
        }

        node.Nodes.Clear();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                node.Nodes.Add(CreateDirectoryNode(directory, Path.GetFileName(directory)));
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.Nodes.Add(new TreeNode("Access denied"));
        }
        catch (IOException ex)
        {
            node.Nodes.Add(new TreeNode(ex.Message));
        }
    }

    private static bool IsPlaceholderOrEmpty(TreeNode node)
    {
        return node.Nodes.Count == 0 ||
            (node.Nodes.Count == 1 && node.Nodes[0].Tag is null && node.Nodes[0].Text == "Loading...");
    }

    private static bool IsSupportedMediaFile(string path)
    {
        return MediaExtensions.Contains(Path.GetExtension(path));
    }

    private static bool IsImageFile(string path)
    {
        return ImageExtensions.Contains(Path.GetExtension(path));
    }

    private IReadOnlyList<string> FindMediaFiles(
        string mediaRootPath,
        string searchText,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(mediaRootPath);

        while (pendingDirectories.Count > 0 && results.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pendingDirectories.Pop();

            try
            {
                foreach (var childDirectory in Directory.EnumerateDirectories(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    pendingDirectories.Push(childDirectory);
                }

                foreach (var file in Directory.EnumerateFiles(directory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsSupportedMediaFile(file) &&
                        Path.GetFileName(file).Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                        if (results.Count >= maxResults)
                        {
                            break;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip folders Windows will not let us scan.
            }
            catch (IOException)
            {
                // Skip folders that disappear or are temporarily unavailable.
            }
        }

        return results
            .OrderBy(GetMediaDisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetMediaDisplayPath(string path)
    {
        try
        {
            return Path.GetRelativePath(_mediaRootPath, path);
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (_isPlaying)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            SetStatus("Refreshing DeckLink devices...", Color.FromArgb(126, 188, 226));
            SetControlsEnabled(false);

            var previous = _deviceBox.SelectedItem?.ToString() ?? _savedDeviceName;
            var devices = await _deckLink.ListDevicesAsync(GetFfmpegPath(), cancellation.Token);

            _deviceBox.Items.Clear();
            _modeBox.Items.Clear();
            foreach (var device in devices)
            {
                _deviceBox.Items.Add(device);
            }

            if (_deviceBox.Items.Count > 0)
            {
                var selectedIndex = FindDeviceIndex(previous) ?? FindDeviceIndex(DefaultDeckLinkDeviceName) ?? 0;

                _deviceBox.SelectedIndex = selectedIndex;
            }
            else
            {
                ApplyDeckLinkUnavailableFallback("No DeckLink device detected. Preview-only mode is available.");
                return;
            }

            AppendLog($"Found {devices.Count} DeckLink device(s).");
            SetStatus("Ready", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex) when (IsDeckLinkUnavailableException(ex))
        {
            ApplyDeckLinkUnavailableFallback($"DeckLink output unavailable. Preview-only mode is available. {FirstLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            SetStatus("Error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task RefreshModesAsync()
    {
        if (_isPlaying || _deviceBox.SelectedItem is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            SetStatus("Loading DeckLink modes...", Color.FromArgb(126, 188, 226));
            SetControlsEnabled(false);

            var device = GetSelectedDevice();
            var previousCode = (_modeBox.SelectedItem as DeckLinkMode)?.Code ?? _savedModeCode;
            var modes = await _deckLink.ListFormatsAsync(GetFfmpegPath(), device, cancellation.Token);

            _modeBox.Items.Clear();
            foreach (var mode in modes)
            {
                _modeBox.Items.Add(mode);
            }

            if (_modeBox.Items.Count > 0)
            {
                var selectedIndex = FindModeIndex(previousCode) ?? FindModeIndex(DefaultDeckLinkModeCode) ?? 0;

                _modeBox.SelectedIndex = selectedIndex;
            }

            AppendLog($"Loaded {modes.Count} mode(s) for {device}.");
            SetStatus("Ready", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex) when (IsDeckLinkUnavailableException(ex))
        {
            ApplyDeckLinkUnavailableFallback($"DeckLink modes unavailable. Preview-only mode is available. {FirstLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            SetStatus("Error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void ApplySelectedModeToFields()
    {
        if (_modeBox.SelectedItem is not DeckLinkMode mode)
        {
            return;
        }

        _videoSizeBox.Text = $"{mode.Width}x{mode.Height}";
        _frameRateBox.Text = mode.FrameRate;

        _statusLabel.Text = mode.IsInterlaced
            ? $"{mode.Code} interlaced selected"
            : "Ready";
        _statusLabel.ForeColor = mode.IsInterlaced
            ? Color.FromArgb(232, 181, 105)
            : Color.FromArgb(130, 210, 164);
    }

    private void ApplyDeckLinkUnavailableFallback(string message)
    {
        _deviceBox.Items.Clear();
        _modeBox.Items.Clear();
        ApplyDefaultPreviewModeValues();

        if (!_previewOnlyCheckBox.Checked)
        {
            _previewOnlyCheckBox.Checked = true;
        }

        AppendLog(message);
        SetStatus("Preview only (no DeckLink)", Color.FromArgb(232, 181, 105));
    }

    private void ApplyDefaultPreviewModeValues()
    {
        if (string.IsNullOrWhiteSpace(_videoSizeBox.Text))
        {
            _videoSizeBox.Text = "1920x1080";
        }

        if (string.IsNullOrWhiteSpace(_frameRateBox.Text))
        {
            _frameRateBox.Text = "25000/1000";
        }
    }

    private static bool IsDeckLinkUnavailableException(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("decklink", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blackmagic", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No such device", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Cannot open video device", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Could not open input", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var firstLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(firstLine) ? string.Empty : firstLine.Trim();
    }

    private async Task StartPlaybackAsync(
        bool dryRun,
        bool useTestPattern = false,
        TimeSpan? startOffset = null,
        bool startPaused = false)
    {
        if (_isPlaying)
        {
            return;
        }

        try
        {
            var previewOnly = PreviewOnlyMode;
            var pcAudio = PcAudioMode;
            var request = BuildRequest(useTestPattern, startOffset ?? _selectedStartOffset);
            var selectedMode = _modeBox.SelectedItem as DeckLinkMode;
            request = _deckLink.ApplyModeDefaults(request, selectedMode);
            var commandText = _sdkPlayer.FormatDecoderCommand(
                request,
                throttleAudioRealtime: previewOnly,
                monitorPcAudio: pcAudio);

            AppendLog("");
            AppendLog("Command:");
            AppendLog(previewOnly ? "Preview-only decoder command:" : "SDK decoder command:");
            AppendLog(commandText);
            AppendLog(previewOnly
                ? pcAudio
                    ? "DeckLink output disabled: app preview with PC audio monitor."
                    : "DeckLink output disabled: app preview only."
                : request.NoAudio
                    ? "DeckLink output: Blackmagic SDK direct video frames."
                    : pcAudio
                        ? "DeckLink output: Blackmagic SDK direct video frames with embedded audio and PC audio monitor."
                        : "DeckLink output: Blackmagic SDK direct video frames with embedded audio.");

            if (dryRun)
            {
                SetStatus("Dry run ready", Color.FromArgb(130, 210, 164));
                return;
            }

            var pauseController = new PlaybackPauseController();
            if (startPaused)
            {
                pauseController.Pause();
            }

            _playbackCancellation = new CancellationTokenSource();
            _playbackPauseController = pauseController;
            _playbackStoppedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPlaying(true);
            StartPlaybackClock(request, startPaused);
            AppendLog(startPaused
                ? previewOnly ? "Starting preview paused..." : "Starting playout paused..."
                : previewOnly ? "Starting preview..." : "Starting playout...");
            if (startPaused)
            {
                SetStatus("Paused", Color.FromArgb(232, 181, 105));
            }

            var logPath = GetPlaybackLogPath();
            AppendLog($"Writing playback log to {logPath}");

            using var logWriter = new StreamWriter(logPath, append: false);
            var logLock = new object();

            void LogPlaybackLine(string line)
            {
                AppendLog(line);
                lock (logLock)
                {
                    logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
                    logWriter.Flush();
                }
            }

            LogPlaybackLine("Command:");
            LogPlaybackLine(previewOnly ? "Preview-only decoder command:" : "SDK decoder command:");
            LogPlaybackLine(commandText);

            var result = await Task.Run(
                () => previewOnly
                    ? _sdkPlayer.PlayPreviewOnlyAsync(
                        request,
                        LogPlaybackLine,
                        _playbackCancellation.Token,
                        pauseController,
                        renderInitialFrameWhilePaused: startPaused,
                        previewFrame: UpdateAppPreviewFrame,
                        audioMeter: UpdateAudioMeters,
                        monitorPcAudio: pcAudio)
                    : _sdkPlayer.PlayAsync(
                        request,
                        LogPlaybackLine,
                        _playbackCancellation.Token,
                        pauseController,
                        renderInitialFrameWhilePaused: startPaused,
                        previewFrame: UpdateAppPreviewFrame,
                        previewFrameInterval: 1,
                        audioMeter: UpdateAudioMeters,
                        monitorPcAudio: pcAudio),
                _playbackCancellation.Token);

            AppendLog(result.Cancelled
                ? "Playback stopped."
                : previewOnly
                    ? $"Preview engine exited with code {result.ExitCode}."
                    : $"DeckLink SDK engine exited with code {result.ExitCode}.");
            SetStatus(result.Cancelled ? "Stopped" : $"Exited with code {result.ExitCode}", result.ExitCode == 0
                ? Color.FromArgb(130, 210, 164)
                : Color.FromArgb(232, 181, 105));
        }
        catch (OperationCanceledException) when (_playbackCancellation?.IsCancellationRequested == true)
        {
            AppendLog("Playback stopped.");
            SetStatus("Stopped", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex) when (!PreviewOnlyMode && IsDeckLinkUnavailableException(ex))
        {
            ApplyDeckLinkUnavailableFallback($"DeckLink output unavailable. Preview-only mode is available. {FirstLine(ex.Message)}");
            DeckLinkSdkPlayer.ReleaseHeldVideoOutput();
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            SetStatus("Error", Color.FromArgb(229, 113, 105));
            DeckLinkSdkPlayer.ReleaseHeldVideoOutput();
        }
        finally
        {
            var stoppedSignal = _playbackStoppedSignal;
            StopPlaybackClock();
            SetPlaying(false);
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
            _playbackStoppedSignal = null;
            stoppedSignal?.TrySetResult();
            UpdateDurationLabel();
        }
    }

    private void StopPlayback()
    {
        if (_scrubPreviewMode)
        {
            AppendLog("Stopping scrub preview...");
            ExitScrubPreviewMode(holdForReplacement: false, setStopped: true);
            return;
        }

        if (_nativeSeekPreviewMode)
        {
            AppendLog("Stopping native seek preview...");
            ExitNativeSeekPreviewMode(setStopped: true);
            return;
        }

        if (!_isPlaying)
        {
            return;
        }

        AppendLog($"Stopping {PlaybackTargetName}...");
        _playbackCancellation?.Cancel();
        _playbackPauseController?.Resume();
    }

    private void PreserveVideoOutputForReplacement()
    {
        if (PreviewOnlyMode)
        {
            return;
        }

        if (_playbackPauseController is null)
        {
            return;
        }

        _playbackPauseController.PreserveVideoOutputOnStop = true;
        AppendLog("Holding last DeckLink frame until replacement playout starts...");
    }

    private async Task TogglePauseResumePlaybackAsync()
    {
        if (_scrubPreviewMode)
        {
            await FinishScrubPreviewAsync(_selectedStartOffset);
            return;
        }

        if (_nativeSeekPreviewMode)
        {
            await ResumeFromNativeSeekPreviewAsync();
            return;
        }

        if (!_isPlaying || _playbackPauseController is null)
        {
            return;
        }

        if (_isPaused)
        {
            ResumePlayback();
        }
        else
        {
            PausePlayback();
        }
    }

    private void PausePlayback()
    {
        if (_playbackPauseController is null || _isPaused)
        {
            return;
        }

        _playbackPauseController.Pause();
        _isPaused = true;
        _playbackPausedAt = DateTime.UtcNow;
        _playbackPositionTimer.Stop();
        _pauseResumeButton.Text = "Resume";
        AppendLog("Playback paused.");
        SetStatus("Paused", Color.FromArgb(232, 181, 105));
        UpdateDurationLabel();
    }

    private void ResumePlayback()
    {
        if (_playbackPauseController is null || !_isPaused)
        {
            return;
        }

        if (_playbackPausedAt.HasValue)
        {
            _playbackPausedDuration += DateTime.UtcNow - _playbackPausedAt.Value;
        }

        _playbackPausedAt = null;
        _isPaused = false;
        _playbackPauseController.Resume();
        _playbackPositionTimer.Start();
        _pauseResumeButton.Text = "Pause";
        AppendLog("Playback resumed.");
        SetStatus("Playing", Color.FromArgb(126, 188, 226));
        UpdateDurationLabel();
    }

    private PlayRequest BuildRequest(bool useTestPattern, TimeSpan startOffset)
    {
        var inputPath = _inputPathBox.Text.Trim();
        if (!useTestPattern && string.IsNullOrWhiteSpace(inputPath))
        {
            throw new InvalidOperationException("Choose a media file first.");
        }

        if (!useTestPattern && !File.Exists(inputPath))
        {
            throw new InvalidOperationException($"Media file not found: {inputPath}");
        }

        var previewOnly = PreviewOnlyMode;
        if (_deviceBox.SelectedItem is null && !previewOnly)
        {
            throw new InvalidOperationException("Choose a DeckLink output device.");
        }

        var audioChannels = (int)_audioChannelsBox.Value;
        if (audioChannels < 2 || audioChannels > 16)
        {
            throw new InvalidOperationException("Audio channels must be between 2 and 16.");
        }

        var duplexMode = _duplexBox.SelectedItem?.ToString();
        if (string.Equals(duplexMode, "unset", StringComparison.OrdinalIgnoreCase))
        {
            duplexMode = null;
        }

        var linkMode = _linkBox.SelectedItem?.ToString();
        if (string.Equals(linkMode, "unset", StringComparison.OrdinalIgnoreCase))
        {
            linkMode = null;
        }

        var levelA = ParseOptionalBool(_levelABox.SelectedItem?.ToString());

        var selectedDevice = previewOnly
            ? _deviceBox.SelectedItem?.ToString() ?? "Preview Only"
            : GetSelectedDevice();
        if (!previewOnly && selectedDevice.Contains("SDI 4K", StringComparison.OrdinalIgnoreCase))
        {
            duplexMode = null;
        }

        var selectedMode = _modeBox.SelectedItem as DeckLinkMode;
        var videoSize = EmptyToNull(_videoSizeBox.Text);
        var frameRate = EmptyToNull(_frameRateBox.Text);
        if (previewOnly)
        {
            videoSize ??= selectedMode is not null ? $"{selectedMode.Width}x{selectedMode.Height}" : "1920x1080";
            frameRate ??= selectedMode?.FrameRate ?? "25000/1000";
        }

        var isStillImage = IsImageFile(inputPath);
        var normalizedStartOffset = useTestPattern || isStillImage
            ? TimeSpan.Zero
            : ClampSeekOffset(startOffset, _selectedMediaDuration);

        return new PlayRequest(
            GetFfmpegPath(),
            inputPath,
            selectedDevice,
            selectedMode?.Code,
            videoSize,
            frameRate,
            EmptyToNull(_pixelFormatBox.Text) ?? FfmpegDeckLink.DefaultPixelFormat,
            audioChannels,
            (double)_prerollBox.Value,
            duplexMode,
            linkMode,
            levelA,
            VideoFilter: null,
            AudioFilter: null,
            false,
            useTestPattern || isStillImage,
            selectedMode?.IsInterlaced == true,
            selectedMode?.FieldOrder,
            useTestPattern,
            normalizedStartOffset);
    }

    private async Task RunUiTaskAsync(string status, Func<CancellationToken, Task> task)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            SetStatus(status, Color.FromArgb(126, 188, 226));
            SetControlsEnabled(false);
            await task(cancellation.Token);
            SetStatus("Ready", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            SetStatus("Error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private string GetFfmpegPath()
    {
        return _deckLink.FindDefaultFfmpegPath();
    }

    private string GetFfprobePath()
    {
        var ffmpegPath = GetFfmpegPath();
        return Path.Combine(
            Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory,
            "ffprobe.exe");
    }

    private static string FindDefaultMediaPath()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                foreach (var candidate in EnumerateDefaultMediaCandidates(directory.FullName))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return DefaultMediaFileName;
    }

    private static IEnumerable<string> EnumerateDefaultMediaCandidates(string directory)
    {
        yield return Path.Combine(directory, DefaultMediaFileName);
        yield return Path.Combine(directory, "bin", DefaultMediaFileName);
        yield return Path.Combine(directory, "bin", "Debug", "net10.0-windows", DefaultMediaFileName);
        yield return Path.Combine(directory, "bin", "Release", "net10.0-windows", DefaultMediaFileName);
    }

    private static string GetPlaybackLogPath()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        return Path.Combine(
            logDirectory,
            $"ffmpeg_playout_{DateTime.Now:ddMMyy_HHmmss}.log");
    }

    private string GetSelectedDevice()
    {
        return _deviceBox.SelectedItem?.ToString()
            ?? throw new InvalidOperationException("Choose a DeckLink output device.");
    }

    private int? FindDeviceIndex(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        for (var i = 0; i < _deviceBox.Items.Count; i++)
        {
            if (string.Equals(_deviceBox.Items[i]?.ToString(), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    private int? FindModeIndex(string? modeCode)
    {
        if (string.IsNullOrWhiteSpace(modeCode))
        {
            return null;
        }

        for (var i = 0; i < _modeBox.Items.Count; i++)
        {
            if (_modeBox.Items[i] is DeckLinkMode mode &&
                string.Equals(mode.Code, modeCode, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? ParseOptionalBool(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    private void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _stopButton.Enabled = isPlaying;
        _pauseResumeButton.Enabled = isPlaying;
        _refreshDevicesButton.Enabled = !isPlaying;
        _refreshModesButton.Enabled = !isPlaying && HasDeckLinkDevice;
        _refreshMediaButton.Enabled = !isPlaying;
        _browseMediaRootButton.Enabled = true;
        _mediaRootPathBox.Enabled = true;
        _mediaSearchBox.Enabled = true;
        _clearMediaSearchButton.Enabled = true;
        _previewOnlyCheckBox.Enabled = !isPlaying;
        _pcAudioCheckBox.Enabled = !isPlaying;
        _mediaTree.Enabled = true;
        _mediaGrid.Enabled = true;
        if (!isPlaying)
        {
            _pauseResumeButton.Text = "Pause";
        }

        SetStatus(isPlaying ? "Playing" : _statusLabel.Text, isPlaying
            ? Color.FromArgb(126, 188, 226)
            : _statusLabel.ForeColor);
    }

    private void SetControlsEnabled(bool enabled)
    {
        _refreshDevicesButton.Enabled = enabled;
        _refreshModesButton.Enabled = enabled && HasDeckLinkDevice;
        _pauseResumeButton.Enabled = _isPlaying;
        _refreshMediaButton.Enabled = enabled && !_isPlaying;
        _browseMediaRootButton.Enabled = enabled;
        _mediaRootPathBox.Enabled = enabled;
        _mediaSearchBox.Enabled = enabled;
        _clearMediaSearchButton.Enabled = enabled;
        _previewOnlyCheckBox.Enabled = enabled && !_isPlaying;
        _pcAudioCheckBox.Enabled = enabled && !_isPlaying;
        _mediaTree.Enabled = enabled;
        _mediaGrid.Enabled = enabled;
        _stopButton.Enabled = _isPlaying;
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void InitializeCpuUsageSampling()
    {
        _hasPdhCpuCounter = TryInitializePdhCpuCounter();
        if (_hasPdhCpuCounter)
        {
            _cpuUsageLabel.Text = "PC CPU --";
            return;
        }

        if (!TryGetSystemCpuTimes(out _lastSystemIdleTime, out _lastSystemKernelTime, out _lastSystemUserTime))
        {
            _hasSystemCpuSample = false;
            _cpuUsageLabel.Text = "PC CPU --";
            return;
        }

        _hasSystemCpuSample = true;
        _cpuUsageLabel.Text = "PC CPU 0%";
    }

    private void UpdateCpuUsageLabel()
    {
        if (_hasPdhCpuCounter)
        {
            if (TryReadPdhCpuPercent(out var pdhCpuPercent))
            {
                UpdateCpuUsageDisplay(SmoothCpuPercent(pdhCpuPercent));
            }

            return;
        }

        if (!TryGetSystemCpuTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            _cpuUsageLabel.Text = "PC CPU --";
            return;
        }

        if (!_hasSystemCpuSample ||
            idleTime < _lastSystemIdleTime ||
            kernelTime < _lastSystemKernelTime ||
            userTime < _lastSystemUserTime)
        {
            _lastSystemIdleTime = idleTime;
            _lastSystemKernelTime = kernelTime;
            _lastSystemUserTime = userTime;
            _hasSystemCpuSample = true;
            _cpuUsageLabel.Text = "PC CPU 0%";
            return;
        }

        var idleDelta = idleTime - _lastSystemIdleTime;
        var kernelDelta = kernelTime - _lastSystemKernelTime;
        var userDelta = userTime - _lastSystemUserTime;
        var totalDelta = kernelDelta + userDelta;

        _lastSystemIdleTime = idleTime;
        _lastSystemKernelTime = kernelTime;
        _lastSystemUserTime = userTime;

        if (totalDelta == 0)
        {
            _cpuUsageLabel.Text = "PC CPU 0%";
            return;
        }

        var busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
        var cpuPercent = Math.Clamp((double)busyDelta / totalDelta * 100d, 0d, 100d);
        UpdateCpuUsageDisplay(SmoothCpuPercent(cpuPercent));
    }

    private void UpdateCpuUsageDisplay(double cpuPercent)
    {
        _cpuUsageLabel.Text = $"PC CPU {cpuPercent:0}%";
        _cpuUsageLabel.ForeColor = GetCpuUsageColor(cpuPercent);
    }

    private double SmoothCpuPercent(double cpuPercent)
    {
        cpuPercent = Math.Clamp(cpuPercent, 0d, 100d);
        _smoothedCpuPercent = _smoothedCpuPercent.HasValue
            ? _smoothedCpuPercent.Value + (cpuPercent - _smoothedCpuPercent.Value) * CpuSmoothingFactor
            : cpuPercent;

        return _smoothedCpuPercent.Value;
    }

    private static Color GetCpuUsageColor(double cpuPercent)
    {
        if (cpuPercent >= 85)
        {
            return Color.FromArgb(229, 113, 105);
        }

        if (cpuPercent >= 60)
        {
            return Color.FromArgb(232, 181, 105);
        }

        return Color.FromArgb(130, 210, 164);
    }

    private bool TryInitializePdhCpuCounter()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _cpuPdhQuery) != PdhSuccess)
        {
            _cpuPdhQuery = IntPtr.Zero;
            return false;
        }

        if (PdhAddEnglishCounter(
                _cpuPdhQuery,
                @"\Processor(_Total)\% Processor Time",
                IntPtr.Zero,
                out _cpuPdhCounter) != PdhSuccess)
        {
            DisposeCpuUsageSampling();
            return false;
        }

        if (PdhCollectQueryData(_cpuPdhQuery) != PdhSuccess)
        {
            DisposeCpuUsageSampling();
            return false;
        }

        return true;
    }

    private bool TryReadPdhCpuPercent(out double cpuPercent)
    {
        cpuPercent = 0d;
        if (_cpuPdhQuery == IntPtr.Zero || _cpuPdhCounter == IntPtr.Zero)
        {
            return false;
        }

        if (PdhCollectQueryData(_cpuPdhQuery) != PdhSuccess)
        {
            return false;
        }

        var status = PdhGetFormattedCounterValue(
            _cpuPdhCounter,
            PdhFormatDouble,
            out _,
            out var value);
        if (status != PdhSuccess || value.CStatus != PdhSuccess)
        {
            return false;
        }

        cpuPercent = Math.Clamp(value.DoubleValue, 0d, 100d);
        return true;
    }

    private void DisposeCpuUsageSampling()
    {
        if (_cpuPdhQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_cpuPdhQuery);
        }

        _cpuPdhQuery = IntPtr.Zero;
        _cpuPdhCounter = IntPtr.Zero;
        _hasPdhCpuCounter = false;
    }

    private static bool TryGetSystemCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime)
    {
        idleTime = 0;
        kernelTime = 0;
        userTime = 0;

        if (!GetSystemTimes(out var idleFileTime, out var kernelFileTime, out var userFileTime))
        {
            return false;
        }

        idleTime = idleFileTime.ToUInt64();
        kernelTime = kernelFileTime.ToUInt64();
        userTime = userFileTime.ToUInt64();
        return true;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(
        string? dataSource,
        IntPtr userData,
        out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern int PdhAddEnglishCounter(
        IntPtr query,
        string counterPath,
        IntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint type,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64()
        {
            return ((ulong)HighDateTime << 32) | LowDateTime;
        }
    }

    private void AppendLog(string line)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }

        var prefix = string.IsNullOrEmpty(line) ? string.Empty : $"[{DateTime.Now:HH:mm:ss}] ";
        _logBox.AppendText(prefix + line + Environment.NewLine);
    }

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi",
        ".dv",
        ".flv",
        ".m2t",
        ".m2ts",
        ".m4v",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".mts",
        ".mxf",
        ".ts",
        ".vob",
        ".webm",
        ".wmv",
        ".bmp",
        ".gif",
        ".jpeg",
        ".jpg",
        ".png",
        ".tga",
        ".tif",
        ".tiff",
        ".webp",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".gif",
        ".jpeg",
        ".jpg",
        ".png",
        ".tga",
        ".tif",
        ".tiff",
        ".webp",
    };

}
