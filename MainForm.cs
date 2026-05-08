namespace ffmpegplayer;

internal sealed class MainForm : Form
{
    private const string DefaultMediaFileName = "go1080p25.mp4";
    private const string DefaultMediaRootPath = @"C:\casparcg\_media";
    private const string DefaultDeckLinkDeviceName = "DeckLink SDI 4K";
    private const string DefaultDeckLinkModeCode = "Hi50";

    private readonly FfmpegDeckLink _deckLink = new();
    private readonly DeckLinkSdkPlayer _sdkPlayer = new();
    private readonly TextBox _inputPathBox = new();
    private readonly TextBox _mediaSearchBox = new();
    private readonly TreeView _mediaTree = new();
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
    private readonly CheckBox _loopBox = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _dryRunButton = new();
    private readonly Button _testPatternButton = new();
    private readonly Button _playSelectedMediaButton = new();
    private readonly Button _refreshMediaButton = new();
    private readonly Button _clearMediaSearchButton = new();
    private readonly Button _refreshDevicesButton = new();
    private readonly Button _refreshModesButton = new();
    private readonly TextBox _logBox = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _mediaSearchTimer = new() { Interval = 350 };

    private CancellationTokenSource? _playbackCancellation;
    private CancellationTokenSource? _mediaSearchCancellation;
    private TaskCompletionSource? _playbackStoppedSignal;
    private bool _isPlaying;
    private bool _switchingPlayback;

    public MainForm()
    {
        Text = "DeckLink Player";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 720);
        Size = new Size(1180, 840);
        BackColor = Color.FromArgb(22, 25, 29);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        BuildUi();

        _inputPathBox.Text = FindDefaultMediaPath();
        LoadMediaTree();
        _pixelFormatBox.Text = FfmpegDeckLink.DefaultPixelFormat;
        _audioChannelsBox.Value = FfmpegDeckLink.DefaultAudioChannels;
        _prerollBox.Value = (decimal)FfmpegDeckLink.DefaultPrerollSeconds;
        _duplexBox.SelectedItem = "unset";
        _linkBox.SelectedItem = "single";
        _levelABox.SelectedItem = "true";
        _loopBox.Checked = true;

        Shown += async (_, _) => await RefreshDevicesAsync();
        FormClosing += (_, _) =>
        {
            _playbackCancellation?.Cancel();
            _mediaSearchCancellation?.Cancel();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = BackColor,
        };

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 430));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildSettingsArea(), 0, 1);
        root.Controls.Add(BuildActionBar(), 0, 2);
        root.Controls.Add(BuildLogPanel(), 0, 3);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2, 0, 2, 0) };

        var title = new Label
        {
            Text = "DeckLink Playout",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(239, 244, 248),
            Location = new Point(0, 4),
        };

        _statusLabel.Text = "Ready";
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _statusLabel.ForeColor = Color.FromArgb(130, 210, 164);
        _statusLabel.Location = new Point(4, 42);

        panel.Controls.Add(title);
        panel.Controls.Add(_statusLabel);
        return panel;
    }

    private Control BuildSettingsArea()
    {
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor,
            Padding = new Padding(0, 4, 0, 8),
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        split.Controls.Add(BuildSourcePanel(), 0, 0);
        split.Controls.Add(BuildOutputPanel(), 1, 0);
        return split;
    }

    private Control BuildSourcePanel()
    {
        var panel = BuildSection("Source");

        _inputPathBox.PlaceholderText = "Media file to play";

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = panel.BackColor,
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        content.Controls.Add(BuildInputRow("Media", _inputPathBox, null), 0, 0);

        _loopBox.Text = "Loop";
        StyleCheckBox(_loopBox);

        _playSelectedMediaButton.Text = "Play Selected";
        _playSelectedMediaButton.Width = 120;
        StyleButton(_playSelectedMediaButton, Color.FromArgb(39, 125, 87));
        _playSelectedMediaButton.Click += async (_, _) => await PlaySelectedMediaNodeAsync(_mediaTree.SelectedNode);

        var checkPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(112, 4, 0, 0),
        };
        checkPanel.Controls.Add(_loopBox);
        checkPanel.Controls.Add(_playSelectedMediaButton);
        content.Controls.Add(checkPanel, 0, 1);

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
            LoadMediaTree();
        };
        content.Controls.Add(BuildInputRow("Search", _mediaSearchBox, _clearMediaSearchButton), 0, 2);

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
        var rootPathBox = new TextBox
        {
            Text = DefaultMediaRootPath,
            ReadOnly = true,
        };
        content.Controls.Add(BuildInputRow("Library", rootPathBox, _refreshMediaButton), 0, 3);

        StyleMediaTree();
        content.Controls.Add(_mediaTree, 0, 4);
        panel.Controls.Add(content);

        return panel;
    }

    private Control BuildOutputPanel()
    {
        var panel = BuildSection("DeckLink Output");

        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;

        _refreshDevicesButton.Text = "Refresh";
        StyleButton(_refreshDevicesButton, Color.FromArgb(52, 67, 82));
        _refreshDevicesButton.Click += async (_, _) => await RefreshDevicesAsync();

        _refreshModesButton.Text = "Modes";
        StyleButton(_refreshModesButton, Color.FromArgb(52, 67, 82));
        _refreshModesButton.Click += async (_, _) => await RefreshModesAsync();

        _deviceBox.SelectedIndexChanged += async (_, _) => await RefreshModesAsync();
        _modeBox.SelectedIndexChanged += (_, _) => ApplySelectedModeToFields();

        panel.Controls.Add(BuildInputRow("Device", _deviceBox, _refreshDevicesButton));
        panel.Controls.Add(BuildInputRow("Mode", _modeBox, _refreshModesButton));

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

        AddGridField(outputGrid, "Size", _videoSizeBox, 0, 0);
        AddGridField(outputGrid, "Rate", _frameRateBox, 2, 0);
        AddGridField(outputGrid, "Audio", _audioChannelsBox, 0, 1);
        AddGridField(outputGrid, "Preroll", _prerollBox, 2, 1);

        panel.Controls.Add(outputGrid);

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
        panel.Controls.Add(tailGrid);

        return panel;
    }

    private Control BuildActionBar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 10, 0, 8),
            BackColor = BackColor,
        };

        _startButton.Text = "Start Playout";
        StyleButton(_startButton, Color.FromArgb(39, 125, 87));
        _startButton.Width = 150;
        _startButton.Click += async (_, _) => await StartPlaybackAsync(dryRun: false);

        _stopButton.Text = "Stop";
        StyleButton(_stopButton, Color.FromArgb(149, 64, 58));
        _stopButton.Width = 100;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopPlayback();

        _dryRunButton.Text = "Dry Run";
        StyleButton(_dryRunButton, Color.FromArgb(52, 67, 82));
        _dryRunButton.Width = 110;
        _dryRunButton.Click += async (_, _) => await StartPlaybackAsync(dryRun: true);

        _testPatternButton.Text = "Moving Test";
        StyleButton(_testPatternButton, Color.FromArgb(63, 96, 135));
        _testPatternButton.Width = 118;
        _testPatternButton.Click += async (_, _) => await StartPlaybackAsync(dryRun: false, useTestPattern: true);

        var clearLogButton = BuildButton("Clear Log");
        clearLogButton.Width = 110;
        clearLogButton.Click += (_, _) => _logBox.Clear();

        panel.Controls.Add(_startButton);
        panel.Controls.Add(_stopButton);
        panel.Controls.Add(_dryRunButton);
        panel.Controls.Add(_testPatternButton);
        panel.Controls.Add(clearLogButton);
        return panel;
    }

    private Control BuildLogPanel()
    {
        var panel = BuildSection("Playback Log");
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
            AutoSize = true,
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
        _mediaTree.MouseDoubleClick -= MediaTree_MouseDoubleClick;
        _mediaTree.BeforeExpand += MediaTree_BeforeExpand;
        _mediaTree.AfterSelect += MediaTree_AfterSelect;
        _mediaTree.MouseDoubleClick += MediaTree_MouseDoubleClick;
    }

    private void LoadMediaTree()
    {
        _mediaSearchCancellation?.Cancel();
        _mediaTree.BeginUpdate();
        try
        {
            _mediaTree.Nodes.Clear();
            if (!Directory.Exists(DefaultMediaRootPath))
            {
                _mediaTree.Nodes.Add(new TreeNode($"{DefaultMediaRootPath} not found"));
                SetStatus("Media folder missing", Color.FromArgb(232, 181, 105));
                return;
            }

            var root = CreateDirectoryNode(DefaultMediaRootPath, DefaultMediaRootPath);
            _mediaTree.Nodes.Add(root);
            LoadDirectoryChildren(root);
            root.Expand();
            SetStatus("Media tree ready", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex)
        {
            AppendLog($"Media tree error: {ex.Message}");
            SetStatus("Media tree error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            _mediaTree.EndUpdate();
        }
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
            LoadMediaTree();
            return;
        }

        if (!Directory.Exists(DefaultMediaRootPath))
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
                () => FindMediaFiles(searchText, maxResults: 300, cancellation.Token),
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
        _mediaTree.BeginUpdate();
        try
        {
            _mediaTree.Nodes.Clear();
            var root = new TreeNode($"Search: {searchText} ({results.Count})");

            if (results.Count == 0)
            {
                root.Nodes.Add(new TreeNode("No matches"));
            }
            else
            {
                foreach (var file in results)
                {
                    root.Nodes.Add(CreateFileNode(file, GetMediaDisplayPath(file)));
                }
            }

            _mediaTree.Nodes.Add(root);
            root.Expand();
            SetStatus($"{results.Count} media match(es)", results.Count > 0
                ? Color.FromArgb(130, 210, 164)
                : Color.FromArgb(232, 181, 105));
        }
        finally
        {
            _mediaTree.EndUpdate();
        }
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
        _ = SelectMediaNode(e.Node);
    }

    private async void MediaTree_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        var node = GetTreeNodeAtPoint(e.Location);
        if (node is not null)
        {
            _mediaTree.SelectedNode = node;
            await PlaySelectedMediaNodeAsync(node);
        }
    }

    private TreeNode? GetTreeNodeAtPoint(Point location)
    {
        var hitNode = _mediaTree.HitTest(location).Node;
        if (hitNode is not null)
        {
            return hitNode;
        }

        foreach (TreeNode node in _mediaTree.Nodes)
        {
            var rowNode = FindVisibleNodeAtY(node, location.Y);
            if (rowNode is not null)
            {
                return rowNode;
            }
        }

        return null;
    }

    private static TreeNode? FindVisibleNodeAtY(TreeNode node, int y)
    {
        if (node.Bounds.Top <= y && y <= node.Bounds.Bottom)
        {
            return node;
        }

        if (!node.IsExpanded)
        {
            return null;
        }

        foreach (TreeNode child in node.Nodes)
        {
            var match = FindVisibleNodeAtY(child, y);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private bool SelectMediaNode(TreeNode? node)
    {
        if (node?.Tag is not string path || !File.Exists(path))
        {
            return false;
        }

        _inputPathBox.Text = path;
        SetStatus(_isPlaying ? $"Next: {Path.GetFileName(path)}" : $"Selected {Path.GetFileName(path)}", Color.FromArgb(130, 210, 164));
        return true;
    }

    private async Task PlaySelectedMediaNodeAsync(TreeNode? node)
    {
        if (!SelectMediaNode(node))
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
            var nextFile = Path.GetFileName(_inputPathBox.Text);
            AppendLog($"Switching playout to {nextFile}...");
            SetStatus($"Switching to {nextFile}", Color.FromArgb(126, 188, 226));

            var stoppedTask = _playbackStoppedSignal?.Task;
            StopPlayback();
            if (stoppedTask is not null)
            {
                await stoppedTask;
            }

            await StartPlaybackAsync(dryRun: false);
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

    private static TreeNode CreateFileNode(string path, string? text = null)
    {
        return new TreeNode(text ?? Path.GetFileName(path)) { Tag = path, ToolTipText = path };
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

            foreach (var file in Directory.EnumerateFiles(path).Where(IsSupportedMediaFile).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                node.Nodes.Add(CreateFileNode(file));
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

    private static IReadOnlyList<string> FindMediaFiles(string searchText, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(DefaultMediaRootPath);

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

    private static string GetMediaDisplayPath(string path)
    {
        try
        {
            return Path.GetRelativePath(DefaultMediaRootPath, path);
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

        await RunUiTaskAsync("Refreshing DeckLink devices...", async cancellationToken =>
        {
            var previous = _deviceBox.SelectedItem?.ToString();
            var devices = await _deckLink.ListDevicesAsync(GetFfmpegPath(), cancellationToken);

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

            AppendLog($"Found {devices.Count} DeckLink device(s).");
        });
    }

    private async Task RefreshModesAsync()
    {
        if (_isPlaying || _deviceBox.SelectedItem is null)
        {
            return;
        }

        await RunUiTaskAsync("Loading DeckLink modes...", async cancellationToken =>
        {
            var device = GetSelectedDevice();
            var previousCode = (_modeBox.SelectedItem as DeckLinkMode)?.Code;
            var modes = await _deckLink.ListFormatsAsync(GetFfmpegPath(), device, cancellationToken);

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
        });
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

    private async Task StartPlaybackAsync(bool dryRun, bool useTestPattern = false)
    {
        if (_isPlaying)
        {
            return;
        }

        try
        {
            var request = BuildRequest(useTestPattern);
            var selectedMode = _modeBox.SelectedItem as DeckLinkMode;
            request = _deckLink.ApplyModeDefaults(request, selectedMode);
            var commandText = _sdkPlayer.FormatDecoderCommand(request);

            AppendLog("");
            AppendLog("Command:");
            AppendLog("SDK decoder command:");
            AppendLog(commandText);
            AppendLog(request.NoAudio
                ? "DeckLink output: Blackmagic SDK direct video frames."
                : "DeckLink output: Blackmagic SDK direct video frames with embedded audio.");

            if (dryRun)
            {
                SetStatus("Dry run ready", Color.FromArgb(130, 210, 164));
                return;
            }

            _playbackCancellation = new CancellationTokenSource();
            _playbackStoppedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetPlaying(true);
            AppendLog("Starting playout...");
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
            LogPlaybackLine("SDK decoder command:");
            LogPlaybackLine(commandText);

            var result = await Task.Run(
                () => _sdkPlayer.PlayAsync(request, LogPlaybackLine, _playbackCancellation.Token),
                _playbackCancellation.Token);

            AppendLog(result.Cancelled ? "Playback stopped." : $"DeckLink SDK engine exited with code {result.ExitCode}.");
            SetStatus(result.Cancelled ? "Stopped" : $"Exited with code {result.ExitCode}", result.ExitCode == 0
                ? Color.FromArgb(130, 210, 164)
                : Color.FromArgb(232, 181, 105));
        }
        catch (OperationCanceledException) when (_playbackCancellation?.IsCancellationRequested == true)
        {
            AppendLog("Playback stopped.");
            SetStatus("Stopped", Color.FromArgb(130, 210, 164));
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            SetStatus("Error", Color.FromArgb(229, 113, 105));
        }
        finally
        {
            var stoppedSignal = _playbackStoppedSignal;
            SetPlaying(false);
            _playbackCancellation?.Dispose();
            _playbackCancellation = null;
            _playbackStoppedSignal = null;
            stoppedSignal?.TrySetResult();
        }
    }

    private void StopPlayback()
    {
        if (!_isPlaying)
        {
            return;
        }

        AppendLog("Stopping playout...");
        _playbackCancellation?.Cancel();
    }

    private PlayRequest BuildRequest(bool useTestPattern)
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

        if (_deviceBox.SelectedItem is null)
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

        var selectedDevice = GetSelectedDevice();
        if (selectedDevice.Contains("SDI 4K", StringComparison.OrdinalIgnoreCase))
        {
            duplexMode = null;
        }

        var selectedMode = _modeBox.SelectedItem as DeckLinkMode;
        var isStillImage = IsImageFile(inputPath);

        return new PlayRequest(
            GetFfmpegPath(),
            inputPath,
            selectedDevice,
            selectedMode?.Code,
            EmptyToNull(_videoSizeBox.Text),
            EmptyToNull(_frameRateBox.Text),
            EmptyToNull(_pixelFormatBox.Text) ?? FfmpegDeckLink.DefaultPixelFormat,
            audioChannels,
            (double)_prerollBox.Value,
            duplexMode,
            linkMode,
            levelA,
            VideoFilter: null,
            AudioFilter: null,
            _loopBox.Checked,
            useTestPattern || isStillImage,
            selectedMode?.IsInterlaced == true,
            selectedMode?.FieldOrder,
            useTestPattern);
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
        _startButton.Enabled = !isPlaying;
        _dryRunButton.Enabled = !isPlaying;
        _stopButton.Enabled = isPlaying;
        _refreshDevicesButton.Enabled = !isPlaying;
        _refreshModesButton.Enabled = !isPlaying;
        _testPatternButton.Enabled = !isPlaying;
        _playSelectedMediaButton.Enabled = true;
        _refreshMediaButton.Enabled = !isPlaying;
        _mediaSearchBox.Enabled = true;
        _clearMediaSearchButton.Enabled = true;
        _mediaTree.Enabled = true;
        SetStatus(isPlaying ? "Playing" : _statusLabel.Text, isPlaying
            ? Color.FromArgb(126, 188, 226)
            : _statusLabel.ForeColor);
    }

    private void SetControlsEnabled(bool enabled)
    {
        _refreshDevicesButton.Enabled = enabled;
        _refreshModesButton.Enabled = enabled;
        _startButton.Enabled = enabled && !_isPlaying;
        _dryRunButton.Enabled = enabled && !_isPlaying;
        _testPatternButton.Enabled = enabled && !_isPlaying;
        _playSelectedMediaButton.Enabled = enabled;
        _refreshMediaButton.Enabled = enabled && !_isPlaying;
        _mediaSearchBox.Enabled = enabled;
        _clearMediaSearchButton.Enabled = enabled;
        _mediaTree.Enabled = enabled;
        _stopButton.Enabled = _isPlaying;
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
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
