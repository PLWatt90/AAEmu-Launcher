// Main window of AAEmu-Launcher. All work runs on a background task; the log box and the
// status line are updated through the UI thread. Layout is built in code (no designer file):
// the key art fills the window, the fields sit in a dark bar along the bottom, and the log is a
// drawer above the bar that the player opens with "Show log" (it opens by itself on warnings).

namespace AAEmuLauncher;

internal sealed partial class MainForm : Form
{
    private readonly TextBox _account = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true }; // never saved, never logged
    private readonly TextBox _server = new();
    private readonly TextBox _share = new();
    private readonly CheckBox _updatePak = new() { Text = "Update game_pak before starting", AutoSize = true, Checked = true };
    private readonly Button _play = new() { Text = "Login" }; // "Play" once Login succeeded
    private readonly Button _check = new() { Text = "Check for updates" };
    private readonly Button _credential = new() { Text = "Store share credential..." };
    private readonly Button _restart = new() { Text = "Restart to update", Visible = false };
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly Button _logToggle = new();
    private readonly GlassPanel _logDrawer = new(Color.FromArgb(222, 18, 15, 12));
    private readonly ToolTip _tips = new();
    private ArtPanel _art;
    private FadePanel _bar;
    private int _unseenLogLines;
    private bool _logOpen; // own flag: Control.Visible reads false until the window itself is shown

    // Colours taken from the art: dark ink, warm gold, cream text.
    private static readonly Color Ink = Color.FromArgb(24, 20, 17);
    private static readonly Color BarInk = Color.FromArgb(18, 15, 12);
    private static readonly Color Gold = Color.FromArgb(214, 176, 104);
    private static readonly Color GoldDeep = Color.FromArgb(196, 150, 72);
    private static readonly Color Cream = Color.FromArgb(244, 237, 224);
    private static readonly Color Muted = Color.FromArgb(190, 178, 158);
    private static readonly Color FieldBack = Color.FromArgb(40, 35, 30);
    private static readonly Color FieldText = Color.FromArgb(246, 240, 228);
    private static readonly Color ToolBorder = Color.FromArgb(78, 70, 60);

    private readonly LauncherCore _core;
    private bool _shareFollowsServer = true; // the share field tracks \\<server>\ArcheAgePatch until edited by hand
    private bool _settingShare;              // true while the code itself writes the share field
    private bool _busy;
    private string _stagedUpdate;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId; // the constructor runs on the UI thread

    public MainForm()
    {
        _core = new LauncherCore(Program.ExeDir, Log);
        BuildLayout();
        LoadSettings();
        SetLoggedIn(false);
        _unseenLogLines = 0; // the start-up lines are routine; count only what comes after
        UpdateLogToggle();
        Shown += (_, _) => OnShown();
        FormClosing += OnFormClosing;
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        Text = Program.WindowTitle;
        Font = new Font("Segoe UI", 9.5f);
        // Sizes below are in 96-dpi pixels; WinForms scales them (and the fonts) to the system DPI.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1120, 630);
        MinimumSize = new Size(940, 560);
        BackColor = Ink;

        // --- the art, filling the window (centre of the group of characters kept in view)
        _art = new ArtPanel(LoadArt(), focus: new PointF(0.5f, 0.4f)) { Dock = DockStyle.Fill };
        ResizeBegin += (_, _) => _art.FastScaling = true;
        ResizeEnd += (_, _) => _art.FastScaling = false;

        var title = new ShadowLabel { Text = Program.Config.ProductName, Font = new Font("Georgia", 28f, FontStyle.Bold), ForeColor = Color.White, Location = new Point(22, 16) };
        var subtitle = new ShadowLabel { Text = (Program.Config.Subtitle.Length > 0 ? Program.Config.Subtitle + "  ·  " : "") + "v" + Program.VersionText, Font = new Font("Segoe UI", 10f), ForeColor = Color.White, Location = new Point(26, 64) };

        // --- bottom bar: fields | connection | options | Play, then status + log toggle
        _bar = new FadePanel { Dock = DockStyle.Bottom, Height = 226, FadeHeight = 50, Tint = Color.FromArgb(220, BarInk), Padding = new Padding(26, 56, 26, 10) };
        var grid = new ClearTable { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(AccountFields(), 0, 0);
        grid.Controls.Add(FieldPair(
            "Server", _server, "IP address or host name of the game's login server",
            "Patch source", _share, "Patch server URL (default " + LauncherCore.DefaultShare(Program.DefaultServer) +
                                    ") or a folder holding manifest.txt; leave the default unless told otherwise"), 1, 0);

        var options = new ClearTable { ColumnCount = 1, RowCount = 3, AutoSize = true, Margin = new Padding(0, 14, 22, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _updatePak.ForeColor = Cream;
        _updatePak.BackColor = Color.Transparent;
        _updatePak.Margin = new Padding(0, 6, 0, 6);
        StyleButton(_check, FieldBack, Cream, ToolBorder, 9f);
        StyleButton(_credential, FieldBack, Cream, ToolBorder, 9f);
        _check.Dock = _credential.Dock = DockStyle.Fill;
        _check.Margin = new Padding(0, 0, 0, 6);
        _credential.Margin = Padding.Empty;
        // The share credential is only of use when the config names a Windows share fallback.
        var optionControls = Program.ShareName.Length > 0 ? new Control[] { _updatePak, _check, _credential } : new Control[] { _updatePak, _check };
        foreach (var control in optionControls)
        {
            options.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            options.Controls.Add(control);
        }
        grid.Controls.Add(options, 2, 0);

        StyleButton(_play, Gold, Ink, GoldDeep, 15f, bold: true);
        _play.FlatAppearance.MouseOverBackColor = Color.FromArgb(226, 192, 124);
        _play.MinimumSize = new Size(190, 64);
        _play.Margin = new Padding(0, 20, 0, 0);
        _play.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        grid.Controls.Add(_play, 3, 0);

        var footer = new ClearTable { ColumnCount = 5, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++)
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.AutoSize = false;
        _status.AutoEllipsis = true;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Muted;
        _status.BackColor = Color.Transparent;
        _status.Font = new Font("Segoe UI", 8.75f, FontStyle.Italic);
        _status.Margin = Padding.Empty;
        // Appears only when a newer launcher has been staged from the share (OnShown).
        StyleButton(_restart, Gold, Ink, GoldDeep, 8.75f, bold: true);
        _restart.MinimumSize = new Size(0, 24);
        _restart.Margin = new Padding(0, 0, 10, 0);
        StyleButton(_logToggle, BarInk, Gold, BarInk, 8.75f);
        _logToggle.MinimumSize = new Size(0, 24);
        _logToggle.Margin = Padding.Empty;
        _logToggle.FlatAppearance.BorderSize = 0;
        BuildAccountDrawers(); // styles the two links too
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_registerLink, 1, 0);
        footer.Controls.Add(_recoverLink, 2, 0);
        footer.Controls.Add(_restart, 3, 0);
        footer.Controls.Add(_logToggle, 4, 0);
        grid.Controls.Add(footer, 0, 1);
        grid.SetColumnSpan(footer, 4);
        _bar.Controls.Add(grid);

        // --- log drawer: translucent box above the bar, bottom-right, hidden until opened
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.Font = new Font("Consolas", 9f);
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = BarInk;
        _log.ForeColor = Color.FromArgb(214, 204, 186);
        _log.Dock = DockStyle.Fill;
        _logDrawer.Padding = new Padding(10, 8, 10, 8);
        _logDrawer.Visible = false;
        _logDrawer.Controls.Add(_log);

        _art.Controls.Add(_logDrawer);
        _art.Controls.Add(_registerDrawer);
        _art.Controls.Add(_recoverDrawer);
        _art.Controls.Add(title);
        _art.Controls.Add(subtitle);
        _art.Controls.Add(_bar);
        Controls.Add(_art);
        _logDrawer.BringToFront();
        _art.Resize += (_, _) => PlaceDrawers();
        UpdateLogToggle();

        AcceptButton = _play;
        _play.Click += (_, _) => OnPlay();
        _check.Click += (_, _) => OnCheckForUpdates();
        _credential.Click += (_, _) => OnStoreCredential();
        _restart.Click += (_, _) => OnRestartToUpdate();
        _logToggle.Click += (_, _) => ShowLog(!_logOpen);
        _server.TextChanged += (_, _) => OnServerChanged();
        foreach (var box in new[] { _account, _password, _pin, _server })
            box.TextChanged += (_, _) => OnCredentialsChanged();
        _share.TextChanged += (_, _) => OnShareChanged();
        Load += (_, _) => FitToScreen();
    }

    /// <summary>Two caption-over-box fields stacked in one bar column.</summary>
    private TableLayoutPanel FieldPair(string caption1, TextBox box1, string hint1, string caption2, TextBox box2, string hint2)
    {
        var table = new ClearTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 0, 22, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (caption, box, hint) in new[] { (caption1, box1, hint1), (caption2, box2, hint2) })
        {
            var label = new Label { Text = caption, AutoSize = true, ForeColor = Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.75f), Margin = Padding.Empty };
            StyleField(box, hint);
            box.AccessibleName ??= caption;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(label);
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(box);
        }
        return table;
    }

    private static void StyleButton(Button button, Color back, Color fore, Color border, float size, bool bold = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = back;
        button.ForeColor = fore;
        button.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        button.AutoSize = true;
        button.MinimumSize = new Size(0, 30);
        button.Padding = new Padding(8, 0, 8, 0);
        button.Cursor = Cursors.Hand;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.15f);
    }

    /// <summary>
    /// The background embedded in the exe (configackground.jpg at build time), or a plain painted
    /// gradient when the build had none.
    /// </summary>
    private static Image LoadArt()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AAEmuLauncher.Background.jpg");
            if (stream != null)
            {
                using var image = Image.FromStream(stream);
                return new Bitmap(image); // detached copy: FromStream needs its stream for the image's lifetime
            }
        }
        catch
        {
            // fall through to the painted background
        }
        return DefaultArt();
    }

    private static Bitmap DefaultArt()
    {
        var bitmap = new Bitmap(1600, 900);
        using var g = Graphics.FromImage(bitmap);
        using var sky = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 1600, 900),
            Color.FromArgb(58, 64, 86), Color.FromArgb(26, 22, 19), System.Drawing.Drawing2D.LinearGradientMode.Vertical);
        g.FillRectangle(sky, 0, 0, 1600, 900);
        using var glowPath = new System.Drawing.Drawing2D.GraphicsPath();
        glowPath.AddEllipse(300, -250, 1000, 800);
        using var glow = new System.Drawing.Drawing2D.PathGradientBrush(glowPath)
        {
            CenterColor = Color.FromArgb(70, 214, 176, 104),
            SurroundColors = [Color.FromArgb(0, 214, 176, 104)],
        };
        g.FillPath(glow, glowPath);
        return bitmap;
    }

    /// <summary>Shrinks the window to the screen's working area on small or high-scaling screens.</summary>
    private void FitToScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        if (Width <= area.Width && Height <= area.Height)
            return;
        Size = new Size(Math.Max(Math.Min(Width, area.Width), MinimumSize.Width), Math.Max(Math.Min(Height, area.Height), MinimumSize.Height));
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    private void ShowLog(bool show)
    {
        _logOpen = show;
        if (show)
        {
            _unseenLogLines = 0;
            PlaceDrawers();
            _logDrawer.Visible = true;
            _logDrawer.BringToFront();
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
        else
        {
            _logDrawer.Visible = false;
        }
        UpdateLogToggle();
    }

    private void UpdateLogToggle() =>
        _logToggle.Text = _logOpen ? "Hide log ▾"
            : _unseenLogLines > 0 ? $"Show log ({_unseenLogLines} new) ▴" : "Show log ▴";

    // ---------------------------------------------------------------- settings

    private void LoadSettings()
    {
        var settings = Settings.Load(_core.RootDir, out var problem);
        var savedServer = settings.Server?.Trim() ?? "";
        var retired = Program.RetiredServers.Contains(savedServer);
        _server.Text = savedServer.Length > 0 && !retired ? savedServer : Program.DefaultServer; // fills the share too
        // A saved value equal to the old default share (\\server\ArcheAgePatch) follows the server again, so
        // launchers updated from 1.2 switch to the patch server by themselves.
        // So does an old default share or patch-server URL of a retired server address.
        var savedShare = settings.Share?.Trim() ?? "";
        var followed = Program.RetiredServers.Append(_server.Text.Trim())
            .SelectMany(s => new[] { LauncherCore.LegacyShare(s), $"http://{s}:{Program.PatchPort}/" })
            .Append(LauncherCore.DefaultShare(_server.Text.Trim()));
        if (savedShare.Length > 0 && !followed.Contains(savedShare, StringComparer.OrdinalIgnoreCase))
            _share.Text = savedShare; // the handler turns following off
        _account.Text = settings.Account ?? "";
        _updatePak.Checked = settings.UpdatePak;

        Log($"{Program.WindowTitle} - client folder: {_core.RootDir}");
        Log("Config: " + (Program.Config.Sources.Count == 0 ? "built-in defaults" : string.Join(", ", Program.Config.Sources)));
        foreach (var configProblem in Program.Config.Problems)
            Log("WARNING: " + configProblem);
        if (problem != null)
            Log(problem);
        if (retired)
            Log($"The server moved from {savedServer} to {Program.DefaultServer}; the Server field now holds the new address.");
        if (!File.Exists(_core.ClientExe))
            Log($"WARNING: archeage.exe not found in \"{_core.ClientExeDir}\". Put this launcher in the client folder, next to {Program.Config.ClientDir} and game_pak.");
        Status(File.Exists(Path.Combine(_core.RootDir, Settings.FileName)) ? "Settings loaded from launcher.json."
            : Trusted ? "Enter your account name and click Play." : "Enter your account name and password and click Login.");
        if (!settings.UpdatePak)
        {
            // An untick is meant for one start only (OnPlay ticks the box again); say so when it stuck.
            Log("WARNING: game_pak updates are OFF (\"Update game_pak before starting\" is unticked); tick the box to get server patches.");
            Status("game_pak updates are OFF; tick the box to get server patches.");
        }
        (_account.Text.Length == 0 || Trusted ? _account : (Control)_password).Select();
    }

    private void SaveSettings()
    {
        try
        {
            new Settings
            {
                Account = _account.Text.Trim(),
                Server = _server.Text.Trim(),
                Share = _share.Text.Trim(),
                UpdatePak = _updatePak.Checked,
            }.Save(_core.RootDir);
        }
        catch (Exception exception)
        {
            Log($"Could not save {Settings.FileName}: {exception.Message}");
        }
    }

    private void OnServerChanged()
    {
        if (!_shareFollowsServer)
            return;
        _settingShare = true;
        try { _share.Text = LauncherCore.DefaultShare(_server.Text.Trim()); }
        finally { _settingShare = false; }
    }

    private void OnShareChanged()
    {
        if (_settingShare)
            return;
        var text = _share.Text.Trim();
        _shareFollowsServer = text.Length == 0 || text == LauncherCore.DefaultShare(_server.Text.Trim());
    }

    private string ShareOrDefault()
    {
        var text = _share.Text.Trim();
        return text.Length > 0 ? text : LauncherCore.DefaultShare(_server.Text.Trim());
    }

    // ---------------------------------------------------------------- startup / shutdown

    private void OnShown()
    {
        var share = ShareOrDefault();
        var server = _server.Text.Trim();
        var staged = Environment.ProcessPath + ".new";
        Task.Run(() =>
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* leftover of an interrupted swap; harmless */ }
            var path = _core.StageLauncherUpdate(share, server);
            if (path == null || IsDisposed)
                return;
            BeginInvoke(() =>
            {
                _stagedUpdate = path;
                _restart.Visible = true;
                Status("A newer launcher is ready; click \"Restart to update\".");
            });
        });
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this, "A step is still running (the game_pak update may be in progress). Close anyway?",
                Program.WindowTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        SaveSettings();
    }

    // ---------------------------------------------------------------- actions

    private bool ValidateFields(out string account, out string password, out string pin, out string server)
    {
        account = _account.Text.Trim();
        password = _password.Text;
        pin = _pin.Text;
        server = _server.Text.Trim();
        if (!LauncherCore.IsValidAccount(account))
        {
            Log("Account name must be 1-32 letters or digits (no spaces or other characters).");
            Status("Please enter a valid account name.");
            _account.Focus();
            _account.SelectAll();
            return false;
        }
        if (Trusted)
        {
            password = "";
            pin = "";
        }
        else if (password.Length < LauncherCore.MinPasswordLength)
        {
            Log($"Password must be at least {LauncherCore.MinPasswordLength} characters. No account yet? Click \"Create account\".");
            Status("Please enter your password.");
            _password.Focus();
            _password.SelectAll();
            return false;
        }
        if (!Trusted && pin.Length > 0 && !LauncherCore.IsValidPin(pin))
        {
            Log($"The PIN is {LauncherCore.MinPinLength}-{LauncherCore.MaxPinLength} digits. Leave it empty only if your account has no PIN.");
            Status("Please enter your PIN.");
            _pin.Focus();
            _pin.SelectAll();
            return false;
        }
        if (!LauncherCore.IsValidServer(server))
        {
            Log("Server must be an IPv4 address or host name (letters, digits, dots, dashes).");
            Status("Please enter a valid server address.");
            _server.Focus();
            _server.SelectAll();
            return false;
        }
        return true;
    }

    private void OnPlay()
    {
        if (_busy)
            return;
        if (!_loggedIn)
        {
            OnLogin(); // the button reads "Login" until the server accepted these details
            return;
        }
        if (!ValidateFields(out var account, out var password, out var pin, out var server))
            return;
        var share = ShareOrDefault();
        var update = _updatePak.Checked;
        SaveSettings();
        Log("---- Play ----");
        RunBusy("Starting ...", () => RunPlay(account, password, pin, server, share, update), result =>
        {
            switch (result)
            {
                case PlayResult.Started:
                    Status($"{Program.Config.ProductName} started as \"{account}\" against {server}:{Program.AuthPort}.");
                    if (!update)
                    {
                        // The untick applies to one start only, so a workaround cannot silently become permanent.
                        _updatePak.Checked = true;
                        SaveSettings();
                        Log("\"Update game_pak before starting\" is ticked again; the untick applied to this start only.");
                    }
                    WindowState = FormWindowState.Minimized;
                    break;
                case PlayResult.UpdateFailed:
                    Status("game_pak could not be updated; the game was not started.");
                    MessageBox.Show(this,
                        "game_pak could not be updated, so the game was not started. It would be missing the server's client changes.\n\n" +
                        "Close any running game client and click Play again. To start anyway, untick " +
                        "\"Update game_pak before starting\" and click Play.",
                        "game_pak not updated", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                default:
                    Status("The game was not started; see the log.");
                    break;
            }
        });
    }

    private enum PlayResult { Started, ClientMissing, UpdateFailed, StartFailed }

    /// <summary>Background thread: the launch steps, in order.</summary>
    private PlayResult RunPlay(string account, string password, string pin, string server, string share, bool update)
    {
        // (a) the client must be there
        if (!_core.ClientPresent())
            return PlayResult.ClientMissing;

        // (b) client locale, once (only when the config names one)
        try { _core.EnsureClientLocale(); }
        catch (Exception exception) { Log($"Client locale step failed: {exception.Message} (continuing)"); }

        // (c)+(d) game_pak update, unless the game already holds the pak open
        if (!update)
        {
            Log("game_pak update skipped (\"Update game_pak before starting\" is unticked).");
        }
        else if (_core.GameRunning())
        {
            Log("WARNING: this folder's archeage.exe is already running, so game_pak is locked; skipping the update this time.");
        }
        else
        {
            UpdateOutcome outcome;
            try { outcome = _core.UpdatePak(share, server, dryRun: false); }
            catch (Exception exception)
            {
                Log($"game_pak update failed: {exception.Message}");
                outcome = new UpdateOutcome(UpdateKind.Failed, 2);
            }
            switch (outcome.Kind)
            {
                case UpdateKind.UpToDate:
                    Log("game_pak is up to date.");
                    break;
                case UpdateKind.ShareUnreachable:
                case UpdateKind.ToolMissing:
                    break; // already explained in the log; the game still starts in these cases
                default:
                    Log("WARNING: game_pak could not be updated. The game would be missing the server's client changes.");
                    Log("Close any running game client and click Play again, or untick \"Update game_pak before starting\" to start anyway.");
                    return PlayResult.UpdateFailed;
            }
        }

        // (e) start the client
        try
        {
            _core.StartGame(account, server, password, pin);
            return PlayResult.Started;
        }
        catch (Exception exception)
        {
            Log($"Could not start archeage.exe: {exception.Message}");
            return PlayResult.StartFailed;
        }
    }

    private void OnCheckForUpdates()
    {
        if (_busy)
            return;
        var server = _server.Text.Trim();
        var share = ShareOrDefault();
        SaveSettings();
        Log("---- Check for updates ----");
        RunBusy("Checking game_pak ...", () =>
        {
            try { return _core.UpdatePak(share, server, dryRun: true); }
            catch (Exception exception)
            {
                Log($"Check failed: {exception.Message}");
                return new UpdateOutcome(UpdateKind.Failed, 2);
            }
        }, outcome => Status(outcome.Kind switch
        {
            UpdateKind.UpToDate => "game_pak is up to date.",
            UpdateKind.Pending => "Updates are pending; Play applies them when the box is ticked.",
            UpdateKind.ShareUnreachable => "Patch source not reachable; see the log.",
            UpdateKind.ToolMissing => "aapatch.exe not found; nothing checked.",
            _ => $"Check failed (aapatch exit code {outcome.ExitCode}); see the log.",
        }));
    }

    private void OnStoreCredential()
    {
        if (_busy)
            return;
        var server = _server.Text.Trim();
        if (HttpPatchSource.IsHttp(ShareOrDefault()))
        {
            Status("The patch server needs no credential; this is only for a \\\\server\\share patch source.");
            return;
        }
        var target = LauncherCore.HostOfShare(ShareOrDefault()) ?? server;
        if (!LauncherCore.IsValidServer(target))
        {
            Status("Enter the server address first.");
            _server.Focus();
            return;
        }
        var password = PasswordDialog.Ask(this, target);
        if (string.IsNullOrEmpty(password))
            return;
        RunBusy("Storing credential ...", () =>
        {
            try { return _core.StoreShareCredential(target, password); }
            catch (Exception exception)
            {
                Log($"cmdkey could not be run: {exception.Message}");
                return false;
            }
        }, ok => Status(ok ? $"Share credential for {target} stored." : "Storing the credential failed; see the log."));
    }

    private void OnRestartToUpdate()
    {
        if (_busy || _stagedUpdate == null || !File.Exists(_stagedUpdate))
        {
            Status("No staged launcher update found.");
            _restart.Visible = false;
            return;
        }
        try
        {
            SaveSettings();
            _core.RestartToUpdate(_stagedUpdate);
            Close();
        }
        catch (Exception exception)
        {
            Log($"Restart failed: {exception.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private void RunBusy<T>(string statusText, Func<T> work, Action<T> done)
    {
        SetBusy(true);
        Status(statusText);
        Task.Run(work).ContinueWith(task =>
        {
            if (IsDisposed)
                return;
            BeginInvoke(() =>
            {
                SetBusy(false);
                if (task.IsFaulted)
                {
                    var error = task.Exception?.GetBaseException();
                    Log($"Unexpected error: {error?.Message}");
                    Status("Unexpected error; see the log.");
                    return;
                }
                done(task.Result);
            });
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Control control in new Control[] { _account, _password, _pin, _server, _share, _updatePak, _play, _check, _credential, _restart,
                     _registerLink, _recoverLink, _registerDrawer, _recoverDrawer })
            control.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void Log(string line) => OnUiThread(() =>
    {
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        if (line.StartsWith("WARNING", StringComparison.Ordinal))
        {
            ShowLog(true); // warnings must not stay hidden in a closed drawer
        }
        else if (!_logOpen)
        {
            _unseenLogLines++;
            UpdateLogToggle();
        }
    });

    private void Status(string text) => OnUiThread(() =>
    {
        _status.Text = text;
        if (text.Contains("see the log", StringComparison.OrdinalIgnoreCase))
            ShowLog(true);
    });

    /// <summary>
    /// Runs the action on the UI thread. Calls from other threads are dropped once the window is disposed
    /// or while it has no handle (before it is shown, or while it closes); InvokeRequired alone cannot tell,
    /// as it reports false without a handle. The UI thread itself may log before the handle exists.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (IsDisposed)
            return;
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            action();
            return;
        }
        if (!IsHandleCreated)
            return;
        try { BeginInvoke(() => OnUiThread(action)); }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException) { }
    }
}

/// <summary>Masked prompt for the share password. The value goes straight to cmdkey and nowhere else.</summary>
internal static class PasswordDialog
{
    public static string Ask(IWin32Window owner, string target)
    {
        using var dialog = new Form
        {
            Text = "Store share credential",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Font = new Font("Segoe UI", 9f),
        };
        var table = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(12) };
        var label = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = $"Password of the share account \"{Program.ShareUser}\" on {target}.\n" +
                   $"It is handed to Windows (cmdkey /add:{target} /user:{Program.ShareUser}) and never written to a file or the log.",
        };
        var box = new TextBox { UseSystemPasswordChar = true, Width = 420, Margin = new Padding(0, 0, 0, 12) };
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
        var ok = new Button { Text = "Store", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 30), Enabled = false };
        box.TextChanged += (_, _) => ok.Enabled = box.Text.Length > 0;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        table.Controls.Add(label);
        table.Controls.Add(box);
        table.Controls.Add(buttons);
        dialog.Controls.Add(table);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => box.Focus();
        return dialog.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
