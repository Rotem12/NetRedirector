using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Text.Json;
using NetRedirector.Core;

namespace NetRedirector;

internal sealed class MainForm : Form
{
    private readonly EndpointEditor _sourceEditor;
    private readonly EndpointEditor _targetEditor;
    private readonly CheckedListBox _interfaceList;
    private readonly Label _statusLabel;
    private readonly Label _metricsLabel;
    private readonly TextBox _logTextBox;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private readonly ToolStripMenuItem _trayStartItem;
    private readonly ToolStripMenuItem _trayStopItem;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _metricsTimer;
    private IReadOnlyList<NetworkInterfaceInfo> _interfaces = [];
    private RedirectorService? _redirector;
    private bool _exitRequested;
    private bool _exitInProgress;
    private bool _loadedInitialWindow;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetRedirector",
        "settings.json");

    public MainForm()
    {
        Text = "NetRedirector";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 560);
        MinimumSize = Size;

        _sourceEditor = new EndpointEditor("Source", isSource: true);
        _targetEditor = new EndpointEditor("Target", isSource: false);

        var endpointLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 178,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 6, 8, 0)
        };
        endpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        endpointLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        endpointLayout.Controls.Add(_sourceEditor, 0, 0);
        endpointLayout.Controls.Add(_targetEditor, 1, 0);

        var interfaceGroup = new GroupBox
        {
            Text = "Multicast / UDP interfaces",
            Dock = DockStyle.Top,
            Height = 126,
            Padding = new Padding(8, 20, 8, 8),
            Margin = new Padding(8, 6, 8, 0)
        };

        _interfaceList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
            HorizontalScrollbar = true
        };
        interfaceGroup.Controls.Add(_interfaceList);

        var interfaceButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 80,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(6, 0, 0, 0)
        };
        var refreshInterfacesButton = CreateButton("Refresh", 68, 28);
        refreshInterfacesButton.Click += (_, _) => RefreshInterfaces();
        interfaceButtons.Controls.Add(refreshInterfacesButton);
        interfaceGroup.Controls.Add(interfaceButtons);

        var interfaceHint = new Label
        {
            Text = "Blank = all active non-loopback IPv4 adapters. Select more than one to join/send on each.",
            Dock = DockStyle.Bottom,
            Height = 18,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText
        };
        interfaceGroup.Controls.Add(interfaceHint);

        var actionPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(10, 8, 10, 4)
        };

        _startButton = CreateButton("Start redirect", 116, 32);
        _startButton.Click += async (_, _) => await StartRedirectAsync();
        _stopButton = CreateButton("Stop", 80, 32);
        _stopButton.Enabled = false;
        _stopButton.Click += async (_, _) => await StopRedirectAsync();
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_stopButton);
        _stopButton.Left = _startButton.Right + 8;

        _statusLabel = new Label
        {
            AutoSize = false,
            Text = "Ready — running in the tray",
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(_stopButton.Right + 18, 8),
            Height = 32,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            ForeColor = Color.DarkGreen
        };
        _statusLabel.Width = actionPanel.ClientSize.Width - _statusLabel.Left - 10;
        actionPanel.Controls.Add(_statusLabel);
        actionPanel.Resize += (_, _) => _statusLabel.Width = actionPanel.ClientSize.Width - _statusLabel.Left - 10;

        var logGroup = new GroupBox
        {
            Text = "Activity",
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 20, 8, 8),
            Margin = new Padding(8, 0, 8, 8)
        };
        _logTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
            TabStop = false
        };
        logGroup.Controls.Add(_logTextBox);

        _metricsLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 18,
            Text = "0 B  •  0 packets",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleRight
        };
        logGroup.Controls.Add(_metricsLabel);

        Controls.Add(logGroup);
        Controls.Add(actionPanel);
        Controls.Add(interfaceGroup);
        Controls.Add(endpointLayout);

        var trayMenu = new ContextMenuStrip();
        var trayOpenItem = new ToolStripMenuItem("Open settings");
        trayOpenItem.Click += (_, _) => ShowSettings();
        _trayStartItem = new ToolStripMenuItem("Start redirect");
        _trayStartItem.Click += async (_, _) => await StartRedirectAsync();
        _trayStopItem = new ToolStripMenuItem("Stop redirect") { Enabled = false };
        _trayStopItem.Click += async (_, _) => await StopRedirectAsync();
        var trayExitItem = new ToolStripMenuItem("Exit");
        trayExitItem.Click += (_, _) => RequestExit();
        trayMenu.Items.AddRange([trayOpenItem, new ToolStripSeparator(), _trayStartItem, _trayStopItem, new ToolStripSeparator(), trayExitItem]);

        _trayIcon = new NotifyIcon
        {
            Text = "NetRedirector",
            Icon = SystemIcons.Application,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        _metricsTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _metricsTimer.Tick += (_, _) => UpdateMetrics();
        _metricsTimer.Start();

        Shown += (_, _) =>
        {
            if (!_loadedInitialWindow)
            {
                _loadedInitialWindow = true;
                Hide();
            }
        };
        FormClosing += MainForm_FormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && !_exitRequested)
            {
                Hide();
                ShowInTaskbar = false;
            }
        };
        FormClosed += (_, _) =>
        {
            _metricsTimer.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        };

        LoadSettings();
        RefreshInterfaces();
        _sourceEditor.RefreshSerialPorts();
        _targetEditor.RefreshSerialPorts();
        AppendLog("Ready. The app is minimized to the notification area.", false);
    }

    private async Task StartRedirectAsync()
    {
        if (_redirector is not null)
        {
            return;
        }

        try
        {
            var config = new RedirectConfig
            {
                Source = _sourceEditor.GetConfig(),
                Target = _targetEditor.GetConfig(),
                MulticastInterfaces = GetCheckedInterfaces().ToList()
            };
            SaveSettings(config);

            var redirector = new RedirectorService(config, (message, isError) => AppendLog(message, isError));
            redirector.StatusChanged += Redirector_StatusChanged;
            await redirector.StartAsync();
            _redirector = redirector;
            SetRunningState(true);
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message, true);
            SetStatus(exception.Message, true);
        }
    }

    private async Task StopRedirectAsync()
    {
        var redirector = _redirector;
        if (redirector is null)
        {
            return;
        }

        _redirector = null;
        SetRunningState(false);
        try
        {
            await redirector.StopAsync();
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message, true);
        }
        finally
        {
            await redirector.DisposeAsync();
        }
    }

    private void Redirector_StatusChanged(object? sender, RedirectStatusEventArgs e)
    {
        SetStatus(e.Message, e.IsError);
    }

    private void SetRunningState(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _trayStartItem.Enabled = !running;
        _trayStopItem.Enabled = running;
        _sourceEditor.Enabled = !running;
        _targetEditor.Enabled = !running;
        _interfaceList.Enabled = !running;
        SetStatus(running ? "Running" : "Stopped", false);
    }

    private void SetStatus(string text, bool isError)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text, isError));
            return;
        }

        _statusLabel.Text = text;
        _statusLabel.ForeColor = isError ? Color.Firebrick : Color.DarkGreen;
    }

    private void AppendLog(string message, bool isError)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message, isError));
            return;
        }

        var prefix = isError ? "ERR" : " OK";
        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {prefix}  {message}{Environment.NewLine}");
        if (_logTextBox.Lines.Length > 250)
        {
            _logTextBox.Lines = _logTextBox.Lines.Skip(_logTextBox.Lines.Length - 200).ToArray();
        }
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private void UpdateMetrics()
    {
        var metrics = _redirector?.Metrics ?? default;
        _metricsLabel.Text = $"{FormatBytes(metrics.BytesForwarded)}  •  {metrics.PacketsForwarded:N0} packets";
    }

    private void RefreshInterfaces()
    {
        var checkedAddresses = GetCheckedInterfaces().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _interfaces = NetworkInterfaceCatalog.GetIpv4Interfaces();
        _interfaceList.Items.Clear();
        foreach (var networkInterface in _interfaces)
        {
            var index = _interfaceList.Items.Add(networkInterface);
            _interfaceList.SetItemChecked(index, checkedAddresses.Contains(networkInterface.Address));
        }

        ApplyPendingInterfaces();
    }

    private IEnumerable<string> GetCheckedInterfaces()
    {
        return _interfaceList.CheckedItems
            .OfType<NetworkInterfaceInfo>()
            .Select(networkInterface => networkInterface.Address);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var config = JsonSerializer.Deserialize<RedirectConfig>(File.ReadAllText(SettingsPath));
            if (config?.Source is not null && config.Target is not null)
            {
                _sourceEditor.SetConfig(config.Source);
                _targetEditor.SetConfig(config.Target);
                _pendingInterfaces = config.MulticastInterfaces ?? [];
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Could not load saved settings: {exception.Message}", true);
        }
    }

    private List<string> _pendingInterfaces = [];

    private void SaveSettings(RedirectConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            AppendLog($"Could not save settings: {exception.Message}", true);
        }
    }

    private void ApplyPendingInterfaces()
    {
        var pending = _pendingInterfaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _interfaceList.Items.Count; index++)
        {
            if (_interfaceList.Items[index] is NetworkInterfaceInfo item)
            {
                _interfaceList.SetItemChecked(index, pending.Contains(item.Address));
            }
        }
        _pendingInterfaces.Clear();
    }

    private void RequestExit()
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitRequested = true;
        if (_redirector is null)
        {
            Close();
            return;
        }

        _exitInProgress = true;
        _ = ExitAfterStopAsync();
    }

    private async Task ExitAfterStopAsync()
    {
        await StopRedirectAsync();
        Close();
    }

    private void ShowSettings()
    {
        if (IsDisposed)
        {
            return;
        }

        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
    }

    private static Button CreateButton(string text, int width, int height) => new()
    {
        Text = text,
        Width = width,
        Height = height,
        FlatStyle = FlatStyle.System,
        Margin = new Padding(0, 0, 6, 0)
    };

    private static string FormatBytes(long value)
    {
        if (value < 1024)
        {
            return $"{value:N0} B";
        }
        if (value < 1024 * 1024)
        {
            return $"{value / 1024d:N1} KiB";
        }
        if (value < 1024 * 1024 * 1024)
        {
            return $"{value / 1024d / 1024d:N1} MiB";
        }
        return $"{value / 1024d / 1024d / 1024d:N2} GiB";
    }

    private sealed class EndpointEditor : GroupBox
    {
        private readonly ComboBox _protocolCombo;
        private readonly TextBox _hostTextBox;
        private readonly NumericUpDown _portNumeric;
        private readonly ComboBox _serialCombo;
        private readonly ComboBox _baudCombo;
        private readonly Label _hostLabel;
        private readonly Label _portLabel;
        private readonly Label _serialLabel;
        private readonly Label _baudLabel;
        private readonly Label _hintLabel;
        private readonly bool _isSource;

        public EndpointEditor(string title, bool isSource)
        {
            _isSource = isSource;
            Text = title;
            Dock = DockStyle.Fill;
            Padding = new Padding(8, 20, 8, 6);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Padding = new Padding(0),
                AutoSize = false
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 5; row++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _protocolCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _protocolCombo.Items.AddRange(["UDP", "TCP client", "TCP server", "Serial"]);
            _protocolCombo.SelectedIndexChanged += (_, _) => UpdateVisibility();
            layout.Controls.Add(new Label { Text = "Protocol", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            layout.Controls.Add(_protocolCombo, 1, 0);

            _hostLabel = new Label { Text = isSource ? "Listen / group" : "Destination", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
            _hostTextBox = new TextBox { Dock = DockStyle.Fill };
            layout.Controls.Add(_hostLabel, 0, 1);
            layout.Controls.Add(_hostTextBox, 1, 1);

            _portLabel = new Label { Text = "Port", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
            _portNumeric = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = isSource ? 5000 : 5001, Dock = DockStyle.Left, Width = 90, ThousandsSeparator = false };
            layout.Controls.Add(_portLabel, 0, 2);
            layout.Controls.Add(_portNumeric, 1, 2);

            _serialLabel = new Label { Text = "Serial port", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
            _serialCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            layout.Controls.Add(_serialLabel, 0, 3);
            layout.Controls.Add(_serialCombo, 1, 3);

            _baudLabel = new Label { Text = "Baud", AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft };
            _baudCombo = new ComboBox { Dock = DockStyle.Left, Width = 105, DropDownStyle = ComboBoxStyle.DropDownList };
            _baudCombo.Items.AddRange(["9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600"]);
            _baudCombo.SelectedItem = "115200";
            layout.Controls.Add(_baudLabel, 0, 4);
            layout.Controls.Add(_baudCombo, 1, 4);

            _hintLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
                Padding = new Padding(0, 3, 0, 0)
            };
            layout.Controls.Add(_hintLabel, 0, 5);
            layout.SetColumnSpan(_hintLabel, 2);
            Controls.Add(layout);

            _protocolCombo.SelectedIndex = 0;
            _hostTextBox.Text = isSource ? "0.0.0.0" : "127.0.0.1";
        }

        public EndpointConfig GetConfig()
        {
            var protocol = (EndpointProtocol)_protocolCombo.SelectedIndex;
            _ = int.TryParse(_baudCombo.Text, out var baudRate);
            return new EndpointConfig
            {
                Protocol = protocol,
                Host = _hostTextBox.Text.Trim(),
                Port = (int)_portNumeric.Value,
                SerialPort = _serialCombo.Text.Trim(),
                BaudRate = baudRate > 0 ? baudRate : 115200
            };
        }

        public void SetConfig(EndpointConfig config)
        {
            _protocolCombo.SelectedIndex = (int)config.Protocol;
            _hostTextBox.Text = config.Host;
            _portNumeric.Value = Math.Clamp(config.Port, 1, 65535);
            _serialCombo.Text = config.SerialPort;
            var baud = config.BaudRate > 0 ? config.BaudRate.ToString() : "115200";
            if (!_baudCombo.Items.Contains(baud))
            {
                _baudCombo.Items.Add(baud);
            }
            _baudCombo.SelectedItem = baud;
            UpdateVisibility();
        }

        public void RefreshSerialPorts()
        {
            var current = _serialCombo.Text;
            _serialCombo.Items.Clear();
            _serialCombo.Items.AddRange(SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray());
            _serialCombo.Text = current;
        }

        private void UpdateVisibility()
        {
            var protocol = (EndpointProtocol)_protocolCombo.SelectedIndex;
            var serial = protocol == EndpointProtocol.Serial;
            var server = protocol == EndpointProtocol.TcpServer;
            _hostLabel.Visible = _hostTextBox.Visible = !serial;
            _portLabel.Visible = _portNumeric.Visible = !serial;
            _serialLabel.Visible = _serialCombo.Visible = serial;
            _baudLabel.Visible = _baudCombo.Visible = serial;

            _hintLabel.Text = protocol switch
            {
                EndpointProtocol.Udp when _isSource => "UDP preserves each datagram; multicast joins checked adapters.",
                EndpointProtocol.Udp => "Multicast destinations send once per checked adapter.",
                EndpointProtocol.TcpClient when _isSource => "Connects and reconnects to the remote TCP endpoint.",
                EndpointProtocol.TcpClient => "Connects to the TCP endpoint when the first bytes arrive.",
                EndpointProtocol.TcpServer when _isSource => "Listens locally; all connected clients feed the redirect.",
                EndpointProtocol.TcpServer => "Listens locally; the latest connected client receives bytes.",
                _ => "Raw 8-N-1 serial bytes."
            };

            if (server)
            {
                _hostLabel.Text = "Bind address";
            }
            else if (protocol == EndpointProtocol.Serial)
            {
                _hostLabel.Text = _isSource ? "Listen / group" : "Destination";
            }
            else
            {
                _hostLabel.Text = _isSource ? "Listen / group" : "Destination";
            }
        }
    }
}
