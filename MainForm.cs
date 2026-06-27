using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ffmpegplayer;

internal sealed class MainForm : Form
{
    private const int WM_SETREDRAW = 0x000B;
    private const string DefaultMediaFileName = "go1080p25.mp4";
    private const string DefaultMediaRootPath = @"C:\casparcg\_media";
    private const string DefaultDeckLinkDeviceName = "DeckLink SDI 4K";
    private const string DefaultDeckLinkModeCode = "Hi50";
    private const string FixedDeckLinkVideoSize = "1920x1080";
    private const string FixedDeckLinkFrameRate = "25";
    private const string FixedDeckLinkFieldOrder = "upper";
    private const string SettingsFolderName = "DeckLinkPlayer";
    private const string SettingsFileName = "settings.txt";
    private const string PlaylistStatusReady = "READY";
    private const string PlaylistStatusNext = "NEXT";
    private const string PlaylistStatusPlaying = "PLAYING";
    private const string PlaylistStatusPlayed = "PLAYED";
    private const string PlaylistStatusMissing = "MISSING";
    private const string PlaylistStatusEnd = "END";
    private const string PlaylistEndMarkerText = "END";
    private const string PlaylistTransitionCut = "Cut";
    private const string PlaylistTransitionMix = "Mix";
    private const string PlaylistTransitionPush = "Push";
    private const string PlaylistTransitionWipe = "Wipe";
    private const string PlaylistTransitionSlide = "Slide";
    private const string PlaylistTransitionFadeBlack = "Fade Black";
    private const string DefaultPlaylistTransition = PlaylistTransitionWipe;
    private const string PlaylistFileFilter = "DeckLink playlist (*.dpl)|*.dpl|JSON playlist (*.json)|*.json|All files (*.*)|*.*";
    private const string MediaGridDragDataFormat = "DeckLinkPlayer.MediaPath";
    private const string PlaylistDragDataFormat = "DeckLinkPlayer.PlaylistRowIndex";
    private const string SearchLibraryToolbarLabelTag = "SearchLibraryToolbarLabel";
    private const uint PdhFormatDouble = 0x00000200;
    private const int PdhSuccess = 0;
    private const double CpuSmoothingFactor = 0.35;
    private const int FixedClientWidth = 1920;
    private const int PreferredClientHeight = 1080;
    private const int RootPadding = 16;
    private const int HeaderRowHeight = 64;
    private const int ActionRowHeight = 198;
    private const int RemainingTimeRowHeight = 32;
    private const int CurrentTimeRowHeight = 32;
    private const int ToggleRowHeight = 44;
    private const int SettingsAreaVerticalPadding = 12;
    private const int PlaylistGridHeight = 430;
    private const int PlaylistControlRowHeight = 42;
    private static readonly TimeSpan ReverseDeckLinkRetryInterval = TimeSpan.FromMilliseconds(500);
    private const int AppPreviewWidth = 848;
    private const int AppPreviewHeight = 477;
    private const int AppPreviewAreaHeight = AppPreviewHeight + RemainingTimeRowHeight + CurrentTimeRowHeight;
    private const int AppAudioMeterColumnWidth = 42;
    private const int AppAudioMeterPanelWidth = AppAudioMeterColumnWidth * 2;
    private const int AppPreviewPanelWidth = AppPreviewWidth + AppAudioMeterPanelWidth;
    private const int PreviewColumnWidth = AppPreviewPanelWidth + 18;
    private const int SourceColumnWidth = 912;
    private const int SeekGroupWidth = AppPreviewPanelWidth;
    private const int SeekPositiveGroupGap = 8;
    private const int TransportSpanWidth = PreviewColumnWidth;
    private int MainAreaHeight => Math.Max(640, ClientSize.Height - RootPadding * 2 - HeaderRowHeight - SettingsAreaVerticalPadding);
    private int SourcePanelHeight => MainAreaHeight;
    private int DetailsPanelHeight => Math.Max(0, MainAreaHeight - AppPreviewAreaHeight - ActionRowHeight - ToggleRowHeight);
    private int CollapsedClientHeight => PreferredClientHeight;
    private int ExpandedClientHeight => PreferredClientHeight;
    // In-process FFmpeg decoding can crash the whole GUI with native access violations.
    // Keep the implementation available for an isolated helper process later, but do not call it here.
    private const bool EnableInProcessNativeSeekPreview = false;
    private static readonly TimeSpan DefaultStillDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromSeconds(1);
    private static readonly double[] PlaybackSpeedOptions = [-20d, -10d, -5d, -2d, -1.5d, -1d, 0d, 1d, 1.5d, 2d, 5d, 10d, 20d];
    private static readonly string[] PlaylistTransitionOptions =
    [
        PlaylistTransitionCut,
        PlaylistTransitionMix,
        PlaylistTransitionPush,
        PlaylistTransitionWipe,
        PlaylistTransitionSlide,
        PlaylistTransitionFadeBlack,
    ];
    private static readonly JsonSerializerOptions PlaylistJsonOptions = new() { WriteIndented = true };

    private readonly FfmpegDeckLink _deckLink = new();
    private readonly DeckLinkSdkPlayer _sdkPlayer = new();
    private readonly TextBox _inputPathBox = new();
    private readonly TextBox _mediaRootPathBox = new();
    private readonly TextBox _mediaSearchBox = new();
    private readonly CheckBox _darkModeCheckBox = new();
    private readonly TreeView _mediaTree = new();
    private readonly DataGridView _mediaGrid = new();
    private readonly ContextMenuStrip _mediaGridContextMenu = new();
    private readonly ToolStripMenuItem _mediaGridMenuCue = new("Cue");
    private readonly ToolStripMenuItem _mediaGridMenuPlay = new("Play");
    private readonly ToolStripMenuItem _mediaGridMenuPlayInVlc = new("Play in VLC");
    private readonly ToolStripMenuItem _mediaGridMenuFileInfo = new("File Information");
    private readonly FlowLayoutPanel _clipTransportPanel = new();
    private readonly ToolTip _buttonToolTip = new();
    private readonly Button _clipPreviousCueButton = new();
    private readonly Button _clipSeekBackButton = new();
    private readonly Button _clipPlayButton = new();
    private readonly Button _clipSeekForwardButton = new();
    private readonly Button _clipPauseButton = new();
    private readonly Button _clipResumeButton = new();
    private readonly Button _clipStopButton = new();
    private readonly Button _clipNextCueButton = new();
    private readonly Button _clipPlayNextButton = new();
    private readonly Button _clipLoopButton = new();
    private readonly DataGridView _playlistGrid = new();
    private readonly ContextMenuStrip _playlistContextMenu = new();
    private readonly ToolStripMenuItem _playlistMenuStop = new("Stop F1");
    private readonly ToolStripMenuItem _playlistMenuPlay = new("Play F2");
    private readonly ToolStripMenuItem _playlistMenuCue = new("Cue F3");
    private readonly ToolStripMenuItem _playlistMenuPause = new("Pause F4");
    private readonly ToolStripMenuItem _playlistMenuResume = new("Resume F4");
    private readonly ToolStripMenuItem _playlistMenuCueNext = new("Cue Next F5");
    private readonly ToolStripMenuItem _playlistMenuPlayNext = new("Play Next F6");
    private readonly ToolStripMenuItem _playlistMenuInsertBlank = new("Insert Blank");
    private readonly ToolStripMenuItem _playlistMenuDelete = new("Delete");
    private readonly ToolStripMenuItem _playlistMenuCopy = new("Copy");
    private readonly ToolStripMenuItem _playlistMenuPaste = new("Paste");
    private readonly ToolStripMenuItem _playlistMenuMoveUp = new("Move Up");
    private readonly ToolStripMenuItem _playlistMenuMoveDown = new("Move Down");
    private readonly ToolStripMenuItem _playlistMenuPlayInVlc = new("Play in VLC");
    private readonly ToolStripMenuItem _playlistMenuCheckFiles = new("Check Files");
    private readonly ToolStripMenuItem _playlistMenuOpenInTrimmer = new("Open in Trimmer");
    private readonly ToolStripMenuItem _playlistMenuFileInfo = new("File information");
    private readonly ToolStripMenuItem _playlistMenuInsertDecklink = new("Insert Decklink");
    private readonly ToolStripMenuItem _playlistMenuShowLiveDecklink = new("Show Live Decklink");
    private readonly ToolStripMenuItem _playlistMenuRefreshThumbnail = new("Refresh Thumbnail");
    private readonly ToolStripMenuItem _playlistMenuInsertEnd = new("Insert End");
    private readonly ToolStripMenuItem _playlistMenuSelectAll = new("Select All");
    private readonly ToolStripMenuItem _playlistMenuDeselectAll = new("De Select All");
    private readonly ToolStripMenuItem _playlistMenuSetStartTime = new("Set Start Time Accordinging To CurrentRow");
    private readonly ToolStripMenuItem _playlistMenuInsertPlaylist = new("Insert Playlist");
    private readonly ToolStripMenuItem _playlistMenuInsertFilter = new("Insert Filter");
    private readonly ToolStripMenuItem _playlistMenuChangeAllTransition = new("Change All Transition");
    private readonly ToolStripMenuItem _playlistMenuPlayInFfplay = new("Play in ffplay");
    private readonly ToolStripMenuItem _playlistMenuCuePrevious = new("Cue Prev");
    private readonly ToolStripMenuItem _playlistMenuPlayPrevious = new("Play Prev");
    private readonly Button _addToPlaylistButton = new();
    private readonly Button _removePlaylistItemButton = new();
    private readonly Button _movePlaylistItemUpButton = new();
    private readonly Button _movePlaylistItemDownButton = new();
    private readonly Button _playPlaylistItemButton = new();
    private readonly Button _clearPlaylistButton = new();
    private readonly Button _openPlaylistButton = new();
    private readonly Button _savePlaylistButton = new();
    private readonly Button _setPlaylistStartTimeButton = new();
    private readonly Button _startPlaylistButton = new();
    private readonly Button _stopPlaylistButton = new();
    private readonly ComboBox _deviceBox = new();
    private readonly Button _stopButton = new();
    private readonly Button _pauseResumeButton = new();
    private readonly Button _refreshMediaButton = new ToolbarTextButton();
    private readonly Button _browseMediaRootButton = new ToolbarTextButton();
    private readonly Button _clearMediaSearchButton = new ToolbarTextButton();
    private readonly TextBox _refreshMediaActionBox = new();
    private readonly TextBox _browseMediaRootActionBox = new();
    private readonly TextBox _clearMediaSearchActionBox = new();
    private readonly Button _toggleSettingsButton = new();
    private readonly Button _toggleLogButton = new();
    private readonly Button _fullscreenPreviewButton = new();
    private readonly CheckBox _previewOnlyCheckBox = new();
    private readonly CheckBox _pcAudioCheckBox = new();
    private readonly Button _refreshDevicesButton = new();
    private readonly Button _seekBackOneSecondButton = new();
    private readonly Button _seekBackTenFramesButton = new();
    private readonly Button _seekBackFiveFramesButton = new();
    private readonly Button _seekBackOneFrameButton = new();
    private readonly Button _seekForwardOneFrameButton = new();
    private readonly Button _seekForwardFiveFramesButton = new();
    private readonly Button _seekForwardTenFramesButton = new();
    private readonly Button _seekForwardOneSecondButton = new();
    private readonly Button _previousCueButton = new();
    private readonly Button _previousPlayButton = new();
    private readonly Button _nextPlayButton = new();
    private readonly Button _nextCueButton = new();
    private readonly Button _markInButton = new();
    private readonly Button _markOutButton = new();
    private readonly TextBox _markInValueBox = new();
    private readonly TextBox _markOutValueBox = new();
    private readonly Button _goToInButton = new();
    private readonly Button _playFromInButton = new();
    private readonly Button _goToTcButton = new();
    private readonly TextBox _goToTcBox = new();
    private readonly Button _goToOutButton = new();
    private readonly NumericUpDown _lastSecondsBox = new();
    private readonly Button _playLastSecondsButton = new();
    private readonly TextBox _logBox = new();
    private readonly PictureBox _appPreviewBox = new();
    private readonly AudioMeterBar _leftAudioMeter = new();
    private readonly AudioMeterBar _rightAudioMeter = new();
    private readonly Label _statusLabel = new();
    private readonly Label _playbackModeLabel = new();
    private readonly Label _cpuUsageLabel = new();
    private readonly Label _loadedFileLabel = new();
    private readonly Label _durationLabel = new();
    private readonly Label _currentTimeLabel = new();
    private readonly Label _reverseCacheStatusLabel = new();
    private readonly Label _positionStartLabel = new();
    private readonly Label _positionEndLabel = new();
    private readonly SeekBarControl _positionBar = new();
    private readonly List<Button> _playbackSpeedButtons = new();
    private readonly List<Process> _externalFfplayProcesses = new();
    private readonly object _externalFfplayLock = new();
    private readonly System.Windows.Forms.Timer _mediaSearchTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _durationProbeTimer = new() { Interval = 350 };
    private readonly System.Windows.Forms.Timer _playbackPositionTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _scrubSeekTimer = new() { Interval = 140 };
    private readonly System.Windows.Forms.Timer _reversePlaybackSpeedTimer = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer _reverseDeckLinkFrameTimer = new() { Interval = 40 };
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
    private PreviewFullscreenForm? _fullscreenPreviewForm;
    private CancellationTokenSource? _scrubPreviewDecodeCancellation;
    private CancellationTokenSource? _reversePlaybackCancellation;
    private Task? _scrubPreviewStartTask;
    private ReverseAudioChunkQueue? _reverseAudio;
    private ReversePcAudioOutput? _reversePcAudioOutput;
    private ReverseWaveOutAudioOutput? _reverseWaveAudioOutput;
    private ReverseDeckLinkAudioOutput? _reverseDeckLinkAudioOutput;
    private bool _reverseDeckLinkAudioEnabled;
    private PlayRequest? _reverseDeckLinkRequest;
    private byte[]? _reverseDeckLinkFrame;
    private readonly List<PlaylistItem> _playlistItems = new();
    private PlaylistItem? _playlistClipboardItem;
    private string? _currentPlaylistPath;
    private string? _mediaGridDragPath;
    private Point _mediaGridDragStart;
    private int? _playlistDragRowIndex;
    private Point _playlistDragStart;
    private bool _clipLoopPlaybackEnabled;
    private TimeSpan? _selectedMediaDuration;
    private TimeSpan? _playbackDuration;
    private DateTime? _playbackStartedAt;
    private DateTime? _playbackPausedAt;
    private DateTime? _playbackClockSampleAt;
    private TimeSpan _playbackClockElapsed;
    private TimeSpan _playbackPausedDuration;
    private TimeSpan _selectedStartOffset;
    private TimeSpan _playbackStartOffset;
    private TimeSpan? _playbackEndOffset;
    private TimeSpan? _markInOffset;
    private TimeSpan? _markOutOffset;
    private TimeSpan? _pendingSeekOffset;
    private string? _trimMarkPath;
    private string? _selectedDurationPath;
    private string? _playbackPath;
    private string? _savedDeviceName;
    private string? _nativeSeekPath;
    private string? _nativeSeekModeCode;
    private string? _nativeSeekDisabledPath;
    private string? _scrubPreviewPath;
    private string? _scrubPreviewModeCode;
    private string? _scrubPreviewHelperDisabledPath;
    private string? _scrubPreviewOutputDisabledPath;
    private DateTime _nextReverseDeckLinkRetryAt = DateTime.MinValue;
    private DateTime _lastReverseDeckLinkFailureLogAt = DateTime.MinValue;
    private string _mediaRootPath = DefaultMediaRootPath;
    private string? _selectedMediaFolderPath;
    private int? _playlistPlayingIndex;
    private int? _playlistTransitionLeadInIndex;
    private TimeSpan _playlistTransitionLeadInDuration;
    private bool _playlistTransitionLeadInReady;
    private bool _playlistPlaybackActive;
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
    private bool _previewOnlyUserSet;
    private bool _darkMode = true;
    private double _selectedPlaybackSpeed = 1d;
    private double _reversePlaybackSpeed;
    private double _reversePlaybackFrameCarry;
    private DateTime? _reversePlaybackLastTickAt;
    private int _appPreviewFramePending;
    private bool _reverseSpeedSeekRunning;
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
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = true;
        SetFixedClientHeight(CollapsedClientHeight);
        PlaceOnPrimaryScreen();
        BackColor = Color.FromArgb(22, 25, 29);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        UpdateStyles();

        _loadingSettings = true;
        BuildUi();
        ConfigureButtonToolTips();

        LoadAppSettings();
        _inputPathBox.Text = FindDefaultMediaPath();
        UpdateLoadedFileLabel();
        _durationProbeTimer.Tick += DurationProbeTimer_Tick;
        _playbackPositionTimer.Tick += (_, _) => UpdateDurationLabel();
        _scrubSeekTimer.Tick += ScrubSeekTimer_Tick;
        _reversePlaybackSpeedTimer.Tick += async (_, _) => await ReversePlaybackSpeedTimer_TickAsync();
        _reverseDeckLinkFrameTimer.Tick += (_, _) => ReverseDeckLinkFrameTimer_Tick();
        KeyDown += MainForm_KeyDown;
        InitializeCpuUsageSampling();
        _cpuUsageTimer.Tick += (_, _) => UpdateCpuUsageLabel();
        _cpuUsageTimer.Start();
        ScheduleDurationProbe();

        Shown += async (_, _) =>
        {
            WindowState = FormWindowState.Normal;
            SetFixedClientHeight(CollapsedClientHeight);
            PlaceOnPrimaryScreen();
            Activate();
            await Task.Delay(100);
            LoadMediaTree();
            await SeedPlaylistFromMediaLibraryAsync();
            await RefreshDevicesAsync();
        };
        FormClosing += (_, _) =>
        {
            _playbackCancellation?.Cancel();
            _mediaSearchCancellation?.Cancel();
            _durationProbeCancellation?.Cancel();
            _mediaGridMetadataCancellation?.Cancel();
            _scrubSeekTimer.Stop();
            _reverseDeckLinkFrameTimer.Stop();
            _cpuUsageTimer.Stop();
            DisposeCpuUsageSampling();
            StopExternalFfplayProcesses();
            ExitNativeSeekPreviewMode(setStopped: false);
            ExitScrubPreviewMode(holdForReplacement: false, setStopped: false);
            CloseFullscreenPreview();
            DisposeAppPreviewImage();
            DeckLinkSdkPlayer.ReleaseHeldVideoOutput();
            SaveAppSettings();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buttonToolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private async void MainForm_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F1:
                e.Handled = true;
                StopPlayback();
                break;
            case Keys.F2:
                e.Handled = true;
                await PlaySelectedPlaylistItemAsync();
                break;
            case Keys.F3:
                e.Handled = true;
                CueSelectedPlaylistItem();
                break;
            case Keys.F4:
                e.Handled = true;
                if (_isPaused)
                {
                    ResumePlayback();
                }
                else
                {
                    PausePlayback();
                }

                break;
            case Keys.F5:
                e.Handled = true;
                CueRelativePlaylistItem(1);
                break;
            case Keys.F6:
                e.Handled = true;
                await PlayRelativePlaylistItemAsync(1);
                break;
        }
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
            var mediaRootPath = GetSetting(settings, "MediaRootPath");
            if (mediaRootPath is not null)
            {
                _mediaRootPath = mediaRootPath;
                _mediaRootPathBox.Text = mediaRootPath;
            }

            _previewOnlyUserSet = TryGetBoolSetting(settings, "PreviewOnlyUserSet", out var previewOnlyUserSet) && previewOnlyUserSet;
            if (_previewOnlyUserSet && TryGetBoolSetting(settings, "PreviewOnly", out var previewOnly))
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

            var settingsPath = GetSettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

            File.WriteAllLines(
                settingsPath,
                [
                    $"DeckLinkDevice={_savedDeviceName ?? string.Empty}",
                    $"MediaRootPath={_mediaRootPath}",
                    $"PreviewOnly={_previewOnlyCheckBox.Checked.ToString(CultureInfo.InvariantCulture)}",
                    $"PreviewOnlyUserSet={_previewOnlyUserSet.ToString(CultureInfo.InvariantCulture)}",
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
        var fixedClientSize = GetWorkingAreaClientSize(clientHeight);
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        if (WindowState == FormWindowState.Normal)
        {
            ClientSize = fixedClientSize;
        }

        MinimumSize = SizeFromClientSize(fixedClientSize);
        MaximumSize = Size.Empty;
    }

    private Size GetWorkingAreaClientSize(int desiredClientHeight)
    {
        var area = GetPrimaryWorkingArea();
        var clientSize = new Size(FixedClientWidth, desiredClientHeight);
        var formSize = SizeFromClientSize(clientSize);

        if (formSize.Width > area.Width)
        {
            clientSize.Width = Math.Max(1200, clientSize.Width - (formSize.Width - area.Width));
        }

        if (formSize.Height > area.Height)
        {
            clientSize.Height = Math.Max(720, clientSize.Height - (formSize.Height - area.Height));
        }

        return clientSize;
    }

    private void PlaceOnPrimaryScreen()
    {
        var area = GetPrimaryWorkingArea();
        Location = new Point(area.Left, area.Top);
    }

    private Rectangle GetPrimaryWorkingArea()
    {
        return Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
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

    private void ConfigureButtonToolTips()
    {
        _buttonToolTip.AutoPopDelay = 12000;
        _buttonToolTip.InitialDelay = 350;
        _buttonToolTip.ReshowDelay = 100;
        _buttonToolTip.ShowAlways = true;
        ApplyToolTipTheme(_buttonToolTip, _darkMode);

        SetButtonToolTip(_clearMediaSearchButton, "Clear the clip search box and show the selected folder.");
        SetButtonToolTip(_refreshMediaButton, "Refresh the folder tree and clip list.");
        SetButtonToolTip(_browseMediaRootButton, "Choose a different media library folder.");
        SetButtonToolTip(_clearMediaSearchActionBox, "Clear the clip search box and show the selected folder.");
        SetButtonToolTip(_refreshMediaActionBox, "Refresh the folder tree and clip list.");
        SetButtonToolTip(_browseMediaRootActionBox, "Choose a different media library folder.");
        SetButtonToolTip(_refreshDevicesButton, "Reload available DeckLink devices.");
        SetButtonToolTip(_addToPlaylistButton, "Add the selected clip to the playlist.");
        SetButtonToolTip(_playPlaylistItemButton, "Play the selected playlist row.");
        SetButtonToolTip(_movePlaylistItemUpButton, "Move the selected playlist row up.");
        SetButtonToolTip(_movePlaylistItemDownButton, "Move the selected playlist row down.");
        SetButtonToolTip(_removePlaylistItemButton, "Remove the selected playlist row.");
        SetButtonToolTip(_clearPlaylistButton, "Clear all playlist rows.");
        SetButtonToolTip(_openPlaylistButton, "Open a saved playlist file.");
        SetButtonToolTip(_savePlaylistButton, "Save the current playlist.");
        SetButtonToolTip(_setPlaylistStartTimeButton, "Set the selected row start time and update following rows.");
        SetButtonToolTip(_startPlaylistButton, "Start playlist playback from the selected row.");
        SetButtonToolTip(_stopPlaylistButton, "Stop playlist playback and disable automatic next clip.");

        SetButtonToolTip(_stopButton, "Stop playback.");
        SetButtonToolTip(_pauseResumeButton, "Pause or resume playback.");
        SetButtonToolTip(_seekBackOneSecondButton, "Seek back 1 second.");
        SetButtonToolTip(_seekForwardOneSecondButton, "Seek forward 1 second.");
        SetButtonToolTip(_seekBackTenFramesButton, "Seek back 10 frames.");
        SetButtonToolTip(_seekForwardTenFramesButton, "Seek forward 10 frames.");
        SetButtonToolTip(_seekBackFiveFramesButton, "Seek back 5 frames.");
        SetButtonToolTip(_seekForwardFiveFramesButton, "Seek forward 5 frames.");
        SetButtonToolTip(_seekBackOneFrameButton, "Seek back 1 frame.");
        SetButtonToolTip(_seekForwardOneFrameButton, "Seek forward 1 frame.");
        SetButtonToolTip(_previousCueButton, "Cue the previous playable playlist row.");
        SetButtonToolTip(_previousPlayButton, "Play the previous playable playlist row.");
        SetButtonToolTip(_nextPlayButton, "Play the next playable playlist row.");
        SetButtonToolTip(_nextCueButton, "Cue the next playable playlist row.");
        SetButtonToolTip(_markInButton, "Mark trim IN at the current position.");
        SetButtonToolTip(_markOutButton, "Mark trim OUT at the current position.");
        SetButtonToolTip(_goToInButton, "Seek to the marked IN point.");
        SetButtonToolTip(_playFromInButton, "Play from the marked IN point.");
        SetButtonToolTip(_goToTcButton, "Seek to the typed timecode.");
        SetButtonToolTip(_playLastSecondsButton, "Play the last selected number of seconds before OUT.");
        SetButtonToolTip(_goToOutButton, "Seek to the marked OUT point.");
        foreach (var button in _playbackSpeedButtons)
        {
            SetButtonToolTip(button, "Set playback shuttle speed.");
        }

        SetButtonToolTip(_toggleSettingsButton, "Show or hide DeckLink output settings.");
        SetButtonToolTip(_toggleLogButton, "Show or hide the playback log.");
        SetButtonToolTip(_fullscreenPreviewButton, "Show or hide the preview fullscreen on the second monitor.");

        SetButtonToolTip(_darkModeCheckBox, "Switch between dark and light mode.");
        SetButtonToolTip(_playbackModeLabel, "Shows whether playback is controlled manually or by the playlist.");
        SetButtonToolTip(_previewOnlyCheckBox, "Play in the preview window without DeckLink output.");
        SetButtonToolTip(_pcAudioCheckBox, "Send preview playback audio to the PC speakers.");

        ApplyMissingButtonToolTips(this);
    }

    private void SetButtonToolTip(Control control, string text)
    {
        _buttonToolTip.SetToolTip(control, text);
    }

    private void UpdateLoadedFileLabel()
    {
        var displayPath = GetLoadedDisplayPath();
        if (string.IsNullOrWhiteSpace(displayPath))
        {
            _loadedFileLabel.Text = "--";
            SetButtonToolTip(_loadedFileLabel, "No file loaded.");
            return;
        }

        var displayName = GetDisplayFileName(displayPath);
        _loadedFileLabel.Text = displayName;
        SetButtonToolTip(_loadedFileLabel, displayPath);
    }

    private string GetLoadedDisplayPath()
    {
        if (_playbackIsTestPattern)
        {
            return "Test pattern";
        }

        if (!string.IsNullOrWhiteSpace(_playbackPath))
        {
            return _playbackPath;
        }

        return _inputPathBox.Text.Trim();
    }

    private static string GetDisplayFileName(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private void ApplyMissingButtonToolTips(Control control)
    {
        foreach (Control child in control.Controls)
        {
            if (child is Button button && string.IsNullOrWhiteSpace(_buttonToolTip.GetToolTip(button)))
            {
                var text = button.Tag as string ?? button.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SetButtonToolTip(button, NormalizeToolTipText(text));
                }
            }

            ApplyMissingButtonToolTips(child);
        }
    }

    private static string NormalizeToolTipText(string text)
    {
        text = text.Trim();
        return text.EndsWith(".", StringComparison.Ordinal) ? text : $"{text}.";
    }

    private static void ApplyToolTipTheme(ToolTip toolTip, bool dark)
    {
        toolTip.BackColor = dark ? Color.FromArgb(30, 35, 40) : Color.White;
        toolTip.ForeColor = dark ? Color.FromArgb(236, 241, 244) : Color.FromArgb(24, 29, 34);
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(2, 0, 2, 0),
            BackColor = BackColor,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 720));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var leftPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = BackColor,
        };

        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = BackColor,
        };
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var rightTopRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 12, 0, 0),
            BackColor = BackColor,
        };

        var title = new Label
        {
            Text = "DecklinkPlayer",
            AutoSize = false,
            Size = new Size(320, 38),
            Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(239, 244, 248),
            Location = new Point(0, 4),
        };

        _statusLabel.Text = "Ready";
        _statusLabel.AutoSize = false;
        _statusLabel.Size = new Size(820, 18);
        _statusLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _statusLabel.ForeColor = Color.FromArgb(130, 210, 164);
        _statusLabel.Location = new Point(526, 18);

        _playbackModeLabel.Text = "MANUAL MODE";
        _playbackModeLabel.AutoSize = false;
        _playbackModeLabel.Size = new Size(176, 30);
        _playbackModeLabel.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point);
        _playbackModeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _playbackModeLabel.Location = new Point(332, 8);
        _playbackModeLabel.Margin = new Padding(0);
        _playbackModeLabel.Padding = new Padding(0);
        ApplyPlaybackModeLabelTheme();

        _darkModeCheckBox.Text = "Dark Mode";
        _darkModeCheckBox.Checked = true;
        _darkModeCheckBox.AutoSize = false;
        _darkModeCheckBox.Size = new Size(124, 28);
        _darkModeCheckBox.Margin = new Padding(14, 0, 0, 0);
        _darkModeCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _darkModeCheckBox.ForeColor = Color.FromArgb(224, 232, 236);
        _darkModeCheckBox.BackColor = BackColor;
        _darkModeCheckBox.CheckedChanged += (_, _) =>
        {
            ApplyTheme(_darkModeCheckBox.Checked);
            SetStatus(_darkModeCheckBox.Checked ? "Dark mode" : "Light mode", Color.FromArgb(130, 210, 164));
        };

        _loadedFileLabel.Text = "--";
        _loadedFileLabel.AutoSize = false;
        _loadedFileLabel.Dock = DockStyle.Fill;
        _loadedFileLabel.Margin = new Padding(0, 0, 0, 0);
        _loadedFileLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
        _loadedFileLabel.TextAlign = ContentAlignment.MiddleLeft;
        _loadedFileLabel.AutoEllipsis = true;
        _loadedFileLabel.Padding = new Padding(12, 0, 8, 0);
        _loadedFileLabel.BorderStyle = BorderStyle.None;
        ApplyLoadedFileLabelTheme(_darkMode);

        _cpuUsageLabel.Text = "PC CPU 0%";
        _cpuUsageLabel.AutoSize = false;
        _cpuUsageLabel.Size = new Size(170, 28);
        _cpuUsageLabel.Margin = new Padding(0);
        _cpuUsageLabel.Font = new Font("Segoe UI Semibold", 13.5F, FontStyle.Bold, GraphicsUnit.Point);
        _cpuUsageLabel.ForeColor = Color.FromArgb(239, 244, 248);
        _cpuUsageLabel.TextAlign = ContentAlignment.MiddleRight;
        _cpuUsageLabel.Padding = new Padding(0);

        leftPanel.Controls.Add(title);
        leftPanel.Controls.Add(_playbackModeLabel);
        leftPanel.Controls.Add(_statusLabel);
        rightTopRow.Controls.Add(_darkModeCheckBox);
        rightTopRow.Controls.Add(_cpuUsageLabel);
        rightPanel.Controls.Add(rightTopRow, 0, 0);
        panel.Controls.Add(leftPanel, 0, 0);
        panel.Controls.Add(rightPanel, 1, 0);
        return panel;
    }

    private Control BuildSettingsArea()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
            Padding = new Padding(0, 4, 0, 8),
        };
        EnableDoubleBuffering(area);
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SourceColumnWidth));
        area.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PreviewColumnWidth));
        area.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var rightColumn = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = BackColor,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        EnableDoubleBuffering(rightColumn);
        rightColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, AppPreviewAreaHeight));
        rightColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, ActionRowHeight));
        rightColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, ToggleRowHeight));
        rightColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        rightColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _settingsSplit = rightColumn;

        var transport = BuildActionBar();
        transport.Margin = new Padding(0);
        transport.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        var toggles = BuildToggleBar();

        var sourcePanel = BuildSourcePanel();
        area.Controls.Add(sourcePanel, 0, 0);
        rightColumn.Controls.Add(BuildAppPreviewPanel(), 0, 0);
        rightColumn.Controls.Add(transport, 0, 1);
        rightColumn.Controls.Add(toggles, 0, 2);
        var details = BuildDetailsPanel();
        rightColumn.Controls.Add(details, 0, 3);
        area.Controls.Add(rightColumn, 1, 0);
        return area;
    }

    private Control BuildSourcePanel()
    {
        var panel = BuildSection("Playlist / Media");
        panel.Dock = DockStyle.None;
        panel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        panel.Size = new Size(SourceColumnWidth - panel.Margin.Horizontal, SourcePanelHeight);

        _inputPathBox.PlaceholderText = "Media file to play";
        _inputPathBox.TextChanged += (_, _) =>
        {
            UpdateLoadedFileLabel();
            ScheduleDurationProbe();
        };
        _mediaRootPathBox.Text = _mediaRootPath;
        _mediaRootPathBox.ReadOnly = true;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = panel.BackColor,
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, PlaylistControlRowHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, PlaylistGridHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        StylePlaylistGrid();
        content.Controls.Add(BuildPlaylistControlRow(), 0, 0);
        content.Controls.Add(_playlistGrid, 0, 1);

        _mediaSearchBox.PlaceholderText = "Search media";
        _mediaSearchBox.TextChanged += (_, _) => ScheduleMediaSearch();
        _mediaSearchTimer.Tick -= MediaSearchTimer_Tick;
        _mediaSearchTimer.Tick += MediaSearchTimer_Tick;
        _clearMediaSearchButton.Text = "Clear";
        StyleButton(_clearMediaSearchButton, Color.FromArgb(52, 67, 82));
        _clearMediaSearchButton.Click += (_, _) => ClearMediaSearch();

        _refreshMediaButton.Text = "Refresh";
        StyleButton(_refreshMediaButton, Color.FromArgb(52, 67, 82));
        _refreshMediaButton.Click += async (_, _) => await RefreshMediaLibraryAsync();
        _browseMediaRootButton.Text = "Browse";
        StyleButton(_browseMediaRootButton, Color.FromArgb(63, 96, 135));
        _browseMediaRootButton.Click += (_, _) => BrowseMediaRoot();
        content.Controls.Add(BuildSearchLibraryRow(), 0, 2);

        StyleMediaTree();
        StyleMediaGrid();
        content.Controls.Add(BuildMediaBrowserPanel(panel.BackColor), 0, 3);
        panel.Controls.Add(content);

        return panel;
    }

    private void ClearMediaSearch()
    {
        _mediaSearchBox.Clear();
        _mediaSearchTimer.Stop();
        ShowSelectedFolderFiles();
    }

    private async Task RefreshMediaLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(_mediaSearchBox.Text))
        {
            LoadMediaTree();
        }
        else
        {
            await ApplyMediaSearchAsync(_mediaSearchBox.Text.Trim());
        }
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
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
        browser.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _mediaTree.Margin = new Padding(0, 0, 8, 0);
        _mediaGrid.Margin = new Padding(0);

        var clipPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = backColor,
            Margin = new Padding(0),
        };
        clipPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        clipPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        clipPanel.Controls.Add(_mediaGrid, 0, 0);
        clipPanel.Controls.Add(BuildClipTransportRow(), 0, 1);

        browser.Controls.Add(_mediaTree, 0, 0);
        browser.Controls.Add(clipPanel, 1, 0);
        return browser;
    }

    private Control BuildClipTransportRow()
    {
        _clipTransportPanel.Dock = DockStyle.Fill;
        _clipTransportPanel.FlowDirection = FlowDirection.LeftToRight;
        _clipTransportPanel.WrapContents = false;
        _clipTransportPanel.Padding = new Padding(0, 5, 0, 4);
        _clipTransportPanel.Margin = new Padding(0);
        _clipTransportPanel.BackColor = ThemePanelColor(_darkMode);
        _clipTransportPanel.Controls.Clear();

        ConfigureClipTransportButton(_clipPreviousCueButton, "Cue", "Cue selected clip", CueSelectedMediaGridAsync, width: 58);
        ConfigureClipTransportButton(_clipPlayButton, "Play", "Play selected clip", PlaySelectedMediaGridAsync, width: 58);
        ConfigureClipTransportButton(_clipPauseButton, "Pause", "Pause or resume playback", TogglePauseResumePlaybackAsync, width: 86);
        ConfigureClipTransportButton(_clipStopButton, "Stop", "Stop playback", () =>
        {
            StopPlayback();
            return Task.CompletedTask;
        }, width: 62);
        ConfigureClipTransportButton(_clipNextCueButton, "Cue Next", "Cue next clip", () => CueRelativeMediaGridItemAsync(1), width: 86);
        ConfigureClipTransportButton(_clipPlayNextButton, "Play Next", "Play next clip", () => PlayRelativeMediaGridItemAsync(1), width: 92);

        _clipTransportPanel.Controls.AddRange(
            [
                _clipPreviousCueButton,
                _clipPlayButton,
                _clipPauseButton,
                _clipStopButton,
                _clipNextCueButton,
                _clipPlayNextButton,
            ]);
        UpdateClipTransportButtons();
        return _clipTransportPanel;
    }

    private void ConfigureClipTransportButton(Button button, string text, string tooltip, Func<Task> action, int width = 44)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 28;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, Color.FromArgb(52, 67, 82));
        button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Tag = tooltip;
        SetButtonToolTip(button, tooltip);
        ApplyButtonTheme(button, _darkMode);
        button.Click += async (_, _) => await action();
    }

    private Control BuildPlaylistControlRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.FromArgb(30, 35, 40),
        };

        ConfigurePlaylistButton(_startPlaylistButton, "Start Playlist", Color.FromArgb(39, 125, 87), StartPlaylistPlaybackAsync);
        ConfigurePlaylistButton(_stopPlaylistButton, "Stop Playlist", Color.FromArgb(149, 64, 58), StopPlaylistPlayback);
        ConfigurePlaylistButton(_movePlaylistItemUpButton, "Up", Color.FromArgb(52, 67, 82), MoveSelectedPlaylistItemUp);
        ConfigurePlaylistButton(_movePlaylistItemDownButton, "Down", Color.FromArgb(52, 67, 82), MoveSelectedPlaylistItemDown);
        ConfigurePlaylistButton(_removePlaylistItemButton, "Remove", Color.FromArgb(149, 64, 58), RemoveSelectedPlaylistItem);
        ConfigurePlaylistButton(_clearPlaylistButton, "Clear", Color.FromArgb(52, 67, 82), ClearPlaylist);
        ConfigurePlaylistButton(_openPlaylistButton, "Open", Color.FromArgb(52, 67, 82), OpenPlaylistFromFileDialog);
        ConfigurePlaylistButton(_savePlaylistButton, "Save", Color.FromArgb(52, 67, 82), SavePlaylistToFileDialog);
        ConfigurePlaylistButton(_setPlaylistStartTimeButton, "Set Start", Color.FromArgb(52, 67, 82), SetSelectedPlaylistStartTime);

        row.Controls.AddRange(
            [
                _startPlaylistButton,
                _stopPlaylistButton,
                _movePlaylistItemUpButton,
                _movePlaylistItemDownButton,
                _removePlaylistItemButton,
                _clearPlaylistButton,
                _openPlaylistButton,
                _savePlaylistButton,
                _setPlaylistStartTimeButton,
            ]);

        return row;
    }

    private static void ConfigurePlaylistButton(Button button, string text, Color color, Action action)
    {
        button.Text = text;
        button.Width = text.Length > 12 ? 102 : text.Length > 8 ? 88 : text.Length > 5 ? 64 : 52;
        button.Height = 28;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, color);
        button.Click += (_, _) => action();
    }

    private static void ConfigurePlaylistButton(Button button, string text, Color color, Func<Task> action)
    {
        button.Text = text;
        button.Width = text.Length > 12 ? 102 : text.Length > 8 ? 88 : text.Length > 5 ? 64 : 52;
        button.Height = 28;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, color);
        button.Click += async (_, _) => await action();
    }

    private Control BuildOutputPanel()
    {
        var panel = BuildSection("DeckLink Output");
        panel.Padding = new Padding(8, 30, 8, 4);
        if (panel.Controls.OfType<Label>().FirstOrDefault() is { } titleLabel)
        {
            titleLabel.Location = new Point(8, 6);
            titleLabel.Size = new Size(220, 18);
            titleLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
        }

        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;

        _refreshDevicesButton.Text = "Refresh";
        StyleButton(_refreshDevicesButton, Color.FromArgb(52, 67, 82));
        _refreshDevicesButton.Click += async (_, _) => await RefreshDevicesAsync();

        _deviceBox.SelectedIndexChanged += (_, _) => SaveAppSettings();

        panel.Controls.Add(BuildDeckLinkDeviceRow());
        return panel;
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
        _reverseCacheStatusLabel.AutoSize = false;
        _reverseCacheStatusLabel.Dock = DockStyle.Fill;
        _reverseCacheStatusLabel.Margin = new Padding(0);
        _reverseCacheStatusLabel.Padding = new Padding(4, 0, 8, 0);
        _reverseCacheStatusLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point);
        _reverseCacheStatusLabel.ForeColor = Color.FromArgb(232, 181, 105);
        _reverseCacheStatusLabel.BackColor = Color.FromArgb(30, 35, 40);
        _reverseCacheStatusLabel.TextAlign = ContentAlignment.MiddleRight;
        _reverseCacheStatusLabel.Text = string.Empty;
        _reverseCacheStatusLabel.Visible = false;
        var previewInfoStrip = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.FromArgb(30, 35, 40),
        };
        previewInfoStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        previewInfoStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewInfoStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        previewInfoStrip.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewInfoStrip.Controls.Add(_durationLabel, 0, 0);
        previewInfoStrip.Controls.Add(_loadedFileLabel, 1, 0);
        previewInfoStrip.Controls.Add(_reverseCacheStatusLabel, 2, 0);
        layout.Controls.Add(previewInfoStrip, 1, 0);

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
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _positionStartLabel.Text = FormatClock(TimeSpan.Zero);
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
        _positionBar.Margin = new Padding(0);
        _positionBar.Minimum = 0;
        _positionBar.Maximum = 1;
        _positionBar.Value = 0;
        _positionBar.TickStyle = TickStyle.None;
        _positionBar.DarkMode = _darkMode;
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

        var positionBarHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.FromArgb(30, 35, 40),
        };
        positionBarHost.Controls.Add(_positionBar);
        positionBarHost.Resize += (_, _) => UpdateSeekRangeHighlight();

        panel.Controls.Add(_positionStartLabel, 0, 0);
        panel.Controls.Add(positionBarHost, 1, 0);
        panel.Controls.Add(_positionEndLabel, 2, 0);
        return panel;
    }

    private void ConfigureSeekStepButton(Button button, string text, Func<Task> action)
    {
        button.Text = text;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, Color.FromArgb(52, 67, 82));
        button.Width = 52;
        button.Height = 26;
        button.Enabled = false;
        button.Click += async (_, _) => await RunSeekSafelyAsync(action);
    }

    private void ConfigureTrimButton(Button button, string text, int width, Color color, Action action)
    {
        button.Text = text;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, color);
        button.Width = width;
        button.Height = 31;
        button.Enabled = false;
        button.Click += (_, _) => action();
    }

    private void ConfigureSeekCommandButton(Button button, string text, int width, Func<Task> action)
    {
        button.Text = text;
        button.Margin = new Padding(0, 0, 4, 0);
        StyleButton(button, Color.FromArgb(52, 67, 82));
        button.Width = width;
        button.Height = 29;
        button.Enabled = false;
        button.Click += async (_, _) => await RunSeekSafelyAsync(action);
    }

    private static void ConfigureSeekTextBox(TextBox textBox, int width, bool readOnly)
    {
        textBox.Width = width;
        textBox.Height = 29;
        textBox.Margin = new Padding(0, 0, 4, 0);
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.BackColor = readOnly ? Color.FromArgb(13, 16, 19) : Color.FromArgb(20, 24, 28);
        textBox.ForeColor = Color.FromArgb(232, 237, 240);
        textBox.TextAlign = HorizontalAlignment.Center;
        textBox.ReadOnly = readOnly;
        textBox.TabStop = !readOnly;
    }

    private static void ConfigureSeekNumberBox(NumericUpDown input, int width)
    {
        input.Width = width;
        input.Height = 29;
        input.Margin = new Padding(0, 0, 4, 0);
        input.Minimum = 1;
        input.Maximum = 999;
        input.Value = 5;
        input.DecimalPlaces = 0;
        input.BackColor = Color.FromArgb(20, 24, 28);
        input.ForeColor = Color.FromArgb(232, 237, 240);
        input.TextAlign = HorizontalAlignment.Center;
    }

    private void ConfigureTransportActionButton(Button button, string text, int width, Color color, Func<Task> action)
    {
        button.Text = text;
        button.Margin = new Padding(0, 0, 6, 0);
        StyleButton(button, color);
        button.Width = width;
        button.Height = 31;
        button.Enabled = false;
        button.Click += async (_, _) => await action();
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
        _stopButton.Width = 66;
        ConfigureTransportButton(_stopButton);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopPlayback();

        _pauseResumeButton.Text = "Pause";
        StyleButton(_pauseResumeButton, Color.FromArgb(183, 126, 46));
        _pauseResumeButton.Width = 76;
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
        ConfigureTransportActionButton(_playPlaylistItemButton, "Play", 64, Color.FromArgb(63, 96, 135), PlaySelectedPlaylistItemAsync);
        ConfigureTrimButton(_previousCueButton, "Prev Cue", 86, Color.FromArgb(52, 67, 82), () => CueRelativePlaylistItem(-1));
        ConfigureTransportActionButton(_previousPlayButton, "Prev Play", 90, Color.FromArgb(63, 96, 135), () => PlayRelativePlaylistItemAsync(-1));
        ConfigureTransportActionButton(_nextPlayButton, "Next Play", 90, Color.FromArgb(63, 96, 135), () => PlayRelativePlaylistItemAsync(1));
        ConfigureTrimButton(_nextCueButton, "Next Cue", 86, Color.FromArgb(52, 67, 82), () => CueRelativePlaylistItem(1));
        ConfigureTrimButton(_markInButton, "IN", 38, Color.FromArgb(52, 67, 82), MarkTrimIn);
        ConfigureTrimButton(_markOutButton, "Out", 44, Color.FromArgb(52, 67, 82), MarkTrimOut);
        ConfigureSeekTextBox(_markInValueBox, 68, readOnly: true);
        ConfigureSeekTextBox(_markOutValueBox, 68, readOnly: true);
        ConfigureSeekCommandButton(_goToInButton, "Go to IN", 70, GoToInAsync);
        ConfigureSeekCommandButton(_playFromInButton, "Play from IN", 98, PlayFromInAsync);
        ConfigureSeekCommandButton(_goToTcButton, "GoTo TC", 72, GoToTimecodeAsync);
        ConfigureSeekTextBox(_goToTcBox, 108, readOnly: false);
        _goToTcBox.Text = "00:00:00:00";
        ConfigureSeekNumberBox(_lastSecondsBox, 52);
        ConfigureSeekCommandButton(_playLastSecondsButton, "Play Last Sec", 104, PlayLastSecondsAsync);
        ConfigureSeekCommandButton(_goToOutButton, "Go to OUT", 78, GoToOutAsync);

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
                _playPlaylistItemButton,
                _pauseResumeButton,
                _stopButton,
            },
            new Control[]
            {
                _previousCueButton,
                _previousPlayButton,
                _nextPlayButton,
                _nextCueButton,
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

        _fullscreenPreviewButton.Text = "Full Preview";
        _fullscreenPreviewButton.Width = 118;
        _fullscreenPreviewButton.Margin = new Padding(0, 0, 6, 0);
        StyleButton(_fullscreenPreviewButton, Color.FromArgb(52, 67, 82));
        _fullscreenPreviewButton.Click += (_, _) => ToggleFullscreenPreview();

        _previewOnlyCheckBox.Text = "Preview only";
        _previewOnlyCheckBox.Checked = false;
        _previewOnlyCheckBox.AutoSize = false;
        _previewOnlyCheckBox.Size = new Size(128, 34);
        _previewOnlyCheckBox.Margin = new Padding(0);
        _previewOnlyCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _previewOnlyCheckBox.ForeColor = Color.FromArgb(224, 232, 236);
        _previewOnlyCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_loadingSettings)
            {
                _previewOnlyUserSet = true;
            }

            SetStatus(PreviewOnlyMode ? "Preview only" : "Ready", PreviewOnlyMode
                ? Color.FromArgb(232, 181, 105)
                : Color.FromArgb(130, 210, 164));
            SaveAppSettings();
        };

        panel.Controls.Add(_toggleSettingsButton);
        panel.Controls.Add(_toggleLogButton);
        panel.Controls.Add(_fullscreenPreviewButton);
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

    private void ToggleFullscreenPreview()
    {
        if (_fullscreenPreviewForm is { IsDisposed: false })
        {
            CloseFullscreenPreview();
            return;
        }

        OpenFullscreenPreview();
    }

    private void OpenFullscreenPreview()
    {
        var screen = Screen.AllScreens.FirstOrDefault(candidate => !candidate.Primary);
        if (screen is null)
        {
            SetStatus("Second monitor not found.", Color.FromArgb(232, 181, 105));
            return;
        }

        var form = new PreviewFullscreenForm();
        _fullscreenPreviewForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_fullscreenPreviewForm, form))
            {
                _fullscreenPreviewForm = null;
            }

            UpdateFullscreenPreviewButton();
        };

        form.Bounds = screen.Bounds;
        form.Show(this);
        form.Bounds = screen.Bounds;
        form.SetPreviewImage(ClonePreviewImage(_appPreviewBox.Image));
        UpdateFullscreenPreviewButton();
        SetStatus($"Fullscreen preview on {screen.DeviceName}.", Color.FromArgb(130, 210, 164));
    }

    private void CloseFullscreenPreview()
    {
        var form = _fullscreenPreviewForm;
        _fullscreenPreviewForm = null;

        if (form is not null && !form.IsDisposed)
        {
            form.Close();
        }

        UpdateFullscreenPreviewButton();
    }

    private void UpdateFullscreenPreviewButton()
    {
        _fullscreenPreviewButton.Text = _fullscreenPreviewForm is { IsDisposed: false }
            ? "Close Preview"
            : "Full Preview";
        ApplyButtonTheme(_fullscreenPreviewButton, _darkMode);
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

    private Control[] BuildPlaybackSpeedButtons()
    {
        if (_playbackSpeedButtons.Count == 0)
        {
            foreach (var speed in PlaybackSpeedOptions)
            {
                _playbackSpeedButtons.Add(CreatePlaybackSpeedButton(speed));
            }
        }

        return _playbackSpeedButtons.Cast<Control>().ToArray();
    }

    private Button CreatePlaybackSpeedButton(double speed)
    {
        var button = new Button
        {
            Text = FormatPlaybackSpeedButtonText(speed),
            Tag = speed,
            Width = Math.Abs(speed) >= 10d ? 56 : Math.Abs(speed) == 1.5d ? 58 : 48,
            Height = 31,
            Margin = new Padding(0, 0, 5, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
            Enabled = false,
        };
        StyleButton(button, Color.FromArgb(52, 67, 82));
        button.Click += async (_, _) => await SetPlaybackSpeedAsync(speed);
        return button;
    }

    private static string FormatPlaybackSpeedButtonText(double speed)
    {
        return speed switch
        {
            0d => "0",
            > 0d => $"+{speed.ToString("0.##", CultureInfo.InvariantCulture)}x",
            _ => $"{speed.ToString("0.##", CultureInfo.InvariantCulture)}x",
        };
    }

    private Control BuildSeekActionGroup(
        int width,
        Control[] negativeControls,
        Control[] transportControls,
        Control[] playlistControls,
        Control[] positiveControls)
    {
        var leftWidth = MeasureControlsWidth(negativeControls);
        var transportWidth = MeasureControlsWidth(transportControls);
        var rightWidth = MeasureControlsWidth(positiveControls);
        var panel = new Panel
        {
            Width = width,
            Height = 184,
            Margin = new Padding(0),
            BackColor = Color.FromArgb(30, 35, 40),
            Padding = new Padding(8, 3, 8, 5),
        };

        var seekPanel = BuildPositionRow();
        seekPanel.Location = new Point(8, 5);
        seekPanel.Size = new Size(width - 16, 35);
        seekPanel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

        var speedControls = BuildPlaybackSpeedButtons();
        var speedWidth = MeasureControlsWidth(speedControls);
        var speedPanel = BuildSeekButtonPanel(speedControls, speedWidth);
        speedPanel.Location = new Point(Math.Max(0, (width - 16 - speedWidth) / 2), 42);

        var transportRow = new Panel
        {
            Location = new Point(8, 76),
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

        var markInPanel = BuildSeekButtonPanel([_markInButton, _markInValueBox], MeasureControlsWidth([_markInButton, _markInValueBox]));
        var markOutPanel = BuildSeekButtonPanel([_markOutValueBox, _markOutButton], MeasureControlsWidth([_markOutValueBox, _markOutButton]));
        var controlsWidth = markInPanel.Width + leftWidth + transportWidth + SeekPositiveGroupGap + rightWidth + markOutPanel.Width;
        var centeredX = Math.Max(0, (transportRow.Width - controlsWidth) / 2);
        markInPanel.Location = new Point(centeredX, 0);
        leftPanel.Location = new Point(markInPanel.Right, 0);
        transportPanel.Location = new Point(leftPanel.Right, 0);
        rightPanel.Location = new Point(transportPanel.Right + SeekPositiveGroupGap, 0);
        markOutPanel.Location = new Point(rightPanel.Right, 0);

        transportRow.Controls.Add(markInPanel);
        transportRow.Controls.Add(leftPanel);
        transportRow.Controls.Add(transportPanel);
        transportRow.Controls.Add(rightPanel);
        transportRow.Controls.Add(markOutPanel);

        var goToInPanel = BuildSeekButtonPanel([_goToInButton], MeasureControlsWidth([_goToInButton]));
        goToInPanel.Location = new Point(markInPanel.Left, 112);

        var goToOutPanel = BuildSeekButtonPanel([_goToOutButton], MeasureControlsWidth([_goToOutButton]));
        goToOutPanel.Location = new Point(Math.Max(0, markOutPanel.Right - goToOutPanel.Width), 112);

        var commandControls = new Control[]
        {
            _playFromInButton,
            _goToTcButton,
            _goToTcBox,
            _lastSecondsBox,
            _playLastSecondsButton,
        };
        var playlistCommandControls = new Control[]
        {
            _previousCueButton,
            _previousPlayButton,
            _nextPlayButton,
            _nextCueButton,
        };
        var commandWidth = MeasureControlsWidth(commandControls);
        var commandPanel = BuildSeekButtonPanel(commandControls, commandWidth);
        commandPanel.Location = new Point(Math.Max(0, (width - 16 - commandWidth) / 2), 112);

        var playlistCommandWidth = MeasureControlsWidth(playlistCommandControls);
        var playlistCommandPanel = BuildSeekButtonPanel(playlistCommandControls, playlistCommandWidth);
        playlistCommandPanel.Location = new Point(Math.Max(0, (width - 16 - playlistCommandWidth) / 2), 148);

        panel.Controls.Add(seekPanel);
        panel.Controls.Add(speedPanel);
        panel.Controls.Add(transportRow);
        panel.Controls.Add(goToInPanel);
        panel.Controls.Add(goToOutPanel);
        panel.Controls.Add(commandPanel);
        panel.Controls.Add(playlistCommandPanel);
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

    private Control BuildDeckLinkDeviceRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 1,
            Height = 34,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));

        row.Controls.Add(BuildLabel("Device"), 0, 0);
        row.Controls.Add(StyleCompactInput(_deviceBox), 1, 0);
        _refreshDevicesButton.Dock = DockStyle.Fill;
        _refreshDevicesButton.Margin = new Padding(4, 0, 4, 2);
        row.Controls.Add(_refreshDevicesButton, 2, 0);

        var fixedModeLabel = BuildLabel("Fixed 1080i50");
        fixedModeLabel.TextAlign = ContentAlignment.MiddleRight;
        fixedModeLabel.ForeColor = Color.FromArgb(130, 210, 164);
        row.Controls.Add(fixedModeLabel, 3, 0);
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

    private Control BuildSearchLibraryRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 8,
            RowCount = 1,
            Height = 43,
            Padding = new Padding(0, 3, 0, 3),
        };

        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 17));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        row.Controls.Add(BuildToolbarTextCell("Search Media", isAction: false), 0, 0);
        row.Controls.Add(StyleInput(_mediaSearchBox), 1, 0);

        ConfigureToolbarActionBox(
            _clearMediaSearchActionBox,
            "Clear",
            Color.FromArgb(52, 67, 82),
            ClearMediaSearch);
        row.Controls.Add(_clearMediaSearchActionBox, 2, 0);

        row.Controls.Add(new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 4, 8, 10),
            BackColor = Color.FromArgb(74, 83, 92),
        }, 3, 0);

        row.Controls.Add(BuildToolbarTextCell("Location", isAction: false), 4, 0);
        row.Controls.Add(StyleInput(_mediaRootPathBox), 5, 0);

        ConfigureToolbarActionBox(
            _browseMediaRootActionBox,
            "Browse",
            Color.FromArgb(63, 96, 135),
            BrowseMediaRoot);
        row.Controls.Add(_browseMediaRootActionBox, 6, 0);

        ConfigureToolbarActionBox(
            _refreshMediaActionBox,
            "Refresh",
            Color.FromArgb(52, 67, 82),
            async () => await RefreshMediaLibraryAsync());
        row.Controls.Add(_refreshMediaActionBox, 7, 0);
        ApplySearchLibraryToolbarTextTheme();
        return row;
    }

    private static TextBox BuildToolbarTextCell(string text, bool isAction)
    {
        return new TextBox
        {
            Text = text,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            TabStop = false,
            ShortcutsEnabled = false,
            BorderStyle = isAction ? BorderStyle.FixedSingle : BorderStyle.None,
            TextAlign = isAction ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            Margin = isAction ? new Padding(8, 0, 0, 6) : new Padding(0, 3, 0, 6),
            Cursor = isAction ? Cursors.Hand : Cursors.Default,
            Tag = SearchLibraryToolbarLabelTag,
            ForeColor = Color.FromArgb(218, 228, 234),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point),
        };
    }

    private void ConfigureToolbarActionBox(TextBox textBox, string text, Color backColor, Action action)
    {
        ConfigureToolbarActionBox(textBox, text, backColor, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    private void ConfigureToolbarActionBox(TextBox textBox, string text, Color backColor, Func<Task> action)
    {
        textBox.Text = text;
        textBox.Dock = DockStyle.Fill;
        textBox.ReadOnly = true;
        textBox.TabStop = false;
        textBox.ShortcutsEnabled = false;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.TextAlign = HorizontalAlignment.Center;
        textBox.Margin = new Padding(8, 0, 0, 6);
        textBox.Cursor = Cursors.Hand;
        textBox.Tag = backColor;
        textBox.Click -= ToolbarActionBox_Click;
        textBox.Click += ToolbarActionBox_Click;
        textBox.MouseDown -= ToolbarActionBox_MouseDown;
        textBox.MouseDown += ToolbarActionBox_MouseDown;
        textBox.MouseUp -= ToolbarActionBox_MouseUp;
        textBox.MouseUp += ToolbarActionBox_MouseUp;
        textBox.Leave -= ToolbarActionBox_Leave;
        textBox.Leave += ToolbarActionBox_Leave;
        textBox.Tag = new ToolbarActionState(backColor, action);
        ApplyToolbarActionBoxTheme(textBox);
    }

    private async void ToolbarActionBox_Click(object? sender, EventArgs e)
    {
        if (sender is TextBox textBox &&
            textBox.Tag is ToolbarActionState state)
        {
            await state.Action();
        }
    }

    private void ToolbarActionBox_MouseDown(object? sender, MouseEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is ToolbarActionState state)
        {
            textBox.BackColor = ControlPaint.Dark(state.BackColor, 0.15f);
        }
    }

    private void ToolbarActionBox_MouseUp(object? sender, MouseEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyToolbarActionBoxTheme(textBox);
        }
    }

    private void ToolbarActionBox_Leave(object? sender, EventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ApplyToolbarActionBoxTheme(textBox);
        }
    }

    private static void AddGridField(TableLayoutPanel grid, string labelText, Control input, int column, int row)
    {
        grid.Controls.Add(BuildLabel(labelText), column, row);
        grid.Controls.Add(StyleInput(input), column + 1, row);
    }

    private static void AddCompactGridField(TableLayoutPanel grid, string labelText, Control input, int column, int row)
    {
        grid.Controls.Add(BuildLabel(labelText), column, row);
        grid.Controls.Add(StyleCompactInput(input), column + 1, row);
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

    private static Control StyleCompactInput(Control input)
    {
        StyleInput(input);
        input.Margin = new Padding(0, 0, 4, 2);
        return input;
    }

    private Button BuildButton(string text)
    {
        var button = new Button { Text = text };
        StyleButton(button, Color.FromArgb(52, 67, 82));
        SetButtonToolTip(button, text);
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

    private void ApplyTheme(bool dark)
    {
        _darkMode = dark;
        BackColor = ThemeBackColor(dark);
        ApplyThemeToControl(this, dark);
        ApplyDataGridTheme(_playlistGrid, dark);
        ApplyDataGridTheme(_mediaGrid, dark);
        ApplyTreeTheme(_mediaTree, dark);
        ApplyContextMenuTheme();
        ApplyToolTipTheme(_buttonToolTip, dark);
        _positionBar.DarkMode = dark;

        var selectedIndex = GetSelectedPlaylistIndex();
        RefreshPlaylistGrid(selectedIndex);
        UpdateTrimControls();
        ApplySearchLibraryToolbarTextTheme();
    }

    private void ApplyThemeToControl(Control control, bool dark)
    {
        if (ReferenceEquals(control, this))
        {
            control.BackColor = ThemeBackColor(dark);
            control.ForeColor = ThemeTextColor(dark);
        }

        switch (control)
        {
            case DataGridView:
                break;
            case TreeView tree:
                ApplyTreeTheme(tree, dark);
                break;
            case Button button:
                ApplyButtonTheme(button, dark);
                break;
            case TextBox textBox:
                if (textBox.Tag is ToolbarActionState)
                {
                    ApplyToolbarActionBoxTheme(textBox);
                }
                else if (textBox.Tag is string tag && tag == SearchLibraryToolbarLabelTag)
                {
                    textBox.BackColor = ThemePanelColor(dark);
                    textBox.ForeColor = dark ? Color.White : Color.FromArgb(18, 23, 28);
                }
                else
                {
                    ApplyInputTheme(textBox, dark);
                }
                break;
            case ComboBox comboBox:
                ApplyInputTheme(comboBox, dark);
                comboBox.FlatStyle = FlatStyle.Flat;
                break;
            case NumericUpDown numeric:
                ApplyInputTheme(numeric, dark);
                break;
            case CheckBox checkBox:
                checkBox.BackColor = ThemePanelColor(dark);
                checkBox.ForeColor = ThemeTextColor(dark);
                break;
            case Label label:
                ApplyLabelTheme(label, dark);
                break;
            case PictureBox:
                control.BackColor = Color.Black;
                break;
            default:
                control.BackColor = ReferenceEquals(control, this)
                    ? ThemeBackColor(dark)
                    : ReferenceEquals(control, _clipTransportPanel)
                    ? ThemePanelColor(dark)
                    : control.Controls.Contains(_appPreviewBox)
                    ? Color.Black
                    : ThemePanelColor(dark);
                control.ForeColor = ThemeTextColor(dark);
                break;
        }

        foreach (Control child in control.Controls)
        {
            ApplyThemeToControl(child, dark);
        }
    }

    private void ApplyLabelTheme(Label label, bool dark)
    {
        if (ReferenceEquals(label, _statusLabel))
        {
            label.BackColor = ThemePanelColor(dark);
            return;
        }

        if (ReferenceEquals(label, _playbackModeLabel))
        {
            ApplyPlaybackModeLabelTheme();
            return;
        }

        if (ReferenceEquals(label, _durationLabel) ||
            ReferenceEquals(label, _currentTimeLabel) ||
            ReferenceEquals(label, _reverseCacheStatusLabel))
        {
            label.BackColor = ThemePanelColor(dark);
            if (!ReferenceEquals(label, _reverseCacheStatusLabel))
            {
                label.ForeColor = ThemeTextColor(dark);
            }

            return;
        }

        if (ReferenceEquals(label, _loadedFileLabel))
        {
            ApplyLoadedFileLabelTheme(dark);
            return;
        }

        if (label.Tag is string tag && tag == SearchLibraryToolbarLabelTag)
        {
            label.BackColor = ThemePanelColor(dark);
            label.ForeColor = dark ? Color.White : Color.FromArgb(18, 23, 28);
            return;
        }

        label.BackColor = ThemePanelColor(dark);
        label.ForeColor = ReferenceEquals(label, _cpuUsageLabel)
            ? ThemeTextColor(dark)
            : ThemeMutedTextColor(dark);
    }

    private void UpdatePlaybackModeLabel()
    {
        if (_playbackModeLabel.IsDisposed)
        {
            return;
        }

        _playbackModeLabel.Text = _playlistPlaybackActive ? "PLAYLIST MODE" : "MANUAL MODE";
        ApplyPlaybackModeLabelTheme();
    }

    private void ApplyPlaybackModeLabelTheme()
    {
        var playlistMode = _playlistPlaybackActive;
        _playbackModeLabel.BackColor = playlistMode
            ? Color.FromArgb(171, 113, 33)
            : Color.FromArgb(39, 125, 87);
        _playbackModeLabel.ForeColor = Color.White;
    }

    private void ApplySearchLibraryToolbarTextTheme()
    {
        var inputBackColor = _darkMode ? Color.FromArgb(12, 15, 18) : Color.White;
        var inputForeColor = _darkMode ? Color.White : Color.FromArgb(18, 23, 28);
        foreach (var textBox in new[] { _mediaSearchBox, _mediaRootPathBox })
        {
            textBox.BackColor = inputBackColor;
            textBox.ForeColor = inputForeColor;
            textBox.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        }

        ApplyToolbarActionBoxTheme(_clearMediaSearchActionBox);
        ApplyToolbarActionBoxTheme(_browseMediaRootActionBox);
        ApplyToolbarActionBoxTheme(_refreshMediaActionBox);
    }

    private void ApplyToolbarActionBoxTheme(TextBox textBox)
    {
        if (textBox.Tag is not ToolbarActionState state)
        {
            return;
        }

        textBox.BackColor = state.BackColor;
        textBox.ForeColor = Color.White;
        textBox.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
    }

    private void ApplyLoadedFileLabelTheme(bool dark)
    {
        _loadedFileLabel.BackColor = ThemePanelColor(dark);
        _loadedFileLabel.ForeColor = ThemeMutedTextColor(dark);
    }

    private void ApplyInputTheme(Control input, bool dark)
    {
        input.BackColor = dark ? Color.FromArgb(20, 24, 28) : Color.White;
        input.ForeColor = dark ? Color.FromArgb(232, 237, 240) : Color.FromArgb(24, 29, 34);
    }

    private void ApplyButtonTheme(Button button, bool dark)
    {
        if (IsClipTransportButton(button))
        {
            var clipAccent = GetClipTransportAccentColor(button, dark);
            button.BackColor = clipAccent.BackColor;
            button.ForeColor = clipAccent.ForeColor;
            button.FlatAppearance.BorderSize = dark ? 0 : 1;
            button.FlatAppearance.BorderColor = button.BackColor;
            return;
        }

        var accent = GetButtonAccentColor(button, dark);
        button.BackColor = accent.BackColor;
        button.ForeColor = accent.ForeColor;
        button.FlatAppearance.BorderSize = dark ? 0 : 1;
        button.FlatAppearance.BorderColor = dark ? accent.BackColor : Color.FromArgb(176, 186, 196);
    }

    private bool IsClipTransportButton(Button button)
    {
        return ReferenceEquals(button, _clipPreviousCueButton) ||
            ReferenceEquals(button, _clipSeekBackButton) ||
            ReferenceEquals(button, _clipPlayButton) ||
            ReferenceEquals(button, _clipSeekForwardButton) ||
            ReferenceEquals(button, _clipPauseButton) ||
            ReferenceEquals(button, _clipResumeButton) ||
            ReferenceEquals(button, _clipStopButton) ||
            ReferenceEquals(button, _clipNextCueButton) ||
            ReferenceEquals(button, _clipPlayNextButton) ||
            ReferenceEquals(button, _clipLoopButton);
    }

    private (Color BackColor, Color ForeColor) GetClipTransportAccentColor(Button button, bool dark)
    {
        var isPlay = ReferenceEquals(button, _clipPlayButton) ||
            ReferenceEquals(button, _clipPreviousCueButton) ||
            ReferenceEquals(button, _clipNextCueButton) ||
            ReferenceEquals(button, _clipPlayNextButton);
        var isPause = ReferenceEquals(button, _clipPauseButton) ||
            ReferenceEquals(button, _clipResumeButton);
        var isDanger = ReferenceEquals(button, _clipStopButton);
        var isLoopActive = ReferenceEquals(button, _clipLoopButton) && _clipLoopPlaybackEnabled;

        if (dark)
        {
            if (isDanger) return (Color.FromArgb(149, 64, 58), Color.White);
            if (isPause) return (Color.FromArgb(183, 126, 46), Color.White);
            if (isLoopActive) return (Color.FromArgb(39, 125, 87), Color.White);
            if (isPlay) return (Color.FromArgb(63, 96, 135), Color.White);
            return (Color.FromArgb(52, 67, 82), Color.White);
        }

        if (isDanger) return (Color.FromArgb(196, 83, 75), Color.White);
        if (isPause) return (Color.FromArgb(205, 143, 47), Color.White);
        if (isLoopActive) return (Color.FromArgb(40, 145, 97), Color.White);
        if (isPlay) return (Color.FromArgb(55, 117, 178), Color.White);
        return (Color.FromArgb(225, 231, 237), Color.FromArgb(24, 29, 34));
    }

    private (Color BackColor, Color ForeColor) GetButtonAccentColor(Button button, bool dark)
    {
        var isDanger = ReferenceEquals(button, _stopButton) ||
            ReferenceEquals(button, _removePlaylistItemButton) ||
            ReferenceEquals(button, _stopPlaylistButton);
        var isGo = ReferenceEquals(button, _addToPlaylistButton) ||
            ReferenceEquals(button, _startPlaylistButton);
        var isPlay = ReferenceEquals(button, _playPlaylistItemButton) ||
            ReferenceEquals(button, _previousPlayButton) ||
            ReferenceEquals(button, _nextPlayButton) ||
            ReferenceEquals(button, _browseMediaRootButton);
        var isPause = ReferenceEquals(button, _pauseResumeButton);
        var playbackSpeed = button.Tag is double speedValue && PlaybackSpeedOptions.Contains(speedValue)
            ? speedValue
            : (double?)null;
        var isActiveSpeed = playbackSpeed.HasValue && Math.Abs(playbackSpeed.Value - _selectedPlaybackSpeed) < 0.001d;

        if (dark)
        {
            if (isActiveSpeed && playbackSpeed < 0d) return (Color.FromArgb(184, 104, 48), Color.White);
            if (isActiveSpeed && playbackSpeed == 0d) return (Color.FromArgb(183, 126, 46), Color.White);
            if (isActiveSpeed) return (Color.FromArgb(39, 125, 87), Color.White);
            if (isDanger) return (Color.FromArgb(149, 64, 58), Color.White);
            if (isGo) return (Color.FromArgb(39, 125, 87), Color.White);
            if (isPlay) return (Color.FromArgb(63, 96, 135), Color.White);
            if (isPause) return (Color.FromArgb(183, 126, 46), Color.White);
            return (Color.FromArgb(52, 67, 82), Color.White);
        }

        if (isActiveSpeed && playbackSpeed < 0d) return (Color.FromArgb(200, 119, 56), Color.White);
        if (isActiveSpeed && playbackSpeed == 0d) return (Color.FromArgb(205, 143, 47), Color.White);
        if (isActiveSpeed) return (Color.FromArgb(40, 145, 97), Color.White);
        if (isDanger) return (Color.FromArgb(196, 83, 75), Color.White);
        if (isGo) return (Color.FromArgb(40, 145, 97), Color.White);
        if (isPlay) return (Color.FromArgb(55, 117, 178), Color.White);
        if (isPause) return (Color.FromArgb(205, 143, 47), Color.White);
        return (Color.FromArgb(225, 231, 237), Color.FromArgb(24, 29, 34));
    }

    private static void ApplyTreeTheme(TreeView tree, bool dark)
    {
        tree.BackColor = dark ? Color.FromArgb(13, 16, 19) : Color.White;
        tree.ForeColor = dark ? Color.FromArgb(226, 234, 238) : Color.FromArgb(24, 29, 34);
        tree.LineColor = dark ? Color.FromArgb(92, 103, 112) : Color.FromArgb(130, 140, 150);
    }

    private static void ApplyDataGridTheme(DataGridView grid, bool dark)
    {
        grid.BackgroundColor = dark ? Color.FromArgb(13, 16, 19) : Color.White;
        grid.GridColor = dark ? Color.FromArgb(54, 61, 68) : Color.FromArgb(210, 218, 226);
        grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? Color.FromArgb(38, 44, 50) : Color.FromArgb(226, 232, 238);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = dark ? Color.FromArgb(236, 241, 244) : Color.FromArgb(24, 29, 34);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = grid.ColumnHeadersDefaultCellStyle.ForeColor;
        grid.DefaultCellStyle.BackColor = dark ? Color.FromArgb(17, 20, 24) : Color.White;
        grid.DefaultCellStyle.ForeColor = dark ? Color.FromArgb(226, 234, 238) : Color.FromArgb(24, 29, 34);
        grid.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(32, 116, 190) : Color.FromArgb(62, 135, 210);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = dark ? Color.FromArgb(29, 34, 39) : Color.FromArgb(245, 248, 251);
    }

    private void ApplyContextMenuTheme()
    {
        ApplyContextMenuTheme(_playlistContextMenu, _darkMode);
        ApplyContextMenuTheme(_mediaGridContextMenu, _darkMode);
    }

    private static void ApplyContextMenuTheme(ContextMenuStrip menu, bool dark)
    {
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = new AppMenuRenderer(dark);
        menu.BackColor = ThemeMenuBackColor(dark);
        menu.ForeColor = ThemeTextColor(dark);
        menu.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        menu.ShowImageMargin = false;
        menu.Padding = new Padding(2, 4, 2, 4);

        foreach (ToolStripItem item in menu.Items)
        {
            ApplyToolStripItemTheme(item, dark);
        }
    }

    private static void ApplyToolStripItemTheme(ToolStripItem item, bool dark)
    {
        item.BackColor = ThemeMenuBackColor(dark);
        item.ForeColor = item.Enabled ? ThemeTextColor(dark) : ThemeDisabledTextColor(dark);
        item.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        item.Margin = new Padding(0);
        item.Padding = new Padding(10, 2, 18, 2);

        if (item is ToolStripMenuItem menuItem)
        {
            menuItem.DropDown.Renderer = new AppMenuRenderer(dark);
            menuItem.DropDown.BackColor = ThemeMenuBackColor(dark);
            menuItem.DropDown.ForeColor = ThemeTextColor(dark);
            menuItem.DropDown.Padding = new Padding(2, 4, 2, 4);

            if (menuItem.DropDown is ToolStripDropDownMenu dropDownMenu)
            {
                dropDownMenu.ShowImageMargin = false;
            }

            foreach (ToolStripItem child in menuItem.DropDownItems)
            {
                ApplyToolStripItemTheme(child, dark);
            }
        }
    }

    private static Color ThemeBackColor(bool dark) => dark ? Color.FromArgb(22, 25, 29) : Color.FromArgb(232, 236, 240);

    private static Color ThemePanelColor(bool dark) => dark ? Color.FromArgb(30, 35, 40) : Color.FromArgb(246, 248, 250);

    private static Color ThemeTextColor(bool dark) => dark ? Color.FromArgb(239, 244, 248) : Color.FromArgb(24, 29, 34);

    private static Color ThemeMutedTextColor(bool dark) => dark ? Color.FromArgb(166, 179, 190) : Color.FromArgb(82, 93, 104);

    private static Color ThemeDisabledTextColor(bool dark) => dark ? Color.FromArgb(102, 116, 126) : Color.FromArgb(146, 156, 166);

    private static Color ThemeMenuBackColor(bool dark) => dark ? Color.FromArgb(17, 20, 24) : Color.White;

    private static Color ThemeMenuHoverColor(bool dark) => dark ? Color.FromArgb(32, 116, 190) : Color.FromArgb(62, 135, 210);

    private static Color ThemeMenuBorderColor(bool dark) => dark ? Color.FromArgb(54, 61, 68) : Color.FromArgb(190, 200, 210);

    private sealed class AppMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public AppMenuRenderer(bool dark)
        {
            _dark = dark;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(ThemeMenuBackColor(_dark));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(ThemeMenuBorderColor(_dark));
            var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
            bounds.Width -= 1;
            bounds.Height -= 1;
            e.Graphics.DrawRectangle(pen, bounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var color = e.Item.Selected && e.Item.Enabled
                ? ThemeMenuHoverColor(_dark)
                : ThemeMenuBackColor(_dark);
            using var brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item;
            e.TextColor = item.Enabled
                ? item.Selected ? Color.White : ThemeTextColor(_dark)
                : ThemeDisabledTextColor(_dark);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var item = e.Item;
            e.ArrowColor = item is not null && item.Enabled
                ? item.Selected ? Color.White : ThemeTextColor(_dark)
                : ThemeDisabledTextColor(_dark);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(ThemeMenuBorderColor(_dark));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
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

    private void StylePlaylistGrid()
    {
        _playlistGrid.Dock = DockStyle.Fill;
        _playlistGrid.BackgroundColor = Color.FromArgb(13, 16, 19);
        _playlistGrid.GridColor = Color.FromArgb(54, 61, 68);
        _playlistGrid.BorderStyle = BorderStyle.FixedSingle;
        _playlistGrid.AllowUserToAddRows = false;
        _playlistGrid.AllowUserToDeleteRows = false;
        _playlistGrid.AllowUserToResizeRows = false;
        _playlistGrid.ReadOnly = false;
        _playlistGrid.MultiSelect = false;
        _playlistGrid.RowHeadersVisible = false;
        _playlistGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _playlistGrid.AutoGenerateColumns = false;
        _playlistGrid.AllowDrop = true;
        _playlistGrid.ScrollBars = ScrollBars.Both;
        _playlistGrid.EnableHeadersVisualStyles = false;
        _playlistGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _playlistGrid.ColumnHeadersHeight = 28;
        _playlistGrid.RowTemplate.Height = 27;
        _playlistGrid.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _playlistGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 44, 50);
        _playlistGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(236, 241, 244);
        _playlistGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 44, 50);
        _playlistGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(236, 241, 244);
        _playlistGrid.DefaultCellStyle.BackColor = Color.FromArgb(17, 20, 24);
        _playlistGrid.DefaultCellStyle.ForeColor = Color.FromArgb(226, 234, 238);
        _playlistGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 116, 190);
        _playlistGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        _playlistGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(29, 34, 39);

        _playlistGrid.Columns.Clear();
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Number",
            HeaderText = "#",
            Width = 36,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "StartTime",
            HeaderText = "Start Time",
            Width = 108,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "PlayEnabled",
            HeaderText = "Play",
            Width = 48,
            TrueValue = true,
            FalseValue = false,
            ThreeState = false,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Clip",
            HeaderText = "Clip",
            Width = 360,
            MinimumWidth = 240,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Duration",
            HeaderText = "Dur",
            Width = 92,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "LoopEnabled",
            HeaderText = "Loop",
            Width = 52,
            TrueValue = true,
            FalseValue = false,
            ThreeState = false,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        var transitionColumn = new DataGridViewComboBoxColumn
        {
            Name = "Transition",
            HeaderText = "Transition",
            Width = 88,
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        transitionColumn.Items.AddRange(PlaylistTransitionOptions);
        _playlistGrid.Columns.Add(transitionColumn);
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "TransitionDuration",
            HeaderText = "Trans Dur",
            Width = 92,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "EndTime",
            HeaderText = "End Time",
            Width = 108,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _playlistGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FullPath",
            HeaderText = "FullPath",
            Visible = false,
        });
        foreach (DataGridViewColumn column in _playlistGrid.Columns)
        {
            column.ReadOnly = column.Name is not "PlayEnabled" and not "LoopEnabled" and not "Transition";
        }
        foreach (var frozenColumnName in new[] { "Number", "StartTime", "PlayEnabled", "Clip" })
        {
            if (_playlistGrid.Columns[frozenColumnName] is DataGridViewColumn frozenColumn)
            {
                frozenColumn.Frozen = true;
            }
        }

        _playlistGrid.SelectionChanged -= PlaylistGrid_SelectionChanged;
        _playlistGrid.CellDoubleClick -= PlaylistGrid_CellDoubleClick;
        _playlistGrid.KeyDown -= PlaylistGrid_KeyDown;
        _playlistGrid.CellMouseDown -= PlaylistGrid_CellMouseDown;
        _playlistGrid.CellContentClick -= PlaylistGrid_CellContentClick;
        _playlistGrid.CurrentCellDirtyStateChanged -= PlaylistGrid_CurrentCellDirtyStateChanged;
        _playlistGrid.CellValueChanged -= PlaylistGrid_CellValueChanged;
        _playlistGrid.DataError -= PlaylistGrid_DataError;
        _playlistGrid.DragEnter -= PlaylistGrid_DragEnter;
        _playlistGrid.DragOver -= PlaylistGrid_DragOver;
        _playlistGrid.DragDrop -= PlaylistGrid_DragDrop;
        _playlistGrid.MouseDown -= PlaylistGrid_MouseDown;
        _playlistGrid.MouseMove -= PlaylistGrid_MouseMove;
        _playlistGrid.MouseUp -= PlaylistGrid_MouseUp;
        _playlistGrid.SelectionChanged += PlaylistGrid_SelectionChanged;
        _playlistGrid.CellDoubleClick += PlaylistGrid_CellDoubleClick;
        _playlistGrid.KeyDown += PlaylistGrid_KeyDown;
        _playlistGrid.CellMouseDown += PlaylistGrid_CellMouseDown;
        _playlistGrid.CellContentClick += PlaylistGrid_CellContentClick;
        _playlistGrid.CurrentCellDirtyStateChanged += PlaylistGrid_CurrentCellDirtyStateChanged;
        _playlistGrid.CellValueChanged += PlaylistGrid_CellValueChanged;
        _playlistGrid.DataError += PlaylistGrid_DataError;
        _playlistGrid.DragEnter += PlaylistGrid_DragEnter;
        _playlistGrid.DragOver += PlaylistGrid_DragOver;
        _playlistGrid.DragDrop += PlaylistGrid_DragDrop;
        _playlistGrid.MouseDown += PlaylistGrid_MouseDown;
        _playlistGrid.MouseMove += PlaylistGrid_MouseMove;
        _playlistGrid.MouseUp += PlaylistGrid_MouseUp;
        ConfigurePlaylistContextMenu();
        RefreshPlaylistGrid();
    }

    private void ConfigurePlaylistContextMenu()
    {
        _playlistContextMenu.Items.Clear();
        _playlistContextMenu.Opening -= PlaylistContextMenu_Opening;

        RebindMenuClick(_playlistMenuStop, PlaylistMenuStop_Click);
        RebindMenuClick(_playlistMenuPlay, PlaylistMenuPlay_Click);
        RebindMenuClick(_playlistMenuCue, PlaylistMenuCue_Click);
        RebindMenuClick(_playlistMenuPause, PlaylistMenuPause_Click);
        RebindMenuClick(_playlistMenuResume, PlaylistMenuResume_Click);
        RebindMenuClick(_playlistMenuCueNext, PlaylistMenuCueNext_Click);
        RebindMenuClick(_playlistMenuPlayNext, PlaylistMenuPlayNext_Click);
        RebindMenuClick(_playlistMenuDelete, PlaylistMenuDelete_Click);
        RebindMenuClick(_playlistMenuCopy, PlaylistMenuCopy_Click);
        RebindMenuClick(_playlistMenuPaste, PlaylistMenuPaste_Click);
        RebindMenuClick(_playlistMenuMoveUp, PlaylistMenuMoveUp_Click);
        RebindMenuClick(_playlistMenuMoveDown, PlaylistMenuMoveDown_Click);
        RebindMenuClick(_playlistMenuPlayInVlc, PlaylistMenuPlayInVlc_Click);
        RebindMenuClick(_playlistMenuCheckFiles, PlaylistMenuCheckFiles_Click);
        RebindMenuClick(_playlistMenuOpenInTrimmer, PlaylistMenuOpenInTrimmer_Click);
        RebindMenuClick(_playlistMenuFileInfo, PlaylistMenuFileInfo_Click);
        RebindMenuClick(_playlistMenuInsertEnd, PlaylistMenuInsertEnd_Click);
        RebindMenuClick(_playlistMenuSelectAll, PlaylistMenuSelectAll_Click);
        RebindMenuClick(_playlistMenuDeselectAll, PlaylistMenuDeselectAll_Click);
        RebindMenuClick(_playlistMenuSetStartTime, PlaylistMenuSetStartTime_Click);
        RebindMenuClick(_playlistMenuInsertPlaylist, PlaylistMenuInsertPlaylist_Click);
        RebindMenuClick(_playlistMenuPlayInFfplay, PlaylistMenuPlayInFfplay_Click);
        _playlistContextMenu.Opening += PlaylistContextMenu_Opening;

        ConfigurePlaceholderSubmenu(_playlistMenuInsertDecklink, "DeckLink input insert is not implemented yet.");
        ConfigurePlaceholderSubmenu(_playlistMenuShowLiveDecklink, "Live DeckLink preview is not implemented yet.");
        ConfigurePlaceholderSubmenu(_playlistMenuInsertFilter, "Filter insert is not implemented yet.");
        ConfigurePlaylistTransitionSubmenu();
        _playlistMenuInsertBlank.Enabled = false;
        _playlistMenuRefreshThumbnail.Enabled = false;

        _playlistContextMenu.Items.AddRange(
            [
                _playlistMenuStop,
                _playlistMenuPlay,
                _playlistMenuCue,
                _playlistMenuPause,
                _playlistMenuResume,
                _playlistMenuCueNext,
                _playlistMenuPlayNext,
                _playlistMenuInsertBlank,
                _playlistMenuDelete,
                _playlistMenuCopy,
                _playlistMenuPaste,
                _playlistMenuMoveUp,
                _playlistMenuMoveDown,
                _playlistMenuPlayInVlc,
                _playlistMenuCheckFiles,
                _playlistMenuOpenInTrimmer,
                _playlistMenuFileInfo,
                _playlistMenuInsertDecklink,
                _playlistMenuShowLiveDecklink,
                _playlistMenuRefreshThumbnail,
                _playlistMenuInsertEnd,
                _playlistMenuSelectAll,
                _playlistMenuDeselectAll,
                _playlistMenuSetStartTime,
                _playlistMenuInsertPlaylist,
                _playlistMenuInsertFilter,
                _playlistMenuChangeAllTransition,
                _playlistMenuPlayInFfplay,
            ]);
        _playlistGrid.ContextMenuStrip = _playlistContextMenu;
        ApplyContextMenuTheme(_playlistContextMenu, _darkMode);
    }

    private void ConfigurePlaylistTransitionSubmenu()
    {
        _playlistMenuChangeAllTransition.DropDownItems.Clear();
        AddPlaylistTransitionMenuItem("Cut", PlaylistTransitionCut, DefaultTransitionDuration);
        AddPlaylistTransitionMenuItem("Mix 1 sec", PlaylistTransitionMix, TimeSpan.FromSeconds(1));
        AddPlaylistTransitionMenuItem("Push 1 sec", PlaylistTransitionPush, TimeSpan.FromSeconds(1));
        AddPlaylistTransitionMenuItem("Wipe 1 sec", PlaylistTransitionWipe, TimeSpan.FromSeconds(1));
        AddPlaylistTransitionMenuItem("Slide 1 sec", PlaylistTransitionSlide, TimeSpan.FromSeconds(1));
        AddPlaylistTransitionMenuItem("Fade Through Black 0.5 sec", PlaylistTransitionFadeBlack, TimeSpan.FromMilliseconds(500));
        AddPlaylistTransitionMenuItem("Fade Through Black 1 sec", PlaylistTransitionFadeBlack, TimeSpan.FromSeconds(1));
        AddPlaylistTransitionMenuItem("Fade Through Black 2 sec", PlaylistTransitionFadeBlack, TimeSpan.FromSeconds(2));
    }

    private void AddPlaylistTransitionMenuItem(string text, string transition, TimeSpan duration)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => SetAllPlaylistTransitions(transition, duration);
        _playlistMenuChangeAllTransition.DropDownItems.Add(item);
    }

    private static void RebindMenuClick(ToolStripMenuItem item, EventHandler handler)
    {
        item.Click -= handler;
        item.Click += handler;
    }

    private static void ConfigurePlaceholderSubmenu(ToolStripMenuItem item, string text)
    {
        item.DropDownItems.Clear();
        item.DropDownItems.Add(new ToolStripMenuItem(text) { Enabled = false });
    }

    private void PlaylistGrid_SelectionChanged(object? sender, EventArgs e)
    {
        UpdatePlaylistButtons();
    }

    private void PlaylistContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selectedIndex = GetSelectedPlaylistIndex();
        var selectedCueable = selectedIndex.HasValue && IsPlaylistRowCueable(selectedIndex.Value);
        var hasNext = FindRelativePlaylistIndex(1, selectedIndex).HasValue;
        var hasSelection = selectedIndex.HasValue &&
            selectedIndex.Value >= 0 &&
            selectedIndex.Value < _playlistItems.Count;
        var selectedItem = hasSelection ? _playlistItems[selectedIndex!.Value] : null;
        var selectedFileExists = selectedItem is not null && !selectedItem.IsEndMarker && File.Exists(selectedItem.FullPath);
        var selectedCanSetStart = selectedIndex.HasValue && CanSetPlaylistStartTime(selectedIndex.Value);
        var manualPlayAllowed = !_playlistPlaybackActive;

        _playlistMenuStop.Enabled = _isPlaying || _scrubPreviewMode || _nativeSeekPreviewMode;
        _playlistMenuPlay.Enabled = manualPlayAllowed && selectedCueable;
        _playlistMenuCue.Enabled = selectedCueable;
        _playlistMenuPause.Enabled = _isPlaying && !_isPaused;
        _playlistMenuResume.Enabled = _isPlaying && _isPaused;
        _playlistMenuCueNext.Enabled = hasNext;
        _playlistMenuPlayNext.Enabled = manualPlayAllowed && hasNext;
        _playlistMenuDelete.Enabled = hasSelection;
        _playlistMenuCopy.Enabled = hasSelection;
        _playlistMenuPaste.Enabled = CanPastePlaylistItem();
        _playlistMenuMoveUp.Enabled = selectedIndex.HasValue && selectedIndex.Value > 0;
        _playlistMenuMoveDown.Enabled = selectedIndex.HasValue && selectedIndex.Value < _playlistItems.Count - 1;
        _playlistMenuPlayInVlc.Enabled = selectedFileExists;
        _playlistMenuCheckFiles.Enabled = _playlistItems.Count > 0;
        _playlistMenuOpenInTrimmer.Enabled = selectedFileExists;
        _playlistMenuFileInfo.Enabled = selectedFileExists;
        _playlistMenuInsertEnd.Enabled = true;
        _playlistMenuSelectAll.Enabled = _playlistItems.Count > 0;
        _playlistMenuDeselectAll.Enabled = _playlistItems.Count > 0;
        _playlistMenuSetStartTime.Enabled = selectedCanSetStart;
        _playlistMenuInsertPlaylist.Enabled = !_isPlaying && !_playlistPlayingIndex.HasValue && !_playlistPlaybackActive;
        _playlistMenuChangeAllTransition.Enabled = _playlistItems.Any(item => !item.IsEndMarker);
        _playlistMenuPlayInFfplay.Enabled = selectedFileExists;
    }

    private void PlaylistMenuStop_Click(object? sender, EventArgs e)
    {
        StopPlayback();
    }

    private async void PlaylistMenuPlay_Click(object? sender, EventArgs e)
    {
        await PlaySelectedPlaylistItemAsync();
    }

    private void PlaylistMenuPause_Click(object? sender, EventArgs e)
    {
        PausePlayback();
    }

    private void PlaylistMenuResume_Click(object? sender, EventArgs e)
    {
        ResumePlayback();
    }

    private void PlaylistMenuCue_Click(object? sender, EventArgs e)
    {
        CueSelectedPlaylistItem();
    }

    private void PlaylistMenuCueNext_Click(object? sender, EventArgs e)
    {
        CueRelativePlaylistItem(1, GetSelectedPlaylistIndex());
    }

    private async void PlaylistMenuPlayNext_Click(object? sender, EventArgs e)
    {
        await PlayRelativePlaylistItemAsync(1, GetSelectedPlaylistIndex());
    }

    private void PlaylistMenuCuePrevious_Click(object? sender, EventArgs e)
    {
        CueRelativePlaylistItem(-1, GetSelectedPlaylistIndex());
    }

    private async void PlaylistMenuPlayPrevious_Click(object? sender, EventArgs e)
    {
        await PlayRelativePlaylistItemAsync(-1, GetSelectedPlaylistIndex());
    }

    private void PlaylistMenuDelete_Click(object? sender, EventArgs e)
    {
        RemoveSelectedPlaylistItem();
    }

    private void PlaylistMenuCopy_Click(object? sender, EventArgs e)
    {
        CopySelectedPlaylistItem();
    }

    private void PlaylistMenuPaste_Click(object? sender, EventArgs e)
    {
        PastePlaylistItem();
    }

    private void PlaylistMenuMoveUp_Click(object? sender, EventArgs e)
    {
        MoveSelectedPlaylistItemUp();
    }

    private void PlaylistMenuMoveDown_Click(object? sender, EventArgs e)
    {
        MoveSelectedPlaylistItemDown();
    }

    private void PlaylistMenuPlayInVlc_Click(object? sender, EventArgs e)
    {
        PlaySelectedPlaylistItemInExternalPlayer("vlc");
    }

    private void PlaylistMenuCheckFiles_Click(object? sender, EventArgs e)
    {
        CheckPlaylistFiles();
    }

    private void PlaylistMenuOpenInTrimmer_Click(object? sender, EventArgs e)
    {
        OpenSelectedPlaylistItemInTrimmer();
    }

    private void PlaylistMenuFileInfo_Click(object? sender, EventArgs e)
    {
        var selectedIndex = GetSelectedPlaylistIndex();
        if (!selectedIndex.HasValue ||
            selectedIndex.Value < 0 ||
            selectedIndex.Value >= _playlistItems.Count)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        if (_playlistItems[selectedIndex.Value].IsEndMarker)
        {
            SetStatus("END marker has no media information", Color.FromArgb(232, 181, 105));
            return;
        }

        OpenMediaInfoForm(_playlistItems[selectedIndex.Value].FullPath);
    }

    private void PlaylistMenuInsertEnd_Click(object? sender, EventArgs e)
    {
        InsertEndMarker();
    }

    private void PlaylistMenuSetStartTime_Click(object? sender, EventArgs e)
    {
        SetSelectedPlaylistStartTime();
    }

    private void PlaylistMenuInsertPlaylist_Click(object? sender, EventArgs e)
    {
        InsertPlaylistFromFileDialog();
    }

    private void PlaylistMenuSelectAll_Click(object? sender, EventArgs e)
    {
        SetPlaylistPlayEnabledForAll(true);
    }

    private void PlaylistMenuDeselectAll_Click(object? sender, EventArgs e)
    {
        SetPlaylistPlayEnabledForAll(false);
    }

    private void SetAllPlaylistTransitions(string transition, TimeSpan duration)
    {
        var changed = 0;
        var normalizedTransition = NormalizePlaylistTransition(transition);
        var normalizedDuration = duration > TimeSpan.Zero ? duration : DefaultTransitionDuration;
        foreach (var item in _playlistItems)
        {
            if (item.IsEndMarker)
            {
                continue;
            }

            item.Transition = normalizedTransition;
            item.TransitionDuration = normalizedDuration;
            changed++;
        }

        RefreshPlaylistGrid(GetSelectedPlaylistIndex());
        SetStatus(
            changed == 0
                ? "No playlist rows to change"
                : normalizedTransition == PlaylistTransitionCut
                    ? $"Changed {changed} row(s) to Cut"
                    : $"Changed {changed} row(s) to Fade Black {FormatPlaylistTime(normalizedDuration)}",
            changed == 0 ? Color.FromArgb(232, 181, 105) : Color.FromArgb(130, 210, 164));
    }

    private void PlaylistMenuPlayInFfplay_Click(object? sender, EventArgs e)
    {
        PlaySelectedPlaylistItemInExternalPlayer("ffplay");
    }

    private void PlaylistGrid_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right ||
            e.RowIndex < 0 ||
            e.RowIndex >= _playlistGrid.Rows.Count)
        {
            return;
        }

        var columnIndex = e.ColumnIndex >= 0 ? e.ColumnIndex : GetFirstVisiblePlaylistColumnIndex();
        if (columnIndex < 0)
        {
            return;
        }

        _playlistGrid.CurrentCell = _playlistGrid.Rows[e.RowIndex].Cells[columnIndex];
        _playlistGrid.ClearSelection();
        _playlistGrid.Rows[e.RowIndex].Selected = true;
    }

    private void PlaylistGrid_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDraggedPlaylistRowIndex(e.Data, out _)
            ? DragDropEffects.Move
            : TryGetDraggedMediaPath(e.Data, out _)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
    }

    private void PlaylistGrid_DragOver(object? sender, DragEventArgs e)
    {
        e.Effect = TryGetDraggedPlaylistRowIndex(e.Data, out _)
            ? DragDropEffects.Move
            : TryGetDraggedMediaPath(e.Data, out _)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        if (e.Effect != DragDropEffects.None)
        {
            SelectPlaylistDropRow(e);
        }
    }

    private void PlaylistGrid_DragDrop(object? sender, DragEventArgs e)
    {
        if (TryGetDraggedPlaylistRowIndex(e.Data, out var sourceIndex))
        {
            var playlistInsertIndex = GetPlaylistDropInsertIndex(e);
            MovePlaylistItemToInsertIndex(sourceIndex, playlistInsertIndex);
            return;
        }

        if (!TryGetDraggedMediaPath(e.Data, out var path))
        {
            SetStatus("Drop a supported media file", Color.FromArgb(232, 181, 105));
            return;
        }

        var insertIndex = GetPlaylistDropInsertIndex(e);
        AddMediaPathToPlaylist(path, insertIndex);
    }

    private void PlaylistGrid_MouseDown(object? sender, MouseEventArgs e)
    {
        _playlistDragRowIndex = null;
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = _playlistGrid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0 || hit.RowIndex >= _playlistItems.Count)
        {
            return;
        }

        var columnIndex = hit.ColumnIndex >= 0 ? hit.ColumnIndex : GetFirstVisiblePlaylistColumnIndex();
        if (columnIndex < 0)
        {
            return;
        }

        _playlistGrid.CurrentCell = _playlistGrid.Rows[hit.RowIndex].Cells[columnIndex];
        _playlistGrid.ClearSelection();
        _playlistGrid.Rows[hit.RowIndex].Selected = true;

        var column = _playlistGrid.Columns[columnIndex];
        if (column is DataGridViewCheckBoxColumn or DataGridViewComboBoxColumn)
        {
            return;
        }

        _playlistDragRowIndex = hit.RowIndex;
        _playlistDragStart = e.Location;
    }

    private void PlaylistGrid_MouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_playlistDragRowIndex.HasValue)
        {
            return;
        }

        var dragSize = SystemInformation.DragSize;
        var dragRectangle = new Rectangle(
            _playlistDragStart.X - dragSize.Width / 2,
            _playlistDragStart.Y - dragSize.Height / 2,
            dragSize.Width,
            dragSize.Height);
        if (dragRectangle.Contains(e.Location))
        {
            return;
        }

        var sourceIndex = _playlistDragRowIndex.Value;
        _playlistDragRowIndex = null;
        var data = new DataObject();
        data.SetData(PlaylistDragDataFormat, sourceIndex);
        _playlistGrid.DoDragDrop(data, DragDropEffects.Move);
    }

    private void PlaylistGrid_MouseUp(object? sender, MouseEventArgs e)
    {
        _playlistDragRowIndex = null;
    }

    private void SelectPlaylistDropRow(DragEventArgs e)
    {
        var point = _playlistGrid.PointToClient(new Point(e.X, e.Y));
        var hit = _playlistGrid.HitTest(point.X, point.Y);
        if (hit.RowIndex < 0 || hit.RowIndex >= _playlistGrid.Rows.Count)
        {
            return;
        }

        var columnIndex = hit.ColumnIndex >= 0 ? hit.ColumnIndex : GetFirstVisiblePlaylistColumnIndex();
        if (columnIndex < 0)
        {
            return;
        }

        _playlistGrid.CurrentCell = _playlistGrid.Rows[hit.RowIndex].Cells[columnIndex];
        _playlistGrid.ClearSelection();
        _playlistGrid.Rows[hit.RowIndex].Selected = true;
    }

    private int GetPlaylistDropInsertIndex(DragEventArgs e)
    {
        var point = _playlistGrid.PointToClient(new Point(e.X, e.Y));
        var hit = _playlistGrid.HitTest(point.X, point.Y);
        if (hit.RowIndex < 0 || hit.RowIndex >= _playlistItems.Count)
        {
            return _playlistItems.Count;
        }

        var rowBounds = _playlistGrid.GetRowDisplayRectangle(hit.RowIndex, cutOverflow: false);
        return point.Y < rowBounds.Top + rowBounds.Height / 2
            ? hit.RowIndex
            : hit.RowIndex + 1;
    }

    private static bool TryGetDraggedPlaylistRowIndex(IDataObject? data, out int rowIndex)
    {
        rowIndex = -1;
        if (data is null ||
            !data.GetDataPresent(PlaylistDragDataFormat) ||
            data.GetData(PlaylistDragDataFormat) is not int draggedIndex ||
            draggedIndex < 0)
        {
            return false;
        }

        rowIndex = draggedIndex;
        return true;
    }

    private static bool TryGetDraggedMediaPath(IDataObject? data, out string path)
    {
        path = string.Empty;
        if (data is null)
        {
            return false;
        }

        if (data.GetDataPresent(MediaGridDragDataFormat) &&
            data.GetData(MediaGridDragDataFormat) is string customPath &&
            IsSupportedDroppedMediaPath(customPath))
        {
            path = customPath;
            return true;
        }

        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var mediaPath = files.FirstOrDefault(IsSupportedDroppedMediaPath);
            if (mediaPath is not null)
            {
                path = mediaPath;
                return true;
            }
        }

        if (data.GetDataPresent(DataFormats.Text) &&
            data.GetData(DataFormats.Text) is string text &&
            IsSupportedDroppedMediaPath(text.Trim()))
        {
            path = text.Trim();
            return true;
        }

        return false;
    }

    private static bool IsSupportedDroppedMediaPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            File.Exists(path) &&
            IsSupportedMediaFile(path);
    }

    private int GetFirstVisiblePlaylistColumnIndex()
    {
        foreach (DataGridViewColumn column in _playlistGrid.Columns)
        {
            if (column.Visible)
            {
                return column.Index;
            }
        }

        return -1;
    }

    private void PlaylistGrid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.ColumnIndex < 0 ||
            e.RowIndex >= _playlistItems.Count ||
            _playlistGrid.Columns[e.ColumnIndex] is not DataGridViewCheckBoxColumn)
        {
            return;
        }

        BeginInvoke(new Action(() =>
        {
            if (e.RowIndex >= _playlistGrid.Rows.Count || e.ColumnIndex >= _playlistGrid.Columns.Count)
            {
                return;
            }

            _playlistGrid.CurrentCell = _playlistGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            _playlistGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            _playlistGrid.EndEdit();
        }));
    }

    private void PlaylistGrid_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_playlistGrid.IsCurrentCellDirty &&
            _playlistGrid.CurrentCell?.OwningColumn is DataGridViewCheckBoxColumn or DataGridViewComboBoxColumn)
        {
            _playlistGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void PlaylistGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 ||
            e.RowIndex >= _playlistItems.Count ||
            e.ColumnIndex < 0 ||
            e.ColumnIndex >= _playlistGrid.Columns.Count)
        {
            return;
        }

        var columnName = _playlistGrid.Columns[e.ColumnIndex].Name;
        if (columnName is not "PlayEnabled" and not "LoopEnabled" and not "Transition")
        {
            return;
        }

        var item = _playlistItems[e.RowIndex];
        var value = _playlistGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is bool checkedValue && checkedValue;
        if (item.IsEndMarker)
        {
            item.PlayEnabled = false;
            item.LoopEnabled = false;
            item.Transition = PlaylistTransitionCut;
            item.TransitionDuration = DefaultTransitionDuration;
            item.Status = PlaylistStatusEnd;
            RefreshPlaylistGrid(e.RowIndex);
            return;
        }

        if (columnName == "Transition")
        {
            var transitionText = _playlistGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
            var transition = NormalizePlaylistTransition(transitionText);
            item.Transition = transition;
            if (transition == PlaylistTransitionCut)
            {
                item.TransitionDuration = DefaultTransitionDuration;
            }
            else if (item.TransitionDuration <= TimeSpan.Zero)
            {
                item.TransitionDuration = DefaultTransitionDuration;
            }

            RefreshPlaylistGrid(e.RowIndex);
            SetStatus($"Transition set to {transition}", Color.FromArgb(130, 210, 164));
            return;
        }

        if (columnName == "PlayEnabled")
        {
            if (_playlistPlayingIndex == e.RowIndex && !value)
            {
                item.PlayEnabled = true;
                RefreshPlaylistGrid(e.RowIndex);
                SetStatus("Stop playback before disabling the playing row", Color.FromArgb(232, 181, 105));
                return;
            }

            item.PlayEnabled = value;
            if (!value && item.Status == PlaylistStatusNext)
            {
                item.Status = PlaylistStatusReady;
            }

            RefreshPlaylistGrid(e.RowIndex);
            SetStatus(value ? "Playlist row enabled" : "Playlist row disabled", Color.FromArgb(130, 210, 164));
            return;
        }

        item.LoopEnabled = value;
        SetStatus(value ? "Playlist row loop enabled" : "Playlist row loop disabled", Color.FromArgb(130, 210, 164));
    }

    private static void PlaylistGrid_DataError(object? sender, DataGridViewDataErrorEventArgs e)
    {
        e.ThrowException = false;
    }

    private async void PlaylistGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 &&
            e.ColumnIndex >= 0 &&
            _playlistGrid.Columns[e.ColumnIndex] is not DataGridViewCheckBoxColumn and not DataGridViewComboBoxColumn)
        {
            await PlaySelectedPlaylistItemAsync();
        }
    }

    private async void PlaylistGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            await PlaySelectedPlaylistItemAsync();
        }
        else if (e.KeyCode == Keys.Delete)
        {
            e.Handled = true;
            RemoveSelectedPlaylistItem();
        }
    }

    private void AddSelectedMediaToPlaylist()
    {
        var path = GetSelectedMediaGridPath();
        if (path is null)
        {
            path = _inputPathBox.Text.Trim();
        }

        AddMediaPathToPlaylist(path);
    }

    private void AddMediaPathToPlaylist(string? path, int? insertIndex = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsSupportedMediaFile(path))
        {
            SetStatus("Choose a media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        var item = CreatePlaylistItemFromMediaPath(path);
        AddPlaylistItem(item, $"Added {Path.GetFileName(path)} to playlist", insertIndex);
    }

    private PlaylistItem CreatePlaylistItemFromMediaPath(string path)
    {
        var duration = GetKnownMediaDuration(path);
        var isStill = IsImageFile(path);
        return new PlaylistItem(path)
        {
            SourceDuration = isStill ? DefaultStillDuration : duration,
            TcIn = TimeSpan.Zero,
            TcOut = isStill ? DefaultStillDuration : duration,
            Status = PlaylistStatusReady,
        };
    }

    private void AddPlaylistItem(PlaylistItem item, string statusMessage, int? explicitInsertIndex = null)
    {
        PreparePlaylistItemForInsert(item);
        var targetIndex = ResolvePlaylistInsertIndex(explicitInsertIndex);
        _playlistItems.Insert(targetIndex, item);
        UpdateRunningPlaylistTimelineAfterInsert(targetIndex, insertedCount: 1);
        RefreshPlaylistGrid(targetIndex);

        SetStatus(statusMessage, Color.FromArgb(130, 210, 164));
    }

    private int ResolvePlaylistInsertIndex(int? explicitInsertIndex = null)
    {
        var insertIndex = explicitInsertIndex ?? GetSelectedPlaylistIndex();
        if (!insertIndex.HasValue ||
            insertIndex.Value < 0 ||
            insertIndex.Value >= _playlistItems.Count)
        {
            return _playlistItems.Count;
        }

        return explicitInsertIndex.HasValue
            ? insertIndex.Value
            : insertIndex.Value + 1;
    }

    private void UpdateRunningPlaylistTimelineAfterInsert(int insertIndex, int insertedCount)
    {
        if (insertedCount <= 0)
        {
            return;
        }

        if (_playlistTransitionLeadInIndex.HasValue && insertIndex <= _playlistTransitionLeadInIndex.Value)
        {
            _playlistTransitionLeadInIndex += insertedCount;
        }

        if (!_playlistPlayingIndex.HasValue)
        {
            return;
        }

        if (insertIndex <= _playlistPlayingIndex.Value)
        {
            _playlistPlayingIndex += insertedCount;
            return;
        }

        if (!_playlistPlaybackActive)
        {
            return;
        }

        var playingIndex = _playlistPlayingIndex.Value;
        if (playingIndex < 0 || playingIndex >= _playlistItems.Count)
        {
            return;
        }

        _playlistItems[playingIndex].TimelineStartOverride ??= DateTime.Now.TimeOfDay;
        ClearFollowingTimelineStartOverridesUntilEnd(playingIndex);
    }

    private static void PreparePlaylistItemForInsert(PlaylistItem item)
    {
        if (!item.IsEndMarker)
        {
            return;
        }

        item.PlayEnabled = false;
        item.LoopEnabled = false;
        item.Transition = PlaylistTransitionCut;
        item.TransitionDuration = DefaultTransitionDuration;
        item.Status = PlaylistStatusEnd;
    }

    private void InsertEndMarker()
    {
        AddPlaylistItem(
            new PlaylistItem(PlaylistEndMarkerText)
            {
                PlayEnabled = false,
                LoopEnabled = false,
                Transition = PlaylistTransitionCut,
                TransitionDuration = DefaultTransitionDuration,
                Status = PlaylistStatusEnd,
            },
            "Inserted END marker");
    }

    private void OpenPlaylistFromFileDialog()
    {
        if (_isPlaying || _playlistPlayingIndex.HasValue || _playlistPlaybackActive)
        {
            SetStatus("Stop playback before opening playlist", Color.FromArgb(232, 181, 105));
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Open Playlist",
            Filter = PlaylistFileFilter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetPlaylistDialogInitialDirectory(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        OpenPlaylistFile(dialog.FileName);
    }

    private void SavePlaylistToFileDialog()
    {
        if (_playlistItems.Count == 0)
        {
            SetStatus("Playlist is empty", Color.FromArgb(232, 181, 105));
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save Playlist",
            Filter = PlaylistFileFilter,
            AddExtension = true,
            DefaultExt = "dpl",
            OverwritePrompt = true,
            InitialDirectory = GetPlaylistDialogInitialDirectory(),
            FileName = _currentPlaylistPath is not null
                ? Path.GetFileName(_currentPlaylistPath)
                : $"playlist_{DateTime.Now:yyyyMMdd_HHmmss}.dpl",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SavePlaylistFile(dialog.FileName);
    }

    private void InsertPlaylistFromFileDialog()
    {
        if (_isPlaying || _playlistPlayingIndex.HasValue || _playlistPlaybackActive)
        {
            SetStatus("Stop playback before inserting playlist", Color.FromArgb(232, 181, 105));
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Insert Playlist",
            Filter = PlaylistFileFilter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetPlaylistDialogInitialDirectory(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        InsertPlaylistFile(dialog.FileName);
    }

    private void OpenPlaylistFile(string path)
    {
        try
        {
            var file = LoadPlaylistFile(path, out var items, out var skipped);
            if (file is null)
            {
                SetStatus("Playlist file is empty", Color.FromArgb(232, 181, 105));
                return;
            }

            _playlistItems.Clear();
            _playlistItems.AddRange(items);
            _playlistPlayingIndex = null;
            _playlistPlaybackActive = false;
            _currentPlaylistPath = path;
            RefreshPlaylistGrid(_playlistItems.Count > 0 ? 0 : null);

            var message = skipped == 0
                ? $"Opened playlist {Path.GetFileName(path)}"
                : $"Opened playlist, skipped {skipped} invalid row(s)";
            SetStatus(message, skipped == 0 ? Color.FromArgb(130, 210, 164) : Color.FromArgb(232, 181, 105));
        }
        catch (JsonException ex)
        {
            AppendLog($"Playlist open failed: {ex.Message}");
            SetStatus("Playlist file is not valid", Color.FromArgb(229, 113, 105));
        }
        catch (Exception ex)
        {
            AppendLog($"Playlist open failed: {ex.Message}");
            SetStatus("Playlist open failed", Color.FromArgb(229, 113, 105));
        }
    }

    private void InsertPlaylistFile(string path)
    {
        try
        {
            var file = LoadPlaylistFile(path, out var items, out var skipped);
            if (file is null)
            {
                SetStatus("Playlist file is empty", Color.FromArgb(232, 181, 105));
                return;
            }

            if (items.Count == 0)
            {
                SetStatus("Playlist has no insertable rows", Color.FromArgb(232, 181, 105));
                return;
            }

            var selectedIndex = GetSelectedPlaylistIndex();
            var insertIndex = selectedIndex.HasValue &&
                selectedIndex.Value >= 0 &&
                selectedIndex.Value < _playlistItems.Count
                    ? selectedIndex.Value + 1
                    : _playlistItems.Count;

            _playlistItems.InsertRange(insertIndex, items);
            RefreshPlaylistGrid(insertIndex);

            var message = skipped == 0
                ? $"Inserted {items.Count} playlist row(s)"
                : $"Inserted {items.Count} row(s), skipped {skipped}";
            SetStatus(message, skipped == 0 ? Color.FromArgb(130, 210, 164) : Color.FromArgb(232, 181, 105));
        }
        catch (JsonException ex)
        {
            AppendLog($"Playlist insert failed: {ex.Message}");
            SetStatus("Playlist file is not valid", Color.FromArgb(229, 113, 105));
        }
        catch (Exception ex)
        {
            AppendLog($"Playlist insert failed: {ex.Message}");
            SetStatus("Playlist insert failed", Color.FromArgb(229, 113, 105));
        }
    }

    private void SavePlaylistFile(string path)
    {
        try
        {
            var file = new PlaylistFile
            {
                Version = 1,
                SavedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                MediaRootPath = _mediaRootPath,
                AutoRepeat = true,
                Items = _playlistItems.Select(CreatePlaylistFileItem).ToList(),
            };

            var json = JsonSerializer.Serialize(file, PlaylistJsonOptions);
            File.WriteAllText(path, json);
            _currentPlaylistPath = path;
            SetStatus($"Saved playlist {Path.GetFileName(path)}", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex)
        {
            AppendLog($"Playlist save failed: {ex.Message}");
            SetStatus("Playlist save failed", Color.FromArgb(229, 113, 105));
        }
    }

    private string GetPlaylistDialogInitialDirectory()
    {
        if (_currentPlaylistPath is not null)
        {
            var currentFolder = Path.GetDirectoryName(_currentPlaylistPath);
            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                return currentFolder;
            }
        }

        return Directory.Exists(_mediaRootPath)
            ? _mediaRootPath
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static PlaylistFile? LoadPlaylistFile(string path, out List<PlaylistItem> items, out int skipped)
    {
        items = [];
        skipped = 0;
        var text = File.ReadAllText(path);
        var file = JsonSerializer.Deserialize<PlaylistFile>(text, PlaylistJsonOptions);
        if (file is null)
        {
            return null;
        }

        foreach (var fileItem in file.Items ?? [])
        {
            var item = CreatePlaylistItemFromFileItem(fileItem);
            if (item is null)
            {
                skipped++;
                continue;
            }

            PreparePlaylistItemForInsert(item);
            items.Add(item);
        }

        return file;
    }

    private static PlaylistFileItem CreatePlaylistFileItem(PlaylistItem item)
    {
        return new PlaylistFileItem
        {
            IsEnd = item.IsEndMarker,
            Path = item.IsEndMarker ? PlaylistEndMarkerText : item.FullPath,
            TcIn = FormatPlaylistFileTime(item.TcIn),
            TcOut = FormatPlaylistFileTime(item.TcOut),
            SourceDuration = FormatPlaylistFileTime(item.SourceDuration),
            TimelineStart = FormatPlaylistFileTime(item.TimelineStartOverride),
            Transition = item.Transition,
            TransitionDuration = FormatPlaylistFileTime(item.TransitionDuration),
            PlayEnabled = item.PlayEnabled,
            LoopEnabled = item.LoopEnabled,
        };
    }

    private static PlaylistItem? CreatePlaylistItemFromFileItem(PlaylistFileItem fileItem)
    {
        if (fileItem.IsEnd || IsPlaylistEndMarkerText(fileItem.Path))
        {
            return new PlaylistItem(PlaylistEndMarkerText)
            {
                PlayEnabled = false,
                LoopEnabled = false,
                Transition = PlaylistTransitionCut,
                TransitionDuration = DefaultTransitionDuration,
                Status = PlaylistStatusEnd,
            };
        }

        var path = fileItem.Path.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var tcIn = TryParsePlaylistFileTime(fileItem.TcIn, out var parsedTcIn)
            ? parsedTcIn
            : TimeSpan.Zero;
        var tcOut = TryParsePlaylistFileTime(fileItem.TcOut, out var parsedTcOut)
            ? parsedTcOut
            : (TimeSpan?)null;
        var sourceDuration = TryParsePlaylistFileTime(fileItem.SourceDuration, out var parsedSourceDuration)
            ? parsedSourceDuration
            : (TimeSpan?)null;
        var timelineStart = TryParsePlaylistFileTime(fileItem.TimelineStart, out var parsedTimelineStart)
            ? parsedTimelineStart
            : (TimeSpan?)null;
        var transitionDuration = TryParsePlaylistFileTime(fileItem.TransitionDuration, out var parsedTransitionDuration)
            ? parsedTransitionDuration
            : DefaultTransitionDuration;

        return new PlaylistItem(path)
        {
            TcIn = tcIn,
            TcOut = tcOut,
            SourceDuration = sourceDuration,
            TimelineStartOverride = timelineStart,
            Transition = NormalizePlaylistTransition(fileItem.Transition),
            TransitionDuration = transitionDuration > TimeSpan.Zero ? transitionDuration : DefaultTransitionDuration,
            PlayEnabled = fileItem.PlayEnabled,
            LoopEnabled = fileItem.LoopEnabled,
            Status = File.Exists(path) ? PlaylistStatusReady : PlaylistStatusMissing,
        };
    }

    private static string FormatPlaylistFileTime(TimeSpan value)
    {
        return value.ToString("c", CultureInfo.InvariantCulture);
    }

    private static string? FormatPlaylistFileTime(TimeSpan? value)
    {
        return value.HasValue ? FormatPlaylistFileTime(value.Value) : null;
    }

    private static bool TryParsePlaylistFileTime(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value) ||
            TryParseGridDuration(text, 25, out value);
    }

    private static string NormalizePlaylistTransition(string? transition)
    {
        if (string.IsNullOrWhiteSpace(transition))
        {
            return PlaylistTransitionCut;
        }

        var value = transition.Trim();
        if (value.Equals(PlaylistTransitionCut, StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistTransitionCut;
        }

        if (value.Equals(PlaylistTransitionMix, StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mix", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistTransitionMix;
        }

        if (value.Equals(PlaylistTransitionPush, StringComparison.OrdinalIgnoreCase) ||
            value.Contains("push", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistTransitionPush;
        }

        if (value.Equals(PlaylistTransitionWipe, StringComparison.OrdinalIgnoreCase) ||
            value.Contains("wipe", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistTransitionWipe;
        }

        if (value.Equals(PlaylistTransitionSlide, StringComparison.OrdinalIgnoreCase) ||
            value.Contains("slide", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistTransitionSlide;
        }

        return value.Contains("fade", StringComparison.OrdinalIgnoreCase)
            ? PlaylistTransitionFadeBlack
            : PlaylistTransitionCut;
    }

    private static bool IsTimedPlaylistTransition(string? transition)
    {
        return NormalizePlaylistTransition(transition) != PlaylistTransitionCut;
    }

    private void SetSelectedPlaylistStartTime()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || !CanSetPlaylistStartTime(index.Value))
        {
            SetStatus("Choose a playable playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        var defaultStart = TryGetPlaylistStartTime(index.Value, out var currentStart)
            ? currentStart
            : TimeSpan.Zero;
        var text = ShowPlaylistStartTimeDialog(defaultStart);
        if (text is null)
        {
            return;
        }

        if (!TryParseDisplayDuration(text, out var startTime))
        {
            SetStatus("Use start time as HH:MM:SS or HH:MM:SS:FF", Color.FromArgb(232, 181, 105));
            return;
        }

        _playlistItems[index.Value].TimelineStartOverride = startTime;
        ClearFollowingTimelineStartOverridesUntilEnd(index.Value);
        RefreshPlaylistGrid(index.Value);
        SetStatus($"Row {index.Value + 1} start set to {FormatGridDuration(startTime)}", Color.FromArgb(130, 210, 164));
    }

    private bool CanSetPlaylistStartTime(int index)
    {
        return index >= 0 &&
            index < _playlistItems.Count &&
            !_playlistItems[index].IsEndMarker &&
            _playlistItems[index].PlayEnabled &&
            File.Exists(_playlistItems[index].FullPath);
    }

    private void ClearFollowingTimelineStartOverridesUntilEnd(int startIndex)
    {
        for (var i = startIndex + 1; i < _playlistItems.Count; i++)
        {
            if (_playlistItems[i].IsEndMarker)
            {
                break;
            }

            _playlistItems[i].TimelineStartOverride = null;
        }
    }

    private string? ShowPlaylistStartTimeDialog(TimeSpan defaultStart)
    {
        using var dialog = new Form
        {
            Text = "Set Start Time",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(284, 128),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            BackColor = ThemePanelColor(_darkMode),
            ForeColor = ThemeTextColor(_darkMode),
        };

        var label = new Label
        {
            Text = "Start Time",
            AutoSize = false,
            Location = new Point(14, 14),
            Size = new Size(256, 20),
            ForeColor = ThemeMutedTextColor(_darkMode),
            BackColor = dialog.BackColor,
        };
        var input = new TextBox
        {
            Text = FormatGridDuration(defaultStart),
            Location = new Point(14, 40),
            Size = new Size(256, 24),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _darkMode ? Color.FromArgb(20, 24, 28) : Color.White,
            ForeColor = ThemeTextColor(_darkMode),
        };
        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(108, 82),
            Size = new Size(76, 28),
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(194, 82),
            Size = new Size(76, 28),
        };

        StyleButton(okButton, Color.FromArgb(39, 125, 87));
        StyleButton(cancelButton, Color.FromArgb(52, 67, 82));
        ApplyButtonTheme(okButton, _darkMode);
        ApplyButtonTheme(cancelButton, _darkMode);

        using var dialogToolTip = new ToolTip
        {
            AutoPopDelay = 12000,
            InitialDelay = 350,
            ReshowDelay = 100,
            ShowAlways = true,
        };
        ApplyToolTipTheme(dialogToolTip, _darkMode);
        dialogToolTip.SetToolTip(okButton, "Apply the start time.");
        dialogToolTip.SetToolTip(cancelButton, "Close without changing start time.");

        dialog.Controls.AddRange([label, input, okButton, cancelButton]);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
    }

    private void MarkTrimIn()
    {
        var path = GetCurrentMediaPath();
        if (path is null || IsImageFile(path))
        {
            SetStatus("Choose a seekable media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        var duration = GetCurrentSeekDuration() ?? GetKnownMediaDuration(path);
        if (!duration.HasValue)
        {
            SetStatus("Duration must be known before marking", Color.FromArgb(232, 181, 105));
            return;
        }

        EnsureTrimMarksForPath(path);
        var position = ClampSeekOffset(GetCurrentSeekPosition(), duration.Value);
        _markInOffset = position;
        if (_markOutOffset.HasValue && _markOutOffset.Value <= position)
        {
            _markOutOffset = null;
        }

        SetStatus($"Mark In {FormatPlaylistTime(position)}", Color.FromArgb(130, 210, 164));
        UpdateDurationLabel();
        UpdateTrimControls();
    }

    private void MarkTrimOut()
    {
        var path = GetCurrentMediaPath();
        if (path is null || IsImageFile(path))
        {
            SetStatus("Choose a seekable media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        var duration = GetCurrentSeekDuration() ?? GetKnownMediaDuration(path);
        if (!duration.HasValue)
        {
            SetStatus("Duration must be known before marking", Color.FromArgb(232, 181, 105));
            return;
        }

        EnsureTrimMarksForPath(path);
        var position = ClampSeekOffset(GetCurrentSeekPosition(), duration.Value);
        var markIn = _markInOffset ?? TimeSpan.Zero;
        if (position <= markIn)
        {
            SetStatus("Mark Out must be after Mark In", Color.FromArgb(232, 181, 105));
            return;
        }

        _markInOffset ??= TimeSpan.Zero;
        _markOutOffset = position;
        SetStatus($"Mark Out {FormatPlaylistTime(position)}", Color.FromArgb(130, 210, 164));
        UpdateDurationLabel();
        UpdateTrimControls();
    }

    private void AddTrimmedClipToPlaylist()
    {
        var path = GetCurrentMediaPath();
        if (path is null)
        {
            SetStatus("Choose a media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        if (IsImageFile(path))
        {
            AddPlaylistItem(
                new PlaylistItem(path)
                {
                    SourceDuration = DefaultStillDuration,
                    TcIn = TimeSpan.Zero,
                    TcOut = DefaultStillDuration,
                    Status = PlaylistStatusReady,
                },
                $"Added 5 sec still {Path.GetFileName(path)}");
            return;
        }

        var sourceDuration = GetKnownMediaDuration(path) ?? GetCurrentSeekDuration();
        if (!sourceDuration.HasValue)
        {
            SetStatus("Duration must be known before adding a trimmed clip", Color.FromArgb(232, 181, 105));
            return;
        }

        var marksMatchPath = string.Equals(_trimMarkPath, path, StringComparison.OrdinalIgnoreCase);
        var tcIn = marksMatchPath && _markInOffset.HasValue
            ? _markInOffset.Value
            : ClampSeekOffset(_selectedStartOffset, sourceDuration.Value);
        var tcOut = marksMatchPath && _markOutOffset.HasValue
            ? _markOutOffset.Value
            : sourceDuration.Value;

        tcIn = ClampSeekOffset(tcIn, sourceDuration.Value);
        tcOut = ClampSeekOffset(tcOut, sourceDuration.Value);
        if (tcOut <= tcIn)
        {
            SetStatus("Trimmed clip needs Mark Out after Mark In", Color.FromArgb(232, 181, 105));
            return;
        }

        AddPlaylistItem(
            new PlaylistItem(path)
            {
                SourceDuration = sourceDuration,
                TcIn = tcIn,
                TcOut = tcOut,
                Status = PlaylistStatusReady,
            },
            $"Added trimmed {Path.GetFileName(path)} {FormatPlaylistTime(tcIn)}-{FormatPlaylistTime(tcOut)}");
    }

    private string? GetCurrentMediaPath()
    {
        var path = _inputPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return path;
        }

        return GetSelectedMediaGridPath();
    }

    private void EnsureTrimMarksForPath(string path)
    {
        if (string.Equals(_trimMarkPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _trimMarkPath = path;
        _markInOffset = null;
        _markOutOffset = null;
    }

    private void ClearTrimMarks()
    {
        _trimMarkPath = null;
        _markInOffset = null;
        _markOutOffset = null;
        UpdateTrimControls();
        UpdateSeekRangeHighlight();
    }

    private async Task SeedPlaylistFromMediaLibraryAsync()
    {
        if (_playlistItems.Count > 0 || !Directory.Exists(_mediaRootPath))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var ambFile = FindMediaFiles(_mediaRootPath, string.Empty, 120, cancellation.Token)
                .Where(file => !IsImageFile(file) &&
                    Path.GetFileNameWithoutExtension(file).Contains("amb", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => string.Equals(Path.GetFileNameWithoutExtension(file), "AMB", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (ambFile is null)
            {
                AppendLog("Playlist demo load skipped: AMB media file not found.");
                return;
            }

            var duration = await TryProbeDurationForPlaylistSeedAsync(ambFile, cancellation.Token);
            var demoTransitions = new[]
            {
                PlaylistTransitionMix,
                PlaylistTransitionPush,
                PlaylistTransitionWipe,
                PlaylistTransitionSlide,
            };
            for (var i = 0; i < demoTransitions.Length; i++)
            {
                _playlistItems.Add(new PlaylistItem(ambFile)
                {
                    SourceDuration = duration,
                    TcIn = TimeSpan.Zero,
                    TcOut = duration,
                    Transition = demoTransitions[i],
                    TransitionDuration = DefaultTransitionDuration,
                    Status = PlaylistStatusReady,
                });
            }

            if (_playlistItems.Count > 0)
            {
                RefreshPlaylistGrid(0);
                SetStatus("Loaded AMB with Mix, Push, Wipe, Slide", Color.FromArgb(130, 210, 164));
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Playlist demo load timed out.");
        }
        catch (Exception ex)
        {
            AppendLog($"Playlist demo load skipped: {ex.Message}");
        }
    }

    private async Task<TimeSpan?> TryProbeDurationForPlaylistSeedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeMediaDurationAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Playlist seed duration unavailable for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private void RemoveSelectedPlaylistItem()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue)
        {
            return;
        }

        if (_playlistPlayingIndex == index.Value)
        {
            SetStatus("Stop playback before removing the playing row", Color.FromArgb(232, 181, 105));
            return;
        }

        _playlistItems.RemoveAt(index.Value);
        if (_playlistPlayingIndex.HasValue && _playlistPlayingIndex.Value > index.Value)
        {
            _playlistPlayingIndex--;
        }

        RefreshPlaylistGrid(Math.Min(index.Value, _playlistItems.Count - 1));
    }

    private void ClearPlaylist()
    {
        if (_playlistPlayingIndex.HasValue || _playlistPlaybackActive)
        {
            SetStatus("Stop playback before clearing playlist", Color.FromArgb(232, 181, 105));
            return;
        }

        _playlistItems.Clear();
        _playlistPlayingIndex = null;
        _playlistPlaybackActive = false;
        RefreshPlaylistGrid();
    }

    private void MoveSelectedPlaylistItemUp()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || index.Value <= 0)
        {
            return;
        }

        MovePlaylistItem(index.Value, index.Value - 1);
    }

    private void MoveSelectedPlaylistItemDown()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || index.Value >= _playlistItems.Count - 1)
        {
            return;
        }

        MovePlaylistItem(index.Value, index.Value + 1);
    }

    private void MovePlaylistItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 ||
            fromIndex >= _playlistItems.Count ||
            toIndex < 0 ||
            toIndex >= _playlistItems.Count ||
            fromIndex == toIndex)
        {
            return;
        }

        var oldPlayingIndex = _playlistPlayingIndex;
        var oldTransitionLeadInIndex = _playlistTransitionLeadInIndex;
        var item = _playlistItems[fromIndex];
        _playlistItems.RemoveAt(fromIndex);
        _playlistItems.Insert(toIndex, item);

        if (_playlistPlaybackActive)
        {
            ClearPlaylistTransitionLeadIn();
        }
        else if (oldTransitionLeadInIndex.HasValue)
        {
            _playlistTransitionLeadInIndex = RemapPlaylistIndexAfterMove(oldTransitionLeadInIndex.Value, fromIndex, toIndex);
        }

        if (oldPlayingIndex.HasValue)
        {
            var newPlayingIndex = RemapPlaylistIndexAfterMove(oldPlayingIndex.Value, fromIndex, toIndex);
            _playlistPlayingIndex = newPlayingIndex;
            if (_playlistPlaybackActive &&
                ShouldRecalculateTimelineAfterRunningMove(fromIndex, toIndex, oldPlayingIndex.Value, newPlayingIndex))
            {
                _playlistItems[newPlayingIndex].TimelineStartOverride ??= DateTime.Now.TimeOfDay;
                ClearFollowingTimelineStartOverridesUntilEnd(newPlayingIndex);
            }

            UpdatePlaylistStatusesForPlayingIndex(newPlayingIndex);
        }

        RefreshPlaylistGrid(toIndex);
    }

    private static int RemapPlaylistIndexAfterMove(int index, int fromIndex, int toIndex)
    {
        if (index == fromIndex)
        {
            return toIndex;
        }

        var adjusted = index;
        if (fromIndex < adjusted)
        {
            adjusted--;
        }

        if (toIndex <= adjusted)
        {
            adjusted++;
        }

        return adjusted;
    }

    private bool ShouldRecalculateTimelineAfterRunningMove(
        int fromIndex,
        int toIndex,
        int oldPlayingIndex,
        int newPlayingIndex)
    {
        return fromIndex == oldPlayingIndex ||
            fromIndex > oldPlayingIndex ||
            toIndex > newPlayingIndex;
    }

    private void MovePlaylistItemToInsertIndex(int fromIndex, int insertIndex)
    {
        if (fromIndex < 0 || fromIndex >= _playlistItems.Count)
        {
            return;
        }

        insertIndex = Math.Clamp(insertIndex, 0, _playlistItems.Count);
        if (insertIndex == fromIndex || insertIndex == fromIndex + 1)
        {
            RefreshPlaylistGrid(fromIndex);
            return;
        }

        var targetIndex = fromIndex < insertIndex ? insertIndex - 1 : insertIndex;
        MovePlaylistItem(fromIndex, targetIndex);
    }

    private void CopySelectedPlaylistItem()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || index.Value < 0 || index.Value >= _playlistItems.Count)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        _playlistClipboardItem = CreatePlaylistInsertCopy(_playlistItems[index.Value]);
        try
        {
            Clipboard.SetText(_playlistClipboardItem.FullPath);
        }
        catch (Exception ex)
        {
            AppendLog($"Clipboard copy skipped: {ex.Message}");
        }

        SetStatus($"Copied row {index.Value + 1}", Color.FromArgb(130, 210, 164));
    }

    private void PastePlaylistItem()
    {
        var item = _playlistClipboardItem is not null
            ? CreatePlaylistInsertCopy(_playlistClipboardItem)
            : TryCreatePlaylistItemFromClipboard();
        if (item is null)
        {
            SetStatus("No playlist item to paste", Color.FromArgb(232, 181, 105));
            return;
        }

        AddPlaylistItem(item, $"Pasted {Path.GetFileName(item.FullPath)}");
    }

    private PlaylistItem? TryCreatePlaylistItemFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                return null;
            }

            var path = Clipboard.GetText().Trim();
            if (IsPlaylistEndMarkerText(path))
            {
                return new PlaylistItem(PlaylistEndMarkerText)
                {
                    PlayEnabled = false,
                    LoopEnabled = false,
                    Transition = PlaylistTransitionCut,
                    TransitionDuration = DefaultTransitionDuration,
                    Status = PlaylistStatusEnd,
                };
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsSupportedMediaFile(path))
            {
                return null;
            }

            var duration = GetKnownMediaDuration(path);
            var isStill = IsImageFile(path);
            return new PlaylistItem(path)
            {
                SourceDuration = isStill ? DefaultStillDuration : duration,
                TcIn = TimeSpan.Zero,
                TcOut = isStill ? DefaultStillDuration : duration,
                Status = PlaylistStatusReady,
            };
        }
        catch (Exception ex)
        {
            AppendLog($"Clipboard paste skipped: {ex.Message}");
            return null;
        }
    }

    private bool CanPastePlaylistItem()
    {
        if (_playlistClipboardItem is not null)
        {
            return true;
        }

        try
        {
            if (!Clipboard.ContainsText())
            {
                return false;
            }

            var text = Clipboard.GetText().Trim();
            return IsPlaylistEndMarkerText(text) ||
                (File.Exists(text) && IsSupportedMediaFile(text));
        }
        catch
        {
            return false;
        }
    }

    private static PlaylistItem CreatePlaylistInsertCopy(PlaylistItem source)
    {
        if (source.IsEndMarker)
        {
            return new PlaylistItem(PlaylistEndMarkerText)
            {
                PlayEnabled = false,
                LoopEnabled = false,
                Transition = PlaylistTransitionCut,
                TransitionDuration = DefaultTransitionDuration,
                Status = PlaylistStatusEnd,
            };
        }

        return new PlaylistItem(source.FullPath)
        {
            SourceDuration = source.SourceDuration,
            TcIn = source.TcIn,
            TcOut = source.TcOut,
            PlayEnabled = source.PlayEnabled,
            LoopEnabled = source.LoopEnabled,
            Transition = source.Transition,
            TransitionDuration = source.TransitionDuration,
            Status = PlaylistStatusReady,
        };
    }

    private void CheckPlaylistFiles()
    {
        var missing = 0;
        for (var i = 0; i < _playlistItems.Count; i++)
        {
            if (_playlistItems[i].IsEndMarker)
            {
                _playlistItems[i].PlayEnabled = false;
                _playlistItems[i].LoopEnabled = false;
                _playlistItems[i].Transition = PlaylistTransitionCut;
                _playlistItems[i].TransitionDuration = DefaultTransitionDuration;
                _playlistItems[i].Status = PlaylistStatusEnd;
                continue;
            }

            if (File.Exists(_playlistItems[i].FullPath))
            {
                if (_playlistItems[i].Status == PlaylistStatusMissing)
                {
                    _playlistItems[i].Status = PlaylistStatusReady;
                }
            }
            else
            {
                _playlistItems[i].Status = PlaylistStatusMissing;
                missing++;
            }
        }

        RefreshPlaylistGrid(GetSelectedPlaylistIndex());
        SetStatus(
            missing == 0 ? "All playlist files found" : $"{missing} playlist file(s) missing",
            missing == 0 ? Color.FromArgb(130, 210, 164) : Color.FromArgb(232, 181, 105));
    }

    private void OpenSelectedPlaylistItemInTrimmer()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || index.Value < 0 || index.Value >= _playlistItems.Count)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        var item = _playlistItems[index.Value];
        if (item.IsEndMarker || !File.Exists(item.FullPath))
        {
            SetStatus("Playlist file missing", Color.FromArgb(229, 113, 105));
            return;
        }

        SelectMediaPath(item.FullPath);
        _selectedStartOffset = item.TcIn;
        if (item.SourceDuration.HasValue)
        {
            _selectedDurationPath = item.FullPath;
            _selectedMediaDuration = item.SourceDuration.Value;
            _selectedDurationUnavailable = false;
        }

        _trimMarkPath = item.FullPath;
        _markInOffset = item.TcIn;
        _markOutOffset = item.TcOut;
        UpdateDurationLabel();
        UpdateTrimControls();
        SetStatus($"Opened row {index.Value + 1} in trimmer", Color.FromArgb(130, 210, 164));
    }

    private void SetPlaylistPlayEnabledForAll(bool enabled)
    {
        var skippedPlaying = false;
        for (var i = 0; i < _playlistItems.Count; i++)
        {
            if (_playlistItems[i].IsEndMarker)
            {
                _playlistItems[i].PlayEnabled = false;
                _playlistItems[i].LoopEnabled = false;
                _playlistItems[i].Transition = PlaylistTransitionCut;
                _playlistItems[i].TransitionDuration = DefaultTransitionDuration;
                _playlistItems[i].Status = PlaylistStatusEnd;
                continue;
            }

            if (!enabled && _playlistPlayingIndex == i)
            {
                skippedPlaying = true;
                continue;
            }

            _playlistItems[i].PlayEnabled = enabled;
            if (!enabled && _playlistItems[i].Status == PlaylistStatusNext)
            {
                _playlistItems[i].Status = PlaylistStatusReady;
            }
        }

        RefreshPlaylistGrid(GetSelectedPlaylistIndex());
        SetStatus(
            enabled ? "Selected all playlist rows" : skippedPlaying ? "Deselected rows except playing row" : "Deselected all playlist rows",
            Color.FromArgb(130, 210, 164));
    }

    private void PlaySelectedPlaylistItemInExternalPlayer(string playerName)
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || index.Value < 0 || index.Value >= _playlistItems.Count)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        var item = _playlistItems[index.Value];
        if (item.IsEndMarker)
        {
            SetStatus("END marker is not a media row", Color.FromArgb(232, 181, 105));
            return;
        }

        var path = item.FullPath;
        PlayMediaPathInExternalPlayer(path, playerName, "Playlist file missing");
    }

    private void PlaySelectedMediaGridInExternalPlayer(string playerName)
    {
        var path = GetSelectedMediaGridPath();
        if (path is null)
        {
            SetStatus("Choose a media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        PlayMediaPathInExternalPlayer(path, playerName, "Media file missing");
    }

    private void PlayMediaPathInExternalPlayer(string path, string playerName, string missingStatus)
    {
        if (!File.Exists(path))
        {
            SetStatus(missingStatus, Color.FromArgb(229, 113, 105));
            return;
        }

        var playerPath = FindExternalPlayerPath(playerName);
        if (playerPath is null)
        {
            SetStatus($"{playerName} not found", Color.FromArgb(232, 181, 105));
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = playerPath,
                UseShellExecute = false,
                ArgumentList = { path },
            });
            TrackExternalFfplayProcess(process, playerName);
            SetStatus($"Opened in {playerName}", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex)
        {
            AppendLog($"{playerName} launch failed: {ex.Message}");
            SetStatus($"{playerName} launch failed", Color.FromArgb(229, 113, 105));
        }
    }

    private void TrackExternalFfplayProcess(Process? process, string playerName)
    {
        if (process is null ||
            !string.Equals(playerName, "ffplay", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                process.Dispose();
                return;
            }

            process.EnableRaisingEvents = true;
            process.Exited += ExternalFfplayProcess_Exited;
            lock (_externalFfplayLock)
            {
                _externalFfplayProcesses.Add(process);
            }
        }
        catch
        {
            process.Dispose();
        }
    }

    private void ExternalFfplayProcess_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        lock (_externalFfplayLock)
        {
            _externalFfplayProcesses.Remove(process);
        }

        process.Exited -= ExternalFfplayProcess_Exited;
        process.Dispose();
    }

    private void StopExternalFfplayProcesses()
    {
        List<Process> processes;
        lock (_externalFfplayLock)
        {
            processes = _externalFfplayProcesses.ToList();
            _externalFfplayProcesses.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                process.Exited -= ExternalFfplayProcess_Exited;
                TryKillProcess(process);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private string? FindExternalPlayerPath(string playerName)
    {
        if (string.Equals(playerName, "ffplay", StringComparison.OrdinalIgnoreCase))
        {
            var ffmpegDirectory = Path.GetDirectoryName(GetFfmpegPath());
            var ffplayPath = string.IsNullOrWhiteSpace(ffmpegDirectory)
                ? null
                : Path.Combine(ffmpegDirectory, "ffplay.exe");
            if (!string.IsNullOrWhiteSpace(ffplayPath) && File.Exists(ffplayPath))
            {
                return ffplayPath;
            }
        }

        if (string.Equals(playerName, "vlc", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in EnumerateVlcCandidates())
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return playerName;
    }

    private static IEnumerable<string> EnumerateVlcCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe");
    }

    private void CueRelativePlaylistItem(int direction, int? referenceIndex = null)
    {
        var index = FindRelativePlaylistIndex(direction, referenceIndex);
        if (!index.HasValue)
        {
            SetStatus(direction > 0 ? "No next playlist row" : "No previous playlist row", Color.FromArgb(232, 181, 105));
            return;
        }

        CuePlaylistItem(index.Value);
    }

    private async Task PlayRelativePlaylistItemAsync(int direction, int? referenceIndex = null)
    {
        if (_playlistPlaybackActive)
        {
            SetStatus("Stop playlist before manual play", Color.FromArgb(232, 181, 105));
            return;
        }

        var index = FindRelativePlaylistIndex(direction, referenceIndex);
        if (!index.HasValue)
        {
            SetStatus(direction > 0 ? "No next playlist row" : "No previous playlist row", Color.FromArgb(232, 181, 105));
            return;
        }

        await PlayPlaylistItemAsync(index.Value);
    }

    private void CueSelectedPlaylistItem()
    {
        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        CuePlaylistItem(index.Value);
    }

    private void CuePlaylistItem(int index)
    {
        _ = CuePlaylistItemAsync(index);
    }

    private async Task CuePlaylistItemAsync(int index)
    {
        try
        {
            await CuePlaylistItemCoreAsync(index);
        }
        catch (Exception ex)
        {
            AppendLog($"Cue failed: {ex.Message}");
            SetStatus("Cue failed", Color.FromArgb(229, 113, 105));
        }
    }

    private async Task CuePlaylistItemCoreAsync(int index)
    {
        if (!IsPlaylistRowCueable(index))
        {
            SetStatus("Playlist row is unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        var item = _playlistItems[index].Snapshot();
        await StopActivePlaybackForReplacementAsync();
        SelectMediaPath(item.FullPath);
        _selectedStartOffset = item.TcIn;
        if (item.SourceDuration.HasValue)
        {
            _selectedDurationPath = item.FullPath;
            _selectedMediaDuration = item.SourceDuration.Value;
            _selectedDurationUnavailable = false;
        }

        RearmPlaylistForManualPlay(index);
        MarkPlaylistItemPlaying(index);
        AppendLog($"Cueing playlist row {index + 1} paused: {Path.GetFileName(item.FullPath)}");
        SetStatus($"Cueing row {index + 1}", Color.FromArgb(126, 188, 226));

        var playDuration = item.PlayDuration;
        await StartPlaybackAsync(
            dryRun: false,
            startOffset: item.TcIn,
            startPaused: true,
            playDuration: playDuration,
            sourceDuration: item.SourceDuration,
            playLoop: item.LoopEnabled && !playDuration.HasValue);
    }

    private int? FindRelativePlaylistIndex(int direction, int? referenceIndex = null)
    {
        if (direction == 0 || _playlistItems.Count == 0)
        {
            return null;
        }

        var reference = referenceIndex ?? GetPlaylistReferenceIndex();
        var index = reference < 0
            ? direction > 0 ? 0 : _playlistItems.Count - 1
            : reference + Math.Sign(direction);

        while (index >= 0 && index < _playlistItems.Count)
        {
            if (_playlistItems[index].IsEndMarker)
            {
                return null;
            }

            if (IsPlaylistRowCueable(index))
            {
                return index;
            }

            index += Math.Sign(direction);
        }

        return null;
    }

    private int GetPlaylistReferenceIndex()
    {
        if (_playlistPlayingIndex.HasValue)
        {
            return _playlistPlayingIndex.Value;
        }

        return GetSelectedPlaylistIndex() ?? -1;
    }

    private bool IsPlaylistRowCueable(int index)
    {
        return index >= 0 &&
            index < _playlistItems.Count &&
            !_playlistItems[index].IsEndMarker &&
            _playlistItems[index].PlayEnabled &&
            File.Exists(_playlistItems[index].FullPath);
    }

    private async Task PlaySelectedPlaylistItemAsync()
    {
        if (_playlistPlaybackActive)
        {
            SetStatus("Stop playlist before manual play", Color.FromArgb(232, 181, 105));
            return;
        }

        var index = GetSelectedPlaylistIndex();
        if (!index.HasValue || index.Value < 0 || index.Value >= _playlistItems.Count)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        await PlayPlaylistItemAsync(index.Value);
    }

    private async Task StartPlaylistPlaybackAsync()
    {
        if (_playlistPlaybackActive)
        {
            return;
        }

        var startIndex = GetSelectedPlaylistIndex();
        if (!startIndex.HasValue)
        {
            SetStatus("Choose a playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        if (!IsPlaylistRowCueable(startIndex.Value))
        {
            SetStatus("Choose a playable playlist row first", Color.FromArgb(232, 181, 105));
            return;
        }

        ClearPlaylistTransitionLeadIn();
        _playlistPlaybackActive = true;
        UpdatePlaylistButtons();
        SetStatus("Playlist started", Color.FromArgb(126, 188, 226));
        await PlayPlaylistItemAsync(startIndex.Value);
    }

    private void StopPlaylistPlayback()
    {
        if (!_playlistPlaybackActive)
        {
            return;
        }

        _playlistPlaybackActive = false;
        ClearPlaylistTransitionLeadIn();
        if (_isPlaying)
        {
            StopPlayback(resetPlaybackSpeed: false);
        }
        else
        {
            ClearPlaylistActivePlaybackForMediaStart();
            UpdatePlaylistButtons();
            SetStatus("Playlist stopped", Color.FromArgb(130, 210, 164));
        }
    }

    private async Task PlayPlaylistItemAsync(int index)
    {
        if (index < 0 || index >= _playlistItems.Count)
        {
            return;
        }

        if (_playlistItems[index].IsEndMarker)
        {
            _playlistItems[index].Status = PlaylistStatusEnd;
            RefreshPlaylistGrid(index);
            SetStatus("END marker stops playlist repeat", Color.FromArgb(232, 181, 105));
            return;
        }

        if (!File.Exists(_playlistItems[index].FullPath))
        {
            _playlistItems[index].Status = PlaylistStatusMissing;
            RefreshPlaylistGrid(index);
            SetStatus("Playlist file missing", Color.FromArgb(229, 113, 105));
            return;
        }

        if (!_playlistItems[index].PlayEnabled)
        {
            RefreshPlaylistGrid(index);
            SetStatus("Playlist row is unchecked", Color.FromArgb(232, 181, 105));
            return;
        }

        RearmPlaylistForManualPlay(index);
        var item = _playlistItems[index].Snapshot();
        var playDuration = GetEffectivePlaylistPlayDuration(item);
        var consumedLeadIn = ConsumePlaylistTransitionLeadIn(index, playDuration);
        var startOffset = IsImageFile(item.FullPath) ? TimeSpan.Zero : item.TcIn + consumedLeadIn;
        if (playDuration.HasValue && consumedLeadIn > TimeSpan.Zero)
        {
            playDuration = playDuration.Value > consumedLeadIn
                ? playDuration.Value - consumedLeadIn
                : TimeSpan.FromMilliseconds(250);
        }

        await StopActivePlaybackForReplacementAsync();

        SelectMediaPath(item.FullPath);
        _selectedStartOffset = startOffset;
        SetPlaylistStartTimeFromPlayback(index);
        MarkPlaylistItemPlaying(index);
        AppendLog($"Starting playlist row {index + 1}: {Path.GetFileName(item.FullPath)}");
        var transitionPlan = BuildPlaylistTransitionPlan(index, item, playDuration);
        if (transitionPlan is not null)
        {
            _playlistTransitionLeadInIndex = transitionPlan.NextIndex;
            _playlistTransitionLeadInDuration = transitionPlan.Segment.Duration;
            _playlistTransitionLeadInReady = false;
            AppendLog(
                $"Transition {transitionPlan.Segment.Transition} to row {transitionPlan.NextIndex + 1} for {FormatPlaylistTime(transitionPlan.Segment.Duration)}.");
        }

        var transitionFilters = transitionPlan is null
            ? BuildPlaylistTransitionFilters(index, item, suppressIncomingTransition: consumedLeadIn > TimeSpan.Zero)
            : PlaylistTransitionFilters.Empty;
        await StartPlaybackAsync(
            dryRun: false,
            startOffset: startOffset,
            playDuration: playDuration,
            sourceDuration: item.SourceDuration,
            playLoop: item.LoopEnabled && !playDuration.HasValue && transitionPlan is null,
            videoFilter: transitionFilters.VideoFilter,
            audioFilter: transitionFilters.AudioFilter,
            transitionSegment: transitionPlan?.Segment);
    }

    private PlaylistTransitionFilters BuildPlaylistTransitionFilters(int index, PlaylistItem item, bool suppressIncomingTransition)
    {
        if (item.IsEndMarker)
        {
            return PlaylistTransitionFilters.Empty;
        }

        var videoFilters = new List<string>();
        var audioFilters = new List<string>();
        var previousIndex = FindPreviousPlayablePlaylistIndex(index);
        if (!suppressIncomingTransition &&
            _playlistPlaybackActive &&
            previousIndex.HasValue &&
            TryGetEffectiveTransitionDuration(_playlistItems[previousIndex.Value].Transition, _playlistItems[previousIndex.Value].TransitionDuration, item.PlayDuration, out var fadeInDuration))
        {
            var duration = FormatFilterSeconds(fadeInDuration);
            videoFilters.Add($"fade=t=in:st=0:d={duration}:color=black");
            audioFilters.Add($"afade=t=in:st=0:d={duration}");
        }

        if (_playlistPlaybackActive &&
            FindNextRepeatingPlaylistIndex(index, out _).HasValue &&
            item.PlayDuration.HasValue &&
            TryGetEffectiveTransitionDuration(item.Transition, item.TransitionDuration, item.PlayDuration, out var fadeOutDuration))
        {
            var duration = FormatFilterSeconds(fadeOutDuration);
            var start = FormatFilterSeconds(item.PlayDuration.Value - fadeOutDuration);
            videoFilters.Add($"fade=t=out:st={start}:d={duration}:color=black");
            audioFilters.Add($"afade=t=out:st={start}:d={duration}");
        }

        return new PlaylistTransitionFilters(
            videoFilters.Count > 0 ? string.Join(",", videoFilters) : null,
            audioFilters.Count > 0 ? string.Join(",", audioFilters) : null);
    }

    private PlaylistTransitionPlan? BuildPlaylistTransitionPlan(int index, PlaylistItem item, TimeSpan? playDuration)
    {
        if (!_playlistPlaybackActive ||
            item.IsEndMarker ||
            !playDuration.HasValue ||
            !TryGetEffectiveTransitionDuration(item.Transition, item.TransitionDuration, playDuration, out var transitionDuration))
        {
            return null;
        }

        var nextIndex = FindNextRepeatingPlaylistIndex(index, out _);
        if (!nextIndex.HasValue || nextIndex.Value == index)
        {
            return null;
        }

        var nextItem = _playlistItems[nextIndex.Value];
        if (nextItem.IsEndMarker || !nextItem.PlayEnabled || !File.Exists(nextItem.FullPath))
        {
            return null;
        }

        var nextDuration = GetEffectivePlaylistPlayDuration(nextItem);
        if (nextDuration.HasValue)
        {
            if (nextDuration.Value <= TimeSpan.FromMilliseconds(250))
            {
                return null;
            }

            var nextMaxDuration = TimeSpan.FromSeconds(Math.Max(0.1d, nextDuration.Value.TotalSeconds / 2d));
            if (transitionDuration > nextMaxDuration)
            {
                transitionDuration = nextMaxDuration;
            }
        }

        if (transitionDuration <= TimeSpan.Zero || transitionDuration >= playDuration.Value)
        {
            return null;
        }

        var segment = new PlaylistTransitionSegment(
            nextItem.FullPath,
            IsImageFile(nextItem.FullPath) ? TimeSpan.Zero : nextItem.TcIn,
            transitionDuration,
            NormalizePlaylistTransition(item.Transition),
            transitionDuration,
            playDuration.Value - transitionDuration);

        return new PlaylistTransitionPlan(nextIndex.Value, segment);
    }

    private TimeSpan? GetEffectivePlaylistPlayDuration(PlaylistItem item)
    {
        if (item.PlayDuration.HasValue)
        {
            return item.PlayDuration;
        }

        return IsImageFile(item.FullPath) ? DefaultStillDuration : null;
    }

    private TimeSpan ConsumePlaylistTransitionLeadIn(int index, TimeSpan? playDuration)
    {
        if (_playlistTransitionLeadInIndex != index ||
            !_playlistTransitionLeadInReady ||
            _playlistTransitionLeadInDuration <= TimeSpan.Zero)
        {
            if (_playlistTransitionLeadInIndex.HasValue && _playlistTransitionLeadInIndex.Value != index)
            {
                ClearPlaylistTransitionLeadIn();
            }

            return TimeSpan.Zero;
        }

        var duration = _playlistTransitionLeadInDuration;
        ClearPlaylistTransitionLeadIn();
        if (playDuration.HasValue)
        {
            var maxDuration = playDuration.Value - TimeSpan.FromMilliseconds(250);
            if (maxDuration <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            if (duration > maxDuration)
            {
                duration = maxDuration;
            }
        }

        return IsImageFile(_playlistItems[index].FullPath) ? TimeSpan.Zero : duration;
    }

    private void ClearPlaylistTransitionLeadIn()
    {
        _playlistTransitionLeadInIndex = null;
        _playlistTransitionLeadInDuration = TimeSpan.Zero;
        _playlistTransitionLeadInReady = false;
    }

    private int? FindPreviousPlayablePlaylistIndex(int referenceIndex)
    {
        if (_playlistItems.Count == 0 || referenceIndex < 0 || referenceIndex >= _playlistItems.Count)
        {
            return null;
        }

        var index = referenceIndex == 0 ? _playlistItems.Count - 1 : referenceIndex - 1;
        for (var offset = 0; offset < _playlistItems.Count - 1; offset++)
        {
            var item = _playlistItems[index];
            if (item.IsEndMarker)
            {
                return null;
            }

            if (item.PlayEnabled && File.Exists(item.FullPath))
            {
                return index;
            }

            index = index == 0 ? _playlistItems.Count - 1 : index - 1;
        }

        return null;
    }

    private static bool TryGetEffectiveTransitionDuration(
        string transition,
        TimeSpan requestedDuration,
        TimeSpan? playDuration,
        out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!IsTimedPlaylistTransition(transition))
        {
            return false;
        }

        duration = requestedDuration > TimeSpan.Zero ? requestedDuration : DefaultTransitionDuration;
        if (playDuration.HasValue)
        {
            if (playDuration.Value <= TimeSpan.FromMilliseconds(250))
            {
                duration = TimeSpan.Zero;
                return false;
            }

            var maxDuration = TimeSpan.FromSeconds(Math.Max(0.1d, playDuration.Value.TotalSeconds / 2d));
            if (duration > maxDuration)
            {
                duration = maxDuration;
            }
        }

        return duration > TimeSpan.Zero;
    }

    private static string FormatFilterSeconds(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void SetPlaylistStartTimeFromPlayback(int index)
    {
        if (!CanSetPlaylistStartTime(index))
        {
            return;
        }

        _playlistItems[index].TimelineStartOverride = DateTime.Now.TimeOfDay;
        ClearFollowingTimelineStartOverridesUntilEnd(index);
    }

    private async Task StopActivePlaybackForReplacementAsync()
    {
        if (!_isPlaying)
        {
            return;
        }

        _switchingPlayback = true;
        try
        {
            var stoppedTask = _playbackStoppedSignal?.Task;
            PreserveVideoOutputForReplacement();
            StopPlayback(resetPlaybackSpeed: false);
            if (stoppedTask is not null)
            {
                await stoppedTask;
            }
        }
        finally
        {
            _switchingPlayback = false;
        }
    }

    private void MarkPlaylistItemPlaying(int index)
    {
        _playlistPlayingIndex = index;
        UpdatePlaylistStatusesForPlayingIndex(index);
        RefreshPlaylistGrid(index);
    }

    private void UpdatePlaylistStatusesForPlayingIndex(int index)
    {
        for (var i = 0; i < _playlistItems.Count; i++)
        {
            if (_playlistItems[i].IsEndMarker)
            {
                _playlistItems[i].PlayEnabled = false;
                _playlistItems[i].LoopEnabled = false;
                _playlistItems[i].Transition = PlaylistTransitionCut;
                _playlistItems[i].TransitionDuration = DefaultTransitionDuration;
                _playlistItems[i].Status = PlaylistStatusEnd;
            }
            else if (!File.Exists(_playlistItems[i].FullPath))
            {
                _playlistItems[i].Status = PlaylistStatusMissing;
            }
            else if (!_playlistItems[i].PlayEnabled && _playlistItems[i].Status == PlaylistStatusNext)
            {
                _playlistItems[i].Status = PlaylistStatusReady;
            }
            else if (i == index)
            {
                _playlistItems[i].Status = PlaylistStatusPlaying;
            }
            else if (_playlistItems[i].Status == PlaylistStatusPlaying || _playlistItems[i].Status == PlaylistStatusNext)
            {
                _playlistItems[i].Status = PlaylistStatusReady;
            }
        }

        var next = _playlistPlaybackActive ? FindNextRepeatingPlaylistIndex(index, out _) : null;
        if (next.HasValue)
        {
            _playlistItems[next.Value].Status = PlaylistStatusNext;
        }
    }

    private void RearmPlaylistForManualPlay(int startIndex)
    {
        if (startIndex < 0 || startIndex >= _playlistItems.Count)
        {
            return;
        }

        var selectedStatus = _playlistItems[startIndex].Status;
        var shouldRearm = selectedStatus == PlaylistStatusPlayed ||
            !FindNextPlayablePlaylistIndex(startIndex).HasValue;
        if (!shouldRearm)
        {
            return;
        }

        var changed = false;
        foreach (var item in _playlistItems)
        {
            if (item.Status == PlaylistStatusNext)
            {
                item.Status = PlaylistStatusReady;
                changed = true;
            }
        }

        for (var i = startIndex; i < _playlistItems.Count; i++)
        {
            var item = _playlistItems[i];
            if (!item.IsEndMarker && item.PlayEnabled && File.Exists(item.FullPath) && item.Status == PlaylistStatusPlayed)
            {
                item.Status = PlaylistStatusReady;
                changed = true;
            }
        }

        if (changed)
        {
            RefreshPlaylistGrid(startIndex);
        }
    }

    private void CompletePlaylistPlayback(bool completedNormally)
    {
        if (!_playlistPlayingIndex.HasValue)
        {
            return;
        }

        var index = _playlistPlayingIndex.Value;
        _playlistPlayingIndex = null;
        int? nextToPlay = null;
        int? endMarkerIndex = null;
        if (index >= 0 && index < _playlistItems.Count)
        {
            if (completedNormally &&
                !_playlistItems[index].IsEndMarker &&
                _playlistItems[index].LoopEnabled &&
                _playlistItems[index].PlayEnabled &&
                File.Exists(_playlistItems[index].FullPath))
            {
                _playlistItems[index].Status = PlaylistStatusReady;
                RefreshPlaylistGrid(index);
                QueuePlaylistAutoNext(index);
                return;
            }

            _playlistItems[index].Status = completedNormally ? PlaylistStatusPlayed : PlaylistStatusReady;
            if (completedNormally && _playlistPlaybackActive)
            {
                nextToPlay = FindNextRepeatingPlaylistIndex(index, out endMarkerIndex);
                if (nextToPlay.HasValue)
                {
                    _playlistItems[nextToPlay.Value].Status = PlaylistStatusNext;
                }
            }
            else
            {
                var next = FindNextPlayablePlaylistIndex(index + 1);
                if (next.HasValue)
                {
                    _playlistItems[next.Value].Status = PlaylistStatusReady;
                }
            }
        }

        var selectedAfterCompletion = nextToPlay ?? endMarkerIndex ?? index;
        if (completedNormally &&
            nextToPlay.HasValue &&
            _playlistTransitionLeadInIndex == nextToPlay.Value)
        {
            _playlistTransitionLeadInReady = true;
        }
        else
        {
            ClearPlaylistTransitionLeadIn();
        }

        RefreshPlaylistGrid(selectedAfterCompletion);
        if (completedNormally && _playlistPlaybackActive && nextToPlay.HasValue)
        {
            QueuePlaylistAutoNext(nextToPlay.Value);
        }
        else if (completedNormally && endMarkerIndex.HasValue)
        {
            _playlistPlaybackActive = false;
            UpdatePlaylistButtons();
            SetStatus("Playlist stopped at END marker", Color.FromArgb(232, 181, 105));
        }
        else if (completedNormally && _playlistPlaybackActive)
        {
            _playlistPlaybackActive = false;
            UpdatePlaylistButtons();
            SetStatus("Playlist stopped", Color.FromArgb(130, 210, 164));
        }
        else if (!completedNormally)
        {
            _playlistPlaybackActive = false;
            UpdatePlaylistButtons();
        }
    }

    private void QueuePlaylistAutoNext(int index)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(new Action(async () =>
        {
            try
            {
                await PlayPlaylistItemAsync(index);
            }
            catch (Exception ex)
            {
                AppendLog($"Auto next failed: {ex.Message}");
                SetStatus("Auto next failed", Color.FromArgb(229, 113, 105));
            }
        }));
    }

    private int? FindNextPlayablePlaylistIndex(int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i < _playlistItems.Count; i++)
        {
            var item = _playlistItems[i];
            if (item.IsEndMarker)
            {
                return null;
            }

            if (item.PlayEnabled && File.Exists(item.FullPath) && item.Status != PlaylistStatusPlayed)
            {
                return i;
            }
        }

        return null;
    }

    private int? FindNextRepeatingPlaylistIndex(int referenceIndex, out int? endMarkerIndex)
    {
        endMarkerIndex = null;
        if (_playlistItems.Count == 0)
        {
            return null;
        }

        var startIndex = referenceIndex < 0 || referenceIndex >= _playlistItems.Count
            ? 0
            : (referenceIndex + 1) % _playlistItems.Count;
        for (var offset = 0; offset < _playlistItems.Count; offset++)
        {
            var index = (startIndex + offset) % _playlistItems.Count;
            var item = _playlistItems[index];
            if (item.IsEndMarker)
            {
                endMarkerIndex = index;
                return null;
            }

            if (item.PlayEnabled && File.Exists(item.FullPath))
            {
                return index;
            }
        }

        return null;
    }

    private bool AreAllPlayablePlaylistItemsPlayed()
    {
        var hasPlayableItem = false;
        foreach (var item in _playlistItems)
        {
            if (item.IsEndMarker || !item.PlayEnabled || !File.Exists(item.FullPath))
            {
                continue;
            }

            hasPlayableItem = true;
            if (item.Status != PlaylistStatusPlayed)
            {
                return false;
            }
        }

        return hasPlayableItem;
    }

    private int? FindFirstExistingPlaylistIndex()
    {
        for (var i = 0; i < _playlistItems.Count; i++)
        {
            if (!_playlistItems[i].IsEndMarker && _playlistItems[i].PlayEnabled && File.Exists(_playlistItems[i].FullPath))
            {
                return i;
            }
        }

        return null;
    }

    private bool TryGetPlaylistStartTime(int targetIndex, out TimeSpan startTime)
    {
        startTime = TimeSpan.Zero;
        if (targetIndex < 0 || targetIndex >= _playlistItems.Count)
        {
            return false;
        }

        var timelineKnown = true;
        var cursor = TimeSpan.Zero;
        for (var i = 0; i <= targetIndex; i++)
        {
            var item = _playlistItems[i];
            if (item.IsEndMarker)
            {
                cursor = TimeSpan.Zero;
                timelineKnown = true;
                if (i == targetIndex)
                {
                    return false;
                }

                continue;
            }

            var isScheduled = item.PlayEnabled && File.Exists(item.FullPath);
            if (isScheduled && item.TimelineStartOverride.HasValue)
            {
                cursor = item.TimelineStartOverride.Value;
                timelineKnown = true;
            }

            if (i == targetIndex)
            {
                if (!isScheduled || !timelineKnown)
                {
                    return false;
                }

                startTime = cursor;
                return true;
            }

            if (!isScheduled)
            {
                continue;
            }

            var duration = item.PlayDuration;
            if (timelineKnown && duration.HasValue)
            {
                cursor += duration.Value;
            }
            else
            {
                timelineKnown = false;
            }
        }

        return false;
    }

    private void RefreshPlaylistGrid(int? selectedIndex = null)
    {
        _playlistGrid.Rows.Clear();
        NormalizePlaylistStatuses();

        var timelineKnown = true;
        var cursor = TimeSpan.Zero;
        for (var i = 0; i < _playlistItems.Count; i++)
        {
            var item = _playlistItems[i];
            var isEndMarker = item.IsEndMarker;
            var duration = item.PlayDuration;
            var isScheduled = !isEndMarker && item.PlayEnabled && File.Exists(item.FullPath);
            if (isScheduled && item.TimelineStartOverride.HasValue)
            {
                cursor = item.TimelineStartOverride.Value;
                timelineKnown = true;
            }

            var startText = isScheduled && timelineKnown ? FormatGridDuration(cursor) : "--";
            var endText = isScheduled && timelineKnown && duration.HasValue ? FormatGridDuration(cursor + duration.Value) : "--";
            var rowIndex = _playlistGrid.Rows.Add(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                startText,
                !isEndMarker && item.PlayEnabled,
                isEndMarker ? PlaylistEndMarkerText : GetMediaDisplayPath(item.FullPath),
                !isEndMarker && duration.HasValue ? FormatPlaylistTime(duration.Value) : "--",
                !isEndMarker && item.LoopEnabled,
                isEndMarker ? PlaylistTransitionCut : NormalizePlaylistTransition(item.Transition),
                isEndMarker || item.Transition == PlaylistTransitionCut ? "--" : FormatPlaylistTime(item.TransitionDuration),
                endText,
                item.FullPath);

            var row = _playlistGrid.Rows[rowIndex];
            row.Tag = item;
            row.Cells["Clip"].ToolTipText = isEndMarker ? "Playlist stops here during repeat" : item.FullPath;
            row.Cells["PlayEnabled"].ReadOnly = isEndMarker;
            row.Cells["LoopEnabled"].ReadOnly = isEndMarker;
            row.Cells["Transition"].ReadOnly = isEndMarker;
            ApplyPlaylistRowStyle(row, isEndMarker ? PlaylistStatusEnd : item.Status);

            if (isEndMarker)
            {
                cursor = TimeSpan.Zero;
                timelineKnown = true;
                continue;
            }

            if (!isScheduled)
            {
                continue;
            }

            if (timelineKnown && duration.HasValue)
            {
                cursor += duration.Value;
            }
            else
            {
                timelineKnown = false;
            }
        }

        if (_playlistGrid.Rows.Count > 0)
        {
            var index = Math.Clamp(selectedIndex ?? GetSelectedPlaylistIndex() ?? 0, 0, _playlistGrid.Rows.Count - 1);
            _playlistGrid.CurrentCell = _playlistGrid.Rows[index].Cells[0];
            _playlistGrid.Rows[index].Selected = true;
        }

        UpdatePlaylistButtons();
    }

    private void NormalizePlaylistStatuses()
    {
        var hasPlaying = false;
        var hasNext = false;
        foreach (var item in _playlistItems)
        {
            if (item.IsEndMarker)
            {
                item.PlayEnabled = false;
                item.LoopEnabled = false;
                item.Transition = PlaylistTransitionCut;
                item.TransitionDuration = DefaultTransitionDuration;
                item.Status = PlaylistStatusEnd;
                continue;
            }

            if (!File.Exists(item.FullPath))
            {
                item.Status = PlaylistStatusMissing;
                continue;
            }

            if (!item.PlayEnabled)
            {
                if (item.Status == PlaylistStatusNext || item.Status == PlaylistStatusPlaying)
                {
                    item.Status = PlaylistStatusReady;
                }

                continue;
            }

            if (item.Status == PlaylistStatusPlaying)
            {
                hasPlaying = true;
            }
            else if (item.Status == PlaylistStatusNext)
            {
                hasNext = true;
            }
            else if (item.Status == PlaylistStatusMissing)
            {
                item.Status = PlaylistStatusReady;
            }
        }

        if (!hasPlaying && !hasNext)
        {
            var next = FindNextPlayablePlaylistIndex(0);
            if (next.HasValue)
            {
                _playlistItems[next.Value].Status = PlaylistStatusNext;
            }
        }
    }

    private void ApplyPlaylistRowStyle(DataGridViewRow row, string status)
    {
        var color = _darkMode
            ? status switch
            {
                PlaylistStatusPlaying => Color.FromArgb(124, 49, 45),
                PlaylistStatusNext => Color.FromArgb(93, 76, 34),
                PlaylistStatusPlayed => Color.FromArgb(48, 53, 58),
                PlaylistStatusMissing => Color.FromArgb(96, 48, 31),
                PlaylistStatusEnd => Color.FromArgb(64, 46, 70),
                _ => row.Index % 2 == 0 ? Color.FromArgb(17, 20, 24) : Color.FromArgb(29, 34, 39),
            }
            : status switch
            {
                PlaylistStatusPlaying => Color.FromArgb(255, 224, 224),
                PlaylistStatusNext => Color.FromArgb(255, 242, 205),
                PlaylistStatusPlayed => Color.FromArgb(232, 236, 240),
                PlaylistStatusMissing => Color.FromArgb(255, 229, 210),
                PlaylistStatusEnd => Color.FromArgb(238, 228, 245),
                _ => row.Index % 2 == 0 ? Color.White : Color.FromArgb(245, 248, 251),
            };

        row.DefaultCellStyle.BackColor = color;
        row.DefaultCellStyle.ForeColor = _darkMode ? Color.FromArgb(226, 234, 238) : Color.FromArgb(24, 29, 34);
        row.DefaultCellStyle.SelectionBackColor = _darkMode
            ? status switch
            {
                PlaylistStatusPlaying => Color.FromArgb(158, 64, 58),
                PlaylistStatusNext => Color.FromArgb(126, 95, 38),
                PlaylistStatusMissing => Color.FromArgb(136, 62, 37),
                PlaylistStatusEnd => Color.FromArgb(95, 68, 106),
                _ => Color.FromArgb(32, 116, 190),
            }
            : status switch
            {
                PlaylistStatusPlaying => Color.FromArgb(221, 93, 86),
                PlaylistStatusNext => Color.FromArgb(217, 167, 59),
                PlaylistStatusMissing => Color.FromArgb(216, 111, 70),
                PlaylistStatusEnd => Color.FromArgb(146, 103, 166),
                _ => Color.FromArgb(62, 135, 210),
            };
        row.DefaultCellStyle.SelectionForeColor = Color.White;
    }

    private int? GetSelectedPlaylistIndex()
    {
        if (_playlistGrid.CurrentRow is not null &&
            _playlistGrid.CurrentRow.Index >= 0 &&
            _playlistGrid.CurrentRow.Index < _playlistItems.Count)
        {
            return _playlistGrid.CurrentRow.Index;
        }

        return null;
    }

    private TimeSpan? GetKnownMediaDuration(string path)
    {
        if (IsImageFile(path))
        {
            return DefaultStillDuration;
        }

        if (string.Equals(_selectedDurationPath, path, StringComparison.OrdinalIgnoreCase) &&
            _selectedMediaDuration.HasValue)
        {
            return _selectedMediaDuration.Value;
        }

        foreach (DataGridViewRow row in _mediaGrid.Rows)
        {
            if (row.Tag is string rowPath &&
                string.Equals(rowPath, path, StringComparison.OrdinalIgnoreCase) &&
                row.Cells["Duration"].Value is string durationText &&
                TryParseDisplayDuration(durationText, out var duration))
            {
                return duration;
            }
        }

        return null;
    }

    private bool TryParseDisplayDuration(string text, out TimeSpan duration)
    {
        return TryParseGridDuration(text, GetDisplayFrameRate(), out duration);
    }

    private static bool TryParseGridDuration(string text, double frameRate, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is not 3 and not 4 ||
            !TryParseNonNegativeInt(parts[0], out var hours) ||
            !TryParseNonNegativeInt(parts[1], out var minutes) ||
            !TryParseNonNegativeInt(parts[2], out var seconds))
        {
            return false;
        }

        var frames = 0;
        var nominalFrameRate = GetNominalFrameRate(frameRate);
        if (parts.Length == 4 &&
            (!TryParseNonNegativeInt(parts[3], out frames) || frames >= nominalFrameRate))
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(
            hours * 3600d +
            minutes * 60d +
            seconds +
            frames / (double)nominalFrameRate);
        return true;
    }

    private string FormatPlaylistTime(TimeSpan value)
    {
        return FormatTimecode(value);
    }

    private void RefreshFrameBasedTimeDisplays()
    {
        if (_loadingSettings || IsDisposed)
        {
            return;
        }

        if (_playlistGrid.Columns.Count > 0)
        {
            RefreshPlaylistGrid(GetSelectedPlaylistIndex());
        }

        UpdateDurationLabel();
        UpdateTrimControls();
        UpdateSeekRangeHighlight();
    }

    private void UpdatePlaylistButtons()
    {
        UpdatePlaybackModeLabel();

        var selectedIndex = GetSelectedPlaylistIndex();
        var hasPlaylistSelection = selectedIndex.HasValue;
        var selectedPlayable = selectedIndex.HasValue &&
            selectedIndex.Value >= 0 &&
            selectedIndex.Value < _playlistItems.Count &&
            IsPlaylistRowCueable(selectedIndex.Value);
        var controlsEnabled = _playlistGrid.Enabled;
        _addToPlaylistButton.Enabled = controlsEnabled && (GetSelectedMediaGridPath() is not null || File.Exists(_inputPathBox.Text.Trim()));
        _startPlaylistButton.Enabled = controlsEnabled && !_isPlaying && !_playlistPlaybackActive && hasPlaylistSelection && selectedPlayable;
        _stopPlaylistButton.Enabled = _playlistPlaybackActive;
        _playPlaylistItemButton.Enabled = controlsEnabled && !_playlistPlaybackActive && hasPlaylistSelection && selectedPlayable;
        _removePlaylistItemButton.Enabled = controlsEnabled && hasPlaylistSelection;
        _movePlaylistItemUpButton.Enabled = controlsEnabled && selectedIndex is > 0;
        _movePlaylistItemDownButton.Enabled = controlsEnabled && selectedIndex.HasValue && selectedIndex.Value < _playlistItems.Count - 1;
        _clearPlaylistButton.Enabled = controlsEnabled && _playlistItems.Count > 0;
        _openPlaylistButton.Enabled = controlsEnabled && !_isPlaying;
        _savePlaylistButton.Enabled = controlsEnabled && _playlistItems.Count > 0;
        _setPlaylistStartTimeButton.Enabled = controlsEnabled && selectedIndex.HasValue && CanSetPlaylistStartTime(selectedIndex.Value);
        var hasPrevious = FindRelativePlaylistIndex(-1).HasValue;
        var hasNext = FindRelativePlaylistIndex(1).HasValue;
        _previousCueButton.Enabled = controlsEnabled && hasPrevious;
        _previousPlayButton.Enabled = controlsEnabled && hasPrevious;
        _nextPlayButton.Enabled = controlsEnabled && hasNext;
        _nextCueButton.Enabled = controlsEnabled && hasNext;
        UpdateTrimControls();
        UpdateClipTransportButtons();
    }

    private void UpdateClipTransportButtons()
    {
        var controlsEnabled = _mediaGrid.Enabled;
        var hasSelectedMedia = GetSelectedMediaGridPath() is not null || File.Exists(_inputPathBox.Text.Trim());

        _clipPreviousCueButton.Enabled = controlsEnabled && GetSelectedMediaGridPath() is not null;
        _clipPlayButton.Enabled = controlsEnabled && hasSelectedMedia;
        var canTogglePauseResume = _isPlaying || _scrubPreviewMode || _nativeSeekPreviewMode;
        var shouldShowResume = _isPaused || _scrubPreviewMode || _nativeSeekPreviewMode;
        _clipPauseButton.Text = shouldShowResume ? "Resume" : "Pause";
        SetButtonToolTip(_clipPauseButton, shouldShowResume ? "Resume playback." : "Pause playback.");
        _clipPauseButton.Enabled = canTogglePauseResume;
        _clipStopButton.Enabled = _isPlaying || _scrubPreviewMode || _nativeSeekPreviewMode;
        var hasNextClip = FindRelativeMediaGridRow(1).HasValue;
        _clipNextCueButton.Enabled = controlsEnabled && hasNextClip;
        _clipPlayNextButton.Enabled = controlsEnabled && hasNextClip;
        ApplyButtonTheme(_clipPauseButton, _darkMode);
    }

    private void UpdateTrimControls()
    {
        var path = _inputPathBox.Text.Trim();
        var hasMedia = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        var isStill = hasMedia && IsImageFile(path);
        var durationKnown = hasMedia && (isStill || GetKnownMediaDuration(path).HasValue || GetCurrentSeekDuration().HasValue);
        var canMark = hasMedia && !isStill && !_playbackIsTestPattern && _positionBar.Enabled && durationKnown;
        var canSeekCommand = hasMedia && !isStill && !_playbackIsTestPattern && _positionBar.Enabled && durationKnown;

        _markInButton.Enabled = canMark;
        _markOutButton.Enabled = canMark;
        _goToInButton.Enabled = canSeekCommand;
        _playFromInButton.Enabled = canSeekCommand;
        _goToTcButton.Enabled = canSeekCommand;
        _goToTcBox.Enabled = canSeekCommand;
        _goToOutButton.Enabled = canSeekCommand;
        _lastSecondsBox.Enabled = canSeekCommand;
        _playLastSecondsButton.Enabled = canSeekCommand;
        UpdatePlaybackSpeedButtons(hasMedia && !isStill && !_playbackIsTestPattern && durationKnown);

        if (TryGetCurrentTrimRange(out _, out var markIn, out var markOut, out _))
        {
            _markInValueBox.Text = FormatPlaylistTime(markIn);
            _markOutValueBox.Text = FormatPlaylistTime(markOut);
        }
        else
        {
            _markInValueBox.Text = "--";
            _markOutValueBox.Text = "--";
        }
    }

    private void UpdatePlaybackSpeedButtons(bool canUseSpeed)
    {
        foreach (var button in _playbackSpeedButtons)
        {
            button.Enabled = canUseSpeed || (_isPlaying && !_playbackIsStillImage && !_playbackIsTestPattern);
            ApplyButtonTheme(button, _darkMode);
        }
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
        _mediaGrid.Font = new Font("Segoe UI", 8.8F, FontStyle.Regular, GraphicsUnit.Point);
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
            Width = 98,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _mediaGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Size",
            HeaderText = "Frame Size",
            Width = 82,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _mediaGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Fps",
            HeaderText = "FPS",
            Width = 58,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft },
        });
        _mediaGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FileSize",
            HeaderText = "File Size",
            Width = 86,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleLeft },
        });

        _mediaGrid.SelectionChanged -= MediaGrid_SelectionChanged;
        _mediaGrid.CellDoubleClick -= MediaGrid_CellDoubleClick;
        _mediaGrid.KeyDown -= MediaGrid_KeyDown;
        _mediaGrid.CellMouseDown -= MediaGrid_CellMouseDown;
        _mediaGrid.MouseDown -= MediaGrid_MouseDown;
        _mediaGrid.MouseMove -= MediaGrid_MouseMove;
        _mediaGrid.MouseUp -= MediaGrid_MouseUp;
        _mediaGrid.SelectionChanged += MediaGrid_SelectionChanged;
        _mediaGrid.CellDoubleClick += MediaGrid_CellDoubleClick;
        _mediaGrid.KeyDown += MediaGrid_KeyDown;
        _mediaGrid.CellMouseDown += MediaGrid_CellMouseDown;
        _mediaGrid.MouseDown += MediaGrid_MouseDown;
        _mediaGrid.MouseMove += MediaGrid_MouseMove;
        _mediaGrid.MouseUp += MediaGrid_MouseUp;
        ConfigureMediaGridContextMenu();
    }

    private void ConfigureMediaGridContextMenu()
    {
        _mediaGridContextMenu.Items.Clear();
        _mediaGridMenuCue.Click -= MediaGridMenuCue_Click;
        _mediaGridMenuPlay.Click -= MediaGridMenuPlay_Click;
        _mediaGridMenuPlayInVlc.Click -= MediaGridMenuPlayInVlc_Click;
        _mediaGridMenuFileInfo.Click -= MediaGridMenuFileInfo_Click;
        _mediaGridContextMenu.Opening -= MediaGridContextMenu_Opening;

        _mediaGridMenuCue.Click += MediaGridMenuCue_Click;
        _mediaGridMenuPlay.Click += MediaGridMenuPlay_Click;
        _mediaGridMenuPlayInVlc.Click += MediaGridMenuPlayInVlc_Click;
        _mediaGridMenuFileInfo.Click += MediaGridMenuFileInfo_Click;
        _mediaGridContextMenu.Opening += MediaGridContextMenu_Opening;

        _mediaGridContextMenu.Items.AddRange(
            [
                _mediaGridMenuCue,
                _mediaGridMenuPlay,
                _mediaGridMenuPlayInVlc,
                _mediaGridMenuFileInfo,
            ]);
        _mediaGrid.ContextMenuStrip = _mediaGridContextMenu;
        ApplyContextMenuTheme(_mediaGridContextMenu, _darkMode);
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
            var emptyRow = _mediaGrid.Rows[_mediaGrid.Rows.Add(emptyMessage, string.Empty, string.Empty, string.Empty, string.Empty)];
            emptyRow.DefaultCellStyle.ForeColor = Color.FromArgb(166, 179, 190);
            return;
        }

        _mediaGrid.SuspendLayout();
        try
        {
            foreach (var file in files)
            {
                var durationText = IsImageFile(file) ? "Still" : "--";
                var rowIndex = _mediaGrid.Rows.Add(GetMediaDisplayPath(file), durationText, "--", "--", GetFileSizeText(file));
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

        UpdateClipTransportButtons();
    }

    private void MediaGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0)
        {
            AddMediaPathToPlaylist(GetSelectedMediaGridPath());
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

    private void MediaGrid_CellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right ||
            e.RowIndex < 0 ||
            e.RowIndex >= _mediaGrid.Rows.Count)
        {
            return;
        }

        var columnIndex = e.ColumnIndex >= 0 ? e.ColumnIndex : GetFirstVisibleMediaGridColumnIndex();
        if (columnIndex < 0)
        {
            return;
        }

        _mediaGrid.CurrentCell = _mediaGrid.Rows[e.RowIndex].Cells[columnIndex];
        _mediaGrid.ClearSelection();
        _mediaGrid.Rows[e.RowIndex].Selected = true;
    }

    private void MediaGrid_MouseDown(object? sender, MouseEventArgs e)
    {
        _mediaGridDragPath = null;
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = _mediaGrid.HitTest(e.X, e.Y);
        if (hit.RowIndex < 0 || hit.RowIndex >= _mediaGrid.Rows.Count)
        {
            return;
        }

        SelectMediaGridRow(hit.RowIndex, hit.ColumnIndex);
        var path = GetMediaGridRowPath(hit.RowIndex);
        if (path is null)
        {
            return;
        }

        _mediaGridDragPath = path;
        _mediaGridDragStart = e.Location;
    }

    private void MediaGrid_MouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _mediaGridDragPath is null)
        {
            return;
        }

        var dragSize = SystemInformation.DragSize;
        var dragRectangle = new Rectangle(
            _mediaGridDragStart.X - dragSize.Width / 2,
            _mediaGridDragStart.Y - dragSize.Height / 2,
            dragSize.Width,
            dragSize.Height);
        if (dragRectangle.Contains(e.Location))
        {
            return;
        }

        var path = _mediaGridDragPath;
        _mediaGridDragPath = null;
        var data = new DataObject();
        data.SetData(MediaGridDragDataFormat, path);
        data.SetData(DataFormats.FileDrop, new[] { path });
        data.SetText(path);
        _mediaGrid.DoDragDrop(data, DragDropEffects.Copy);
    }

    private void MediaGrid_MouseUp(object? sender, MouseEventArgs e)
    {
        _mediaGridDragPath = null;
    }

    private void SelectMediaGridRow(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= _mediaGrid.Rows.Count)
        {
            return;
        }

        var targetColumnIndex = columnIndex >= 0 ? columnIndex : GetFirstVisibleMediaGridColumnIndex();
        if (targetColumnIndex < 0)
        {
            return;
        }

        _mediaGrid.CurrentCell = _mediaGrid.Rows[rowIndex].Cells[targetColumnIndex];
        _mediaGrid.ClearSelection();
        _mediaGrid.Rows[rowIndex].Selected = true;
    }

    private string? GetMediaGridRowPath(int rowIndex)
    {
        return rowIndex >= 0 &&
            rowIndex < _mediaGrid.Rows.Count &&
            _mediaGrid.Rows[rowIndex].Tag is string path &&
            IsSupportedDroppedMediaPath(path)
                ? path
                : null;
    }

    private int? FindRelativeMediaGridRow(int direction)
    {
        if (direction == 0 || _mediaGrid.Rows.Count == 0)
        {
            return null;
        }

        var currentIndex = _mediaGrid.CurrentRow?.Index ?? -1;
        var index = currentIndex < 0
            ? direction > 0 ? 0 : _mediaGrid.Rows.Count - 1
            : currentIndex + Math.Sign(direction);
        while (index >= 0 && index < _mediaGrid.Rows.Count)
        {
            if (GetMediaGridRowPath(index) is not null)
            {
                return index;
            }

            index += Math.Sign(direction);
        }

        return null;
    }

    private async Task CueRelativeMediaGridItemAsync(int direction)
    {
        var index = FindRelativeMediaGridRow(direction);
        if (!index.HasValue)
        {
            SetStatus(direction > 0 ? "No next clip" : "No previous clip", Color.FromArgb(232, 181, 105));
            return;
        }

        SelectMediaGridRow(index.Value, GetFirstVisibleMediaGridColumnIndex());
        await CueSelectedMediaGridAsync();
    }

    private async Task PlayRelativeMediaGridItemAsync(int direction)
    {
        var index = FindRelativeMediaGridRow(direction);
        if (!index.HasValue)
        {
            SetStatus(direction > 0 ? "No next clip" : "No previous clip", Color.FromArgb(232, 181, 105));
            return;
        }

        SelectMediaGridRow(index.Value, GetFirstVisibleMediaGridColumnIndex());
        await PlaySelectedMediaGridAsync();
    }

    private void ToggleClipLoopPlayback()
    {
        _clipLoopPlaybackEnabled = !_clipLoopPlaybackEnabled;
        ApplyButtonTheme(_clipLoopButton, _darkMode);
        SetStatus(
            _clipLoopPlaybackEnabled ? "Single clip loop enabled" : "Single clip loop disabled",
            Color.FromArgb(130, 210, 164));
    }

    private int GetFirstVisibleMediaGridColumnIndex()
    {
        foreach (DataGridViewColumn column in _mediaGrid.Columns)
        {
            if (column.Visible)
            {
                return column.Index;
            }
        }

        return -1;
    }

    private void MediaGridContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var hasMedia = GetSelectedMediaGridPath() is not null;
        _mediaGridMenuCue.Enabled = hasMedia;
        _mediaGridMenuPlay.Enabled = hasMedia;
        _mediaGridMenuPlayInVlc.Enabled = hasMedia;
        _mediaGridMenuFileInfo.Enabled = hasMedia;
    }

    private async void MediaGridMenuCue_Click(object? sender, EventArgs e)
    {
        await CueSelectedMediaGridAsync();
    }

    private async void MediaGridMenuPlay_Click(object? sender, EventArgs e)
    {
        await PlaySelectedMediaGridAsync();
    }

    private void MediaGridMenuPlayInVlc_Click(object? sender, EventArgs e)
    {
        PlaySelectedMediaGridInExternalPlayer("vlc");
    }

    private void MediaGridMenuFileInfo_Click(object? sender, EventArgs e)
    {
        var path = GetSelectedMediaGridPath();
        if (path is null)
        {
            SetStatus("Choose a media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        OpenMediaInfoForm(path);
    }

    private void OpenMediaInfoForm(string path)
    {
        if (!File.Exists(path))
        {
            SetStatus("File missing", Color.FromArgb(229, 113, 105));
            return;
        }

        var infoForm = new MediaInfoForm(path);
        infoForm.Show(this);
        AppendLog($"Opened MediaInfo for {Path.GetFileName(path)}");
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
            ClearTrimMarks();
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
                UpdateMediaGridMetadata(file, metadata.Duration, metadata.Size, metadata.Fps, metadata.FileSize, cancellation.Token);
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

    private async Task<(string Duration, string Size, string Fps, string FileSize)> ProbeMediaGridItemAsync(
        string ffprobePath,
        string path,
        CancellationToken cancellationToken)
    {
        var durationText = IsImageFile(path) ? "Still" : "--";
        var sizeText = "--";
        var fpsText = "--";
        var fileSizeText = GetFileSizeText(path);

        var result = await _deckLink.RunProcessAsync(
            ffprobePath,
            [
                "-v",
                "error",
                "-select_streams",
                "v:0",
                "-show_entries",
                "stream=width,height,avg_frame_rate,r_frame_rate:format=duration",
                "-of",
                "default=noprint_wrappers=1",
                path,
            ],
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
        {
            return (durationText, sizeText, fpsText, fileSizeText);
        }

        int? width = null;
        int? height = null;
        double? seconds = null;
        string? avgFrameRate = null;
        string? streamFrameRate = null;
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
            else if (string.Equals(key, "avg_frame_rate", StringComparison.OrdinalIgnoreCase))
            {
                avgFrameRate = value;
            }
            else if (string.Equals(key, "r_frame_rate", StringComparison.OrdinalIgnoreCase))
            {
                streamFrameRate = value;
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

        if (!IsImageFile(path))
        {
            var fps = ParseFrameRate(avgFrameRate);
            if (fps <= 0)
            {
                fps = ParseFrameRate(streamFrameRate);
            }

            fpsText = FormatFpsText(fps);
        }

        return (durationText, sizeText, fpsText, fileSizeText);
    }

    private void UpdateMediaGridMetadata(string path, string duration, string size, string fps, string fileSize, CancellationToken cancellationToken)
    {
        if (IsDisposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => UpdateMediaGridMetadata(path, duration, size, fps, fileSize, cancellationToken));
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
                row.Cells["Fps"].Value = fps;
                row.Cells["FileSize"].Value = fileSize;
                return;
            }
        }
    }

    private string FormatGridDuration(TimeSpan duration)
    {
        return FormatTimecode(duration);
    }

    private static string FormatFpsText(double fps)
    {
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
        {
            return "--";
        }

        var rounded = Math.Round(fps);
        return Math.Abs(fps - rounded) < 0.005d
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : fps.ToString("0.###", CultureInfo.InvariantCulture);
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
            ClearTrimMarks();
        }

        UpdateDurationLabel();

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && IsImageFile(path))
        {
            _selectedDurationPath = path;
            _selectedMediaDuration = DefaultStillDuration;
            _selectedDurationUnavailable = false;
            if (string.Equals(_playbackPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _playbackDuration = DefaultStillDuration;
                _playbackDurationUnavailable = false;
            }

            UpdateDurationLabel();
            return;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
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

    private void StartPlaybackClock(PlayRequest request, bool startPaused, TimeSpan? sourceDuration = null)
    {
        _playbackStartedAt = DateTime.UtcNow;
        _playbackClockSampleAt = _playbackStartedAt;
        _playbackClockElapsed = TimeSpan.Zero;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackStartOffset = TimeSpan.Zero;
        _playbackEndOffset = null;
        _playbackPath = request.UseTestPattern ? null : request.InputPath;
        _playbackIsStillImage = !request.UseTestPattern && IsImageFile(request.InputPath);
        _playbackIsTestPattern = request.UseTestPattern;
        UpdateLoadedFileLabel();
        _playbackDuration = null;
        _playbackDurationUnavailable = false;

        if (_playbackPath is not null &&
            string.Equals(_selectedDurationPath, _playbackPath, StringComparison.OrdinalIgnoreCase))
        {
            _playbackDuration = _selectedMediaDuration;
            _playbackDurationUnavailable = _selectedDurationUnavailable;
        }

        if (sourceDuration.HasValue)
        {
            _playbackDuration = sourceDuration.Value;
            _playbackDurationUnavailable = false;
        }

        if (request.Duration.HasValue)
        {
            _playbackEndOffset = request.StartOffset + request.Duration.Value;
            if (!_playbackDuration.HasValue || _playbackDuration.Value < _playbackEndOffset.Value)
            {
                _playbackDuration = _playbackEndOffset.Value;
                _playbackDurationUnavailable = false;
            }
        }
        else if (_playbackIsStillImage)
        {
            _playbackDuration = DefaultStillDuration;
            _playbackDurationUnavailable = false;
        }

        _playbackStartOffset = ClampSeekOffset(request.StartOffset, _playbackDuration);
        if (_playbackPath is not null)
        {
            _selectedStartOffset = _playbackStartOffset;
        }

        _isPaused = startPaused;
        _playbackPausedAt = startPaused ? DateTime.UtcNow : null;
        _pauseResumeButton.Text = startPaused ? "Resume" : "Pause";
        UpdatePlaybackSpeedButtons(CanUsePlaybackSpeed());
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
        _playbackClockSampleAt = null;
        _playbackClockElapsed = TimeSpan.Zero;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackStartOffset = TimeSpan.Zero;
        _playbackEndOffset = null;
        _playbackPath = null;
        _playbackDuration = null;
        _playbackDurationUnavailable = false;
        _playbackIsStillImage = false;
        _playbackIsTestPattern = false;
        UpdateLoadedFileLabel();
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
            _selectedMediaDuration = DefaultStillDuration;
            _selectedStartOffset = TimeSpan.Zero;
            SetRemainingDisplay(DefaultStillDuration);
            SetCurrentTimeDisplay(TimeSpan.Zero);
            UpdatePositionBar(DefaultStillDuration, TimeSpan.Zero);
            return;
        }

        if (string.Equals(_selectedDurationPath, path, StringComparison.OrdinalIgnoreCase) &&
            _selectedMediaDuration.HasValue)
        {
            var duration = _selectedMediaDuration.Value;
            _selectedStartOffset = ClampSeekOffset(_selectedStartOffset, duration);
            var remaining = GetMarkedRemainingDuration(path, duration) ?? duration - _selectedStartOffset;
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

    private TimeSpan? GetMarkedRemainingDuration(string path, TimeSpan duration)
    {
        if (!string.Equals(_trimMarkPath, path, StringComparison.OrdinalIgnoreCase) ||
            (!_markInOffset.HasValue && !_markOutOffset.HasValue))
        {
            return null;
        }

        var markIn = ClampSeekOffset(_markInOffset ?? _selectedStartOffset, duration);
        var markOut = ClampSeekOffset(_markOutOffset ?? duration, duration);
        return markOut > markIn ? markOut - markIn : TimeSpan.Zero;
    }

    private void UpdatePlaybackDurationLabel()
    {
        CapturePlaybackClockProgress();
        var elapsedSinceDecoderStart = _playbackClockElapsed;
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

        var remainingEnd = _playbackEndOffset.HasValue && _playbackEndOffset.Value > TimeSpan.Zero
            ? _playbackEndOffset.Value
            : duration;
        if (remainingEnd > duration)
        {
            remainingEnd = duration;
        }

        var remaining = remainingEnd - position;
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

    private async Task BeginScrubPreviewAsync(TimeSpan target, bool queueInitialFrame = true)
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

            StopPlayback(resetPlaybackSpeed: false);
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

        if (queueInitialFrame)
        {
            await QueueScrubPreviewFrameAsync(target);
        }
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
        var size = ParseVideoSize(request.VideoSize)
            ?? throw new InvalidOperationException("Choose a valid DeckLink output size before scrubbing.");

        if (!PreviewOnlyMode &&
            !string.Equals(_scrubPreviewOutputDisabledPath, request.InputPath, StringComparison.OrdinalIgnoreCase))
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
            try
            {
                _scrubPreviewOutput?.DisplayFrame(frame);
            }
            catch (Exception ex)
            {
                _scrubPreviewOutputDisabledPath = _scrubPreviewPath;
                _scrubPreviewOutput?.Dispose();
                _scrubPreviewOutput = null;
                AppendLog($"DeckLink scrub preview output disabled: {ex.Message}");
            }
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

        try
        {
            var fullscreenImage = _fullscreenPreviewForm is { IsDisposed: false }
                ? ClonePreviewImage(bitmap)
                : null;
            var previous = _appPreviewBox.Image;
            _appPreviewBox.Image = bitmap;
            previous?.Dispose();
            UpdateFullscreenPreviewImage(fullscreenImage);
        }
        finally
        {
            Interlocked.Exchange(ref _appPreviewFramePending, 0);
        }
    }

    private void UpdateFullscreenPreviewImage(Image? image)
    {
        var form = _fullscreenPreviewForm;
        if (form is null || form.IsDisposed)
        {
            image?.Dispose();
            return;
        }

        form.SetPreviewImage(image);
    }

    private static Image? ClonePreviewImage(Image? image)
    {
        if (image is null)
        {
            return null;
        }

        try
        {
            return (Image)image.Clone();
        }
        catch
        {
            return null;
        }
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

    private void EnsureScrubPreviewOutput(PlayRequest request, bool forceRetry = false, bool logFailure = true)
    {
        if (!forceRetry &&
            string.Equals(_scrubPreviewOutputDisabledPath, request.InputPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var modeCode = request.FormatCode ?? string.Empty;
        if (_scrubPreviewOutput is not null &&
            string.Equals(_scrubPreviewPath, request.InputPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_scrubPreviewModeCode, modeCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _scrubPreviewOutput?.Dispose();
        try
        {
            _scrubPreviewOutput = new NativeDeckLinkPreviewOutput(request);
            _scrubPreviewPath = request.InputPath;
            _scrubPreviewModeCode = modeCode;
            _scrubPreviewOutputDisabledPath = null;
        }
        catch (Exception ex)
        {
            _scrubPreviewOutput = null;
            _scrubPreviewPath = null;
            _scrubPreviewModeCode = null;
            _scrubPreviewOutputDisabledPath = forceRetry ? null : request.InputPath;
            if (logFailure)
            {
                AppendLog($"DeckLink scrub preview output unavailable: {ex.Message}");
            }
        }
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

        var trackLeft = 10;
        var trackWidth = Math.Max(1, _positionBar.Width - trackLeft * 2);
        var ratio = Math.Clamp((x - trackLeft) / (double)trackWidth, 0, 1);
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

    private bool TryGetCurrentTrimRange(
        out TimeSpan sourceDuration,
        out TimeSpan markIn,
        out TimeSpan markOut,
        out string? path)
    {
        sourceDuration = TimeSpan.Zero;
        markIn = TimeSpan.Zero;
        markOut = TimeSpan.Zero;
        path = GetCurrentMediaPath();
        if (path is null || IsImageFile(path))
        {
            return false;
        }

        var duration = GetCurrentSeekDuration() ?? GetKnownMediaDuration(path);
        if (!duration.HasValue || duration.Value <= TimeSpan.Zero)
        {
            return false;
        }

        sourceDuration = duration.Value;
        var marksMatchPath = string.Equals(_trimMarkPath, path, StringComparison.OrdinalIgnoreCase);
        markIn = marksMatchPath && _markInOffset.HasValue ? _markInOffset.Value : TimeSpan.Zero;
        markOut = marksMatchPath && _markOutOffset.HasValue ? _markOutOffset.Value : sourceDuration;
        markIn = ClampSeekOffset(markIn, sourceDuration);
        markOut = ClampSeekOffset(markOut, sourceDuration);
        if (markOut < markIn)
        {
            markOut = markIn;
        }

        return true;
    }

    private async Task GoToInAsync()
    {
        if (!TryGetCurrentTrimRange(out _, out var markIn, out _, out _))
        {
            SetStatus("IN point unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        await SeekToOffsetAsync(markIn);
    }

    private async Task GoToOutAsync()
    {
        if (!TryGetCurrentTrimRange(out _, out _, out var markOut, out _))
        {
            SetStatus("OUT point unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        await SeekToOffsetAsync(markOut);
    }

    private async Task PlayFromInAsync()
    {
        if (!TryGetCurrentTrimRange(out var sourceDuration, out var markIn, out _, out _))
        {
            SetStatus("IN point unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        await PlayCurrentMediaRangeAsync(markIn, null, sourceDuration);
    }

    private async Task PlayInToOutAsync()
    {
        if (!TryGetCurrentTrimRange(out var sourceDuration, out var markIn, out var markOut, out _))
        {
            SetStatus("IN/OUT points unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        if (markOut <= markIn)
        {
            SetStatus("OUT must be after IN", Color.FromArgb(232, 181, 105));
            return;
        }

        await PlayCurrentMediaRangeAsync(markIn, markOut - markIn, sourceDuration);
    }

    private async Task GoToTimecodeAsync()
    {
        if (!TryParseTimecodeOffset(_goToTcBox.Text, out var target))
        {
            SetStatus("Invalid timecode", Color.FromArgb(232, 181, 105));
            return;
        }

        await SeekToOffsetAsync(target);
    }

    private bool TryParseTimecodeOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        var hours = 0;
        var minutes = 0;
        var seconds = 0;
        var frames = 0;
        if (parts.Length == 4)
        {
            if (!TryParseNonNegativeInt(parts[0], out hours) ||
                !TryParseNonNegativeInt(parts[1], out minutes) ||
                !TryParseNonNegativeInt(parts[2], out seconds) ||
                !TryParseNonNegativeInt(parts[3], out frames))
            {
                return false;
            }
        }
        else if (parts.Length == 3)
        {
            if (!TryParseNonNegativeInt(parts[0], out hours) ||
                !TryParseNonNegativeInt(parts[1], out minutes) ||
                !TryParseNonNegativeInt(parts[2], out seconds))
            {
                return false;
            }
        }
        else if (parts.Length == 2)
        {
            if (!TryParseNonNegativeInt(parts[0], out minutes) ||
                !TryParseNonNegativeInt(parts[1], out seconds))
            {
                return false;
            }
        }
        else if (!TryParseNonNegativeInt(parts[0], out seconds))
        {
            return false;
        }

        var nominalFrameRate = GetNominalFrameRate(GetDisplayFrameRate());
        if (frames >= nominalFrameRate)
        {
            return false;
        }

        offset = TimeSpan.FromSeconds(hours * 3600d + minutes * 60d + seconds + frames / (double)nominalFrameRate);
        return true;
    }

    private static bool TryParseNonNegativeInt(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private async Task PlayLastSecondsAsync()
    {
        if (!TryGetCurrentTrimRange(out var sourceDuration, out _, out var markOut, out _))
        {
            SetStatus("OUT point unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        var seconds = TimeSpan.FromSeconds((double)_lastSecondsBox.Value);
        var start = markOut - seconds;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var duration = markOut - start;
        if (duration <= TimeSpan.Zero)
        {
            SetStatus("Last seconds duration unavailable", Color.FromArgb(232, 181, 105));
            return;
        }

        await PlayCurrentMediaRangeAsync(start, duration, sourceDuration);
    }

    private async Task PlayCurrentMediaRangeAsync(TimeSpan startOffset, TimeSpan? playDuration, TimeSpan? sourceDuration)
    {
        var path = GetCurrentMediaPath();
        if (path is null || !SelectMediaPath(path))
        {
            SetStatus("Choose a media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        await StopActivePlaybackForReplacementAsync();
        _selectedStartOffset = ClampSeekOffset(startOffset, sourceDuration);
        await StartPlaybackAsync(
            dryRun: false,
            startOffset: _selectedStartOffset,
            playDuration: playDuration,
            sourceDuration: sourceDuration);
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
        StopReverseDeckLinkFramePump();
        DisposeReverseAudio();
        SetReverseCacheStatusFromAnyThread(null, Color.Empty);
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
        _scrubPreviewOutputDisabledPath = null;
        _nextReverseDeckLinkRetryAt = DateTime.MinValue;
        _lastReverseDeckLinkFailureLogAt = DateTime.MinValue;
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

        CapturePlaybackClockProgress();
        var elapsedSinceDecoderStart = _playbackClockElapsed;
        if (elapsedSinceDecoderStart < TimeSpan.Zero)
        {
            elapsedSinceDecoderStart = TimeSpan.Zero;
        }

        return ClampSeekOffset(_playbackStartOffset + elapsedSinceDecoderStart, _playbackDuration);
    }

    private TimeSpan GetFrameDuration()
    {
        var rate = GetDisplayFrameRate();
        if (rate <= 0)
        {
            rate = 25;
        }

        return TimeSpan.FromSeconds(1 / rate);
    }

    private double GetDisplayFrameRate()
    {
        return 25;
    }

    private static int GetNominalFrameRate(double frameRate)
    {
        if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
        {
            return 25;
        }

        return Math.Clamp((int)Math.Round(frameRate, MidpointRounding.AwayFromZero), 1, 120);
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
        _positionStartLabel.Text = FormatClock(TimeSpan.Zero);
        _positionEndLabel.Text = endText;
        SetCurrentTimeDisplay(string.Equals(endText, "live", StringComparison.OrdinalIgnoreCase) ? "LIVE" : "--");
        UpdateSeekRangeHighlight();
    }

    private void UpdatePositionBar(TimeSpan duration, TimeSpan position)
    {
        if (duration <= TimeSpan.Zero)
        {
            ResetPositionBar();
            return;
        }

        var selectedPath = _inputPathBox.Text.Trim();
        var selectedStill = !_isPlaying && File.Exists(selectedPath) && IsImageFile(selectedPath);
        SetSeekControlsEnabled(!_playbackIsStillImage && !_playbackIsTestPattern && !selectedStill);
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

        _positionStartLabel.Text = FormatClock(TimeSpan.Zero);
        _positionEndLabel.Text = FormatClock(duration, roundUp: true);
        SetCurrentTimeDisplay(position);
        UpdateSeekRangeHighlight();
    }

    private void UpdateSeekRangeHighlight()
    {
        if (!TryGetSeekRangeHighlight(out var startRatio, out var endRatio))
        {
            _positionBar.ClearRange();
            return;
        }

        _positionBar.SetRange(startRatio, endRatio);
    }

    private bool TryGetSeekRangeHighlight(out double startRatio, out double endRatio)
    {
        startRatio = 0;
        endRatio = 0;
        if (!_positionBar.Enabled)
        {
            return false;
        }

        var path = GetCurrentMediaPath();
        if (path is null ||
            !string.Equals(_trimMarkPath, path, StringComparison.OrdinalIgnoreCase) ||
            !_markInOffset.HasValue ||
            !_markOutOffset.HasValue)
        {
            return false;
        }

        var duration = GetCurrentSeekDuration() ?? GetKnownMediaDuration(path);
        if (!duration.HasValue || duration.Value <= TimeSpan.Zero)
        {
            return false;
        }

        var markIn = ClampSeekOffset(_markInOffset.Value, duration.Value);
        var markOut = ClampSeekOffset(_markOutOffset.Value, duration.Value);
        if (markOut <= markIn)
        {
            return false;
        }

        startRatio = Math.Clamp(markIn.TotalMilliseconds / duration.Value.TotalMilliseconds, 0d, 1d);
        endRatio = Math.Clamp(markOut.TotalMilliseconds / duration.Value.TotalMilliseconds, 0d, 1d);
        return endRatio > startRatio;
    }

    private void SetSeekControlsEnabled(bool enabled)
    {
        _positionBar.Enabled = enabled;
        if (!enabled)
        {
            _positionBar.ClearRange();
        }

        _seekBackOneSecondButton.Enabled = enabled;
        _seekBackTenFramesButton.Enabled = enabled;
        _seekBackFiveFramesButton.Enabled = enabled;
        _seekBackOneFrameButton.Enabled = enabled;
        _seekForwardOneFrameButton.Enabled = enabled;
        _seekForwardFiveFramesButton.Enabled = enabled;
        _seekForwardTenFramesButton.Enabled = enabled;
        _seekForwardOneSecondButton.Enabled = enabled;
        UpdateTrimControls();
        UpdateClipTransportButtons();
    }

    private string FormatClock(TimeSpan value, bool roundUp = false)
    {
        return FormatTimecode(value, roundUp);
    }

    private string FormatTimecode(TimeSpan value, bool roundUp = false)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var frameRate = GetNominalFrameRate(GetDisplayFrameRate());
        var totalSeconds = value.TotalSeconds;
        var wholeSeconds = (long)Math.Floor(totalSeconds);
        var fractionalFrames = (totalSeconds - wholeSeconds) * frameRate;
        var frame = roundUp
            ? (int)Math.Ceiling(fractionalFrames - 0.000001d)
            : (int)Math.Floor(fractionalFrames + 0.000001d);

        if (frame >= frameRate)
        {
            wholeSeconds++;
            frame = 0;
        }

        var hours = wholeSeconds / 3600;
        var minutes = (wholeSeconds / 60) % 60;
        var seconds = wholeSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}:{frame:00}";
    }

    private async Task PlaySelectedMediaGridAsync()
    {
        var path = GetSelectedMediaGridPath();
        if (path is null || !SelectMediaPath(path))
        {
            if (!_isPlaying && File.Exists(_inputPathBox.Text.Trim()))
            {
                await StartPlaybackAsync(dryRun: false, playLoop: _clipLoopPlaybackEnabled);
            }

            return;
        }

        if (!_isPlaying)
        {
            ClearPlaylistActivePlaybackForMediaStart();
            await StartPlaybackAsync(
                dryRun: false,
                sourceDuration: GetKnownMediaDuration(path),
                playLoop: _clipLoopPlaybackEnabled);
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
            ClearPlaylistActivePlaybackForMediaStart();
            _ = StartPlaybackAsync(
                dryRun: false,
                sourceDuration: GetKnownMediaDuration(path),
                playLoop: _clipLoopPlaybackEnabled);
        }
        finally
        {
            _switchingPlayback = false;
        }
    }

    private async Task CueSelectedMediaGridAsync()
    {
        var path = GetSelectedMediaGridPath();
        if (path is null || !SelectMediaPath(path))
        {
            SetStatus("Choose a media file first", Color.FromArgb(232, 181, 105));
            return;
        }

        if (_switchingPlayback)
        {
            return;
        }

        try
        {
            await StopActivePlaybackForReplacementAsync();
            ClearPlaylistActivePlaybackForMediaStart();
            _selectedStartOffset = TimeSpan.Zero;
            AppendLog($"Cueing clip paused: {Path.GetFileName(path)}");
            SetStatus($"Cueing {Path.GetFileName(path)}", Color.FromArgb(126, 188, 226));
            await StartPlaybackAsync(
                dryRun: false,
                startOffset: TimeSpan.Zero,
                startPaused: true,
                sourceDuration: GetKnownMediaDuration(path));
        }
        catch (Exception ex)
        {
            AppendLog($"Cue failed: {ex.Message}");
            SetStatus("Cue failed", Color.FromArgb(229, 113, 105));
        }
    }

    private void ClearPlaylistActivePlaybackForMediaStart()
    {
        _playlistPlaybackActive = false;
        ClearPlaylistTransitionLeadIn();
        if (!_playlistPlayingIndex.HasValue)
        {
            UpdatePlaylistButtons();
            return;
        }

        var index = _playlistPlayingIndex.Value;
        _playlistPlayingIndex = null;
        if (index >= 0 &&
            index < _playlistItems.Count &&
            _playlistItems[index].Status == PlaylistStatusPlaying)
        {
            _playlistItems[index].Status = PlaylistStatusReady;
            RefreshPlaylistGrid(index);
        }
        else
        {
            UpdatePlaylistButtons();
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
        if (IsPlaylistEndMarkerText(path))
        {
            return PlaylistEndMarkerText;
        }

        try
        {
            return Path.GetRelativePath(_mediaRootPath, path);
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private static bool IsPlaylistEndMarkerText(string text)
    {
        return string.Equals(text.Trim(), PlaylistEndMarkerText, StringComparison.OrdinalIgnoreCase);
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

    private void ApplyDeckLinkUnavailableFallback(string message, bool clearDeckLinkSelection = true)
    {
        if (clearDeckLinkSelection)
        {
            _deviceBox.Items.Clear();
        }

        AppendLog(message);
        SetStatus(clearDeckLinkSelection ? "No DeckLink detected" : "DeckLink unavailable", Color.FromArgb(232, 181, 105));
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
        bool startPaused = false,
        TimeSpan? playDuration = null,
        TimeSpan? sourceDuration = null,
        bool playLoop = false,
        string? videoFilter = null,
        string? audioFilter = null,
        PlaylistTransitionSegment? transitionSegment = null)
    {
        if (_isPlaying)
        {
            return;
        }

        var completedNormally = false;
        try
        {
            var previewOnly = PreviewOnlyMode;
            var pcAudio = PcAudioMode;
            var request = BuildRequest(useTestPattern, startOffset ?? _selectedStartOffset, playDuration, playLoop, videoFilter, audioFilter, transitionSegment);
            var holdDeckLinkVideoForPlaylistAdvance = ShouldHoldDeckLinkVideoForPlaylistAdvance(previewOnly);
            var commandText = _sdkPlayer.FormatDecoderCommand(
                request,
                throttleAudioRealtime: false,
                monitorPcAudio: pcAudio,
                useInternalPcAudioMonitor: pcAudio);

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

            if (_selectedPlaybackSpeed <= 0d)
            {
                startPaused = true;
            }

            var pauseController = new PlaybackPauseController
            {
                PlaybackSpeed = _selectedPlaybackSpeed > 0d ? _selectedPlaybackSpeed : 1d,
            };
            if (startPaused)
            {
                pauseController.Pause();
            }

            _playbackCancellation = new CancellationTokenSource();
            _playbackPauseController = pauseController;
            _playbackStoppedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPlaying(true);
            StartPlaybackClock(request, startPaused, sourceDuration);
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
                        previewFrameInterval: 5,
                        audioMeter: UpdateAudioMeters,
                        monitorPcAudio: pcAudio,
                        holdVideoOutputOnNaturalEnd: holdDeckLinkVideoForPlaylistAdvance),
                _playbackCancellation.Token);

            AppendLog(result.Cancelled
                ? "Playback stopped."
                : previewOnly
                    ? $"Preview engine exited with code {result.ExitCode}."
                    : $"DeckLink SDK engine exited with code {result.ExitCode}.");
            completedNormally = !result.Cancelled && result.ExitCode == 0;
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
            ApplyDeckLinkUnavailableFallback($"DeckLink output unavailable. Preview-only mode is available. {FirstLine(ex.Message)}", clearDeckLinkSelection: false);
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
            if (_playlistPlayingIndex.HasValue && !_switchingPlayback)
            {
                CompletePlaylistPlayback(completedNormally);
            }

            StopPlaybackClock();
            SetPlaying(false);
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
            _playbackStoppedSignal = null;
            stoppedSignal?.TrySetResult();
            UpdateDurationLabel();
        }
    }

    private void StopPlayback(bool resetPlaybackSpeed = true)
    {
        if (!_switchingPlayback)
        {
            if (resetPlaybackSpeed)
            {
                StopReversePlaybackSpeed();
                _selectedPlaybackSpeed = 1d;
                UpdatePlaybackSpeedButtons(CanUsePlaybackSpeed());
            }

            _playlistPlaybackActive = false;
            UpdatePlaylistButtons();
        }

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

    private bool ShouldHoldDeckLinkVideoForPlaylistAdvance(bool previewOnly)
    {
        if (previewOnly || !_playlistPlaybackActive || !_playlistPlayingIndex.HasValue)
        {
            return false;
        }

        var index = _playlistPlayingIndex.Value;
        if (index < 0 || index >= _playlistItems.Count)
        {
            return false;
        }

        var currentItem = _playlistItems[index];
        if (!currentItem.IsEndMarker &&
            currentItem.LoopEnabled &&
            currentItem.PlayEnabled &&
            File.Exists(currentItem.FullPath))
        {
            return true;
        }

        return FindNextRepeatingPlaylistIndex(index, out _).HasValue;
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

        CapturePlaybackClockProgress();
        _playbackPauseController.Pause();
        _isPaused = true;
        _playbackPausedAt = DateTime.UtcNow;
        _playbackPositionTimer.Stop();
        _pauseResumeButton.Text = "Resume";
        AppendLog("Playback paused.");
        SetStatus("Paused", Color.FromArgb(232, 181, 105));
        UpdateDurationLabel();
        UpdateClipTransportButtons();
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
        _playbackClockSampleAt = DateTime.UtcNow;
        _playbackPauseController.Resume();
        _playbackPositionTimer.Start();
        _pauseResumeButton.Text = "Pause";
        AppendLog("Playback resumed.");
        SetStatus("Playing", Color.FromArgb(126, 188, 226));
        UpdateDurationLabel();
        UpdateClipTransportButtons();
    }

    private async Task SetPlaybackSpeedAsync(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed))
        {
            return;
        }

        CapturePlaybackClockProgress();
        _selectedPlaybackSpeed = speed;
        UpdatePlaybackSpeedButtons(CanUsePlaybackSpeed());

        if (speed < 0d)
        {
            await StartReversePlaybackSpeedAsync(speed);
            return;
        }

        StopReversePlaybackSpeed();
        if (speed == 0d)
        {
            if (_playbackPauseController is not null)
            {
                _playbackPauseController.PlaybackSpeed = 1d;
            }

            if (_isPlaying && !_isPaused)
            {
                PausePlayback();
            }

            SetStatus("Speed 0x", Color.FromArgb(232, 181, 105));
            return;
        }

        if (_scrubPreviewMode)
        {
            _scrubPreviewReturnPaused = false;
            await FinishScrubPreviewAsync(_selectedStartOffset);
            SetStatus($"Speed {FormatPlaybackSpeedButtonText(speed)}", Color.FromArgb(126, 188, 226));
            return;
        }

        if (_playbackPauseController is not null)
        {
            _playbackPauseController.PlaybackSpeed = speed;
        }

        if (!_isPlaying)
        {
            if (!CanUsePlaybackSpeed())
            {
                SetStatus("Choose a video file first", Color.FromArgb(232, 181, 105));
                return;
            }

            await StartPlaybackAsync(dryRun: false, startOffset: GetCurrentSeekPosition());
        }
        else if (_isPaused)
        {
            ResumePlayback();
        }

        _playbackPositionTimer.Start();
        SetStatus($"Speed {FormatPlaybackSpeedButtonText(speed)}", Color.FromArgb(126, 188, 226));
    }

    private async Task StartReversePlaybackSpeedAsync(double speed)
    {
        if (_playbackIsStillImage || _playbackIsTestPattern)
        {
            SetStatus("Reverse speed is for video files", Color.FromArgb(232, 181, 105));
            StopReversePlaybackSpeed();
            return;
        }

        if (!CanUsePlaybackSpeed() && !_isPlaying)
        {
            SetStatus("Choose a video file first", Color.FromArgb(232, 181, 105));
            StopReversePlaybackSpeed();
            return;
        }

        StopReversePlaybackSpeed();
        _reversePlaybackSpeed = speed;
        var target = GetCurrentSeekPosition();

        if (!NativeFfmpegFrameDecoder.IsAvailable())
        {
            await StartReverseSeekTimerAsync(target, speed);
            return;
        }

        try
        {
            await StartReverseCacheAsync(target, speed);
        }
        catch (Exception ex)
        {
            AppendLog($"Reverse cache startup failed: {ex.Message}");
            SetStatus("Reverse cache unavailable", Color.FromArgb(232, 181, 105));
            await StartReverseSeekTimerAsync(target, speed);
        }
    }

    private async Task StartReverseSeekTimerAsync(TimeSpan target, double speed)
    {
        _reversePlaybackSpeed = speed;
        if (!_scrubPreviewMode)
        {
            await BeginScrubPreviewAsync(target);
        }

        _reversePlaybackFrameCarry = 0d;
        _reversePlaybackLastTickAt = DateTime.UtcNow;
        _reversePlaybackSpeedTimer.Interval = GetReversePlaybackTimerInterval();
        _reversePlaybackSpeedTimer.Start();
        SetStatus($"Reverse {Math.Abs(speed).ToString("0.##", CultureInfo.InvariantCulture)}x", Color.FromArgb(232, 181, 105));
    }

    private async Task StartReverseCacheAsync(TimeSpan target, double speed)
    {
        var duration = GetCurrentSeekDuration();
        if (!duration.HasValue || duration.Value <= TimeSpan.Zero)
        {
            SetStatus("Duration unavailable", Color.FromArgb(232, 181, 105));
            StopReversePlaybackSpeed();
            return;
        }

        target = ClampSeekOffset(target, duration.Value);
        if (!_scrubPreviewMode)
        {
            await BeginScrubPreviewAsync(target, queueInitialFrame: false);
        }

        DisposeScrubPreviewHelper();
        var request = BuildRequest(false, target);
        var size = ParseVideoSize(request.VideoSize)
            ?? throw new InvalidOperationException("Choose a valid DeckLink output size before reverse playback.");
        var previewOnly = PreviewOnlyMode;
        if (!previewOnly)
        {
            _scrubPreviewOutputDisabledPath = null;
            _nextReverseDeckLinkRetryAt = DateTime.MinValue;
            _lastReverseDeckLinkFailureLogAt = DateTime.MinValue;
            EnsureScrubPreviewOutput(request, forceRetry: true);
        }

        UpdateScrubPreviewClock(request, target);
        UpdatePositionBar(duration.Value, target);
        ResetAudioMeters();
        SetReverseCacheStatusFromAnyThread("CACHE", Color.FromArgb(232, 181, 105));
        DisposeReverseAudio();
        var frameDuration = GetFrameDuration();
        var reverseAudioSpeed = Math.Abs(speed);
        var reverseAudioEnabled = !request.NoAudio && reverseAudioSpeed <= 20.001d;
        if (reverseAudioEnabled)
        {
            _reverseAudio = new ReverseAudioChunkQueue(request, reverseAudioSpeed, target, AppendLog);
            if (!previewOnly && reverseAudioSpeed <= 20.001d && _scrubPreviewOutput is not null)
            {
                try
                {
                    _reverseDeckLinkAudioEnabled = _scrubPreviewOutput.TryEnableAudio(request.AudioChannels);
                    if (_reverseDeckLinkAudioEnabled)
                    {
                        AppendLog(reverseAudioSpeed > 2.001d
                            ? "Reverse DeckLink audio enabled directly for high-speed shuttle test."
                            : "Reverse DeckLink audio enabled.");
                    }
                    else
                    {
                        AppendLog("Reverse DeckLink audio unavailable on this SDK output interface.");
                    }
                }
                catch (Exception ex)
                {
                    _reverseDeckLinkAudioEnabled = false;
                    AppendLog($"Reverse DeckLink audio unavailable: {ex.Message}");
                }
            }
            else if (!previewOnly && reverseAudioSpeed > 20.001d)
            {
                AppendLog("Reverse DeckLink audio muted above -20x for stability.");
            }

            if (PcAudioMode)
            {
                try
                {
                    _reverseWaveAudioOutput = new ReverseWaveOutAudioOutput(request.AudioChannels, GetReverseAudioMonitorGain(reverseAudioSpeed));
                    AppendLog("Reverse PC audio monitor started.");
                }
                catch (Exception ex)
                {
                    AppendLog($"Reverse PC audio unavailable: {ex.Message}");
                }
            }
        }
        else if (!request.NoAudio)
        {
            AppendLog("Reverse audio muted above -20x to keep high-speed shuttle responsive.");
        }
        if (!previewOnly)
        {
            _reverseDeckLinkFrameTimer.Interval = GetReverseDeckLinkFrameInterval();
            _reverseDeckLinkRequest = request;
            _reverseDeckLinkFrame = null;
            _reverseDeckLinkFrameTimer.Start();
        }

        var cancellation = new CancellationTokenSource();
        _reversePlaybackCancellation = cancellation;
        _reversePlaybackFrameCarry = 0d;
        _reversePlaybackLastTickAt = null;
        SetStatus($"Building reverse cache {Math.Abs(speed).ToString("0.##", CultureInfo.InvariantCulture)}x", Color.FromArgb(232, 181, 105));

        _ = Task.Run(
            () => RunReverseCachePlaybackAsync(
                request,
                size.Width,
                size.Height,
                duration.Value,
                target,
                frameDuration,
                speed,
                previewOnly,
                cancellation),
            cancellation.Token);
    }

    private async Task RunReverseCachePlaybackAsync(
        PlayRequest request,
        int width,
        int height,
        TimeSpan duration,
        TimeSpan startPosition,
        TimeSpan frameDuration,
        double requestedSpeed,
        bool previewOnly,
        CancellationTokenSource cancellationSource)
    {
        var cancellationToken = cancellationSource.Token;
        try
        {
            var requestedSpeedMagnitude = Math.Abs(requestedSpeed);
            using var cache = new ReverseFrameCache(request.InputPath, width, height, frameDuration, requestedSpeedMagnitude, AppendLog);
            var position = ClampSeekOffset(startPosition, duration);
            var stopwatch = Stopwatch.StartNew();
            var frameTicks = Math.Max(1L, (long)Math.Round(Stopwatch.Frequency * frameDuration.TotalSeconds));
            var nextFrameDueTicks = stopwatch.ElapsedTicks;
            var sourceFrameCarry = 0d;

            while (!cancellationToken.IsCancellationRequested && _reversePlaybackSpeed < 0d)
            {
                var requestedPosition = position;
                var cacheWait = Stopwatch.StartNew();
                var cachedFrame = cache.GetFrame(position, cancellationToken);
                cacheWait.Stop();
                position = ClampSeekOffset(cachedFrame.Position, duration);
                cancellationToken.ThrowIfCancellationRequested();
                UpdateReverseCacheStatusFromAnyThread(requestedPosition, cachedFrame.Position, cacheWait.Elapsed, frameDuration, requestedSpeedMagnitude);
                if (!DisplayReverseCachedFrame(request, cachedFrame.Data, width, height, cachedFrame.Position, duration, frameDuration, previewOnly))
                {
                    break;
                }

                if (position <= TimeSpan.Zero)
                {
                    CompleteReversePlaybackAtStart();
                    break;
                }

                var speed = Math.Abs(_reversePlaybackSpeed);
                if (speed <= 0d)
                {
                    break;
                }

                sourceFrameCarry += speed;
                var framesToStep = Math.Max(1, (int)Math.Floor(sourceFrameCarry));
                sourceFrameCarry -= framesToStep;
                var nextTicks = position.Ticks - frameDuration.Ticks * framesToStep;
                position = nextTicks > 0 ? TimeSpan.FromTicks(nextTicks) : TimeSpan.Zero;

                nextFrameDueTicks += frameTicks;
                var delayTicks = nextFrameDueTicks - stopwatch.ElapsedTicks;
                if (delayTicks > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency), cancellationToken);
                }
                else
                {
                    nextFrameDueTicks = stopwatch.ElapsedTicks;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user stops reverse, changes speed, or resumes forward playback.
        }
        catch (Exception ex)
        {
            AppendLog($"Reverse cache playback stopped: {ex.Message}");
            SetStatusFromAnyThread("Reverse cache error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            if (ReferenceEquals(_reversePlaybackCancellation, cancellationSource))
            {
                _reversePlaybackCancellation = null;
            }

            SetReverseCacheStatusFromAnyThread(null, Color.Empty);
            cancellationSource.Dispose();
        }
    }

    private bool DisplayReverseCachedFrame(
        PlayRequest request,
        byte[] frame,
        int width,
        int height,
        TimeSpan target,
        TimeSpan duration,
        TimeSpan frameDuration,
        bool previewOnly)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return false;
        }

        if (InvokeRequired)
        {
            try
            {
                return (bool)Invoke(new Func<bool>(() => DisplayReverseCachedFrame(
                    request,
                    frame,
                    width,
                    height,
                    target,
                    duration,
                    frameDuration,
                    previewOnly)));
            }
            catch
            {
                return false;
            }
        }

        if (!_scrubPreviewMode || _reversePlaybackSpeed >= 0d)
        {
            return false;
        }

        _selectedStartOffset = target;
        _playbackStartOffset = target;
        _playbackDuration = duration;
        _playbackStartedAt = DateTime.UtcNow;
        _playbackPausedAt = _playbackStartedAt;
        _playbackPausedDuration = TimeSpan.Zero;
        _playbackPath = request.InputPath;
        _playbackIsStillImage = false;
        _playbackIsTestPattern = false;
        _isPaused = true;
        UpdatePositionBar(duration, target);

        if (!previewOnly)
        {
            SetReverseDeckLinkFrame(request, frame);
        }

        PumpReverseAudioFrame(frameDuration, previewOnly);
        UpdateAppPreviewFrame(frame, width, height);
        SetStatus($"Reverse cache {FormatClock(target)}", Color.FromArgb(232, 181, 105));
        UpdateDurationLabel();
        return true;
    }

    private void PumpReverseAudioFrame(TimeSpan frameDuration, bool previewOnly)
    {
        var reverseAudio = _reverseAudio;
        if (reverseAudio is null)
        {
            return;
        }

        var audioFrame = reverseAudio.ReadFrame(frameDuration);
        var audioByteCount = audioFrame.SampleFrames * FfmpegDeckLink.DefaultAudioChannels * sizeof(int);
        if (!audioFrame.HasAudio)
        {
            _reverseWaveAudioOutput?.Enqueue(audioFrame.Pcm, audioByteCount);
            _reversePcAudioOutput?.Enqueue(audioFrame.Pcm, audioByteCount);
            WriteReverseDeckLinkAudio(audioFrame.Pcm, audioFrame.SampleFrames, previewOnly);
            return;
        }

        UpdateReverseAudioMeters(audioFrame.Pcm, audioFrame.SampleFrames);
        _reverseWaveAudioOutput?.Enqueue(audioFrame.Pcm, audioByteCount);
        _reversePcAudioOutput?.Enqueue(audioFrame.Pcm, audioByteCount);
        WriteReverseDeckLinkAudio(audioFrame.Pcm, audioFrame.SampleFrames, previewOnly);
    }

    private void WriteReverseDeckLinkAudio(byte[] pcm, int sampleFrames, bool previewOnly)
    {
        if (!_reverseDeckLinkAudioEnabled || previewOnly || sampleFrames <= 0)
        {
            return;
        }

        var audioByteCount = sampleFrames * FfmpegDeckLink.DefaultAudioChannels * sizeof(int);
        if (_reverseDeckLinkAudioOutput is not null)
        {
            if (!_reverseDeckLinkAudioOutput.Enqueue(pcm, audioByteCount, sampleFrames))
            {
                _reverseDeckLinkAudioEnabled = false;
            }

            return;
        }

        try
        {
            var wrote = _scrubPreviewOutput?.WriteAudioSamples(pcm, sampleFrames) == true;
            if (!wrote)
            {
                _reverseDeckLinkAudioEnabled = false;
                AppendLog("Reverse DeckLink audio stopped: audio write stalled or output is unavailable.");
            }
        }
        catch (Exception ex)
        {
            _reverseDeckLinkAudioEnabled = false;
            AppendLog($"Reverse DeckLink audio stopped: {ex.Message}");
        }
    }

    private void UpdateReverseAudioMeters(byte[] pcm, int sampleFrames)
    {
        var channels = FfmpegDeckLink.DefaultAudioChannels;
        var bytesPerSampleFrame = channels * sizeof(int);
        if (channels <= 0 || bytesPerSampleFrame <= 0 || pcm.Length < bytesPerSampleFrame)
        {
            return;
        }

        var usableSampleFrames = Math.Min(sampleFrames, pcm.Length / bytesPerSampleFrame);
        var peakLeft = 0L;
        var peakRight = 0L;
        for (var sampleFrame = 0; sampleFrame < usableSampleFrames; sampleFrame++)
        {
            var offset = sampleFrame * bytesPerSampleFrame;
            var left = BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset, sizeof(int)));
            var right = channels > 1
                ? BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset + sizeof(int), sizeof(int)))
                : left;
            peakLeft = Math.Max(peakLeft, Math.Abs((long)left));
            peakRight = Math.Max(peakRight, Math.Abs((long)right));
        }

        UpdateAudioMeters(ToDbfs(peakLeft), ToDbfs(peakRight));
    }

    private static double ToDbfs(long peak)
    {
        if (peak <= 0)
        {
            return -90;
        }

        var normalized = Math.Min(1.0, peak / (double)int.MaxValue);
        return Math.Max(-90, 20 * Math.Log10(normalized));
    }

    private static double GetReverseAudioMonitorGain(double speed)
    {
        if (speed >= 10d)
        {
            return 0.45d;
        }

        if (speed >= 5d)
        {
            return 0.65d;
        }

        return 0.85d;
    }

    private void DisposeReverseAudio()
    {
        _reverseDeckLinkAudioEnabled = false;
        _reverseDeckLinkAudioOutput?.Dispose();
        _reverseDeckLinkAudioOutput = null;
        _reverseWaveAudioOutput?.Dispose();
        _reverseWaveAudioOutput = null;
        _reversePcAudioOutput?.Dispose();
        _reversePcAudioOutput = null;
        _reverseAudio?.Dispose();
        _reverseAudio = null;
    }

    private void UpdateReverseCacheStatusFromAnyThread(
        TimeSpan requestedPosition,
        TimeSpan deliveredPosition,
        TimeSpan waitTime,
        TimeSpan frameDuration,
        double speed)
    {
        if (speed < 5d)
        {
            SetReverseCacheStatusFromAnyThread(null, Color.Empty);
            return;
        }

        var jumpTolerance = TimeSpan.FromTicks(Math.Max(frameDuration.Ticks * 3, TimeSpan.FromMilliseconds(120).Ticks));
        if (requestedPosition - deliveredPosition > jumpTolerance)
        {
            SetReverseCacheStatusFromAnyThread("SKIP", Color.FromArgb(232, 181, 105));
            return;
        }

        if (waitTime > TimeSpan.FromMilliseconds(120))
        {
            SetReverseCacheStatusFromAnyThread("CACHE", Color.FromArgb(232, 181, 105));
            return;
        }

        SetReverseCacheStatusFromAnyThread(null, Color.Empty);
    }

    private void SetReverseCacheStatusFromAnyThread(string? text, Color color)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetReverseCacheStatusFromAnyThread(text, color));
            }
            catch
            {
                // The form may be closing while a cache worker is stopping.
            }

            return;
        }

        _reverseCacheStatusLabel.Text = text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text) && color != Color.Empty)
        {
            _reverseCacheStatusLabel.ForeColor = color;
        }

        _reverseCacheStatusLabel.Visible = !string.IsNullOrWhiteSpace(text);
    }

    private void SetReverseDeckLinkFrame(PlayRequest request, byte[] frame)
    {
        _reverseDeckLinkRequest = request;
        _reverseDeckLinkFrame = frame;
        if (!_reverseDeckLinkFrameTimer.Enabled)
        {
            _reverseDeckLinkFrameTimer.Interval = GetReverseDeckLinkFrameInterval();
            _reverseDeckLinkFrameTimer.Start();
        }

        DisplayReverseFrameToDeckLink(request, frame);
    }

    private void ReverseDeckLinkFrameTimer_Tick()
    {
        if (!_scrubPreviewMode || _reverseDeckLinkRequest is null || _reverseDeckLinkFrame is null)
        {
            return;
        }

        DisplayReverseFrameToDeckLink(_reverseDeckLinkRequest, _reverseDeckLinkFrame);
    }

    private void StopReverseDeckLinkFramePump()
    {
        _reverseDeckLinkFrameTimer.Stop();
        _reverseDeckLinkRequest = null;
        _reverseDeckLinkFrame = null;
    }

    private void DisplayReverseFrameToDeckLink(PlayRequest request, byte[] frame)
    {
        if (_scrubPreviewOutput is null)
        {
            var now = DateTime.UtcNow;
            if (now < _nextReverseDeckLinkRetryAt)
            {
                return;
            }

            _nextReverseDeckLinkRetryAt = now + ReverseDeckLinkRetryInterval;
            _scrubPreviewOutputDisabledPath = null;
            EnsureScrubPreviewOutput(request, forceRetry: true, logFailure: false);
        }

        try
        {
            _scrubPreviewOutput?.DisplayFrame(frame);
        }
        catch (Exception ex)
        {
            _scrubPreviewOutput?.Dispose();
            _scrubPreviewOutput = null;
            _scrubPreviewPath = null;
            _scrubPreviewModeCode = null;
            _scrubPreviewOutputDisabledPath = null;
            _nextReverseDeckLinkRetryAt = DateTime.UtcNow + ReverseDeckLinkRetryInterval;

            if (DateTime.UtcNow - _lastReverseDeckLinkFailureLogAt > TimeSpan.FromSeconds(2))
            {
                _lastReverseDeckLinkFailureLogAt = DateTime.UtcNow;
                AppendLog($"DeckLink reverse output lost; retrying. {ex.Message}");
            }
        }
    }

    private void CompleteReversePlaybackAtStart()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => CompleteReversePlaybackAtStart());
            }
            catch
            {
                // The form may be closing.
            }

            return;
        }

        _selectedPlaybackSpeed = 0d;
        StopReversePlaybackSpeed();
        UpdatePlaybackSpeedButtons(CanUsePlaybackSpeed());
        SetStatus("Speed 0x", Color.FromArgb(232, 181, 105));
    }

    private void SetStatusFromAnyThread(string text, Color color)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => SetStatus(text, color));
            }
            catch
            {
                // The form may be closing.
            }

            return;
        }

        SetStatus(text, color);
    }

    private async Task ReversePlaybackSpeedTimer_TickAsync()
    {
        if (_reverseSpeedSeekRunning || _reversePlaybackSpeed >= 0d)
        {
            return;
        }

        var duration = GetCurrentSeekDuration();
        if (!duration.HasValue || duration.Value <= TimeSpan.Zero)
        {
            StopReversePlaybackSpeed();
            return;
        }

        var frameDuration = GetFrameDuration();
        var framesToStep = GetReversePlaybackFrameStep(frameDuration);
        if (framesToStep <= 0)
        {
            return;
        }

        var target = GetCurrentSeekPosition() - TimeSpan.FromTicks(frameDuration.Ticks * framesToStep);
        var reachedStart = target <= TimeSpan.Zero;
        if (reachedStart)
        {
            target = TimeSpan.Zero;
        }

        _reverseSpeedSeekRunning = true;
        try
        {
            if (!_scrubPreviewMode)
            {
                await BeginScrubPreviewAsync(target);
            }
            else
            {
                await QueueScrubPreviewFrameAsync(target);
            }
        }
        finally
        {
            _reverseSpeedSeekRunning = false;
        }

        if (reachedStart)
        {
            _selectedPlaybackSpeed = 0d;
            StopReversePlaybackSpeed();
            UpdatePlaybackSpeedButtons(CanUsePlaybackSpeed());
            SetStatus("Speed 0x", Color.FromArgb(232, 181, 105));
        }
    }

    private void StopReversePlaybackSpeed()
    {
        _reversePlaybackSpeedTimer.Stop();
        var reverseCancellation = _reversePlaybackCancellation;
        _reversePlaybackCancellation = null;
        reverseCancellation?.Cancel();
        _reversePlaybackSpeed = 0d;
        _reversePlaybackFrameCarry = 0d;
        _reversePlaybackLastTickAt = null;
        DisposeReverseAudio();
        SetReverseCacheStatusFromAnyThread(null, Color.Empty);
    }

    private int GetReversePlaybackTimerInterval()
    {
        var frameMilliseconds = GetFrameDuration().TotalMilliseconds;
        if (double.IsNaN(frameMilliseconds) || double.IsInfinity(frameMilliseconds) || frameMilliseconds <= 0d)
        {
            frameMilliseconds = 40d;
        }

        return Math.Clamp((int)Math.Round(frameMilliseconds), 10, 1000);
    }

    private int GetReverseDeckLinkFrameInterval()
    {
        var frameMilliseconds = GetFrameDuration().TotalMilliseconds;
        if (double.IsNaN(frameMilliseconds) || double.IsInfinity(frameMilliseconds) || frameMilliseconds <= 0d)
        {
            frameMilliseconds = 40d;
        }

        return Math.Clamp((int)Math.Round(frameMilliseconds * 4d), 80, 250);
    }

    private int GetReversePlaybackFrameStep(TimeSpan frameDuration)
    {
        var now = DateTime.UtcNow;
        var elapsed = _reversePlaybackLastTickAt.HasValue
            ? now - _reversePlaybackLastTickAt.Value
            : TimeSpan.FromMilliseconds(_reversePlaybackSpeedTimer.Interval);
        _reversePlaybackLastTickAt = now;

        if (elapsed <= TimeSpan.Zero)
        {
            elapsed = TimeSpan.FromMilliseconds(_reversePlaybackSpeedTimer.Interval);
        }

        // Avoid one large visual jump if Windows stalls the UI timer for a moment.
        if (elapsed > TimeSpan.FromMilliseconds(250))
        {
            elapsed = TimeSpan.FromMilliseconds(250);
        }

        var frameSeconds = frameDuration.TotalSeconds;
        if (frameSeconds <= 0d)
        {
            frameSeconds = 1d / 25d;
        }

        _reversePlaybackFrameCarry += Math.Abs(_reversePlaybackSpeed) * elapsed.TotalSeconds / frameSeconds;
        var framesToStep = (int)Math.Floor(_reversePlaybackFrameCarry);
        if (framesToStep <= 0)
        {
            return 0;
        }

        _reversePlaybackFrameCarry -= framesToStep;
        return framesToStep;
    }

    private bool CanUsePlaybackSpeed()
    {
        var path = _inputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || IsImageFile(path))
        {
            return false;
        }

        return !_playbackIsTestPattern && (GetCurrentSeekDuration().HasValue || GetKnownMediaDuration(path).HasValue);
    }

    private void CapturePlaybackClockProgress()
    {
        if (!_playbackStartedAt.HasValue)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!_playbackClockSampleAt.HasValue)
        {
            _playbackClockSampleAt = now;
            return;
        }

        var elapsed = now - _playbackClockSampleAt.Value;
        if (elapsed > TimeSpan.Zero && !_isPaused && _selectedPlaybackSpeed > 0d)
        {
            _playbackClockElapsed += TimeSpan.FromTicks((long)Math.Round(elapsed.Ticks * _selectedPlaybackSpeed));
        }

        _playbackClockSampleAt = now;
    }

    private PlayRequest BuildRequest(
        bool useTestPattern,
        TimeSpan startOffset,
        TimeSpan? playDuration = null,
        bool playLoop = false,
        string? videoFilter = null,
        string? audioFilter = null,
        PlaylistTransitionSegment? transitionSegment = null)
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

        var selectedDevice = previewOnly
            ? _deviceBox.SelectedItem?.ToString() ?? "Preview Only"
            : GetSelectedDevice();

        var isStillImage = IsImageFile(inputPath);
        var normalizedStartOffset = useTestPattern || isStillImage
            ? TimeSpan.Zero
            : ClampSeekOffset(startOffset, _selectedMediaDuration);
        var normalizedPlayDuration = useTestPattern
            ? null
            : isStillImage
                ? DefaultStillDuration
                : playDuration;
        var loopPlayback = playLoop && !useTestPattern && !isStillImage;

        return new PlayRequest(
            GetFfmpegPath(),
            inputPath,
            selectedDevice,
            DefaultDeckLinkModeCode,
            FixedDeckLinkVideoSize,
            FixedDeckLinkFrameRate,
            FfmpegDeckLink.DefaultPixelFormat,
            FfmpegDeckLink.DefaultAudioChannels,
            FfmpegDeckLink.DefaultPrerollSeconds,
            null,
            "single",
            true,
            VideoFilter: videoFilter,
            AudioFilter: audioFilter,
            loopPlayback,
            useTestPattern || isStillImage,
            true,
            FixedDeckLinkFieldOrder,
            useTestPattern,
            normalizedStartOffset,
            normalizedPlayDuration,
            transitionSegment);
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

    private static string GetFfplayPath(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory;
        var ffplayPath = Path.Combine(directory, "ffplay.exe");
        if (File.Exists(ffplayPath))
        {
            return ffplayPath;
        }

        throw new InvalidOperationException($"ffplay.exe not found next to {Path.GetFileName(ffmpegPath)}.");
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
        _refreshMediaButton.Enabled = !isPlaying;
        _browseMediaRootButton.Enabled = true;
        _mediaRootPathBox.Enabled = true;
        _mediaSearchBox.Enabled = true;
        _clearMediaSearchButton.Enabled = true;
        _previewOnlyCheckBox.Enabled = !isPlaying;
        _pcAudioCheckBox.Enabled = !isPlaying;
        _mediaTree.Enabled = true;
        _mediaGrid.Enabled = true;
        _playlistGrid.Enabled = true;
        UpdatePlaylistButtons();
        UpdatePlaybackSpeedButtons(CanUsePlaybackSpeed());
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
        _playlistGrid.Enabled = enabled;
        UpdatePlaylistButtons();
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

    private sealed class PlaylistFile
    {
        public int Version { get; set; } = 1;

        public string? SavedAt { get; set; }

        public string? MediaRootPath { get; set; }

        public bool AutoRepeat { get; set; } = true;

        public List<PlaylistFileItem> Items { get; set; } = [];
    }

    private sealed class PlaylistFileItem
    {
        public bool IsEnd { get; set; }

        public string Path { get; set; } = string.Empty;

        public string? TcIn { get; set; }

        public string? TcOut { get; set; }

        public string? SourceDuration { get; set; }

        public string? TimelineStart { get; set; }

        public string? Transition { get; set; }

        public string? TransitionDuration { get; set; }

        public bool PlayEnabled { get; set; } = true;

        public bool LoopEnabled { get; set; }
    }

    private sealed record PlaylistTransitionFilters(string? VideoFilter, string? AudioFilter)
    {
        public static readonly PlaylistTransitionFilters Empty = new(null, null);
    }

    private sealed record PlaylistTransitionPlan(int NextIndex, PlaylistTransitionSegment Segment);

    private sealed class PlaylistItem
    {
        public PlaylistItem(string fullPath)
        {
            FullPath = fullPath;
        }

        public string FullPath { get; }

        public bool IsEndMarker => IsPlaylistEndMarkerText(FullPath);

        public TimeSpan TcIn { get; init; }

        public TimeSpan? TcOut { get; init; }

        public TimeSpan? SourceDuration { get; init; }

        public TimeSpan? TimelineStartOverride { get; set; }

        public string Transition { get; set; } = DefaultPlaylistTransition;

        public TimeSpan TransitionDuration { get; set; } = DefaultTransitionDuration;

        public bool PlayEnabled { get; set; } = true;

        public bool LoopEnabled { get; set; }

        public string Status { get; set; } = PlaylistStatusReady;

        public TimeSpan? PlayDuration
        {
            get
            {
                if (IsEndMarker)
                {
                    return null;
                }

                if (TcOut.HasValue && TcOut.Value > TcIn)
                {
                    return TcOut.Value - TcIn;
                }

                if (SourceDuration.HasValue && SourceDuration.Value > TcIn)
                {
                    return SourceDuration.Value - TcIn;
                }

                return null;
            }
        }

        public PlaylistItem Snapshot()
        {
            return new PlaylistItem(FullPath)
            {
                TcIn = TcIn,
                TcOut = TcOut,
                SourceDuration = SourceDuration,
                TimelineStartOverride = TimelineStartOverride,
                Transition = Transition,
                TransitionDuration = TransitionDuration,
                PlayEnabled = PlayEnabled,
                LoopEnabled = LoopEnabled,
                Status = Status,
            };
        }
    }

    private sealed record ToolbarActionState(Color BackColor, Func<Task> Action);

    private sealed class ToolbarTextLabel : Control
    {
        public ToolbarTextLabel()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private sealed class ToolbarTextButton : Button
    {
        private bool _hovered;
        private bool _pressed;

        public ToolbarTextButton()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var backColor = Enabled
                ? BackColor
                : Color.FromArgb(43, 50, 57);
            if (_pressed && Enabled)
            {
                backColor = ControlPaint.Dark(backColor, 0.12f);
            }
            else if (_hovered && Enabled)
            {
                backColor = ControlPaint.Light(backColor, 0.08f);
            }

            using var brush = new SolidBrush(backColor);
            pevent.Graphics.FillRectangle(brush, ClientRectangle);
            using var borderPen = new Pen(FlatAppearance.BorderColor == Color.Empty ? Color.FromArgb(104, 116, 126) : FlatAppearance.BorderColor);
            pevent.Graphics.DrawRectangle(borderPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

            var textColor = Enabled
                ? ForeColor
                : Color.FromArgb(204, 212, 218);
            TextRenderer.DrawText(
                pevent.Graphics,
                Text,
                Font,
                ClientRectangle,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }
    }

    private sealed class SeekBarControl : Control
    {
        private int _minimum;
        private int _maximum = 1;
        private int _value;
        private int _tickFrequency = 1;
        private bool _rangeVisible;
        private double _startRatio;
        private double _endRatio;
        private bool _darkMode = true;
        private bool _dragging;

        public SeekBarControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);
            BackColor = Color.FromArgb(30, 35, 40);
            TabStop = true;
        }

        public event EventHandler? Scroll;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_maximum < _minimum)
                {
                    _maximum = _minimum;
                }

                Value = _value;
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(value, _minimum);
                Value = _value;
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                var normalized = Math.Clamp(value, _minimum, _maximum);
                if (_value == normalized)
                {
                    return;
                }

                _value = normalized;
                Invalidate();
            }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int TickFrequency
        {
            get => _tickFrequency;
            set => _tickFrequency = Math.Max(1, value);
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public TickStyle TickStyle { get; set; } = TickStyle.None;

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool DarkMode
        {
            get => _darkMode;
            set
            {
                _darkMode = value;
                BackColor = _darkMode ? Color.FromArgb(30, 35, 40) : Color.FromArgb(236, 241, 245);
                Invalidate();
            }
        }

        public void SetRange(double startRatio, double endRatio)
        {
            _startRatio = Math.Clamp(startRatio, 0d, 1d);
            _endRatio = Math.Clamp(endRatio, 0d, 1d);
            _rangeVisible = _endRatio > _startRatio;
            Invalidate();
        }

        public void ClearRange()
        {
            _rangeVisible = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown ||
                base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var oldValue = Value;
            var step = Math.Max(1, _tickFrequency / 10);
            var pageStep = Math.Max(step, _tickFrequency);
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down:
                    Value -= step;
                    break;
                case Keys.Right:
                case Keys.Up:
                    Value += step;
                    break;
                case Keys.PageDown:
                    Value -= pageStep;
                    break;
                case Keys.PageUp:
                    Value += pageStep;
                    break;
                case Keys.Home:
                    Value = Minimum;
                    break;
                case Keys.End:
                    Value = Maximum;
                    break;
                default:
                    base.OnKeyDown(e);
                    return;
            }

            e.Handled = true;
            if (Value != oldValue)
            {
                Scroll?.Invoke(this, EventArgs.Empty);
            }

            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                SetValueFromMouse(e.X, raiseScroll: true);
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                SetValueFromMouse(e.X, raiseScroll: true);
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                SetValueFromMouse(e.X, raiseScroll: true);
                _dragging = false;
            }

            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }

        private void SetValueFromMouse(int x, bool raiseScroll)
        {
            var track = GetTrackBounds();
            var ratio = track.Width <= 0
                ? 0d
                : Math.Clamp((x - track.Left) / (double)track.Width, 0d, 1d);
            var oldValue = Value;
            Value = (int)Math.Round(_minimum + ratio * (_maximum - _minimum));
            if (raiseScroll && Value != oldValue)
            {
                Scroll?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Width <= 28 || Height <= 8)
            {
                return;
            }

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var track = GetTrackBounds();
            var trackRadius = track.Height / 2;
            using var baseBrush = new SolidBrush(Enabled
                ? _darkMode ? Color.FromArgb(70, 80, 88) : Color.FromArgb(188, 198, 207)
                : _darkMode ? Color.FromArgb(48, 55, 61) : Color.FromArgb(210, 216, 222));
            FillRoundedRectangle(e.Graphics, baseBrush, track, trackRadius);

            if (_rangeVisible)
            {
                var rangeLeft = track.Left + (int)Math.Round(_startRatio * track.Width);
                var rangeRight = track.Left + (int)Math.Round(_endRatio * track.Width);
                var rangeRect = new Rectangle(rangeLeft, track.Top, Math.Max(4, rangeRight - rangeLeft), track.Height);
                using var rangeBrush = new SolidBrush(Enabled
                    ? _darkMode ? Color.FromArgb(224, 159, 57) : Color.FromArgb(230, 150, 45)
                    : _darkMode ? Color.FromArgb(108, 91, 63) : Color.FromArgb(205, 185, 156));
                FillRoundedRectangle(e.Graphics, rangeBrush, rangeRect, trackRadius);
            }

            var valueRatio = _maximum <= _minimum
                ? 0d
                : Math.Clamp((_value - _minimum) / (double)(_maximum - _minimum), 0d, 1d);
            var thumbX = track.Left + (int)Math.Round(valueRatio * track.Width);
            using var progressBrush = new SolidBrush(Enabled
                ? _darkMode ? Color.FromArgb(62, 135, 210) : Color.FromArgb(55, 117, 178)
                : _darkMode ? Color.FromArgb(76, 88, 99) : Color.FromArgb(180, 190, 200));
            var progressRect = new Rectangle(track.Left, track.Top, Math.Max(0, thumbX - track.Left), track.Height);
            if (progressRect.Width > 0)
            {
                FillRoundedRectangle(e.Graphics, progressBrush, progressRect, trackRadius);
            }

            var thumbRect = new Rectangle(thumbX - 6, track.Top - 7, 12, track.Height + 14);
            using var thumbBrush = new SolidBrush(Enabled
                ? _darkMode ? Color.FromArgb(240, 247, 252) : Color.FromArgb(25, 36, 46)
                : _darkMode ? Color.FromArgb(128, 138, 146) : Color.FromArgb(145, 154, 162));
            using var thumbBorder = new Pen(_darkMode ? Color.FromArgb(28, 36, 44) : Color.White, 2f);
            FillRoundedRectangle(e.Graphics, thumbBrush, thumbRect, 5);
            e.Graphics.DrawRectangle(thumbBorder, thumbRect);
        }

        private Rectangle GetTrackBounds()
        {
            var width = Math.Max(1, Width - 20);
            return new Rectangle(10, Math.Max(2, Height / 2 - 4), width, 8);
        }

        private static void FillRoundedRectangle(Graphics graphics, Brush brush, Rectangle rectangle, int radius)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                return;
            }

            if (radius <= 1)
            {
                graphics.FillRectangle(brush, rectangle);
                return;
            }

            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            graphics.FillPath(brush, path);
        }
    }

    private sealed class PreviewFullscreenForm : Form
    {
        private readonly PictureBox _previewBox = new();

        public PreviewFullscreenForm()
        {
            Text = "DeckLink Player Preview";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.Black;
            Padding = new Padding(0);

            _previewBox.Dock = DockStyle.Fill;
            _previewBox.Margin = new Padding(0);
            _previewBox.BackColor = Color.Black;
            _previewBox.BorderStyle = BorderStyle.None;
            _previewBox.SizeMode = PictureBoxSizeMode.Zoom;
            Controls.Add(_previewBox);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    Close();
                }
            };
        }

        public void SetPreviewImage(Image? image)
        {
            if (IsDisposed)
            {
                image?.Dispose();
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(() => SetPreviewImage(image));
                }
                catch
                {
                    image?.Dispose();
                }

                return;
            }

            var previous = _previewBox.Image;
            _previewBox.Image = image;
            previous?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var image = _previewBox.Image;
                _previewBox.Image = null;
                image?.Dispose();
            }

            base.Dispose(disposing);
        }
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
