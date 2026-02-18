using System.Windows.Forms;

namespace BoltMacro;

public sealed class MainForm : Form
{
    private readonly Controller _controller;
    private readonly Storage _storage;
    private readonly TimelineService _timeline;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;

    private bool _isLoadingUi;

    private List<MacroModel> _macros = new();
    private List<RuleModel> _rules = new();

    private readonly ListBox _lstMacros = new();
    private readonly ListBox _lstRules = new();
    private readonly ListBox _lstTimeline = new();

    private readonly ComboBox _cmbRuleMacro = new();
    private readonly TextBox _txtRuleName = new();

    private readonly NumericUpDown _numHz = new();
    private readonly NumericUpDown _numThreshold = new();
    private readonly NumericUpDown _numDebounce = new();
    private readonly NumericUpDown _numHits = new();
    private readonly NumericUpDown _numCooldown = new();

    private readonly ComboBox _cmbRepeatMode = new();
    private readonly NumericUpDown _numRepeatN = new();

    private readonly CheckBox _chkMultiScale = new();
    private readonly CheckBox _chkRuleOcrAnchors = new();
    private readonly CheckBox _chkOrbFallback = new();
    private readonly CheckBox _chkRuleWindowFilter = new();
    private readonly TextBox _txtWindowProcess = new();
    private readonly TextBox _txtWindowTitle = new();
    private readonly TextBox _txtWindowClass = new();
    private readonly ComboBox _cmbWindowMatchMode = new();
    private readonly CheckBox _chkAllowGlobalFallback = new();
    private readonly NumericUpDown _numLocalSearchPadding = new();
    private readonly NumericUpDown _numGlobalReacquireSeconds = new();
    private readonly CheckBox _chkCollectIncidentOnFire = new();

    private readonly CheckBox _chkSmartEnabled = new();
    private readonly CheckBox _chkUiaWatcher = new();
    private readonly CheckBox _chkOcrWatcher = new();
    private readonly NumericUpDown _numSmartThreshold = new();
    private readonly NumericUpDown _numSmartStability = new();
    private readonly NumericUpDown _numFreshnessMs = new();
    private readonly CheckBox _chkRequireWindowMatch = new();
    private readonly TextBox _txtUiaSelector = new();
    private readonly TextBox _txtOcrPattern = new();
    private readonly CheckBox _chkOcrRegex = new();
    private readonly Label _lblRuleScore = new();
    private readonly Label _lblRuleWhy = new();

    private readonly Label _lblMode = new();
    private readonly Label _lblStatusDot = new();

    private readonly CheckBox _chkOcrModule = new();
    private readonly NumericUpDown _numUiFontSize = new();
    private readonly NumericUpDown _numUiScalePercent = new();
    private readonly NumericUpDown _numDiagnosticsFontSize = new();
    private readonly CheckBox _chkTriggerDebug = new();

    private RecordingOverlayForm? _recOverlay;
    private DiagnosticsViewerForm? _diagForm;

    private readonly HotkeyCaptureTextBox _txtDiagZoomInKey = new();
    private readonly HotkeyCaptureTextBox _txtDiagZoomOutKey = new();
    private readonly HotkeyCaptureTextBox _txtDiagPageUpKey = new();
    private readonly HotkeyCaptureTextBox _txtDiagPageDownKey = new();
    private readonly HotkeyCaptureTextBox _txtDiagCloseKey = new();
    private readonly HotkeyCaptureTextBox _txtDiagOpenKey = new();
    private readonly HotkeyCaptureTextBox _txtPanicKey = new();

    private string? _selectedMacroId;
    private string? _selectedRuleId;

    private const int HOTKEY_PANIC = 0xB001;
    private bool _hotkeysRegistered;
    private bool _panicHotkeyWarningShown;
    private SplitContainer? _rootSplit;
    private bool _splitApplied;

    public MainForm(Controller controller, Storage storage, TimelineService timeline, SettingsStore settingsStore, AppSettings settings)
    {
        _controller = controller;
        _storage = storage;
        _timeline = timeline;
        _settingsStore = settingsStore;
        _settings = settings;

        Text = "✦ Bolt Macro (MVP)";
        Width = 1360;
        Height = 860;
        MinimumSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        WireUi();

        Shown += (_, __) => BeginInvoke(new Action(ApplyRootSplitLayoutSafely));
        SizeChanged += (_, __) =>
        {
            if (_rootSplit is null || _rootSplit.IsDisposed) return;
            if (!_splitApplied || IsRootSplitOutOfRange(_rootSplit.SplitterDistance))
                ApplyRootSplitLayoutSafely();
        };

        FormClosing += (_, __) => _controller.PanicStop();

        RefreshAll();
        ApplySettingsToUi();

        var t = new System.Windows.Forms.Timer { Interval = 350 };
        t.Tick += (_, __) =>
        {
            RefreshTimelineOnly();
            UpdateModeIndicator();
        };
        t.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterGlobalHotkeys();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterGlobalHotkeys();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_PANIC)
        {
            _controller.PanicStop();
            _recOverlay?.Close();
            _recOverlay = null;
            RefreshAll();
            return;
        }

        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_PANIC + 1)
        {
            OpenDiagnostics();
            return;
        }

        base.WndProc(ref m);
    }

    private void BuildUi()
    {
        _rootSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal
        };
        Controls.Add(_rootSplit);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _rootSplit.Panel1.Controls.Add(top);

        var pnlMacros = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        top.Controls.Add(pnlMacros, 0, 0);

        var macrosLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        macrosLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        macrosLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        macrosLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pnlMacros.Controls.Add(macrosLayout);

        var macroTop = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        macrosLayout.Controls.Add(macroTop, 0, 0);

        _lblStatusDot.AutoSize = true;
        _lblStatusDot.Font = new Font(FontFamily.GenericSansSerif, 15, FontStyle.Bold);
        _lblStatusDot.Text = "●";
        _lblStatusDot.Padding = new Padding(0, 1, 0, 0);
        macroTop.Controls.Add(_lblStatusDot);

        _lblMode.AutoSize = true;
        _lblMode.Text = "IDLE";
        _lblMode.Padding = new Padding(0, 8, 0, 0);
        macroTop.Controls.Add(_lblMode);

        var btnRefresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(84, 0) };
        btnRefresh.Click += (_, __) => RefreshAll();
        macroTop.Controls.Add(btnRefresh);

        _lstMacros.Dock = DockStyle.Fill;
        _lstMacros.SelectionMode = SelectionMode.MultiExtended;
        macrosLayout.Controls.Add(_lstMacros, 0, 1);

        var macroButtons = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        macrosLayout.Controls.Add(macroButtons, 0, 2);

        macroButtons.Controls.AddRange(new Control[]
        {
            CreateActionButton("Record", "btnRecord"),
            CreateActionButton("Stop", "btnStop"),
            CreateActionButton("Play", "btnPlay"),
            CreateActionButton("New (Empty)", "btnNewMacro"),
            CreateActionButton("Delete Selected", "btnDeleteMacro"),
            CreateActionButton("Delete All", "btnDeleteAll"),
            CreateActionButton("Diagnostics", "btnDiag")
        });

        var pnlRules = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        top.Controls.Add(pnlRules, 1, 0);

        var rulesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        rulesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rulesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rulesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pnlRules.Controls.Add(rulesLayout);

        rulesLayout.Controls.Add(new Label { Text = "Rules", Dock = DockStyle.Top, Height = 22, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) }, 0, 0);

        _lstRules.Dock = DockStyle.Fill;
        rulesLayout.Controls.Add(_lstRules, 0, 1);

        var ruleButtons = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        rulesLayout.Controls.Add(ruleButtons, 0, 2);
        ruleButtons.Controls.AddRange(new Control[]
        {
            CreateActionButton("New Rule", "btnNewRule"),
            CreateActionButton("Delete", "btnDeleteRule"),
            CreateActionButton("Select ROI", "btnSelectRoi"),
            CreateActionButton("Arm", "btnArm"),
            CreateActionButton("Disarm", "btnDisarm"),
            CreateActionButton("Save", "btnSaveRule"),
            CreateActionButton("✓ Correct", "btnFeedbackGood"),
            CreateActionButton("✕ False", "btnFeedbackBad")
        });

        var pnlEditor = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        top.Controls.Add(pnlEditor, 2, 0);

        var editorLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pnlEditor.Controls.Add(editorLayout);

        editorLayout.Controls.Add(new Label { Text = "Rule Editor", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) }, 0, 0);

        var hint = new Label
        {
            Text = "Hotkey: Ctrl+F12 = Panic Stop\nROI selection: context first, button second",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        editorLayout.Controls.Add(hint, 0, 1);

        var scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        editorLayout.Controls.Add(scrollHost, 0, 2);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 50, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        for (int i = 0; i < grid.RowCount; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scrollHost.Controls.Add(grid);

        AddRow(grid, 0, "Name", _txtRuleName);

        _cmbRuleMacro.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbRuleMacro.IntegralHeight = false;
        _cmbRuleMacro.DropDownHeight = 240;
        _cmbRuleMacro.DropDownWidth = 420;
        AddRow(grid, 1, "Macro", _cmbRuleMacro);

        _numHz.Minimum = 1; _numHz.Maximum = 30; AddRow(grid, 2, "Sampling Hz", _numHz);
        _numThreshold.DecimalPlaces = 3; _numThreshold.Minimum = 0; _numThreshold.Maximum = 1; _numThreshold.Increment = 0.01M; AddRow(grid, 3, "Threshold (0-1)", _numThreshold);
        _numDebounce.Minimum = 0; _numDebounce.Maximum = 5000; _numDebounce.Increment = 50; AddRow(grid, 4, "Debounce ms", _numDebounce);
        _numHits.Minimum = 1; _numHits.Maximum = 10; AddRow(grid, 5, "Hits required", _numHits);
        _numCooldown.Minimum = 0; _numCooldown.Maximum = 30000; _numCooldown.Increment = 100; AddRow(grid, 6, "Cooldown ms", _numCooldown);

        _cmbRepeatMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbRepeatMode.Items.AddRange(new object[] { RepeatMode.Once, RepeatMode.RepeatN, RepeatMode.Infinite });
        AddRow(grid, 7, "Repeat mode", _cmbRepeatMode);

        _numRepeatN.Minimum = 1; _numRepeatN.Maximum = 9999; AddRow(grid, 8, "Repeat N", _numRepeatN);

        _chkMultiScale.Text = "Use multiscale"; AddRow(grid, 9, "Options", _chkMultiScale);
        _chkRuleOcrAnchors.Text = "Use OCR anchors (rule)"; AddRow(grid, 10, "", _chkRuleOcrAnchors);
        _chkOrbFallback.Text = "Use ORB fallback"; AddRow(grid, 11, "", _chkOrbFallback);
        _chkRuleWindowFilter.Text = "Use window filter"; AddRow(grid, 12, "", _chkRuleWindowFilter);

        _cmbWindowMatchMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbWindowMatchMode.Items.AddRange(new object[]
        {
            WindowMatchMode.ProcessOnly,
            WindowMatchMode.ProcessAndTitle,
            WindowMatchMode.ProcessAndClass,
            WindowMatchMode.StrictAll
        });
        AddRow(grid, 13, "Window match mode", _cmbWindowMatchMode);
        AddRow(grid, 14, "Window process", _txtWindowProcess);
        AddRow(grid, 15, "Window title contains", _txtWindowTitle);
        AddRow(grid, 16, "Window class", _txtWindowClass);

        _numLocalSearchPadding.Minimum = 20; _numLocalSearchPadding.Maximum = 1200; _numLocalSearchPadding.Increment = 20;
        AddRow(grid, 17, "Local search padding px", _numLocalSearchPadding);

        _chkAllowGlobalFallback.Text = "Allow global fallback";
        AddRow(grid, 18, "", _chkAllowGlobalFallback);

        _numGlobalReacquireSeconds.Minimum = 5; _numGlobalReacquireSeconds.Maximum = 300; _numGlobalReacquireSeconds.Increment = 5;
        AddRow(grid, 19, "Global reacquire sec", _numGlobalReacquireSeconds);

        _chkCollectIncidentOnFire.Text = "Collect incident on fire";
        AddRow(grid, 20, "", _chkCollectIncidentOnFire);

        _chkOcrModule.Text = "OCR module enabled";
        AddRow(grid, 21, "Global", _chkOcrModule);

        _chkTriggerDebug.Text = "Trigger debug details";
        AddRow(grid, 22, "", _chkTriggerDebug);

        _numUiFontSize.Minimum = 8; _numUiFontSize.Maximum = 24; _numUiFontSize.DecimalPlaces = 1; _numUiFontSize.Increment = 0.5M;
        AddRow(grid, 23, "UI font size", _numUiFontSize);

        _numUiScalePercent.Minimum = 80; _numUiScalePercent.Maximum = 180; _numUiScalePercent.Increment = 5;
        AddRow(grid, 24, "UI font scale %", _numUiScalePercent);

        _numDiagnosticsFontSize.Minimum = 8; _numDiagnosticsFontSize.Maximum = 28; _numDiagnosticsFontSize.DecimalPlaces = 1; _numDiagnosticsFontSize.Increment = 0.5M;
        AddRow(grid, 25, "Diagnostics font", _numDiagnosticsFontSize);

        AddRow(grid, 26, "Diag zoom in hotkey", _txtDiagZoomInKey);
        AddRow(grid, 27, "Diag zoom out hotkey", _txtDiagZoomOutKey);
        AddRow(grid, 28, "Diag page up hotkey", _txtDiagPageUpKey);
        AddRow(grid, 29, "Diag page down hotkey", _txtDiagPageDownKey);
        AddRow(grid, 30, "Diag close hotkey", _txtDiagCloseKey);
        AddRow(grid, 31, "Diag open hotkey", _txtDiagOpenKey);
        AddRow(grid, 32, "Panic stop hotkey", _txtPanicKey);

        _chkSmartEnabled.Text = "Enable scoring policy";
        AddRow(grid, 33, "Smart Rules", _chkSmartEnabled);
        _chkUiaWatcher.Text = "Enable UIA watcher";
        AddRow(grid, 34, "UIA watcher", _chkUiaWatcher);
        _txtUiaSelector.PlaceholderText = "AutomationId+ControlType+Name+parent";
        AddRow(grid, 35, "UIA selector", _txtUiaSelector);
        _chkOcrWatcher.Text = "Enable OCR watcher";
        AddRow(grid, 36, "OCR watcher", _chkOcrWatcher);
        _txtOcrPattern.PlaceholderText = "contains/equals/regex pattern";
        AddRow(grid, 37, "OCR pattern", _txtOcrPattern);
        _chkOcrRegex.Text = "Pattern is regex";
        AddRow(grid, 38, "OCR regex", _chkOcrRegex);
        _numSmartThreshold.DecimalPlaces = 2; _numSmartThreshold.Minimum = 0; _numSmartThreshold.Maximum = 1; _numSmartThreshold.Increment = 0.01M;
        AddRow(grid, 39, "Fire score threshold", _numSmartThreshold);
        _numSmartStability.Minimum = 1; _numSmartStability.Maximum = 8;
        AddRow(grid, 40, "Stability events", _numSmartStability);
        _numFreshnessMs.Minimum = 200; _numFreshnessMs.Maximum = 10000; _numFreshnessMs.Increment = 100;
        AddRow(grid, 41, "Observation freshness ms", _numFreshnessMs);
        _chkRequireWindowMatch.Text = "Require active window match before fire";
        AddRow(grid, 42, "Window safety gate", _chkRequireWindowMatch);
        _lblRuleScore.Text = "Score: n/a";
        AddRow(grid, 43, "Live score", _lblRuleScore);
        _lblRuleWhy.Text = "Why: n/a";
        _lblRuleWhy.MaximumSize = new Size(360, 0);
        AddRow(grid, 44, "Why", _lblRuleWhy);

        var btnApplyUi = new Button { Text = "Apply UI Settings", AutoSize = true, MinimumSize = new Size(140, 0), Margin = new Padding(3, 6, 3, 12) };
        btnApplyUi.Click += (_, __) => SaveSettingsFromUi();
        AddRow(grid, 45, "", btnApplyUi);

        var pnlTimeline = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        _rootSplit.Panel2.Controls.Add(pnlTimeline);

        pnlTimeline.Controls.Add(new Label { Text = "Timeline", Dock = DockStyle.Top, Height = 22, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) });

        _lstTimeline.Dock = DockStyle.Fill;
        pnlTimeline.Controls.Add(_lstTimeline);
        _lstTimeline.BringToFront();
    }

    private static Button CreateActionButton(string text, string name)
    {
        return new Button
        {
            Text = text,
            Name = name,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(76, 0),
            Margin = new Padding(3)
        };
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control input)
    {
        var lbl = new Label { Text = label, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };
        input.Dock = DockStyle.Top;
        input.Margin = new Padding(3, 3, 3, 6);
        grid.Controls.Add(lbl, 0, row);
        grid.Controls.Add(input, 1, row);
    }

    private void WireUi()
    {
        FindButton("btnRecord").Click += (_, __) => StartRecordingFlow();
        FindButton("btnStop").Click += (_, __) => StopRecordingFlow();
        FindButton("btnPlay").Click += async (_, __) => await PlaySelectedMacro();
        FindButton("btnNewMacro").Click += (_, __) => CreateEmptyMacro();
        FindButton("btnDeleteMacro").Click += (_, __) => DeleteSelectedMacros();
        FindButton("btnDeleteAll").Click += (_, __) => DeleteAllMacros();
        FindButton("btnDiag").Click += (_, __) => OpenDiagnostics();

        _lstMacros.SelectedIndexChanged += (_, __) => { if (_lstMacros.SelectedItem is UiComboItem it) _selectedMacroId = it.Id; };
        _lstMacros.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                DeleteSelectedMacros();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.F2)
            {
                RenameSelectedMacro();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        _lstMacros.DoubleClick += (_, __) => RenameSelectedMacro();

        FindButton("btnNewRule").Click += (_, __) => CreateRule();
        FindButton("btnDeleteRule").Click += (_, __) => DeleteSelectedRule();
        FindButton("btnSelectRoi").Click += (_, __) => SelectRoiForRule();
        FindButton("btnArm").Click += (_, __) =>
        {
            if (_selectedRuleId is null) return;
            if (!EnsureRuleMacroAssignedAndSaved(_selectedRuleId, out RuleModel? _)) return;
            _controller.ArmRule(_selectedRuleId);
            RefreshAll();
        };
        FindButton("btnDisarm").Click += (_, __) => { if (_selectedRuleId is not null) { _controller.DisarmRule(_selectedRuleId); RefreshAll(); } };
        FindButton("btnSaveRule").Click += (_, __) => SaveRuleEdits();
        FindButton("btnFeedbackGood").Click += (_, __) => { if (_selectedRuleId is not null) { _controller.ApplyRuleFeedback(_selectedRuleId, true); RefreshAll(); } };
        FindButton("btnFeedbackBad").Click += (_, __) => { if (_selectedRuleId is not null) { _controller.ApplyRuleFeedback(_selectedRuleId, false); RefreshAll(); } };

        _lstRules.SelectedIndexChanged += (_, __) =>
        {
            if (_lstRules.SelectedItem is UiComboItem it)
            {
                _selectedRuleId = it.Id;
                ShowSelectedRule();
            }
        };
        _lstRules.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.F2) return;
            RenameSelectedRule();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        _lstRules.DoubleClick += (_, __) => RenameSelectedRule();

        _chkOcrModule.CheckedChanged += (_, __) =>
        {
            if (_isLoadingUi) return;
            _settings.OcrModuleEnabled = _chkOcrModule.Checked;
            _settingsStore.Save(_settings);
            _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Message = $"OCR module setting updated: {(_settings.OcrModuleEnabled ? "Enabled" : "Disabled")}." });
        };

        _cmbRuleMacro.SelectedIndexChanged += (_, __) =>
        {
            if (_isLoadingUi || _selectedRuleId is null) return;
            SaveRuleEdits(refreshAfterSave: false, addTimelineEntry: false);
        };
    }

    private static bool TryParseHotkey(string? raw, out HotkeyBinding hotkey)
    {
        if (HotkeyBinding.TryParse(raw, out hotkey)) return true;
        if (Enum.TryParse(raw, out Keys legacyKey) && legacyKey != Keys.None)
        {
            hotkey = new HotkeyBinding(legacyKey);
            return true;
        }

        hotkey = default;
        return false;
    }

    private static string NormalizeHotkeyString(string? raw, string fallback)
    {
        return TryParseHotkey(raw, out var hotkey) && !hotkey.IsEmpty
            ? hotkey.ToString()
            : fallback;
    }

    private Button FindButton(string name)
    {
        foreach (var b in Controls.Find(name, true).OfType<Button>()) return b;
        throw new InvalidOperationException($"Button '{name}' not found.");
    }

    private void RefreshAll()
    {
        _macros = _storage.ListMacros();
        _rules = _storage.ListRules();

        _lstMacros.BeginUpdate();
        _lstMacros.Items.Clear();
        foreach (var m in _macros) _lstMacros.Items.Add(new UiComboItem($"{m.Name} ({m.Steps.Count} steps)", m.Id));
        _lstMacros.EndUpdate();

        _lstRules.BeginUpdate();
        _lstRules.Items.Clear();
        foreach (var r in _rules)
        {
            string flag = r.State == RuleState.ArmedMonitoring ? "[ARMED]" : r.State == RuleState.Running ? "[RUN]" : "";
            _lstRules.Items.Add(new UiComboItem($"{r.Name} {flag}", r.Id));
        }
        _lstRules.EndUpdate();

        _cmbRuleMacro.BeginUpdate();
        _cmbRuleMacro.Items.Clear();
        _cmbRuleMacro.Items.Add(new UiComboItem("(none)", null));
        foreach (var m in _macros) _cmbRuleMacro.Items.Add(new UiComboItem(m.Name, m.Id));
        _cmbRuleMacro.EndUpdate();

        RestoreSelection();
        ShowSelectedRule();
        RefreshTimelineOnly();
        UpdateModeIndicator();
    }

    private void RestoreSelection()
    {
        if (_selectedMacroId is not null)
        {
            for (int i = 0; i < _lstMacros.Items.Count; i++)
            {
                if (_lstMacros.Items[i] is UiComboItem it && it.Id == _selectedMacroId)
                {
                    _lstMacros.SelectedIndex = i;
                    break;
                }
            }
        }

        if (_selectedRuleId is not null)
        {
            for (int i = 0; i < _lstRules.Items.Count; i++)
            {
                if (_lstRules.Items[i] is UiComboItem it && it.Id == _selectedRuleId)
                {
                    _lstRules.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void RefreshTimelineOnly()
    {
        var events = _timeline.Snapshot(400);
        _lstTimeline.BeginUpdate();
        _lstTimeline.Items.Clear();
        foreach (var ev in events.Reverse())
        {
            string line = $"{ev.UtcTime.ToLocalTime():HH:mm:ss} [{ev.Source}/{ev.Severity}] {ev.Message}";
            _lstTimeline.Items.Add(line);
        }
        _lstTimeline.EndUpdate();

        _diagForm?.SetLines(_lstTimeline.Items.Cast<object>().Select(x => x?.ToString() ?? string.Empty));
    }

    private void UpdateModeIndicator()
    {
        string mode = _controller.Mode.ToString().ToUpperInvariant();
        _lblMode.Text = mode;
        Text = _controller.Mode == AppMode.Idle ? "✦ Bolt Macro (MVP)" : $"✦ Bolt Macro — {mode}";

        _lblStatusDot.ForeColor = _controller.Mode switch
        {
            AppMode.Recording => Color.Red,
            AppMode.Playing or AppMode.RunningRule => Color.OrangeRed,
            AppMode.Monitoring => Color.DarkGreen,
            _ => Color.Gray
        };
    }

    private void StartRecordingFlow()
    {
        bool ok = _controller.StartRecording();
        if (!ok) { RefreshAll(); return; }

        _recOverlay?.Close();
        _recOverlay = new RecordingOverlayForm();
        _recOverlay.StartAt(new Point(20, 20));
        UpdateModeIndicator();
    }

    private void StopRecordingFlow()
    {
        if (_controller.Mode != AppMode.Recording) return;

        _controller.StopRecording();
        _recOverlay?.Close();
        _recOverlay = null;

        using var dlg = new Form { Text = "Save Recording", Width = 420, Height = 170, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var txt = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Macro name" };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft };
        var btnSave = new Button { Text = "Save", Width = 90 };
        var btnDiscard = new Button { Text = "Discard", Width = 90 };
        var btnCancel = new Button { Text = "Cancel", Width = 90 };

        panel.Controls.AddRange(new Control[] { btnSave, btnDiscard, btnCancel });
        dlg.Controls.Add(panel);
        dlg.Controls.Add(txt);

        btnSave.Click += (_, __) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        btnDiscard.Click += (_, __) => { dlg.DialogResult = DialogResult.No; dlg.Close(); };
        btnCancel.Click += (_, __) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

        var res = dlg.ShowDialog(this);
        if (res == DialogResult.OK) _controller.SaveRecordedMacro(txt.Text);
        else _controller.DiscardRecordedMacro();

        RefreshAll();
    }

    private async Task PlaySelectedMacro()
    {
        if (_selectedMacroId is null) return;
        await _controller.RunMacroManualAsync(_selectedMacroId);
        RefreshAll();
    }

    private void CreateEmptyMacro()
    {
        var m = new MacroModel
        {
            Id = IdUtil.NewId(),
            Name = "Empty Macro",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps = new List<MacroStep> { new DelayStep { DelayMsBefore = 0, Ms = 500 } }
        };
        _storage.UpsertMacro(m);
        _selectedMacroId = m.Id;
        RefreshAll();
    }

    private void DeleteSelectedMacros()
    {
        var ids = _lstMacros.SelectedItems.Cast<object>().OfType<UiComboItem>().Select(x => x.Id)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct().ToList();

        if (ids.Count == 0 && _selectedMacroId is not null) ids.Add(_selectedMacroId);

        foreach (var id in ids) _storage.DeleteMacro(id);

        _selectedMacroId = null;
        RefreshAll();
    }

    private void DeleteAllMacros()
    {
        if (_macros.Count == 0) return;
        var ok = MessageBox.Show(this, "Delete ALL macros?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ok != DialogResult.Yes) return;

        foreach (var m in _macros) _storage.DeleteMacro(m.Id);
        _selectedMacroId = null;
        RefreshAll();
    }

    private void CreateRule()
    {
        if (!TryPromptForName("New Rule", "New rule name", "Rule", out var name)) return;

        var r = new RuleModel
        {
            Id = IdUtil.NewId(),
            Name = name,
            Enabled = false,
            State = RuleState.Disarmed,
            MacroId = null,
            Trigger = new RoiTrigger(),
            Repeat = new RepeatPolicy { Mode = RepeatMode.Infinite, N = 1 }
        };
        _storage.UpsertRule(r);
        _selectedRuleId = r.Id;
        RefreshAll();
    }

    private void RenameSelectedRule()
    {
        if (_selectedRuleId is null) return;

        var selectedIds = _lstRules.SelectedItems.Cast<object>().OfType<UiComboItem>().Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct().ToList();
        if (selectedIds.Count > 1)
        {
            MessageBox.Show(this, "Select exactly one rule to rename.", "Rename Rule", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var rule = _rules.FirstOrDefault(x => x.Id == _selectedRuleId);
        if (rule is null) return;

        if (!TryPromptForName("Rename Rule", "Rename rule", rule.Name, out var newName)) return;
        if (string.Equals(newName, rule.Name, StringComparison.Ordinal)) return;

        rule.Name = newName;
        _storage.UpsertRule(rule);
        _selectedRuleId = rule.Id;
        RefreshAll();
    }

    private void RenameSelectedMacro()
    {
        if (_selectedMacroId is null) return;

        var selectedIds = _lstMacros.SelectedItems.Cast<object>().OfType<UiComboItem>().Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct().ToList();
        if (selectedIds.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one macro to rename.", "Rename Macro", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var macro = _macros.FirstOrDefault(x => x.Id == selectedIds[0]);
        if (macro is null) return;

        if (!TryPromptForName("Rename Macro", "Rename macro", macro.Name, out var newName)) return;
        if (string.Equals(newName, macro.Name, StringComparison.Ordinal)) return;

        macro.Name = newName;
        macro.UpdatedAt = DateTime.UtcNow;
        _storage.UpsertMacro(macro);
        _selectedMacroId = macro.Id;
        RefreshAll();
    }

    private bool TryPromptForName(string title, string label, string defaultValue, out string value)
    {
        value = defaultValue;

        using var dlg = new Form
        {
            Text = title,
            Width = 430,
            Height = 165,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dlg.Controls.Add(layout);

        var lbl = new Label { AutoSize = true, Text = label, Margin = new Padding(0, 0, 0, 4) };
        var txt = new TextBox { Dock = DockStyle.Top, Text = defaultValue, MaxLength = 80 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(btnOk);
        buttons.Controls.Add(btnCancel);

        layout.Controls.Add(lbl, 0, 0);
        layout.Controls.Add(txt, 0, 1);
        layout.Controls.Add(buttons, 0, 2);

        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;

        txt.SelectAll();
        txt.Focus();

        if (dlg.ShowDialog(this) != DialogResult.OK) return false;

        var normalized = (txt.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        value = string.IsNullOrWhiteSpace(normalized) ? defaultValue : normalized;
        return true;
    }

    private void DeleteSelectedRule()
    {
        if (_selectedRuleId is null) return;
        _controller.DisarmRule(_selectedRuleId);
        _storage.DeleteRule(_selectedRuleId);
        _selectedRuleId = null;
        RefreshAll();
    }

    private void SelectRoiForRule()
    {
        if (_selectedRuleId is null) return;
        var rule = _rules.FirstOrDefault(x => x.Id == _selectedRuleId);
        if (rule is null) return;

        MessageBox.Show(this, "Step 1: Select CONTEXT ROI and press Enter.", "ROI Capture", MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var contextSelector = new RoiSelectorForm();
        if (contextSelector.ShowDialog(this) != DialogResult.OK || contextSelector.Result is null) return;

        MessageBox.Show(this, "Step 2: Select BUTTON ROI and press Enter.", "ROI Capture", MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var buttonSelector = new RoiSelectorForm();
        if (buttonSelector.ShowDialog(this) != DialogResult.OK || buttonSelector.Result is null) return;

        var ctx = contextSelector.Result;
        var btn = buttonSelector.Result;
        if (ctx is null || btn is null) return;

        rule.Trigger.ContextRoi = ctx;
        rule.Trigger.ButtonRoiRelative = new RelativeRoiRect
        {
            OffsetX = btn.X - ctx.X,
            OffsetY = btn.Y - ctx.Y,
            W = btn.W,
            H = btn.H,
            SourceContextW = Math.Max(1, ctx.W),
            SourceContextH = Math.Max(1, ctx.H),
            OffsetXFrac = (btn.X - ctx.X) / (double)Math.Max(1, ctx.W),
            OffsetYFrac = (btn.Y - ctx.Y) / (double)Math.Max(1, ctx.H),
            WFrac = btn.W / (double)Math.Max(1, ctx.W),
            HFrac = btn.H / (double)Math.Max(1, ctx.H)
        };
        rule.Trigger.Roi = btn;

        _storage.UpsertRule(rule);
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"ROI updated for rule '{rule.Name}'." });
        RefreshAll();
    }

    private void ShowSelectedRule()
    {
        _isLoadingUi = true;
        try
        {
            var r = _selectedRuleId is null ? null : _rules.FirstOrDefault(x => x.Id == _selectedRuleId);
        if (r is null)
        {
            _txtRuleName.Text = "";
            _cmbRuleMacro.SelectedIndex = 0;
            _numHz.Value = 4;
            _numThreshold.Value = 0.12M;
            _numDebounce.Value = 250;
            _numHits.Value = 2;
            _numCooldown.Value = 1000;
            _cmbRepeatMode.SelectedItem = RepeatMode.Infinite;
            _numRepeatN.Value = 1;
            _chkMultiScale.Checked = true;
            _chkRuleOcrAnchors.Checked = false;
            _chkOrbFallback.Checked = false;
            _chkRuleWindowFilter.Checked = false;
            _txtWindowProcess.Text = string.Empty;
            _txtWindowTitle.Text = string.Empty;
            _txtWindowClass.Text = string.Empty;
            _cmbWindowMatchMode.SelectedItem = WindowMatchMode.ProcessOnly;
            _chkAllowGlobalFallback.Checked = false;
            _numLocalSearchPadding.Value = 180;
            _numGlobalReacquireSeconds.Value = 15;
            _chkCollectIncidentOnFire.Checked = false;
            _numFreshnessMs.Value = 2200;
            _chkRequireWindowMatch.Checked = false;
            return;
        }

        _txtRuleName.Text = r.Name;

        int sel = 0;
        if (r.MacroId is not null)
        {
            for (int i = 0; i < _cmbRuleMacro.Items.Count; i++)
            {
                if (_cmbRuleMacro.Items[i] is UiComboItem it && it.Id == r.MacroId)
                {
                    sel = i;
                    break;
                }
            }
        }
        _cmbRuleMacro.SelectedIndex = sel;

        _numHz.Value = Math.Clamp(r.Trigger.SamplingHz, 1, 30);
        _numThreshold.Value = (decimal)Math.Clamp(r.Trigger.Threshold, 0.0, 1.0);
        _numDebounce.Value = Math.Clamp(r.Trigger.DebounceMs, 0, 5000);
        _numHits.Value = Math.Clamp(r.Trigger.ConsecutiveHitsRequired, 1, 10);
        _numCooldown.Value = Math.Clamp(r.Trigger.CooldownMs, 0, 30000);

        _cmbRepeatMode.SelectedItem = r.Repeat.Mode;
        _numRepeatN.Value = Math.Clamp(r.Repeat.N, 1, 9999);

        _chkMultiScale.Checked = r.Trigger.UseMultiScaleMatching;
        _chkRuleOcrAnchors.Checked = r.Trigger.UseOcrAnchors;
        _chkOrbFallback.Checked = r.Trigger.UseOrbFallback;
        _chkRuleWindowFilter.Checked = r.Trigger.UseWindowFilter;
        _txtWindowProcess.Text = r.Trigger.WindowProcessName ?? string.Empty;
        _txtWindowTitle.Text = r.Trigger.WindowTitleContains ?? string.Empty;
        _txtWindowClass.Text = r.Trigger.WindowClassName ?? string.Empty;
        _cmbWindowMatchMode.SelectedItem = r.Trigger.WindowMatchMode;
        _chkAllowGlobalFallback.Checked = r.Trigger.AllowGlobalSearchFallback;
        _numLocalSearchPadding.Value = Math.Clamp(r.Trigger.LocalSearchPaddingPx, 20, 1200);
        _numGlobalReacquireSeconds.Value = Math.Clamp(r.Trigger.GlobalReacquireEveryNSeconds, 5, 300);
        _chkCollectIncidentOnFire.Checked = r.Trigger.CollectIncidentOnFire;

        _chkSmartEnabled.Checked = r.Smart.EnableSmartRules;
        _chkUiaWatcher.Checked = r.Smart.EnableUiaWatcher;
        _chkOcrWatcher.Checked = r.Smart.EnableOcrWatcher;
        _txtUiaSelector.Text = r.Smart.UiaSelectorRecipe;
        _txtOcrPattern.Text = r.Smart.OcrPattern;
        _chkOcrRegex.Checked = r.Smart.OcrPatternIsRegex;
        _numSmartThreshold.Value = (decimal)Math.Clamp(r.Smart.FireThreshold, 0.0, 1.0);
        _numSmartStability.Value = Math.Clamp(r.Smart.StabilityEvents, 1, 8);
        _numFreshnessMs.Value = Math.Clamp(r.Smart.ObservationFreshnessMs, 200, 10000);
        _chkRequireWindowMatch.Checked = r.Smart.RequireForegroundWindowMatch;
        var score = _controller.GetRuleScore(r.Id);
        _lblRuleScore.Text = $"Score: {score.score:0.000}";
        _lblRuleWhy.Text = $"Why: {score.explanation}";
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void SaveRuleEdits(bool refreshAfterSave = true, bool addTimelineEntry = true)
    {
        if (_selectedRuleId is null) return;
        var r = _storage.ListRules().FirstOrDefault(x => x.Id == _selectedRuleId);
        if (r is null) return;

        r.Name = string.IsNullOrWhiteSpace(_txtRuleName.Text) ? r.Name : _txtRuleName.Text.Trim();
        r.MacroId = (_cmbRuleMacro.SelectedItem as UiComboItem)?.Id;

        r.Trigger.SamplingHz = (int)_numHz.Value;
        r.Trigger.Threshold = (double)_numThreshold.Value;
        r.Trigger.DebounceMs = (int)_numDebounce.Value;
        r.Trigger.ConsecutiveHitsRequired = (int)_numHits.Value;
        r.Trigger.CooldownMs = (int)_numCooldown.Value;

        r.Trigger.UseMultiScaleMatching = _chkMultiScale.Checked;
        r.Trigger.UseOcrAnchors = _chkRuleOcrAnchors.Checked;
        r.Trigger.UseOrbFallback = _chkOrbFallback.Checked;
        r.Trigger.UseWindowFilter = _chkRuleWindowFilter.Checked;
        r.Trigger.WindowProcessName = string.IsNullOrWhiteSpace(_txtWindowProcess.Text) ? null : _txtWindowProcess.Text.Trim();
        r.Trigger.WindowTitleContains = string.IsNullOrWhiteSpace(_txtWindowTitle.Text) ? null : _txtWindowTitle.Text.Trim();
        r.Trigger.WindowClassName = string.IsNullOrWhiteSpace(_txtWindowClass.Text) ? null : _txtWindowClass.Text.Trim();
        r.Trigger.WindowMatchMode = _cmbWindowMatchMode.SelectedItem is WindowMatchMode wm ? wm : WindowMatchMode.ProcessOnly;
        r.Trigger.AllowGlobalSearchFallback = _chkAllowGlobalFallback.Checked;
        r.Trigger.LocalSearchPaddingPx = (int)_numLocalSearchPadding.Value;
        r.Trigger.GlobalReacquireEveryNSeconds = (int)_numGlobalReacquireSeconds.Value;
        r.Trigger.CollectIncidentOnFire = _chkCollectIncidentOnFire.Checked;
        r.Trigger.TriggerDebugDetails = _settings.TriggerDebugDetails;
        EnsureNormalizedButtonRect(r.Trigger);

        r.Smart.EnableSmartRules = _chkSmartEnabled.Checked;
        r.Smart.EnableUiaWatcher = _chkUiaWatcher.Checked;
        r.Smart.EnableOcrWatcher = _chkOcrWatcher.Checked;
        r.Smart.UiaSelectorRecipe = _txtUiaSelector.Text.Trim();
        r.Smart.OcrPattern = _txtOcrPattern.Text.Trim();
        r.Smart.OcrPatternIsRegex = _chkOcrRegex.Checked;
        r.Smart.FireThreshold = (double)_numSmartThreshold.Value;
        r.Smart.StabilityEvents = (int)_numSmartStability.Value;
        r.Smart.ObservationFreshnessMs = (int)_numFreshnessMs.Value;
        r.Smart.RequireForegroundWindowMatch = _chkRequireWindowMatch.Checked;

        r.Repeat.Mode = _cmbRepeatMode.SelectedItem is RepeatMode rm ? rm : RepeatMode.Infinite;
        r.Repeat.N = (int)_numRepeatN.Value;

        _storage.UpsertRule(r);
        if (addTimelineEntry)
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"Rule saved: {r.Name}." });
        if (refreshAfterSave)
            RefreshAll();
    }

    private static void EnsureNormalizedButtonRect(RoiTrigger trigger)
    {
        if (trigger.ContextRoi is null || trigger.ButtonRoiRelative is null) return;

        var rel = trigger.ButtonRoiRelative;
        rel.SourceContextW = rel.SourceContextW > 0 ? rel.SourceContextW : Math.Max(1, trigger.ContextRoi.W);
        rel.SourceContextH = rel.SourceContextH > 0 ? rel.SourceContextH : Math.Max(1, trigger.ContextRoi.H);

        if (!rel.OffsetXFrac.HasValue || !rel.OffsetYFrac.HasValue || !rel.WFrac.HasValue || !rel.HFrac.HasValue)
        {
            rel.OffsetXFrac = rel.OffsetX / (double)Math.Max(1, rel.SourceContextW);
            rel.OffsetYFrac = rel.OffsetY / (double)Math.Max(1, rel.SourceContextH);
            rel.WFrac = rel.W / (double)Math.Max(1, rel.SourceContextW);
            rel.HFrac = rel.H / (double)Math.Max(1, rel.SourceContextH);
        }
    }

    private bool EnsureRuleMacroAssignedAndSaved(string ruleId, out RuleModel? savedRule)
    {
        SaveRuleEdits(refreshAfterSave: false, addTimelineEntry: false);

        savedRule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
        if (savedRule is null) return false;

        if (string.IsNullOrWhiteSpace(savedRule.MacroId))
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Arm denied for '{savedRule.Name}': macro is not assigned." });
            return false;
        }

        _storage.UpsertRule(savedRule);
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"Rule '{savedRule.Name}' auto-saved before arm (MacroId={savedRule.MacroId})." });
        return true;
    }

    private void OpenDiagnostics()
    {
        _diagForm ??= new DiagnosticsViewerForm();
        if (_diagForm.WindowState == FormWindowState.Minimized) _diagForm.WindowState = FormWindowState.Normal;
        _diagForm.SetFontSize(_settings.DiagnosticsFontSize);
        var zoomIn = TryParseHotkey(_settings.DiagnosticsZoomInKey, out var zin) ? zin : new HotkeyBinding(Keys.Add);
        var zoomOut = TryParseHotkey(_settings.DiagnosticsZoomOutKey, out var zout) ? zout : new HotkeyBinding(Keys.Subtract);
        var pageUp = TryParseHotkey(_settings.DiagnosticsPageUpKey, out var pup) ? pup : new HotkeyBinding(Keys.PageUp);
        var pageDown = TryParseHotkey(_settings.DiagnosticsPageDownKey, out var pdown) ? pdown : new HotkeyBinding(Keys.PageDown);
        var closeHotkey = TryParseHotkey(_settings.DiagnosticsCloseKey, out var closeK) ? closeK : new HotkeyBinding(Keys.Escape);
        _diagForm.SetKeyBindings(zoomIn, zoomOut, pageUp, pageDown, closeHotkey);
        _diagForm.Show();
        _diagForm.BringToFront();
        RefreshTimelineOnly();
    }

    private void ApplySettingsToUi()
    {
        _isLoadingUi = true;
        try
        {
            _numUiFontSize.Value = (decimal)Math.Clamp(_settings.UiFontSize, 8f, 24f);
            _numUiScalePercent.Value = Math.Clamp(_settings.UiScalePercent, 80, 180);
            _numDiagnosticsFontSize.Value = (decimal)Math.Clamp(_settings.DiagnosticsFontSize, 8f, 28f);
            _chkOcrModule.Checked = _settings.OcrModuleEnabled;
            _chkTriggerDebug.Checked = _settings.TriggerDebugDetails;
            _txtDiagZoomInKey.Value = TryParseHotkey(_settings.DiagnosticsZoomInKey, out var di) ? di : new HotkeyBinding(Keys.Add);
            _txtDiagZoomOutKey.Value = TryParseHotkey(_settings.DiagnosticsZoomOutKey, out var dout) ? dout : new HotkeyBinding(Keys.Subtract);
            _txtDiagPageUpKey.Value = TryParseHotkey(_settings.DiagnosticsPageUpKey, out var dpu) ? dpu : new HotkeyBinding(Keys.PageUp);
            _txtDiagPageDownKey.Value = TryParseHotkey(_settings.DiagnosticsPageDownKey, out var dpd) ? dpd : new HotkeyBinding(Keys.PageDown);
            _txtDiagCloseKey.Value = TryParseHotkey(_settings.DiagnosticsCloseKey, out var dc) ? dc : new HotkeyBinding(Keys.Escape);
            _txtDiagOpenKey.Value = TryParseHotkey(_settings.DiagnosticsOpenKey, out var dop) ? dop : new HotkeyBinding(Keys.F9);
            _txtPanicKey.Value = TryParseHotkey(_settings.PanicStopKey, out var p) ? p : new HotkeyBinding(Keys.F12, Ctrl: true);

            float effectiveSize = _settings.UiFontSize * (_settings.UiScalePercent / 100f);
            var uiFont = new Font(Font.FontFamily, effectiveSize, FontStyle.Regular);
            SuspendLayout();
            ApplyFontsToControlTree(this, uiFont);
            _lstTimeline.Font = new Font(FontFamily.GenericMonospace, _settings.DiagnosticsFontSize);
            _diagForm?.SetFontSize(_settings.DiagnosticsFontSize);
            ResumeLayout(true);
            PerformLayout();
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void SaveSettingsFromUi()
    {
        if (_isLoadingUi) return;

        _settings.UiFontSize = (float)_numUiFontSize.Value;
        _settings.UiScalePercent = (int)_numUiScalePercent.Value;
        _settings.DiagnosticsFontSize = (float)_numDiagnosticsFontSize.Value;
        _settings.OcrModuleEnabled = _chkOcrModule.Checked;
        _settings.TriggerDebugDetails = _chkTriggerDebug.Checked;

        if (_txtPanicKey.Value.IsEmpty || _txtDiagOpenKey.Value.IsEmpty)
        {
            MessageBox.Show(this, "Panic stop and Diagnostics open hotkeys cannot be empty.", "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.DiagnosticsZoomInKey = _txtDiagZoomInKey.Value.ToString();
        _settings.DiagnosticsZoomOutKey = _txtDiagZoomOutKey.Value.ToString();
        _settings.DiagnosticsPageUpKey = _txtDiagPageUpKey.Value.ToString();
        _settings.DiagnosticsPageDownKey = _txtDiagPageDownKey.Value.ToString();
        _settings.DiagnosticsCloseKey = _txtDiagCloseKey.Value.ToString();
        _settings.DiagnosticsOpenKey = _txtDiagOpenKey.Value.ToString();
        _settings.PanicStopKey = _txtPanicKey.Value.ToString();

        if (!RegisterGlobalHotkeys())
        {
            ApplySettingsToUi();
            return;
        }

        _settingsStore.Save(_settings);
        RegisterGlobalHotkeys();
        ApplySettingsToUi();

        _timeline.Add(new TimelineEvent
        {
            Source = TimelineSource.System,
            Message = $"UI settings saved (font={_settings.UiFontSize:0.0}, fontScale={_settings.UiScalePercent}%, diag={_settings.DiagnosticsFontSize:0.0}, OCR={_settings.OcrModuleEnabled})."
        });
    }

    private bool RegisterGlobalHotkeys()
    {
        if (!IsHandleCreated) return false;

        var previousPanic = _settings.PanicStopKey;
        var previousOpen = _settings.DiagnosticsOpenKey;

        _settings.PanicStopKey = NormalizeHotkeyString(_settings.PanicStopKey, "Ctrl+F12");
        _settings.DiagnosticsOpenKey = NormalizeHotkeyString(_settings.DiagnosticsOpenKey, nameof(Keys.F9));

        if (!TryParseHotkey(_settings.PanicStopKey, out var panicHotkey) || panicHotkey.IsEmpty)
            panicHotkey = new HotkeyBinding(Keys.F12, Ctrl: true);

        if (!TryParseHotkey(_settings.DiagnosticsOpenKey, out var openHotkey) || openHotkey.IsEmpty)
            openHotkey = new HotkeyBinding(Keys.F9);

        if (panicHotkey.IsReservedCombo())
        {
            MessageBox.Show(this, "Ctrl+Shift+Esc is reserved by Windows. Choose another panic hotkey.", "Reserved Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settings.PanicStopKey = previousPanic;
            return false;
        }

        UnregisterGlobalHotkeys();

        bool panicOk = NativeMethods.RegisterHotKey(Handle, HOTKEY_PANIC, panicHotkey.RegisterModifiers, (uint)panicHotkey.Key);
        if (!panicOk)
        {
            _settings.PanicStopKey = previousPanic;
            _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Severity = TimelineSeverity.Error, Message = $"Failed to register panic hotkey {panicHotkey}. Reverted to last working hotkey." });
            if (!_panicHotkeyWarningShown)
            {
                _panicHotkeyWarningShown = true;
                MessageBox.Show(this, $"Cannot register panic hotkey {panicHotkey}. Reverting to previous hotkey.", "Hotkey Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return false;
        }

        bool openOk = NativeMethods.RegisterHotKey(Handle, HOTKEY_PANIC + 1, openHotkey.RegisterModifiers, (uint)openHotkey.Key);
        if (!openOk)
        {
            NativeMethods.UnregisterHotKey(Handle, HOTKEY_PANIC);
            _settings.DiagnosticsOpenKey = previousOpen;
            _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Severity = TimelineSeverity.Warn, Message = $"Failed to register diagnostics open hotkey {openHotkey}. Reverted to last working hotkey." });
            MessageBox.Show(this, $"Cannot register diagnostics hotkey {openHotkey}. Reverting to previous hotkey.", "Hotkey Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _hotkeysRegistered = true;
        return true;
    }

    private void UnregisterGlobalHotkeys()
    {
        if (!IsHandleCreated || !_hotkeysRegistered) return;
        try { NativeMethods.UnregisterHotKey(Handle, HOTKEY_PANIC); } catch { }
        try { NativeMethods.UnregisterHotKey(Handle, HOTKEY_PANIC + 1); } catch { }
        _hotkeysRegistered = false;
    }


    private void ApplyRootSplitLayoutSafely()
    {
        if (_rootSplit is null || _rootSplit.IsDisposed) return;

        int size = _rootSplit.Orientation == Orientation.Horizontal
            ? _rootSplit.ClientSize.Height
            : _rootSplit.ClientSize.Width;

        if (size <= 0) return;

        int splitter = _rootSplit.SplitterWidth;
        int available = size - splitter;
        if (available <= 0) return;

        const int targetP1 = 430;
        const int targetP2 = 180;

        int p1 = targetP1;
        int p2 = targetP2;

        int sum = p1 + p2;
        if (sum > available)
        {
            double ratio = available / (double)sum;
            p1 = Math.Max(0, (int)Math.Floor(p1 * ratio));
            p2 = Math.Max(0, available - p1);
        }

        int min = p1;
        int max = size - p2 - splitter;
        if (max < min) return;

        int desired = (int)(size * 0.75);
        if (desired < min) desired = min;
        if (desired > max) desired = max;

        if (_rootSplit.SplitterDistance != desired)
            _rootSplit.SplitterDistance = desired;

        if (_rootSplit.Panel1MinSize != p1) _rootSplit.Panel1MinSize = p1;
        if (_rootSplit.Panel2MinSize != p2) _rootSplit.Panel2MinSize = p2;

        size = _rootSplit.Orientation == Orientation.Horizontal
            ? _rootSplit.ClientSize.Height
            : _rootSplit.ClientSize.Width;

        if (size > 0)
        {
            min = _rootSplit.Panel1MinSize;
            max = size - _rootSplit.Panel2MinSize - _rootSplit.SplitterWidth;
            if (max >= min)
            {
                int distance = _rootSplit.SplitterDistance;
                if (distance < min) _rootSplit.SplitterDistance = min;
                else if (distance > max) _rootSplit.SplitterDistance = max;
            }
        }

        _splitApplied = true;
    }

    private bool IsRootSplitOutOfRange(int splitterDistance)
    {
        if (_rootSplit is null || _rootSplit.IsDisposed) return false;

        int size = _rootSplit.Orientation == Orientation.Horizontal
            ? _rootSplit.ClientSize.Height
            : _rootSplit.ClientSize.Width;

        if (size <= 0) return false;

        int splitter = _rootSplit.SplitterWidth;
        int available = size - splitter;
        if (available <= 0) return false;

        int p1 = 430;
        int p2 = 180;
        int sum = p1 + p2;
        if (sum > available)
        {
            double ratio = available / (double)sum;
            p1 = Math.Max(0, (int)Math.Floor(p1 * ratio));
            p2 = Math.Max(0, available - p1);
        }

        int min = p1;
        int max = size - p2 - splitter;
        if (max < min) return false;

        return splitterDistance < min || splitterDistance > max;
    }

    private static void ApplyFontsToControlTree(Control root, Font font)
    {
        root.Font = font;
        foreach (Control child in root.Controls)
            ApplyFontsToControlTree(child, font);
    }
}
