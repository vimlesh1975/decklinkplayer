namespace ffmpegplayer;

internal sealed class MediaRootDialog : Form
{
    private const int MaxDirectoriesPerNode = 800;

    private readonly TextBox _pathBox = new();
    private readonly TreeView _tree = new();
    private readonly ToolTip _buttonToolTip = new();
    private readonly Button _useButton = new();
    private readonly Button _defaultButton = new();
    private readonly Button _cancelButton = new();
    private readonly string _defaultPath;

    public MediaRootDialog(string currentPath, string defaultPath)
    {
        _defaultPath = defaultPath;
        SelectedPath = Directory.Exists(currentPath) ? currentPath : defaultPath;

        Text = "Choose Media Library";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(680, 520);
        BackColor = Color.FromArgb(22, 25, 29);
        ForeColor = Color.FromArgb(236, 241, 244);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        BuildUi();
        LoadRootNodes();
        _pathBox.Text = SelectedPath;
    }

    public string SelectedPath { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buttonToolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        ConfigureButtonToolTip();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(14),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Select library folder",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(236, 241, 244),
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        root.Controls.Add(title, 0, 0);

        _pathBox.Dock = DockStyle.Fill;
        _pathBox.BorderStyle = BorderStyle.FixedSingle;
        _pathBox.BackColor = Color.FromArgb(13, 16, 19);
        _pathBox.ForeColor = Color.FromArgb(226, 234, 238);
        root.Controls.Add(_pathBox, 0, 1);

        _tree.Dock = DockStyle.Fill;
        _tree.BackColor = Color.FromArgb(13, 16, 19);
        _tree.ForeColor = Color.FromArgb(226, 234, 238);
        _tree.BorderStyle = BorderStyle.FixedSingle;
        _tree.HideSelection = false;
        _tree.BeforeExpand += Tree_BeforeExpand;
        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is string path)
            {
                _pathBox.Text = path;
            }
        };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is string path)
            {
                _pathBox.Text = path;
                UseSelectedPath();
            }
        };
        root.Controls.Add(_tree, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = BackColor,
        };

        _useButton.Text = "Use";
        _useButton.Width = 96;
        StyleDialogButton(_useButton, Color.FromArgb(39, 125, 87));
        _buttonToolTip.SetToolTip(_useButton, "Use the selected folder as the media library.");
        _useButton.Click += (_, _) => UseSelectedPath();

        _cancelButton.Text = "Cancel";
        _cancelButton.Width = 96;
        StyleDialogButton(_cancelButton, Color.FromArgb(52, 67, 82));
        _buttonToolTip.SetToolTip(_cancelButton, "Close without changing the media library.");
        _cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        _defaultButton.Text = "Default";
        _defaultButton.Width = 96;
        StyleDialogButton(_defaultButton, Color.FromArgb(63, 96, 135));
        _buttonToolTip.SetToolTip(_defaultButton, "Fill in the default media library folder.");
        _defaultButton.Click += (_, _) => _pathBox.Text = _defaultPath;

        buttons.Controls.Add(_useButton);
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_defaultButton);
        root.Controls.Add(buttons, 0, 3);

        AcceptButton = _useButton;
        CancelButton = _cancelButton;
    }

    private void ConfigureButtonToolTip()
    {
        _buttonToolTip.AutoPopDelay = 12000;
        _buttonToolTip.InitialDelay = 350;
        _buttonToolTip.ReshowDelay = 100;
        _buttonToolTip.ShowAlways = true;
        _buttonToolTip.BackColor = Color.FromArgb(30, 35, 40);
        _buttonToolTip.ForeColor = Color.FromArgb(236, 241, 244);
    }

    private void LoadRootNodes()
    {
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            foreach (var rootPath in Directory.GetLogicalDrives().OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                _tree.Nodes.Add(CreateDirectoryNode(rootPath, rootPath));
            }

            var rootNode = _tree.Nodes.Cast<TreeNode>().FirstOrDefault(node =>
                node.Tag is string path &&
                SelectedPath.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            if (rootNode is not null)
            {
                _tree.SelectedNode = rootNode;
                LoadDirectoryChildren(rootNode);
                rootNode.Expand();
            }
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private void Tree_BeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node is null)
        {
            return;
        }

        Cursor previousCursor = Cursor;
        Cursor = Cursors.WaitCursor;
        try
        {
            LoadDirectoryChildren(e.Node);
        }
        finally
        {
            Cursor = previousCursor;
        }
    }

    private void UseSelectedPath()
    {
        var path = _pathBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(
                this,
                "Please choose an existing folder.",
                "Media Library",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SelectedPath = Path.GetFullPath(path);
        DialogResult = DialogResult.OK;
        Close();
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
            var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(path)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                node.Nodes.Add(CreateDirectoryNode(directory, Path.GetFileName(directory)));
                count++;
                if (count >= MaxDirectoriesPerNode)
                {
                    node.Nodes.Add(new TreeNode("Too many folders to show here. Type the path above."));
                    break;
                }
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

    private static void StyleDialogButton(Button button, Color backColor)
    {
        button.Height = 32;
        button.Margin = new Padding(8, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = backColor;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.TextAlign = ContentAlignment.MiddleCenter;
    }
}
