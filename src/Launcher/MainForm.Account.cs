// Account part of the main window: the PIN field, the Login -> Play button, and the two hideable drawers
// "Create account" (Register) and "Forgot password?" (new password with Discord username + PIN).
// All three talk to the login server through AuthClient; passwords and PINs are never saved or logged.

namespace AAEmuLauncher;

internal sealed partial class MainForm
{
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true, MaxLength = LauncherCore.MaxPinLength }; // never saved, never logged
    private readonly Button _registerLink = new() { Text = "Create account" };
    private readonly Button _recoverLink = new() { Text = "Forgot password?" };

    private readonly TextBox _regAccount = new();
    private readonly TextBox _regDiscord = new();
    private readonly TextBox _regPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _regPassword2 = new() { UseSystemPasswordChar = true };
    private readonly TextBox _regPin = new() { UseSystemPasswordChar = true, MaxLength = LauncherCore.MaxPinLength };
    private readonly TextBox _regPin2 = new() { UseSystemPasswordChar = true, MaxLength = LauncherCore.MaxPinLength };
    private readonly TextBox _recAccount = new();
    private readonly TextBox _recDiscord = new();
    private readonly TextBox _recPin = new() { UseSystemPasswordChar = true, MaxLength = LauncherCore.MaxPinLength };
    private readonly TextBox _recPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _recPassword2 = new() { UseSystemPasswordChar = true };

    private GlassPanel _registerDrawer;
    private GlassPanel _recoverDrawer;
    private GlassPanel _openAccountDrawer; // the one of the two that is open, or null
    private bool _loggedIn;                // Login succeeded for the current account / password / PIN / server

    /// <summary>Stock AAEmu (AuthMode Trusted): no password, PIN, Login check or account drawers.</summary>
    private static bool Trusted => Program.Config.AuthMode == AuthMode.Trusted;

    private string PinHint =>
        $"{LauncherCore.MinPinLength}-{LauncherCore.MaxPinLength} digits, chosen when the account was created. " +
        "Leave it empty if your account has no PIN (made before PINs existed). Never saved.";

    // ---------------------------------------------------------------- layout

    /// <summary>Bar column 1: account name across, then password and PIN side by side.</summary>
    private TableLayoutPanel AccountFields()
    {
        var table = new ClearTable { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = new Padding(0, 0, 22, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        for (var i = 0; i < 4; i++)
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddCaptioned(table, 0, 0, 2, "Account name", _account, "1-32 letters or digits");
        if (Trusted)
            return table; // the server logs in by account name alone
        AddCaptioned(table, 0, 2, 1, "Password", _password, $"At least {LauncherCore.MinPasswordLength} characters; never saved");
        AddCaptioned(table, 1, 2, 1, "PIN", _pin, PinHint);
        _password.Margin = new Padding(0, 2, 8, 8);
        return table;
    }

    /// <summary>Caption above a styled box in a table cell.</summary>
    private void AddCaptioned(TableLayoutPanel table, int column, int row, int span, string caption, TextBox box, string hint)
    {
        var label = new Label { Text = caption, AutoSize = true, ForeColor = Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.75f), Margin = Padding.Empty };
        StyleField(box, hint);
        box.AccessibleName ??= caption;
        table.Controls.Add(label, column, row);
        table.Controls.Add(box, column, row + 1);
        table.SetColumnSpan(label, span);
        table.SetColumnSpan(box, span);
    }

    private void StyleField(TextBox box, string hint)
    {
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = FieldBack;
        box.ForeColor = FieldText;
        box.Font = new Font("Segoe UI", 10.5f);
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 2, 0, 8);
        if (hint != null)
            _tips.SetToolTip(box, hint);
    }

    private void StyleLink(Button link)
    {
        StyleButton(link, BarInk, Gold, BarInk, 8.75f);
        link.MinimumSize = new Size(0, 24);
        link.Margin = new Padding(0, 0, 6, 0);
        link.FlatAppearance.BorderSize = 0;
    }

    private void BuildAccountDrawers()
    {
        StyleLink(_registerLink);
        StyleLink(_recoverLink);
        _registerLink.Click += (_, _) => ToggleAccountDrawer(_registerDrawer);
        _recoverLink.Click += (_, _) => ToggleAccountDrawer(_recoverDrawer);
        _registerLink.Visible = _recoverLink.Visible = !Trusted;

        _registerDrawer = BuildFormDrawer("Create account",
            "Pick an account name, a password and a PIN. You will need the password and the PIN every time you log in; " +
            "your Discord username and PIN let you set a new password if you forget it.",
            new (string, TextBox, string)[]
            {
                ("Account name", _regAccount, "1-32 letters or digits"),
                ("Discord username", _regDiscord, "Your Discord username (not the display name), e.g. some.player"),
                ("Password", _regPassword, $"At least {LauncherCore.MinPasswordLength} characters"),
                ("Repeat password", _regPassword2, null),
                ("PIN", _regPin, $"{LauncherCore.MinPinLength}-{LauncherCore.MaxPinLength} digits"),
                ("Repeat PIN", _regPin2, null),
            },
            "Create account", OnRegister);

        _recoverDrawer = BuildFormDrawer("Forgot password?",
            "Enter your account name with the Discord username and PIN you gave when you created it, then choose a new password.",
            new (string, TextBox, string)[]
            {
                ("Account name", _recAccount, null),
                ("Discord username", _recDiscord, null),
                ("PIN", _recPin, null),
                (null, null, null),
                ("New password", _recPassword, $"At least {LauncherCore.MinPasswordLength} characters"),
                ("Repeat new password", _recPassword2, null),
            },
            "Set new password", OnRecover);
    }

    /// <summary>A translucent drawer with a title, a hint, fields two per row, and submit / cancel buttons.</summary>
    private GlassPanel BuildFormDrawer(string title, string hint, (string Caption, TextBox Box, string Hint)[] fields,
        string submitText, Action submit)
    {
        var drawer = new GlassPanel(Color.FromArgb(232, 18, 15, 12)) { Visible = false, Padding = new Padding(16, 12, 16, 12) };
        var table = new ClearTable { Dock = DockStyle.Fill, ColumnCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var heading = new Label { Text = title, AutoSize = true, ForeColor = Gold, BackColor = Color.Transparent, Font = new Font("Georgia", 13f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
        var explain = new Label { Text = hint, AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 8.5f), Margin = new Padding(0, 0, 0, 8) };
        var row = 0;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(heading, 0, row);
        table.SetColumnSpan(heading, 2);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(explain, 0, ++row);
        table.SetColumnSpan(explain, 2);
        row++;

        for (var i = 0; i < fields.Length; i += 2)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (var column = 0; column < 2 && i + column < fields.Length; column++)
            {
                var (caption, box, fieldHint) = fields[i + column];
                if (box == null)
                    continue;
                box.AccessibleName = $"{title}: {caption}"; // the bar has fields with the same captions
                AddCaptioned(table, column, row, 1, caption, box, fieldHint);
                box.Margin = new Padding(0, 2, column == 0 ? 8 : 0, 6);
            }
            row += 2;
        }

        var ok = new Button { Text = submitText };
        StyleButton(ok, Gold, Ink, GoldDeep, 9.5f, bold: true);
        ok.Margin = new Padding(0, 6, 8, 0);
        ok.Click += (_, _) => submit();
        var cancel = new Button { Text = "Cancel" };
        StyleButton(cancel, FieldBack, Cream, ToolBorder, 9f);
        cancel.Margin = new Padding(0, 6, 0, 0);
        cancel.Click += (_, _) => ToggleAccountDrawer(null);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = Padding.Empty, WrapContents = false };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(buttons, 0, row);
        table.SetColumnSpan(buttons, 2);

        drawer.Controls.Add(table);
        // Enter submits the drawer while the focus is in it, and Play / Login again once it leaves.
        drawer.Enter += (_, _) => AcceptButton = ok;
        drawer.Leave += (_, _) => AcceptButton = _play;
        return drawer;
    }

    /// <summary>Opens <paramref name="drawer"/> (closing the other one), or closes it when it is already open.</summary>
    private void ToggleAccountDrawer(GlassPanel drawer)
    {
        if (drawer != null && drawer == _openAccountDrawer)
            drawer = null;
        foreach (var each in new[] { _registerDrawer, _recoverDrawer })
            each.Visible = each == drawer;
        if (_openAccountDrawer != null && _openAccountDrawer != drawer)
            ClearSecrets(_openAccountDrawer);
        _openAccountDrawer = drawer;
        _registerLink.Text = drawer == _registerDrawer ? "Create account ▾" : "Create account";
        _recoverLink.Text = drawer == _recoverDrawer ? "Forgot password? ▾" : "Forgot password?";
        AcceptButton = _play;
        PlaceDrawers();
        if (drawer == null)
        {
            _account.Select();
            return;
        }
        drawer.BringToFront();
        var first = drawer == _registerDrawer ? _regAccount : _recAccount;
        if (drawer == _recoverDrawer && _recAccount.Text.Length == 0)
            _recAccount.Text = _account.Text.Trim();
        (drawer == _recoverDrawer && _recAccount.Text.Length > 0 ? _recDiscord : first).Select();
    }

    private void ClearSecrets(GlassPanel drawer)
    {
        foreach (var box in drawer == _registerDrawer
                     ? new[] { _regPassword, _regPassword2, _regPin, _regPin2 }
                     : new[] { _recPin, _recPassword, _recPassword2 })
            box.Clear();
    }

    // ---------------------------------------------------------------- login state

    private void SetLoggedIn(bool loggedIn)
    {
        if (Trusted)
        {
            // Nothing to check: the server takes the account name as it is.
            _loggedIn = true;
            _play.Text = "Play";
            _tips.SetToolTip(_play, $"Update game_pak (when ticked) and start {Program.Config.ProductName}");
            return;
        }
        _loggedIn = loggedIn;
        _play.Text = loggedIn ? "Play" : "Login";
        _tips.SetToolTip(_play, loggedIn
            ? $"Update game_pak (when ticked) and start {Program.Config.ProductName}"
            : "Check your account name, password and PIN with the server");
    }

    /// <summary>Any change to what Login checked asks for a new Login.</summary>
    private void OnCredentialsChanged()
    {
        if (!_loggedIn || Trusted)
            return;
        SetLoggedIn(false);
        Status("Account details changed; click Login again.");
    }

    private void OnLogin()
    {
        if (_busy || !ValidateFields(out var account, out var password, out var pin, out var server))
            return;
        SaveSettings();
        Log($"---- Login as \"{account}\" at {server}:{Program.AuthPort} ----");
        RunBusy("Logging in ...", () => AuthClient.Login(server, account, password, pin), reply =>
        {
            switch (reply.Outcome)
            {
                case AuthOutcome.Accepted:
                    SetLoggedIn(true);
                    Log($"Logged in as \"{account}\". Click Play to start the game.");
                    Status($"Logged in as \"{account}\". Click Play to start the game.");
                    _play.Select();
                    break;
                case AuthOutcome.Denied:
                {
                    var text = DeniedText(reply);
                    Log("Login refused: " + text);
                    Status("Login refused: " + text);
                    MessageBox.Show(this, text, "Login refused", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _password.Select();
                    _password.SelectAll();
                    break;
                }
                default:
                    ShowServerProblem(reply);
                    break;
            }
        });
    }

    private void OnRegister()
    {
        if (_busy)
            return;
        var account = _regAccount.Text.Trim();
        var discord = LauncherCore.NormalizeDiscord(_regDiscord.Text);
        var password = _regPassword.Text;
        var pin = _regPin.Text;
        var server = _server.Text.Trim();
        var problem =
            !LauncherCore.IsValidAccount(account) ? (_regAccount, "The account name must be 1-32 letters or digits.") :
            !LauncherCore.IsValidDiscord(discord) ? (_regDiscord, "Enter your Discord username (2-32 letters, digits, '_' or '.'), as shown under your name in Discord.") :
            password.Length < LauncherCore.MinPasswordLength ? (_regPassword, $"The password must have at least {LauncherCore.MinPasswordLength} characters.") :
            password != _regPassword2.Text ? (_regPassword2, "The two passwords are not the same.") :
            !LauncherCore.IsValidPin(pin) ? (_regPin, $"The PIN must be {LauncherCore.MinPinLength}-{LauncherCore.MaxPinLength} digits.") :
            pin != _regPin2.Text ? (_regPin2, "The two PINs are not the same.") :
            !LauncherCore.IsValidServer(server) ? (_server, "Enter the server address first (bottom bar).") :
            ((TextBox)null, (string)null);
        if (problem.Item2 != null)
        {
            MessageBox.Show(this, problem.Item2, "Create account", MessageBoxButtons.OK, MessageBoxIcon.Information);
            problem.Item1.Select();
            problem.Item1.SelectAll();
            return;
        }

        Log($"---- Create account \"{account}\" (Discord {discord}) at {server}:{Program.AuthPort} ----");
        RunBusy("Creating the account ...", () => AuthClient.Register(server, account, password, pin, discord), reply =>
        {
            if (reply.Outcome == AuthOutcome.Accepted)
            {
                Log($"Account \"{account}\" created.");
                ToggleAccountDrawer(null);
                _regAccount.Clear();
                _regDiscord.Clear();
                FillLogin(account, password, pin);
                Status($"Account \"{account}\" created. Click Login.");
                MessageBox.Show(this, $"Account \"{account}\" was created.\n\nClick Login to sign in with it.", "Create account",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _play.Select();
            }
            else if (reply.Outcome == AuthOutcome.Denied)
            {
                var text = DeniedText(reply);
                Log("Create account refused: " + text);
                Status("Create account refused.");
                MessageBox.Show(this, text, "Create account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                ShowServerProblem(reply);
            }
        });
    }

    private void OnRecover()
    {
        if (_busy)
            return;
        var account = _recAccount.Text.Trim();
        var discord = LauncherCore.NormalizeDiscord(_recDiscord.Text);
        var pin = _recPin.Text;
        var password = _recPassword.Text;
        var server = _server.Text.Trim();
        var problem =
            !LauncherCore.IsValidAccount(account) ? (_recAccount, "Enter your account name (1-32 letters or digits).") :
            !LauncherCore.IsValidDiscord(discord) ? (_recDiscord, "Enter the Discord username you gave when you created the account.") :
            !LauncherCore.IsValidPin(pin) ? (_recPin, $"Enter your PIN ({LauncherCore.MinPinLength}-{LauncherCore.MaxPinLength} digits).") :
            password.Length < LauncherCore.MinPasswordLength ? (_recPassword, $"The new password must have at least {LauncherCore.MinPasswordLength} characters.") :
            password != _recPassword2.Text ? (_recPassword2, "The two passwords are not the same.") :
            !LauncherCore.IsValidServer(server) ? (_server, "Enter the server address first (bottom bar).") :
            ((TextBox)null, (string)null);
        if (problem.Item2 != null)
        {
            MessageBox.Show(this, problem.Item2, "Forgot password", MessageBoxButtons.OK, MessageBoxIcon.Information);
            problem.Item1.Select();
            problem.Item1.SelectAll();
            return;
        }

        Log($"---- New password for \"{account}\" at {server}:{Program.AuthPort} ----");
        RunBusy("Setting the new password ...", () => AuthClient.Recover(server, account, password, pin, discord), reply =>
        {
            if (reply.Outcome == AuthOutcome.Accepted)
            {
                Log($"New password set for \"{account}\".");
                ToggleAccountDrawer(null);
                FillLogin(account, password, pin);
                Status($"New password set for \"{account}\". Click Login.");
                MessageBox.Show(this, $"The new password for \"{account}\" is set.\n\nClick Login to sign in.", "Forgot password",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _play.Select();
            }
            else if (reply.Outcome == AuthOutcome.Denied)
            {
                var text = DeniedText(reply);
                Log("New password refused: " + text);
                Status("New password refused.");
                MessageBox.Show(this, text, "Forgot password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                ShowServerProblem(reply);
            }
        });
    }

    /// <summary>Puts a just created / reset account into the bar so the player only has to click Login.</summary>
    private void FillLogin(string account, string password, string pin)
    {
        _account.Text = account;
        _password.Text = password;
        _pin.Text = pin;
        SetLoggedIn(false);
    }

    private void ShowServerProblem(AuthReply reply)
    {
        Log(reply.Message);
        Status(reply.Outcome == AuthOutcome.Unreachable ? "The login server could not be reached." : "The login server did not answer; see the log.");
        MessageBox.Show(this, reply.Message + "\n\nCheck the Server field and that the server is running.", "Login server",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>The server's own text when it sent one, otherwise a text for the client's reason code.</summary>
    private static string DeniedText(AuthReply reply) =>
        reply.Message.Length > 0 ? reply.Message : reply.Reason switch
        {
            1 => "Wrong account name, password or PIN.",
            3 => "This account is already logged in.",
            >= 5 => $"This account is blocked (reason {reply.Reason}). Ask the server owner.",
            _ => $"The server refused (reason {reply.Reason}). Is the server up to date?",
        };

    // ---------------------------------------------------------------- drawer placement

    /// <summary>Account drawer bottom-left, log drawer bottom-right, both just above the bar.</summary>
    private void PlaceDrawers()
    {
        var scale = DeviceDpi / 96f;
        var margin = (int)(26 * scale);
        var area = _art.ClientSize;
        var bottom = _bar.Top + (int)(40 * scale); // overlaps the clear top of the bar's fade, not its controls
        var minTop = (int)(110 * scale);            // stays below the title
        var accountWidth = 0;

        if (_openAccountDrawer != null)
        {
            accountWidth = Math.Min((int)(440 * scale), area.Width - 2 * margin);
            var table = _openAccountDrawer.Controls[0];
            var inner = accountWidth - _openAccountDrawer.Padding.Horizontal;
            var wanted = table.GetPreferredSize(new Size(inner, 0)).Height + _openAccountDrawer.Padding.Vertical;
            var top = Math.Max(minTop, bottom - wanted);
            if (accountWidth > 0 && bottom > top)
                _openAccountDrawer.Bounds = new Rectangle(margin, top, accountWidth, bottom - top);
        }

        var room = area.Width - 2 * margin - (accountWidth > 0 ? accountWidth + margin : 0);
        var logWidth = Math.Min((int)(600 * scale), room);
        var logTop = Math.Max(minTop, bottom - (int)(180 * scale));
        if (logWidth > 0 && bottom > logTop)
            _logDrawer.Bounds = new Rectangle(area.Width - margin - logWidth, logTop, logWidth, bottom - logTop);
    }
}
