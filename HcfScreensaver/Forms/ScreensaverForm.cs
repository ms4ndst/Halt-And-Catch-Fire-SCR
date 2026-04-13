using System.Runtime.InteropServices;
using HcfScreensaver.Models;

namespace HcfScreensaver.Forms;

// ── Particle structs ──────────────────────────────────────────────────────────

internal struct DriftColumn
{
    public float Y;      // head Y position (pixels from top)
    public float Speed;  // pixels per frame at speed-multiplier 1
    public int   Trail;  // trail length in characters
}

internal struct TraceHead
{
    public PointF From;
    public PointF Current;
    public PointF Target;
    public bool   Active;
}

internal struct CompletedSeg
{
    public PointF A, B;
}

// ── Main form ────────────────────────────────────────────────────────────────

public sealed class ScreensaverForm : Form
{
    // ── Win32 interop ─────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWnd, IntPtr hParent);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const int GwlStyle  = -16;
    private const int WsChild   = 0x40000000;
    private const int WsVisible = 0x10000000;

    // ── Constants ─────────────────────────────────────────────────────────────
    private const string Title      = ScreensaverSettings.DefaultText;
    private const string DriftChars = "0123456789ABCDEF!@#$%^&*.:/\\|<>";
    private const int    GridSize   = 24;   // circuit trace grid in pixels

    // ── Phosphor palettes ─────────────────────────────────────────────────────
    private static readonly Color Amber    = Color.FromArgb(255, 176, 0);
    private static readonly Color AmberDim = Color.FromArgb(70, 44, 0);

    // ── Core state ────────────────────────────────────────────────────────────
    private readonly ScreensaverSettings      _settings;
    private readonly bool                     _previewMode;
    private readonly IntPtr                   _previewHandle;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Random                   _rng = new();
    private int                               _frameCount;
    private Point                             _lastMousePosition;

    // ── Mono font (shared by drift/hex/boot) ──────────────────────────────────
    private Font? _monoFont;
    private int   _monoCharW = 8;
    private int   _monoCharH = 16;

    // ── PhosphorDrift / BinaryRain ────────────────────────────────────────────
    private DriftColumn[] _driftCols = [];

    // ── BootSequence ──────────────────────────────────────────────────────────
    private int   _bootPhase;          // 0=typing  1=title-reveal  2=hold  3=fade-out
    private int   _bootLineIdx;
    private int   _bootCharIdx;
    private float _bootAccum;
    private float _bootCursorAccum;
    private bool  _bootCursor;
    private float _bootTitleAlpha;
    private float _bootHoldAccum;
    private float _bootFadeAlpha = 1f;
    private readonly List<string> _bootDone = [];

    private static readonly string[] BootLines =
    [
        "IBM Personal Computer BIOS Version 1.00",
        "(C) Copyright 1981, 1982, 1983 IBM Corp.",
        "",
        "Memory Test: 640 KB OK",
        "",
        "CARDIFF GIANT COMPUTING — OS v0.9 BETA",
        "  Loading device drivers ........... OK",
        "  Initializing memory manager ...... OK",
        "  Mounting GIANT filesystem ......... OK",
        "  Starting I/O subsystem ............ OK",
        "",
        "C:\\> DIR /W",
        "  HALT    .EXE    4096  08-15-83",
        "  CATCH   .EXE    8192  08-15-83",
        "  FIRE    .EXE   16384  08-15-83",
        "  GIANT   .SYS    2048  08-15-83",
        "",
        "C:\\> HALT.EXE /CATCH /FIRE",
        "",
        "WARNING: Undocumented instruction executed",
        "PROCESSOR HALT DETECTED — core temp CRITICAL",
        "",
    ];

    // ── CircuitTrace ──────────────────────────────────────────────────────────
    private readonly List<CompletedSeg> _circSegs  = [];
    private readonly List<PointF>       _circPads  = [];
    private TraceHead[]                 _circHeads = new TraceHead[5];
    private float                       _circFade  = 1f;
    private bool                        _circFading;
    private float                       _circSpawnTimer;

    // ── HexStream ─────────────────────────────────────────────────────────────
    private readonly List<string> _hexLines = [];
    private float                 _hexScrollY;
    private int                   _hexAddr;
    private Font?                 _hexFont;

    // Embed the title in a hex-readable payload
    private static readonly byte[] HexPayload =
        System.Text.Encoding.ASCII.GetBytes("HALT AND CATCH FIRE v1.0 - CARDIFF GIANT COMPUTING - 1983 ");

    // ── CrtTitle ──────────────────────────────────────────────────────────────
    private float _crtFlicker  = 1f;
    private float _crtFlickerTimer;
    private float _crtTheta;

    // ── DataCorrupt ───────────────────────────────────────────────────────────
    private char[]  _corruptDisplay = Title.ToCharArray();
    private float   _corruptAccum;
    private float   _tearY;
    private float   _tearAmt;
    private float   _tearTimer;
    private static readonly char[] GlitchChars = "!@#$%^&*[]{}|<>?/\\~`01░▒▓".ToCharArray();

    // ── Interference ──────────────────────────────────────────────────────────
    private float _interfereBandY;
    private float _interfereTitleAlpha;

    // ─────────────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────────────

    public ScreensaverForm(Rectangle bounds, ScreensaverSettings settings, bool previewMode, IntPtr previewHandle)
    {
        _settings      = settings;
        _previewMode   = previewMode;
        _previewHandle = previewHandle;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        BackColor = Color.Black;
        ForeColor = settings.TextColor;
        KeyPreview = true;

        if (_previewMode)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds        = new Rectangle(0, 0, 320, 200);
            ShowInTaskbar = false;
            TopMost       = false;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            Bounds          = bounds;
            ShowInTaskbar   = false;
            TopMost         = true;
            Cursor.Hide();
        }

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => { UpdateAnimation(); Invalidate(); };

        KeyDown    += (_, _) => CloseIfAllowed();
        MouseDown  += (_, _) => CloseIfAllowed();
        MouseMove  += OnMouseMoved;
        Deactivate += (_, _) => CloseIfAllowed();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_previewMode && _previewHandle != IntPtr.Zero)
        {
            SetParent(Handle, _previewHandle);
            SetWindowLong(Handle, GwlStyle, (IntPtr)(WsChild | WsVisible));
            GetClientRect(_previewHandle, out var rect);
            Bounds = new Rectangle(0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        InitState();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _monoFont?.Dispose();
            _hexFont?.Dispose();
            if (!_previewMode) Cursor.Show();
        }
        base.Dispose(disposing);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Input handling
    // ─────────────────────────────────────────────────────────────────────────

    private void CloseIfAllowed()
    {
        if (!_previewMode) Close();
    }

    private void OnMouseMoved(object? sender, MouseEventArgs e)
    {
        if (_previewMode) return;
        if (_lastMousePosition == Point.Empty) { _lastMousePosition = e.Location; return; }
        if (Math.Abs(e.X - _lastMousePosition.X) > 5 || Math.Abs(e.Y - _lastMousePosition.Y) > 5)
            Close();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Initialisation
    // ─────────────────────────────────────────────────────────────────────────

    private void InitState()
    {
        _monoFont?.Dispose();
        _monoFont = new Font("Courier New", _previewMode ? 9f : 13f, FontStyle.Regular, GraphicsUnit.Point);

        using var g = CreateGraphics();
        var sz = g.MeasureString("M", _monoFont, PointF.Empty, StringFormat.GenericTypographic);
        _monoCharW = Math.Max(1, (int)Math.Ceiling(sz.Width));
        _monoCharH = Math.Max(1, (int)Math.Ceiling(sz.Height));

        // PhosphorDrift / BinaryRain columns
        int numCols = ClientSize.Width / _monoCharW + 1;
        _driftCols = new DriftColumn[numCols];
        for (int c = 0; c < numCols; c++)
        {
            _driftCols[c] = new DriftColumn
            {
                Y     = _rng.Next(-ClientSize.Height, 0),
                Speed = 1.5f + (float)_rng.NextDouble() * 3.5f,
                Trail = 10 + _rng.Next(22)
            };
        }

        // BootSequence
        _bootPhase     = 0;
        _bootLineIdx   = 0;
        _bootCharIdx   = 0;
        _bootAccum     = 0f;
        _bootCursor    = true;
        _bootTitleAlpha = 0f;
        _bootHoldAccum  = 0f;
        _bootFadeAlpha  = 1f;
        _bootDone.Clear();

        // CircuitTrace
        _circSegs.Clear();
        _circPads.Clear();
        _circFade    = 1f;
        _circFading  = false;
        _circSpawnTimer = 0f;
        for (int i = 0; i < _circHeads.Length; i++)
            _circHeads[i].Active = false;

        // HexStream
        _hexFont?.Dispose();
        _hexFont = new Font("Courier New", _previewMode ? 8f : 11f, FontStyle.Regular, GraphicsUnit.Point);
        _hexLines.Clear();
        _hexScrollY = 0f;
        _hexAddr    = 0;
        for (int i = 0; i < 40; i++) _hexLines.Add(NextHexRow());

        // CrtTitle
        _crtFlicker     = 1f;
        _crtFlickerTimer = 0f;
        _crtTheta        = 0f;

        // DataCorrupt
        _corruptDisplay = Title.ToCharArray();
        _corruptAccum   = 0f;
        _tearTimer      = 0f;
        _tearY          = 0f;
        _tearAmt        = 0f;

        // Interference
        _interfereBandY      = 0f;
        _interfereTitleAlpha = 0f;

        _lastMousePosition = Point.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Paint dispatch
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(_settings.BackgroundColor);
        g.SmoothingMode      = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint  = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

        switch (_settings.AnimationStyle)
        {
            case AnimationStyle.PhosphorDrift:  DrawPhosphorDrift(g, false); break;
            case AnimationStyle.BootSequence:   DrawBootSequence(g);         break;
            case AnimationStyle.CircuitTrace:   DrawCircuitTrace(g);         break;
            case AnimationStyle.HexStream:      DrawHexStream(g);            break;
            case AnimationStyle.CrtTitle:       DrawCrtTitle(g);             break;
            case AnimationStyle.BinaryRain:     DrawPhosphorDrift(g, true);  break;
            case AnimationStyle.DataCorrupt:    DrawDataCorrupt(g);          break;
            case AnimationStyle.Interference:   DrawInterference(g);         break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Update dispatch
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateAnimation()
    {
        _frameCount++;
        var speed = Math.Max(1f, _settings.Speed) * 0.75f;

        switch (_settings.AnimationStyle)
        {
            case AnimationStyle.PhosphorDrift:
            case AnimationStyle.BinaryRain:    UpdateDrift(speed);        break;
            case AnimationStyle.BootSequence:  UpdateBoot(speed);         break;
            case AnimationStyle.CircuitTrace:  UpdateCircuit(speed);      break;
            case AnimationStyle.HexStream:     UpdateHex(speed);          break;
            case AnimationStyle.CrtTitle:      UpdateCrtTitle(speed);     break;
            case AnimationStyle.DataCorrupt:   UpdateDataCorrupt(speed);  break;
            case AnimationStyle.Interference:  UpdateInterference(speed); break;
        }
    }

    // =========================================================================
    // PhosphorDrift / BinaryRain
    // =========================================================================

    private void UpdateDrift(float speed)
    {
        for (int c = 0; c < _driftCols.Length; c++)
        {
            _driftCols[c].Y += _driftCols[c].Speed * speed;
            int bottom = ClientSize.Height + _driftCols[c].Trail * _monoCharH;
            if (_driftCols[c].Y > bottom)
            {
                _driftCols[c].Y     = -_rng.Next(10, 120);
                _driftCols[c].Speed  = 1.5f + (float)_rng.NextDouble() * 3.5f;
                _driftCols[c].Trail  = 10 + _rng.Next(22);
            }
        }
    }

    private void DrawPhosphorDrift(Graphics g, bool binary)
    {
        if (_monoFont == null) return;
        var baseCol = binary ? Amber : _settings.TextColor;

        for (int c = 0; c < _driftCols.Length; c++)
        {
            int x        = c * _monoCharW;
            float headY  = _driftCols[c].Y;
            int trail    = _driftCols[c].Trail;

            for (int t = 0; t < trail; t++)
            {
                float charY = headY - t * _monoCharH;
                if (charY < -_monoCharH || charY > ClientSize.Height) continue;

                Color col;
                if (t == 0)
                {
                    col = Color.White;
                }
                else
                {
                    int alpha = (int)(255 * (1f - (float)t / trail) * 0.9f);
                    if (alpha < 5) continue;
                    col = Color.FromArgb(alpha, baseCol);
                }

                char ch = GetDriftChar(c, t, binary);
                using var brush = new SolidBrush(col);
                g.DrawString(ch.ToString(), _monoFont, brush, (float)x, charY);
            }
        }
    }

    private char GetDriftChar(int col, int trailPos, bool binary)
    {
        // Change slowly (every 4 frames), vary by position
        var seed = unchecked(col * 137 + trailPos * 31 + _frameCount / 4 * 17);
        if (binary) return (seed & 1) == 0 ? '0' : '1';
        int idx = ((seed % DriftChars.Length) + DriftChars.Length) % DriftChars.Length;
        return DriftChars[idx];
    }

    // =========================================================================
    // BootSequence
    // =========================================================================

    private void UpdateBoot(float speed)
    {
        const float CharsPerSec = 60f;
        const float CursorHz    = 2f;
        float dt = speed / 60f;

        _bootCursorAccum += dt;
        if (_bootCursorAccum > 1f / CursorHz)
        {
            _bootCursor      = !_bootCursor;
            _bootCursorAccum = 0f;
        }

        switch (_bootPhase)
        {
            case 0: // Typing
                _bootAccum += CharsPerSec * dt * speed * 0.8f;
                while (_bootAccum >= 1f && _bootLineIdx < BootLines.Length)
                {
                    _bootAccum -= 1f;
                    var line = BootLines[_bootLineIdx];
                    if (_bootCharIdx >= line.Length)
                    {
                        if (_bootDone.Count < BootLines.Length)
                            _bootDone.Add(line);
                        _bootLineIdx++;
                        _bootCharIdx = 0;
                        if (_bootLineIdx >= BootLines.Length)
                        {
                            _bootPhase = 1;
                            _bootTitleAlpha = 0f;
                        }
                    }
                    else
                    {
                        _bootCharIdx++;
                    }
                }
                break;

            case 1: // Fade in title
                _bootTitleAlpha = Math.Min(1f, _bootTitleAlpha + dt * 1.2f);
                if (_bootTitleAlpha >= 1f) { _bootPhase = 2; _bootHoldAccum = 0f; }
                break;

            case 2: // Hold
                _bootHoldAccum += dt;
                if (_bootHoldAccum > 3.5f) { _bootPhase = 3; _bootFadeAlpha = 1f; }
                break;

            case 3: // Fade out and restart
                _bootFadeAlpha -= dt * 0.6f;
                if (_bootFadeAlpha <= 0f)
                {
                    _bootPhase     = 0;
                    _bootLineIdx   = 0;
                    _bootCharIdx   = 0;
                    _bootAccum     = 0f;
                    _bootTitleAlpha = 0f;
                    _bootFadeAlpha  = 1f;
                    _bootDone.Clear();
                }
                break;
        }
    }

    private void DrawBootSequence(Graphics g)
    {
        if (_monoFont == null) return;

        float alpha = _bootPhase == 3 ? Math.Max(0f, _bootFadeAlpha) : 1f;
        var textCol = Color.FromArgb((int)(220 * alpha), _settings.TextColor);
        using var brush     = new SolidBrush(textCol);
        using var dimBrush  = new SolidBrush(Color.FromArgb((int)(120 * alpha), _settings.TextColor));

        int lineH = _monoCharH + 2;
        int y     = 20;

        // Completed lines
        foreach (var line in _bootDone)
        {
            g.DrawString(line, _monoFont, brush, 20, y);
            y += lineH;
        }

        // Current line (partially typed)
        if (_bootPhase == 0 && _bootLineIdx < BootLines.Length)
        {
            var partial = BootLines[_bootLineIdx][.._bootCharIdx];
            var cursor  = _bootCursor ? "_" : " ";
            g.DrawString(partial + cursor, _monoFont, brush, 20, y);
        }

        // Title reveal
        if (_bootPhase >= 1)
        {
            float ta = _bootPhase == 3 ? _bootFadeAlpha : _bootTitleAlpha;
            using var font = _settings.CreateTitleFont(_previewMode ? 20f : 52f);
            DrawPhosphorTitle(g, font, (int)(255 * ta));
        }
    }

    // =========================================================================
    // CircuitTrace
    // =========================================================================

    private void UpdateCircuit(float speed)
    {
        // Fade and reset when saturated
        if (_circSegs.Count > 200 || _circFading)
        {
            _circFading = true;
            _circFade  -= 0.008f * speed;
            if (_circFade <= 0f)
            {
                _circSegs.Clear();
                _circPads.Clear();
                for (int i = 0; i < _circHeads.Length; i++) _circHeads[i].Active = false;
                _circFade   = 1f;
                _circFading = false;
                _circSpawnTimer = 0f;
            }
            return;
        }

        // Spawn new heads
        _circSpawnTimer -= speed;
        if (_circSpawnTimer <= 0f)
        {
            SpawnTraceHead();
            _circSpawnTimer = 20f + _rng.Next(40);
        }

        // Advance active heads
        for (int i = 0; i < _circHeads.Length; i++)
        {
            ref var h = ref _circHeads[i];
            if (!h.Active) continue;

            float step = 3f * speed;
            float dx   = h.Target.X - h.From.X;
            float dy   = h.Target.Y - h.From.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float traveled = MathF.Sqrt(
                (h.Current.X - h.From.X) * (h.Current.X - h.From.X) +
                (h.Current.Y - h.From.Y) * (h.Current.Y - h.From.Y));

            if (traveled + step >= dist)
            {
                h.Current = h.Target;
                _circSegs.Add(new CompletedSeg { A = h.From, B = h.Target });
                _circPads.Add(h.Target);

                // Decide next action
                if (_circSegs.Count > 180 || _rng.Next(4) == 0)
                {
                    h.Active = false;
                }
                else
                {
                    // Turn or continue in same direction
                    bool turn = _rng.Next(2) == 0;
                    int  steps = (3 + _rng.Next(8)) * GridSize;
                    var  newFrom = h.Target;
                    PointF newTarget;
                    if (turn)
                    {
                        // 90-degree turn
                        bool wasHoriz = Math.Abs(dx) > Math.Abs(dy);
                        newTarget = wasHoriz
                            ? new PointF(newFrom.X, Math.Clamp(newFrom.Y + (_rng.Next(2) == 0 ? steps : -steps), GridSize, ClientSize.Height - GridSize))
                            : new PointF(Math.Clamp(newFrom.X + (_rng.Next(2) == 0 ? steps : -steps), GridSize, ClientSize.Width - GridSize), newFrom.Y);
                    }
                    else
                    {
                        // Continue same axis
                        float norm = dist > 0 ? 1f / dist : 1f;
                        newTarget = new PointF(
                            Math.Clamp(newFrom.X + dx * norm * steps, GridSize, ClientSize.Width - GridSize),
                            Math.Clamp(newFrom.Y + dy * norm * steps, GridSize, ClientSize.Height - GridSize));
                    }
                    h = new TraceHead { From = newFrom, Current = newFrom, Target = newTarget, Active = true };
                }
            }
            else
            {
                float norm = dist > 0 ? 1f / dist : 1f;
                h.Current = new PointF(h.Current.X + dx * norm * step, h.Current.Y + dy * norm * step);
            }
        }
    }

    private void SpawnTraceHead()
    {
        for (int i = 0; i < _circHeads.Length; i++)
        {
            if (_circHeads[i].Active) continue;

            // Start from a random grid-aligned point on screen, or from existing pad
            PointF from;
            if (_circPads.Count > 0 && _rng.Next(3) != 0)
                from = _circPads[_rng.Next(_circPads.Count)];
            else
                from = new PointF(
                    (_rng.Next(ClientSize.Width  / GridSize)) * GridSize + GridSize,
                    (_rng.Next(ClientSize.Height / GridSize)) * GridSize + GridSize);

            int    steps  = (3 + _rng.Next(10)) * GridSize;
            bool   horiz  = _rng.Next(2) == 0;
            int    sign   = _rng.Next(2) == 0 ? 1 : -1;
            PointF target = horiz
                ? new PointF(Math.Clamp(from.X + sign * steps, GridSize, ClientSize.Width - GridSize), from.Y)
                : new PointF(from.X, Math.Clamp(from.Y + sign * steps, GridSize, ClientSize.Height - GridSize));

            _circHeads[i] = new TraceHead { From = from, Current = from, Target = target, Active = true };
            _circPads.Add(from);
            return;
        }
    }

    private void DrawCircuitTrace(Graphics g)
    {
        var col = _settings.TextColor;
        int fadeA = (int)(200 * _circFade);
        if (fadeA < 1) return;

        using var pen    = new Pen(Color.FromArgb(fadeA, col), 1.5f);
        using var padBr  = new SolidBrush(Color.FromArgb(fadeA, col));
        using var glowPen = new Pen(Color.FromArgb(fadeA / 5, col), 4f);

        foreach (var seg in _circSegs)
        {
            g.DrawLine(glowPen, seg.A, seg.B);
            g.DrawLine(pen, seg.A, seg.B);
        }

        foreach (ref var h in _circHeads.AsSpan())
        {
            if (!h.Active) continue;
            g.DrawLine(glowPen, h.From, h.Current);
            g.DrawLine(pen, h.From, h.Current);
        }

        foreach (var pad in _circPads)
            g.FillEllipse(padBr, pad.X - 3, pad.Y - 3, 6, 6);
    }

    // =========================================================================
    // HexStream
    // =========================================================================

    private string NextHexRow()
    {
        var sb = new System.Text.StringBuilder(80);
        sb.Append($"{_hexAddr:X4}: ");

        var bytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            int payloadIdx = (_hexAddr + i) % HexPayload.Length;
            bytes[i] = (byte)(HexPayload[payloadIdx] ^ (_rng.Next(8) == 0 ? _rng.Next(256) : 0));
            sb.Append($"{bytes[i]:X2} ");
            if (i == 7) sb.Append(' ');
        }

        sb.Append(" |");
        foreach (var b in bytes)
            sb.Append(b is >= 32 and < 127 ? (char)b : '.');
        sb.Append('|');

        _hexAddr += 16;
        return sb.ToString();
    }

    private void UpdateHex(float speed)
    {
        if (_hexFont == null) return;
        _hexScrollY -= 0.8f * speed;

        // When a row scrolls off the top, remove it and append a new one
        if (_hexScrollY <= -_monoCharH)
        {
            _hexScrollY += _monoCharH;
            if (_hexLines.Count > 0) _hexLines.RemoveAt(0);
            _hexLines.Add(NextHexRow());
        }
    }

    private void DrawHexStream(Graphics g)
    {
        if (_hexFont == null) return;

        // Measure line height using mono font
        int lineH = _monoCharH + 1;
        var phosphor = _settings.TextColor;

        for (int i = 0; i < _hexLines.Count; i++)
        {
            float y = _hexScrollY + i * lineH;
            if (y < -lineH || y > ClientSize.Height) continue;

            // Highlight address in brighter color, data in standard phosphor
            var line = _hexLines[i];
            int addrAlpha = 180 + _rng.Next(30);
            using var addrBrush = new SolidBrush(Color.FromArgb(addrAlpha, phosphor));
            using var dataBrush = new SolidBrush(Color.FromArgb(150, phosphor));
            using var asciiBrush = new SolidBrush(Color.FromArgb(100, phosphor));

            if (line.Length > 6)
            {
                g.DrawString(line[..5], _hexFont, addrBrush, 10f, y);
                int asciiIdx = line.IndexOf('|');
                if (asciiIdx > 0 && asciiIdx < line.Length - 1)
                {
                    g.DrawString(line[5..asciiIdx], _hexFont, dataBrush, 10f + 5 * _monoCharW, y);
                    g.DrawString(line[asciiIdx..], _hexFont, asciiBrush,
                                 10f + asciiIdx * _monoCharW, y);
                }
                else
                {
                    g.DrawString(line[5..], _hexFont, dataBrush, 10f + 5 * _monoCharW, y);
                }
            }
        }
    }

    // =========================================================================
    // CrtTitle
    // =========================================================================

    private void UpdateCrtTitle(float speed)
    {
        _crtTheta += 0.02f * speed;
        _crtFlickerTimer -= speed;
        if (_crtFlickerTimer <= 0f)
        {
            _crtFlicker      = 0.88f + (float)_rng.NextDouble() * 0.12f;
            _crtFlickerTimer = _rng.Next(10, 50);
        }
    }

    private void DrawCrtTitle(Graphics g)
    {
        float intensity = _settings.CrtIntensity / 5f;
        var phosphor    = _settings.TextColor;

        // Scanlines
        int scanStep = Math.Max(2, 5 - _settings.CrtIntensity);
        using var scanBr = new SolidBrush(Color.FromArgb((int)(60 * intensity), Color.Black));
        for (int y = 0; y < ClientSize.Height; y += scanStep)
            g.FillRectangle(scanBr, 0, y, ClientSize.Width, 1);

        // Screen-edge vignette
        using var vigBr = new SolidBrush(Color.FromArgb(40, Color.Black));
        int vw = ClientSize.Width, vh = ClientSize.Height;
        g.FillRectangle(vigBr, 0,      0,      40,  vh);
        g.FillRectangle(vigBr, vw - 40,0,      40,  vh);
        g.FillRectangle(vigBr, 0,      0,      vw,  30);
        g.FillRectangle(vigBr, 0,      vh - 30, vw, 30);

        // Title with phosphor glow
        using var font = _settings.CreateTitleFont(_previewMode ? 20f : 64f);
        using var measG = CreateGraphics();
        var sz    = measG.MeasureString(Title, font);
        float tx  = (ClientSize.Width  - sz.Width)  / 2f;
        float ty  = (ClientSize.Height - sz.Height) / 2f
                    + (float)Math.Sin(_crtTheta * 0.4) * (ClientSize.Height * 0.02f);

        int flickAlpha = (int)(255 * _crtFlicker);
        // Outer glow passes
        int glowAlpha = (int)(40 * intensity);
        if (glowAlpha > 0)
        {
            using var glowBr = new SolidBrush(Color.FromArgb(glowAlpha, phosphor));
            for (int d = 4; d >= 1; d--)
            {
                g.DrawString(Title, font, glowBr, tx - d, ty);
                g.DrawString(Title, font, glowBr, tx + d, ty);
                g.DrawString(Title, font, glowBr, tx, ty - d);
                g.DrawString(Title, font, glowBr, tx, ty + d);
            }
        }
        using var mainBr = new SolidBrush(Color.FromArgb(flickAlpha, phosphor));
        g.DrawString(Title, font, mainBr, tx, ty);

        // Occasional horizontal noise bar
        if (_rng.Next(40) == 0)
        {
            int barY = _rng.Next(ClientSize.Height);
            using var noiseBr = new SolidBrush(Color.FromArgb(30, phosphor));
            g.FillRectangle(noiseBr, 0, barY, ClientSize.Width, _rng.Next(2, 8));
        }
    }

    // =========================================================================
    // DataCorrupt
    // =========================================================================

    private void UpdateDataCorrupt(float speed)
    {
        float dt = speed / 60f;

        // Corrupt random characters
        _corruptAccum += dt * 8f * speed;
        while (_corruptAccum >= 1f)
        {
            _corruptAccum -= 1f;
            int idx = _rng.Next(_corruptDisplay.Length);
            // 30% chance to glitch, 70% chance to restore
            _corruptDisplay[idx] = _rng.Next(10) < 3
                ? GlitchChars[_rng.Next(GlitchChars.Length)]
                : Title[idx];
        }

        // Horizontal tear effect
        _tearTimer -= dt;
        if (_tearTimer <= 0f)
        {
            _tearY   = _rng.Next(ClientSize.Height);
            _tearAmt = (_rng.Next(2) == 0 ? 1 : -1) * _rng.Next(10, 50);
            _tearTimer = 0.05f + (float)_rng.NextDouble() * 0.15f;
        }
    }

    private void DrawDataCorrupt(Graphics g)
    {
        using var font = _settings.CreateTitleFont(_previewMode ? 18f : 52f);
        using var measG = CreateGraphics();
        var sz   = measG.MeasureString(Title, font);
        float tx = (ClientSize.Width  - sz.Width)  / 2f;
        float ty = (ClientSize.Height - sz.Height) / 2f;

        var phosphor = _settings.TextColor;

        // RGB channel split
        using var rBr = new SolidBrush(Color.FromArgb(140, 255, 40, 40));
        using var gBr = new SolidBrush(Color.FromArgb(140, phosphor));
        using var bBr = new SolidBrush(Color.FromArgb(140, 40, 40, 255));
        g.DrawString(new string(_corruptDisplay), font, rBr, tx + 4, ty);
        g.DrawString(new string(_corruptDisplay), font, bBr, tx - 4, ty);
        g.DrawString(new string(_corruptDisplay), font, gBr, tx, ty);

        // Horizontal tear
        if (Math.Abs(_tearAmt) > 1)
        {
            int tearH = 8 + _rng.Next(20);
            var clip  = new Region(new Rectangle(0, (int)_tearY, ClientSize.Width, tearH));
            g.Clip = clip;
            g.DrawString(new string(_corruptDisplay), font, gBr, tx + _tearAmt, ty);
            g.ResetClip();
        }

        // Noise blocks
        for (int i = 0; i < 6; i++)
        {
            int nx = _rng.Next(ClientSize.Width);
            int ny = _rng.Next(ClientSize.Height);
            int nw = _rng.Next(4, 30);
            int nh = _rng.Next(2, 12);
            using var noiseBr = new SolidBrush(
                Color.FromArgb(_rng.Next(30, 120), _rng.Next(256), _rng.Next(256), _rng.Next(256)));
            g.FillRectangle(noiseBr, nx, ny, nw, nh);
        }
    }

    // =========================================================================
    // Interference
    // =========================================================================

    private void UpdateInterference(float speed)
    {
        _interfereBandY += 2.5f * speed;
        if (_interfereBandY > ClientSize.Height) _interfereBandY -= ClientSize.Height;

        // Pulse title alpha in and out
        _crtTheta += 0.015f * speed;
        _interfereTitleAlpha = 0.35f + 0.3f * (float)Math.Sin(_crtTheta);
    }

    private void DrawInterference(Graphics g)
    {
        // Static noise
        for (int i = 0; i < (_previewMode ? 80 : 500); i++)
        {
            int grey = _rng.Next(10, 80);
            using var nb = new SolidBrush(Color.FromArgb(grey, grey, grey));
            g.FillRectangle(nb, _rng.Next(ClientSize.Width), _rng.Next(ClientSize.Height), 2, 2);
        }

        // Moving interference bands
        using var bandBr = new SolidBrush(Color.FromArgb(35, 200, 200, 200));
        float by = _interfereBandY % ClientSize.Height;
        g.FillRectangle(bandBr, 0, by,       ClientSize.Width, 12);
        g.FillRectangle(bandBr, 0, (by + 180) % ClientSize.Height, ClientSize.Width, 6);
        g.FillRectangle(bandBr, 0, (by + 420) % ClientSize.Height, ClientSize.Width, 4);

        // Title visible through the static
        using var font = _settings.CreateTitleFont(_previewMode ? 18f : 52f);
        using var measG = CreateGraphics();
        var sz   = measG.MeasureString(Title, font);
        float tx = (ClientSize.Width  - sz.Width)  / 2f;
        float ty = (ClientSize.Height - sz.Height) / 2f;

        int ta = (int)(255 * _interfereTitleAlpha);
        using var titleBr = new SolidBrush(Color.FromArgb(ta, _settings.TextColor));
        g.DrawString(Title, font, titleBr, tx, ty);
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    private void DrawPhosphorTitle(Graphics g, Font font, int alpha)
    {
        if (alpha < 1) return;
        using var measG = CreateGraphics();
        var sz   = measG.MeasureString(Title, font);
        float tx = (ClientSize.Width  - sz.Width)  / 2f;
        float ty = (ClientSize.Height - sz.Height) / 2f;

        var phosphor = _settings.TextColor;
        // Glow
        using var glowBr = new SolidBrush(Color.FromArgb(alpha / 8, phosphor));
        for (int d = 3; d >= 1; d--)
        {
            g.DrawString(Title, font, glowBr, tx - d, ty);
            g.DrawString(Title, font, glowBr, tx + d, ty);
            g.DrawString(Title, font, glowBr, tx, ty - d);
            g.DrawString(Title, font, glowBr, tx, ty + d);
        }
        using var mainBr = new SolidBrush(Color.FromArgb(alpha, phosphor));
        g.DrawString(Title, font, mainBr, tx, ty);
    }
}
