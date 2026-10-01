// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

using System.Diagnostics;

namespace AllianceWatch;

internal sealed class DatabaseCompressionForm : Form
{
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = UiTheme.Cyan, Text = "Pausing collection…", TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Maximum = 1000 };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, ScrollBars = ScrollBars.Vertical, TabStop = false };
    private readonly Button _close = UiTheme.Button("CLOSE");
    private readonly Button _backup = UiTheme.Button("OPEN BACKUP FOLDER");
    private bool _finished;
    internal DatabaseCompressionResult? Result { get; private set; }

    internal DatabaseCompressionForm(Storage storage, Func<Task> pauseCollection, CancellationToken cancellationToken)
    {
        Text = "AllianceWatch // Compress Database";
        Size = new Size(680, 370);
        MinimumSize = new Size(640, 350);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = UiTheme.Surface;
        ForeColor = UiTheme.Text;
        Font = UiTheme.Small;
        ControlBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Creates a verified backup, shares identical image content, and reclaims unused space. Large archives can take several minutes. Collection resumes when this window closes." }, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_progress, 0, 2);
        layout.Controls.Add(_details, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        _close.Enabled = false;
        _close.Click += (_, _) => Close();
        _backup.Visible = false;
        _backup.Click += (_, _) =>
        {
            if (Result is not null) Process.Start(new ProcessStartInfo(Path.GetDirectoryName(Result.BackupPath)!) { UseShellExecute = true });
        };
        actions.Controls.AddRange([_close, _backup]);
        layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout);
        FormClosing += (_, e) => { if (!_finished && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        Shown += async (_, _) =>
        {
            try
            {
                await pauseCollection();
                var progress = new Progress<DatabaseCompressionProgress>(value =>
                {
                    if (_finished || IsDisposed) return;
                    _status.Text = value.Total > 0 ? $"{value.Stage} // {value.Processed:N0} / {value.Total:N0}" : value.Stage + "…";
                    _progress.Style = value.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                    if (value.Total > 0) _progress.Value = (int)Math.Clamp(value.Processed * 1000d / value.Total, 0, 1000);
                });
                Result = await Task.Run(() => storage.CompressDatabase(progress, cancellationToken), cancellationToken);
                _status.Text = "Compression complete. Every article-image link is preserved.";
                _details.Text = $"{Result.ImageLinks:N0} article-image links → {Result.UniqueImages:N0} unique images\r\n"
                    + $"Database: {SizeText(Result.BeforeBytes)} → {SizeText(Result.AfterBytes)}\r\n"
                    + $"Space reclaimed: {SizeText(Math.Max(0, Result.BeforeBytes - Result.AfterBytes))}\r\n\r\n"
                    + $"Backup: {Result.BackupPath}\r\nKeep the backup until you have checked the gallery.";
                _backup.Visible = true;
            }
            catch (Exception ex)
            {
                _status.Text = "Compression stopped.";
                _details.Text = ex.Message + "\r\nAny completed image merges remain valid. A backup created before compression is retained in the database folder.";
            }
            finally
            {
                _finished = true;
                if (!IsDisposed)
                {
                    _progress.Style = ProgressBarStyle.Continuous;
                    _progress.Value = Result is null ? 0 : 1000;
                    _close.Enabled = true;
                    ControlBox = true;
                }
            }
        };
    }

    private static string SizeText(long bytes) => $"{bytes / (1024d * 1024):N1} MB";
}
