using HcfScreensaver.Models;
using HcfScreensaver.Services;

namespace HcfScreensaver.Forms;

public sealed class ConfigForm : Form
{
    // ── Palette — phosphor-terminal aesthetic ─────────────────────────────────
    private static readonly Color ColBg      = Color.FromArgb(6,  10,  6);
    private static readonly Color ColPanel   = Color.FromArgb(10, 18, 10);
    private static readonly Color ColGreen   = Color.FromArgb(57, 255, 20);
    private static readonly Color ColGreenHi = Color.FromArgb(140, 255, 90);
    private static readonly Color ColFg      = Color.FromArgb(160, 240, 140);
    private static readonly Color ColFgDim   = Color.FromArgb(60,  110, 55);
    private static readonly Color ColBorder  = Color.FromArgb(25,  55,  25);
    private static readonly Color ColBtnBg   = Color.FromArgb(12,  24,  12);
    private static readonly Color ColBtnHov  = Color.FromArgb(20,  44,  20);

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly SettingsService     _settingsService;
    private readonly ScreensaverSettings _settings;
    private readonly ToolTip             _tip;

    private ComboBox  _animCombo   = null!;
    private TrackBar  _speedTrack  = null!;
    private Label     _speedVal    = null!;
    private TrackBar  _crtTrack    = null!;
    private Label     _crtVal      = null!;
    private Panel     _previewPanel = null!;
    private Label     _previewLabel = null!;

    public ConfigForm(SettingsService settingsService, ScreensaverSettings settings)
    {
        _settingsService = settingsService;
        _settings        = settings;
        _tip             = new ToolTip { AutoPopDelay = 5000, InitialDelay = 400 };

        SuspendLayout();
        BuildUi();
        ResumeLayout(false);
        PerformLayout();
        LoadSettings();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Build UI
    // ─────────────────────────────────────────────────────────────────────────

    private void BuildUi()
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(860, 680);
        BackColor       = ColBg;
        ForeColor       = ColFg;
        Font            = new Font("Courier New", 9f);

        // ── Header ───────────────────────────────────────────────────────────
        var header = new Panel
        {
            Location  = new Point(0, 0),
            Size      = new Size(860, 88),
            BackColor = ColPanel
        };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(ColGreen, 2f);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            // Scanline effect on header
            using var scanBr = new SolidBrush(Color.FromArgb(18, Color.Black));
            for (int y = 0; y < header.Height; y += 3)
                e.Graphics.FillRectangle(scanBr, 0, y, header.Width, 1);
        };
        var titleLbl = new Label
        {
            AutoSize  = true,
            Text      = "HALT AND CATCH FIRE",
            Font      = new Font("Courier New", 22, FontStyle.Bold),
            ForeColor = ColGreen,
            BackColor = Color.Transparent,
            Location  = new Point(18, 12)
        };
        var subLbl = new Label
        {
            AutoSize  = true,
            Text      = "SCREENSAVER SETTINGS  //  CARDIFF GIANT COMPUTING  //  1983",
            Font      = new Font("Courier New", 7.5f),
            ForeColor = ColFgDim,
            BackColor = Color.Transparent,
            Location  = new Point(20, 54)
        };
        header.Controls.AddRange([titleLbl, subLbl]);

        // ── Section: Animation ────────────────────────────────────────────────
        var animBox = MakeSection("ANIMATION MODE", new Point(18, 104), new Size(824, 92));

        var animLbl = SectionLabel("MODE:", new Point(12, 24));
        _animCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location      = new Point(12, 42),
            Width         = 260,
            BackColor     = ColPanel,
            ForeColor     = ColFg,
            FlatStyle     = FlatStyle.Flat,
            Font          = new Font("Courier New", 9f)
        };
        // Add friendly display names
        _animCombo.Items.AddRange([
            "PhosphorDrift  — cascading hex columns",
            "BootSequence   — 80s PC boot-up",
            "CircuitTrace   — PCB board traces",
            "HexStream      — scrolling hex dump",
            "CrtTitle       — CRT phosphor title",
            "BinaryRain     — binary 0/1 rainfall",
            "DataCorrupt    — glitch/corruption",
            "Interference   — TV static noise"
        ]);
        _animCombo.SelectedIndexChanged += (_, _) =>
        {
            _settings.AnimationStyle = (AnimationStyle)_animCombo.SelectedIndex;
            RefreshPreview();
        };
        _tip.SetToolTip(_animCombo, "Select the animation style");

        var speedLbl = SectionLabel("SPEED:", new Point(300, 24));
        _speedTrack = MakeTrackBar(new Point(300, 42), 400, 1, 20);
        _speedVal   = new Label
        {
            AutoSize  = true,
            ForeColor = ColGreen,
            Font      = new Font("Courier New", 9f, FontStyle.Bold),
            Location  = new Point(710, 50)
        };
        _speedTrack.Scroll += (_, _) =>
        {
            _settings.Speed    = _speedTrack.Value;
            _speedVal.Text     = _speedTrack.Value.ToString();
        };
        _tip.SetToolTip(_speedTrack, "Animation speed (1 = slow, 20 = fast)");

        animBox.Controls.AddRange([animLbl, _animCombo, speedLbl, _speedTrack, _speedVal]);

        // ── Section: CRT Effects ──────────────────────────────────────────────
        var crtBox = MakeSection("CRT EFFECTS", new Point(18, 212), new Size(824, 92));

        var crtLbl = SectionLabel("INTENSITY:", new Point(12, 24));
        _crtTrack = MakeTrackBar(new Point(12, 42), 300, 1, 5);
        _crtVal   = new Label
        {
            AutoSize  = true,
            ForeColor = ColGreen,
            Font      = new Font("Courier New", 9f, FontStyle.Bold),
            Location  = new Point(322, 50)
        };
        _crtTrack.Scroll += (_, _) =>
        {
            _settings.CrtIntensity = _crtTrack.Value;
            _crtVal.Text           = _crtTrack.Value.ToString();
        };
        _tip.SetToolTip(_crtTrack, "CRT effect strength — affects scanlines, glow, and flicker");

        var crtNote = new Label
        {
            AutoSize  = true,
            Text      = "Controls scanline density, phosphor glow and flicker intensity.",
            ForeColor = ColFgDim,
            Font      = new Font("Courier New", 8f),
            Location  = new Point(360, 50),
            BackColor = Color.Transparent
        };

        crtBox.Controls.AddRange([crtLbl, _crtTrack, _crtVal, crtNote]);

        // ── Section: Colour ───────────────────────────────────────────────────
        var colBox = MakeSection("PHOSPHOR COLOUR", new Point(18, 320), new Size(824, 76));

        var btnGreen  = MakeColorPresetBtn("GREEN",  Color.FromArgb(57, 255, 20),  new Point(12, 30));
        var btnAmber  = MakeColorPresetBtn("AMBER",  Color.FromArgb(255, 176, 0),  new Point(110, 30));
        var btnWhite  = MakeColorPresetBtn("WHITE",  Color.White,                   new Point(208, 30));
        var btnCyan   = MakeColorPresetBtn("CYAN",   Color.FromArgb(0, 230, 230),   new Point(306, 30));
        var btnCustom = MakeAccentButton("CUSTOM...", new Point(404, 30), new Size(100, 30));
        btnCustom.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = _settings.TextColor, FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _settings.TextColorArgb = dlg.Color.ToArgb();
                RefreshPreview();
            }
        };

        colBox.Controls.AddRange([btnGreen, btnAmber, btnWhite, btnCyan, btnCustom]);

        // ── Preview panel ─────────────────────────────────────────────────────
        var prevBox = MakeSection("PREVIEW", new Point(18, 412), new Size(824, 190));
        _previewPanel = new Panel
        {
            Location  = new Point(12, 24),
            Size      = new Size(800, 155),
            BackColor = _settings.BackgroundColor
        };
        _previewPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(ColBorder, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, _previewPanel.Width - 1, _previewPanel.Height - 1);
            // Scanlines on preview
            using var scanBr = new SolidBrush(Color.FromArgb(20, Color.Black));
            for (int y = 0; y < _previewPanel.Height; y += 3)
                e.Graphics.FillRectangle(scanBr, 0, y, _previewPanel.Width, 1);
        };
        _previewLabel = new Label
        {
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Fill,
            Text      = ScreensaverSettings.DefaultText,
            ForeColor = _settings.TextColor,
            BackColor = Color.Transparent
        };
        _previewPanel.Controls.Add(_previewLabel);
        prevBox.Controls.Add(_previewPanel);

        // ── Button row ────────────────────────────────────────────────────────
        var testBtn   = MakeAccentButton("[ TEST ]",   new Point(18,  626), new Size(150, 34));
        var saveBtn   = MakePrimaryButton("[ SAVE ]",  new Point(614, 626), new Size(120, 34));
        var cancelBtn = MakeDimButton("[ CANCEL ]",    new Point(744, 626), new Size(100, 34));

        testBtn.Click   += (_, _) => RunTest();
        saveBtn.Click   += (_, _) => { _settingsService.Save(_settings); DialogResult = DialogResult.OK; Close(); };
        cancelBtn.Click += (_, _) => Close();

        Controls.AddRange([header, testBtn, saveBtn, cancelBtn]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers — section panel
    // ─────────────────────────────────────────────────────────────────────────

    private Panel MakeSection(string title, Point loc, Size size)
    {
        var panel = new Panel { Location = loc, Size = size, BackColor = ColPanel };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(ColBorder, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            using var titleBr = new SolidBrush(ColFgDim);
            using var titleFont = new Font("Courier New", 7.5f, FontStyle.Bold);
            e.Graphics.DrawString(title, titleFont, titleBr, new Point(10, 5));
        };
        Controls.Add(panel);
        return panel;
    }

    private static Label SectionLabel(string text, Point loc) => new()
    {
        AutoSize  = true,
        Text      = text,
        ForeColor = ColFgDim,
        Font      = new Font("Courier New", 7.5f),
        Location  = loc,
        BackColor = Color.Transparent
    };

    private static TrackBar MakeTrackBar(Point loc, int width, int min, int max) => new()
    {
        Minimum       = min,
        Maximum       = max,
        TickFrequency = 1,
        Value         = min,
        Location      = loc,
        Width         = width,
        BackColor     = ColPanel
    };

    private Button MakeColorPresetBtn(string label, Color color, Point loc)
    {
        var btn = new Button
        {
            Text      = label,
            Location  = loc,
            Size      = new Size(88, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = ColBtnBg,
            ForeColor = color,
            Font      = new Font("Courier New", 8.5f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderColor = color;
        btn.FlatAppearance.BorderSize  = 1;
        btn.FlatAppearance.MouseOverBackColor = ColBtnHov;
        btn.Click += (_, _) =>
        {
            _settings.TextColorArgb = color.ToArgb();
            RefreshPreview();
        };
        return btn;
    }

    private static Button MakeAccentButton(string text, Point loc, Size size)
    {
        var btn = new Button
        {
            Text      = text,
            Location  = loc,
            Size      = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = ColBtnBg,
            ForeColor = ColGreen,
            Font      = new Font("Courier New", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderColor = ColGreen;
        btn.FlatAppearance.BorderSize  = 1;
        btn.FlatAppearance.MouseOverBackColor = ColBtnHov;
        return btn;
    }

    private static Button MakePrimaryButton(string text, Point loc, Size size)
    {
        var btn = new Button
        {
            Text      = text,
            Location  = loc,
            Size      = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = ColGreen,
            ForeColor = Color.Black,
            Font      = new Font("Courier New", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderSize  = 0;
        btn.FlatAppearance.MouseOverBackColor = ColGreenHi;
        return btn;
    }

    private static Button MakeDimButton(string text, Point loc, Size size)
    {
        var btn = new Button
        {
            Text      = text,
            Location  = loc,
            Size      = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = ColBtnBg,
            ForeColor = ColFgDim,
            Font      = new Font("Courier New", 9f),
            Cursor    = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        btn.FlatAppearance.BorderColor = ColBorder;
        btn.FlatAppearance.BorderSize  = 1;
        btn.FlatAppearance.MouseOverBackColor = ColBtnHov;
        return btn;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Settings
    // ─────────────────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        _animCombo.SelectedIndex  = (int)_settings.AnimationStyle;
        _speedTrack.Value         = Math.Clamp(_settings.Speed, 1, 20);
        _speedVal.Text            = _speedTrack.Value.ToString();
        _crtTrack.Value           = Math.Clamp(_settings.CrtIntensity, 1, 5);
        _crtVal.Text              = _crtTrack.Value.ToString();
        Text = "HALT AND CATCH FIRE — Screensaver Settings";
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _previewPanel.BackColor  = _settings.BackgroundColor;
        _previewLabel.ForeColor  = _settings.TextColor;
        _previewLabel.Font       = FitPreviewFont();
        _previewPanel.Invalidate();
    }

    private Font FitPreviewFont()
    {
        using var g = _previewPanel.CreateGraphics();
        float size = 48f;
        while (size > 8f)
        {
            using var f = new Font("Courier New", size, FontStyle.Bold, GraphicsUnit.Point);
            var sz = g.MeasureString(ScreensaverSettings.DefaultText, f);
            if (sz.Width <= _previewPanel.Width - 20 && sz.Height <= _previewPanel.Height - 10)
                return new Font("Courier New", size, FontStyle.Bold, GraphicsUnit.Point);
            size -= 2f;
        }
        return new Font("Courier New", 10f, FontStyle.Bold, GraphicsUnit.Point);
    }

    private void RunTest()
    {
        _settingsService.Save(_settings);
        using var form = new ScreensaverForm(
            new Rectangle(0, 0, 1024, 640), _settings, false, IntPtr.Zero)
        {
            StartPosition = FormStartPosition.CenterScreen,
            TopMost       = true
        };
        form.ShowDialog(this);
    }
}
