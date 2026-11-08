namespace NPPAI.Setup;

internal sealed class SetupForm : Form
{
    readonly Package package;
    readonly TextBox folder = new() { Dock = DockStyle.Fill };
    readonly Button browse = new() { Text = "Browse…", AutoSize = true };
    readonly Button install = new() { Text = "Install", AutoSize = true };
    readonly Button cancel = new() { Text = "Close", AutoSize = true };
    readonly CheckBox accept = new() { Text = "I accept the bundled model licence terms", AutoSize = true };
    readonly Label status = new() { Text = "Choose the folder containing notepad++.exe.", AutoSize = false, Dock = DockStyle.Fill };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    CancellationTokenSource? operation;

    internal SetupForm(Package package)
    {
        this.package = package;
        Text = $"NPPAI {package.Tag} Setup";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(650, 365);
        MinimumSize = new Size(620, 420);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Install NPPAI for x64 Notepad++", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
        layout.Controls.Add(new Label { Text = "Downloads about 8.7 GB. Allow 20 GB free space. Save and close Notepad++ first.\nBoth models and runtimes are included; Ollama is not required.", AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 12) });
        var destination = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.Controls.Add(folder); destination.Controls.Add(browse);
        layout.Controls.Add(destination);
        var terms = new LinkLabel { Text = "Model licences — Qwen2.5-3B is for non-commercial research/evaluation", AutoSize = true, MaximumSize = new Size(600, 0), Margin = new Padding(0, 0, 0, 8) };
        terms.LinkClicked += (_, _) => ShowLicences();
        layout.Controls.Add(terms);
        layout.Controls.Add(accept);
        status.Margin = new Padding(0, 12, 0, 8);
        layout.Controls.Add(status);
        layout.Controls.Add(progress);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        actions.Controls.Add(cancel); actions.Controls.Add(install); layout.Controls.Add(actions);
        Controls.Add(layout);
        folder.Text = SetupCore.FindNotepad().FirstOrDefault() ?? "";
        if (folder.Text.Length > 0) status.Text = "Detected x64 Notepad++. Confirm the folder, accept the model licences, then install.";
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select the folder containing x64 notepad++.exe", SelectedPath = folder.Text, UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        install.Click += async (_, _) => await Install();
        cancel.Click += (_, _) => { if (operation != null) operation.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (operation != null) { operation.Cancel(); e.Cancel = true; status.Text = "Cancelling safely…"; } };
    }

    void ShowLicences()
    {
        using var dialog = new Form { Text = "Bundled model licences", Width = 750, Height = 600, StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Text = Program.Resource("Model-Licenses.txt").Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill });
        dialog.ShowDialog(this);
    }

    async Task Install()
    {
        if (!accept.Checked) { status.Text = "Review and accept the model licences to continue."; return; }
        var directory = folder.Text.Trim();
        operation = new CancellationTokenSource();
        folder.Enabled = browse.Enabled = accept.Enabled = install.Enabled = false;
        cancel.Text = "Cancel";
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPPAI", "Setup", package.Tag);
            var updates = new Progress<SetupProgress>(p => { status.Text = p.Message; progress.Style = p.Total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee; if (p.Total > 0) progress.Value = (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000); });
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NPPAI-Setup/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            var warning = await Task.Run(() => SetupCore.Install(package, directory, cache, client, updates, operation.Token));
            progress.Style = ProgressBarStyle.Continuous; progress.Value = 1000;
            status.Text = "Installed and verified. Open Notepad++ normally, then check Plugins → NPPAI → Status." + (warning == null ? "" : "\n" + warning);
            install.Text = "Installed";
        }
        catch (OperationCanceledException) { status.Text = "Cancelled. Your previous plugin is unchanged. Run Install again to resume the downloads."; install.Enabled = true; }
        catch (Exception error) { status.Text = error.Message + "\nFix the issue and retry. Verified downloads will be reused."; install.Enabled = true; }
        finally
        {
            operation.Dispose(); operation = null;
            folder.Enabled = browse.Enabled = accept.Enabled = true; cancel.Text = "Close";
            if (progress.Style == ProgressBarStyle.Marquee) { progress.Style = ProgressBarStyle.Continuous; progress.Value = 0; }
        }
    }
}
