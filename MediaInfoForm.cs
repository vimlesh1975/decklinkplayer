namespace ffmpegplayer;

internal sealed class MediaInfoForm : Form
{
    private readonly string _path;
    private readonly CancellationTokenSource _loadCancellation = new();
    private readonly DataGridView _grid = new();
    private readonly Label _titleLabel = new();
    private readonly Label _pathLabel = new();

    public MediaInfoForm(string path)
    {
        _path = path;

        Text = $"MediaInfo - {Path.GetFileName(path)}";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(1040, 700);
        MinimumSize = new Size(760, 480);
        BackColor = Color.FromArgb(22, 25, 29);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        ShowInTaskbar = false;

        BuildUi();
        FormClosed += (_, _) =>
        {
            _loadCancellation.Cancel();
        };
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CenterNearOwner();
        await LoadMediaInfoAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(14),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = BackColor,
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _titleLabel.AutoSize = false;
        _titleLabel.Dock = DockStyle.Fill;
        _titleLabel.Text = Path.GetFileName(_path);
        _titleLabel.Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold, GraphicsUnit.Point);
        _titleLabel.ForeColor = Color.FromArgb(239, 244, 248);
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft;

        _pathLabel.AutoSize = false;
        _pathLabel.Dock = DockStyle.Fill;
        _pathLabel.Text = _path;
        _pathLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        _pathLabel.ForeColor = Color.FromArgb(166, 179, 190);
        _pathLabel.TextAlign = ContentAlignment.MiddleLeft;

        header.Controls.Add(_titleLabel, 0, 0);
        header.Controls.Add(_pathLabel, 0, 1);
        root.Controls.Add(header, 0, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.FromArgb(13, 16, 19);
        _grid.GridColor = Color.FromArgb(54, 61, 68);
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoGenerateColumns = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = 28;
        _grid.RowTemplate.Height = 24;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 44, 50);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(236, 241, 244);
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 44, 50);
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(236, 241, 244);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(17, 20, 24);
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(226, 234, 238);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 116, 190);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(29, 34, 39);

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Property",
            HeaderText = "Property",
            Width = 280,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value",
            HeaderText = "Value",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        _grid.Rows.Add("Status", "Loading...");
        root.Controls.Add(_grid, 0, 1);
    }

    private async Task LoadMediaInfoAsync()
    {
        try
        {
            var rows = await Task.Run(
                () => MediaInfoProvider.Read(_path, _loadCancellation.Token),
                _loadCancellation.Token);

            if (_loadCancellation.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            PopulateRows(rows);
        }
        catch (OperationCanceledException)
        {
            // The window was closed before MediaInfo finished.
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                PopulateRows(
                    [
                        new MediaInfoRow("MediaInfo", "Error", ex.Message),
                    ]);
            }
        }
    }

    private void PopulateRows(IReadOnlyList<MediaInfoRow> rows)
    {
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();

            if (rows.Count == 0)
            {
                AddSectionHeader("MediaInfo");
                _grid.Rows.Add("Status", "No information returned.");
                return;
            }

            string? currentSection = null;
            foreach (var row in rows)
            {
                if (!string.Equals(currentSection, row.Section, StringComparison.Ordinal))
                {
                    currentSection = row.Section;
                    AddSectionHeader(currentSection);
                }

                _grid.Rows.Add(row.Property, row.Value);
            }
        }
        finally
        {
            _grid.ResumeLayout();
        }
    }

    private void AddSectionHeader(string section)
    {
        var rowIndex = _grid.Rows.Add(section, string.Empty);
        var row = _grid.Rows[rowIndex];
        row.DefaultCellStyle.BackColor = Color.FromArgb(38, 44, 50);
        row.DefaultCellStyle.ForeColor = Color.FromArgb(239, 244, 248);
        row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 44, 50);
        row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(239, 244, 248);
        row.DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
        row.Height = 28;
    }

    private void CenterNearOwner()
    {
        if (Owner is null)
        {
            var screen = Screen.FromControl(this).WorkingArea;
            Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
            return;
        }

        var ownerBounds = Owner.Bounds;
        var area = Screen.FromControl(Owner).WorkingArea;
        var x = ownerBounds.Left + (ownerBounds.Width - Width) / 2;
        var y = ownerBounds.Top + (ownerBounds.Height - Height) / 2;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
    }
}
