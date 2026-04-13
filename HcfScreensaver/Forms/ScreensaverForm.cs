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

internal struct Asteroid
{
    public float    X, Y, VX, VY;
    public float    Angle, AngVel;
    public float    Scale;
    public PointF[] Poly;    // normalized polygon verts (unit-radius-ish)
    public int      Size;    // 0=large 1=medium 2=small
    public float    Fade;    // 1=alive, fades to 0 when dying
    public bool     Dying;
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

    // ── VectorSpin — 3D wireframe data ───────────────────────────────────────
    private float _vecRX, _vecRY, _vecRZ;

    private static readonly float[][] CubeVerts =
    [
        [-1f,-1f,-1f], [ 1f,-1f,-1f], [ 1f, 1f,-1f], [-1f, 1f,-1f],
        [-1f,-1f, 1f], [ 1f,-1f, 1f], [ 1f, 1f, 1f], [-1f, 1f, 1f]
    ];
    private static readonly (int a, int b)[] CubeEdges =
    [
        (0,1),(1,2),(2,3),(3,0), (4,5),(5,6),(6,7),(7,4),
        (0,4),(1,5),(2,6),(3,7)
    ];
    private static readonly float[][] TetraVerts =
    [
        [ 1f, 1f, 1f], [-1f,-1f, 1f], [-1f, 1f,-1f], [ 1f,-1f,-1f]
    ];
    private static readonly (int a, int b)[] TetraEdges =
    [
        (0,1),(0,2),(0,3),(1,2),(1,3),(2,3)
    ];

    // ── OscilloScope ─────────────────────────────────────────────────────────
    private float _scopeA = 2f, _scopeB = 3f, _scopeDelta;
    private float _scopeTransTimer;
    private int   _scopeRatioIdx = 1;
    private static readonly (float a, float b)[] ScopeRatios =
        [(1f,1f),(1f,2f),(2f,3f),(3f,4f),(1f,3f),(3f,5f),(2f,5f),(1f,4f)];

    // ── DosShell ─────────────────────────────────────────────────────────────
    private int   _dosPhase, _dosLineIdx, _dosCharIdx;
    private float _dosAccum, _dosCursorAccum;
    private bool  _dosCursor;
    private float _dosPauseAccum, _dosFadeAlpha = 1f;
    private readonly List<string> _dosDone = [];

    private static readonly string[] DosLines =
    [
        "C:\\GIANT> DIR",
        "",
        " Volume in drive C is CARDIFF",
        " Volume Serial Number is 1983-HCF",
        " Directory of C:\\GIANT",
        "",
        "COMMAND  COM   25307  08-15-83  12:00p",
        "AUTOEXEC BAT     512  08-15-83  12:00p",
        "GIANT    SYS   16384  08-15-83  12:00p",
        "HALT     EXE    4096  09-01-83   2:47p",
        "CATCH    EXE    8192  09-01-83   2:47p",
        "FIRE     EXE   16384  09-01-83   3:12p",
        "ROADMAP  TXT    2048  09-02-83  10:43a",
        "BIOS     BIN   32768  08-25-83   9:00a",
        "         8 File(s)    76159 bytes",
        "         327680 bytes free",
        "",
        "C:\\GIANT> TYPE ROADMAP.TXT",
        "",
        "  GIANT COMPUTER — PRODUCT ROADMAP",
        "  Cardiff Giant Computing Corp.",
        "  Confidential — September 1983",
        "",
        "  1. BIOS complete          (Joe)     DUE: 10/15",
        "  2. OS shell v1.0          (Cameron) DUE: 10/15",
        "  3. Networking stack       (Donna)   DUE: 11/01",
        "  4. Retail packaging + docs (Gordon)",
        "",
        "  Target ship: Q1 1984.",
        "",
        "C:\\GIANT> DEBUG HALT.EXE",
        "-D CS:0100",
        "1A3F:0100  48 41 4C 54  20 41 4E 44   HALT AND",
        "1A3F:0108  20 43 41 54  43 48 20 46   CATCH F",
        "1A3F:0110  49 52 45 00  F4 AF C0 00   IRE.....",
        "-U 0110",
        "1A3F:0110  F4          HLT",
        "1A3F:0111  AF          SCASW",
        "1A3F:0112  C0          DB  C0",
        "-G 0110",
        "",
        "** ILLEGAL INSTRUCTION AT 1A3F:0112 **",
        "PROCESSOR HALTED — CORE TEMP CRITICAL",
        "",
        "-Q",
        "",
    ];

    // ── DiskMap ───────────────────────────────────────────────────────────────
    private byte[] _diskSectors = [];
    private int    _diskCols, _diskRows;
    private float  _diskHeadCol;
    private float  _diskHeadAccum;
    private int    _diskFlashSector = -1;
    private float  _diskFlashTimer;

    // ── AsteroidField ─────────────────────────────────────────────────────────
    private Asteroid[] _asteroids = [];
    private float      _shipX, _shipY, _shipAngle;
    private float      _shotX, _shotY, _shotDX, _shotDY, _shotLife;
    private float      _shotCooldown;
    private float      _astSplitTimer;

    // ── TankWars ──────────────────────────────────────────────────────────────
    private float[] _terrain   = [];
    private float   _tw1X, _tw2X;
    private float   _twProjX, _twProjY, _twProjVX, _twProjVY;
    private bool    _twProjActive;
    private float   _twFireTimer;
    private int     _twTurn;          // 0 = tank1 fires, 1 = tank2 fires
    private float   _twExplX, _twExplY, _twExplR, _twExplTimer;
    private int     _twScore1, _twScore2;

    // ── MutinyBBS ─────────────────────────────────────────────────────────────
    private int   _bbsPhase, _bbsLineIdx, _bbsCharIdx;
    private float _bbsAccum, _bbsCursorAccum;
    private bool  _bbsCursor;
    private float _bbsPauseAccum, _bbsFadeAlpha = 1f;
    private readonly List<string> _bbsDone = [];

    private static readonly string[] BbsLines =
    [
        "ATDT 214-555-0174",
        "",
        "CONNECT 2400",
        "",
        "=================================================",
        "  MUTINY  //  ONLINE GAMING NETWORK",
        "  Dallas, TX  ~  (214) 555-0174  ~  2400 BAUD",
        "=================================================",
        "",
        "Last login: Thu Oct 14 1987  11:47pm",
        "Welcome back, HOOLIGAN",
        "Users online: 47  |  New messages: 14",
        "",
        "[C]hat  [G]ames  [M]ail  [B]ulletin  [Q]uit > B",
        "",
        "=== MUTINY BULLETIN BOARD ===",
        "",
        "[*] SONARIS Tournament — Friday 8pm CST  (14 new)",
        "[*] Anyone else lag on level 3??          (8 new)",
        "[ ] 56k modem upgrade — worth it?         (3 new)",
        "[ ] Donna's network patch feedback        (5 new)",
        "[ ] Joe MacMillan sighting — Dallas??     (2 new)",
        "",
        "> G",
        "",
        "=== GAME LOBBY ===",
        "",
        "  1. SONARIS          8 players online  [JOIN]",
        "  2. Space Siege       2 players        [JOIN]",
        "  3. Dungeon Lords      FULL            [WATCH]",
        "",
        "Joining SONARIS room 1...",
        "Connected. Waiting for players...",
        "",
    ];

    // ── SonarisGame (Breakout) ────────────────────────────────────────────────
    private float  _sbrBallX, _sbrBallY, _sbrBallVX, _sbrBallVY;
    private float  _sbrPaddleX;
    private bool[] _sbrBricks = [];
    private int    _sbrScore;
    private float  _sbrResetTimer;
    private const  string SbrWord = "HALT AND CATCH FIRE";

    // ── TokenRing ─────────────────────────────────────────────────────────────
    private float _tokAngle;       // current token position on ring (radians)
    private float _tokSendTimer;
    private int   _tokSrcNode = -1, _tokDstNode = -1;
    private float _tokPacketAngle; // where the active packet is (radians)
    private bool  _tokTransmitting;
    private float _tokFlashTimer;
    private int   _tokFlashNode;

    private static readonly string[] TokenNodes =
        ["GIANT-01", "GIANT-02", "GIANT-03", "ROUTER", "MODEM", "GIANT-04"];

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

        // VectorSpin
        _vecRX = 0.2f; _vecRY = 0f; _vecRZ = 0.1f;

        // OscilloScope
        _scopeRatioIdx   = 1;
        _scopeA          = ScopeRatios[_scopeRatioIdx].a;
        _scopeB          = ScopeRatios[_scopeRatioIdx].b;
        _scopeDelta      = 0f;
        _scopeTransTimer = 0f;

        // DosShell
        _dosPhase     = 0; _dosLineIdx = 0; _dosCharIdx = 0;
        _dosAccum     = 0f; _dosCursor = true; _dosFadeAlpha = 1f;
        _dosPauseAccum = 0f;
        _dosDone.Clear();

        // DiskMap
        _diskCols = _previewMode ? 28 : 50;
        _diskRows = _previewMode ? 12 : 22;
        _diskSectors = new byte[_diskCols * _diskRows];
        for (int i = 0; i < _diskSectors.Length; i++)
        {
            int r = _rng.Next(100);
            _diskSectors[i] = (byte)(r < 55 ? 1 : r < 65 ? 2 : r < 72 ? 3 : 0);
        }
        _diskHeadCol    = 0f;
        _diskFlashSector = -1;
        _diskFlashTimer  = 0f;

        // AsteroidField
        _shipX = ClientSize.Width / 2f;
        _shipY = ClientSize.Height / 2f;
        _shipAngle  = 0f;
        _shotLife   = 0f;
        _shotCooldown = 0f;
        _astSplitTimer  = 60f;
        _asteroids = new Asteroid[8];
        for (int i = 0; i < _asteroids.Length; i++)
            _asteroids[i] = MakeAsteroid(
                _rng.Next(ClientSize.Width), _rng.Next(ClientSize.Height), 0);

        // TankWars
        GenerateTerrain();
        _tw1X = ClientSize.Width * 0.18f;
        _tw2X = ClientSize.Width * 0.82f;
        _twProjActive = false;
        _twFireTimer  = 60f;
        _twTurn       = 0;
        _twExplTimer  = 0f;
        _twScore1     = 0; _twScore2 = 0;

        // MutinyBBS
        _bbsPhase = 0; _bbsLineIdx = 0; _bbsCharIdx = 0;
        _bbsAccum = 0f; _bbsCursor = true; _bbsFadeAlpha = 1f;
        _bbsPauseAccum = 0f;
        _bbsDone.Clear();

        // SonarisGame
        _sbrBallX  = ClientSize.Width  / 2f;
        _sbrBallY  = ClientSize.Height * 0.6f;
        _sbrBallVX = 3f; _sbrBallVY = -3f;
        _sbrPaddleX = ClientSize.Width / 2f;
        _sbrScore   = 0;
        _sbrResetTimer = 0f;
        _sbrBricks  = new bool[SbrWord.Length];
        for (int i = 0; i < _sbrBricks.Length; i++) _sbrBricks[i] = true;

        // TokenRing
        _tokAngle       = 0f;
        _tokSendTimer   = 40f;
        _tokTransmitting = false;
        _tokFlashTimer  = 0f;
        _tokFlashNode   = -1;

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
            case AnimationStyle.VectorSpin:     DrawVectorSpin(g);           break;
            case AnimationStyle.OscilloScope:   DrawOscilloScope(g);         break;
            case AnimationStyle.DosShell:       DrawDosShell(g);             break;
            case AnimationStyle.DiskMap:        DrawDiskMap(g);              break;
            case AnimationStyle.AsteroidField:  DrawAsteroidField(g);        break;
            case AnimationStyle.TankWars:       DrawTankWars(g);             break;
            case AnimationStyle.MutinyBBS:      DrawMutinyBBS(g);            break;
            case AnimationStyle.SonarisGame:    DrawSonarisGame(g);          break;
            case AnimationStyle.TokenRing:      DrawTokenRing(g);            break;
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
            case AnimationStyle.VectorSpin:    UpdateVectorSpin(speed);   break;
            case AnimationStyle.OscilloScope:  UpdateOscilloScope(speed); break;
            case AnimationStyle.DosShell:      UpdateDosShell(speed);     break;
            case AnimationStyle.DiskMap:       UpdateDiskMap(speed);      break;
            case AnimationStyle.AsteroidField: UpdateAsteroidField(speed); break;
            case AnimationStyle.TankWars:      UpdateTankWars(speed);     break;
            case AnimationStyle.MutinyBBS:     UpdateMutinyBBS(speed);    break;
            case AnimationStyle.SonarisGame:   UpdateSonarisGame(speed);  break;
            case AnimationStyle.TokenRing:     UpdateTokenRing(speed);    break;
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

    // =========================================================================
    // VectorSpin
    // =========================================================================

    private void UpdateVectorSpin(float speed)
    {
        _vecRX += 0.007f * speed;
        _vecRY += 0.011f * speed;
        _vecRZ += 0.004f * speed;
    }

    private void DrawVectorSpin(Graphics g)
    {
        var col    = _settings.TextColor;
        float cx   = ClientSize.Width  / 2f;
        float cy   = ClientSize.Height / 2f;
        float unit = Math.Min(ClientSize.Width, ClientSize.Height);
        float sc   = unit * 0.20f;

        // Large cube — centred slightly left
        DrawWireframe(g, CubeVerts, CubeEdges,
            cx - unit * 0.15f, cy, sc,
            _vecRX, _vecRY, _vecRZ, col, 200);

        // Tetrahedron — smaller, offset right, opposite spin
        DrawWireframe(g, TetraVerts, TetraEdges,
            cx + unit * 0.28f, cy, sc * 0.55f,
            -_vecRX * 1.2f, _vecRY * 0.8f + 1f, _vecRZ * 1.3f, col, 170);

        // Label
        if (_monoFont != null)
        {
            using var lbBr = new SolidBrush(Color.FromArgb(90, col));
            g.DrawString("CARDIFF GIANT COMPUTING — CAD/3D v1.0",
                _monoFont, lbBr, 20f, ClientSize.Height - _monoCharH - 10f);
        }
    }

    private void DrawWireframe(Graphics g, float[][] verts, (int a, int b)[] edges,
        float cx, float cy, float scale,
        float rx, float ry, float rz,
        Color col, int baseAlpha)
    {
        var pts = new PointF[verts.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            Rotate3D(verts[i][0], verts[i][1], verts[i][2], rx, ry, rz,
                out float ox, out float oy, out float oz);
            pts[i] = Perspective3D(ox, oy, oz, cx, cy, scale);
        }

        using var glowPen = new Pen(Color.FromArgb(baseAlpha / 7, col), 5f);
        using var mainPen = new Pen(Color.FromArgb(baseAlpha, col), 1.5f);
        foreach (var (a, b) in edges)
        {
            g.DrawLine(glowPen, pts[a], pts[b]);
            g.DrawLine(mainPen, pts[a], pts[b]);
        }

        // Vertex dots
        using var dotBr = new SolidBrush(Color.FromArgb(baseAlpha, col));
        foreach (var p in pts)
            g.FillEllipse(dotBr, p.X - 2, p.Y - 2, 4, 4);
    }

    private static void Rotate3D(float x, float y, float z,
        float rx, float ry, float rz,
        out float ox, out float oy, out float oz)
    {
        // X rotation
        float ny = y * MathF.Cos(rx) - z * MathF.Sin(rx);
        float nz = y * MathF.Sin(rx) + z * MathF.Cos(rx);
        y = ny; z = nz;
        // Y rotation
        float nx = x * MathF.Cos(ry) + z * MathF.Sin(ry);
        nz = -x * MathF.Sin(ry) + z * MathF.Cos(ry);
        x = nx; z = nz;
        // Z rotation
        nx = x * MathF.Cos(rz) - y * MathF.Sin(rz);
        ny = x * MathF.Sin(rz) + y * MathF.Cos(rz);
        ox = nx; oy = ny; oz = z;
    }

    private static PointF Perspective3D(float x, float y, float z,
        float cx, float cy, float scale)
    {
        float d = z + 4f;
        if (d < 0.1f) d = 0.1f;
        return new PointF(cx + x * scale / d, cy + y * scale / d);
    }

    // =========================================================================
    // OscilloScope
    // =========================================================================

    private void UpdateOscilloScope(float speed)
    {
        // Slowly rotate phase — makes the figure continuously morph
        _scopeDelta += 0.004f * speed;

        // Periodically transition to a new ratio
        _scopeTransTimer += speed / 60f;
        if (_scopeTransTimer > 12f)
        {
            _scopeTransTimer = 0f;
            _scopeRatioIdx   = (_scopeRatioIdx + 1) % ScopeRatios.Length;
            _scopeA          = ScopeRatios[_scopeRatioIdx].a;
            _scopeB          = ScopeRatios[_scopeRatioIdx].b;
        }
    }

    private void DrawOscilloScope(Graphics g)
    {
        var col    = _settings.TextColor;
        int margin = _previewMode ? 8 : 36;

        var scopeR = new Rectangle(margin, margin,
            ClientSize.Width  - margin * 2,
            ClientSize.Height - margin * 2);

        // Bezel background + amber border
        using var bgBr   = new SolidBrush(Color.FromArgb(4, 10, 4));
        using var bezPen = new Pen(Color.FromArgb(160, 80, 0), _previewMode ? 2f : 5f);
        g.FillRectangle(bgBr, scopeR);
        g.DrawRectangle(bezPen, scopeR);

        // Inner glow on bezel
        using var glowBezPen = new Pen(Color.FromArgb(30, col), 10f);
        g.DrawRectangle(glowBezPen, scopeR);

        // Grid
        int gx = 8, gy = 6;
        float cw = scopeR.Width / (float)gx;
        float ch = scopeR.Height / (float)gy;
        using var gridPen  = new Pen(Color.FromArgb(20, col), 1f);
        using var axisPen  = new Pen(Color.FromArgb(40, col), 1f);
        for (int i = 1; i < gx; i++)
            g.DrawLine(i == gx / 2 ? axisPen : gridPen,
                scopeR.X + i * cw, scopeR.Y,
                scopeR.X + i * cw, scopeR.Bottom);
        for (int i = 1; i < gy; i++)
            g.DrawLine(i == gy / 2 ? axisPen : gridPen,
                scopeR.X, scopeR.Y + i * ch,
                scopeR.Right, scopeR.Y + i * ch);

        // Lissajous trace
        float scx = scopeR.Width  * 0.44f;
        float scy = scopeR.Height * 0.44f;
        float ocx = scopeR.X + scopeR.Width  / 2f;
        float ocy = scopeR.Y + scopeR.Height / 2f;

        // Period = 2π * LCM when ratios are integers; use 4π as a safe approximation
        float period = 4f * MathF.PI;
        const int N  = 800;
        var pts = new PointF[N];
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)(N - 1) * period;
            pts[i] = new PointF(
                ocx + MathF.Sin(_scopeA * t + _scopeDelta) * scx,
                ocy + MathF.Sin(_scopeB * t) * scy);
        }

        // Glow pass (wide, low alpha)
        using var glowPen  = new Pen(Color.FromArgb(18, col), 6f);
        for (int i = 1; i < N; i++)
            g.DrawLine(glowPen, pts[i - 1], pts[i]);

        // Main trace
        using var tracePen = new Pen(Color.FromArgb(200, col), 1.5f);
        for (int i = 1; i < N; i++)
            g.DrawLine(tracePen, pts[i - 1], pts[i]);

        // Label — ratio and delta
        if (_monoFont != null)
        {
            using var lbBr = new SolidBrush(Color.FromArgb(90, col));
            g.DrawString(
                $"CH1 FREQ:{_scopeA:F0}  CH2 FREQ:{_scopeB:F0}  " +
                $"DELTA:{_scopeDelta % (MathF.PI * 2):F2} rad  |  LISSAJOUS",
                _monoFont, lbBr,
                scopeR.X + 4f, scopeR.Bottom + 4f);
        }
    }

    // =========================================================================
    // DosShell
    // =========================================================================

    private void UpdateDosShell(float speed)
    {
        const float CharsPerSec = 55f;
        const float CursorHz    = 2f;
        float dt = speed / 60f;

        _dosCursorAccum += dt;
        if (_dosCursorAccum > 1f / CursorHz) { _dosCursor = !_dosCursor; _dosCursorAccum = 0f; }

        switch (_dosPhase)
        {
            case 0: // Typing
                _dosAccum += CharsPerSec * dt * speed * 0.7f;
                while (_dosAccum >= 1f && _dosLineIdx < DosLines.Length)
                {
                    _dosAccum -= 1f;
                    string line = DosLines[_dosLineIdx];
                    if (_dosCharIdx >= line.Length)
                    {
                        _dosDone.Add(line);
                        _dosLineIdx++;
                        _dosCharIdx = 0;
                        if (_dosLineIdx >= DosLines.Length) { _dosPhase = 1; _dosPauseAccum = 0f; }
                    }
                    else { _dosCharIdx++; }
                }
                break;
            case 1: // Hold
                _dosPauseAccum += dt;
                if (_dosPauseAccum > 3.5f) { _dosPhase = 2; _dosFadeAlpha = 1f; }
                break;
            case 2: // Fade out + restart
                _dosFadeAlpha -= dt * 0.7f;
                if (_dosFadeAlpha <= 0f)
                {
                    _dosPhase = 0; _dosLineIdx = 0; _dosCharIdx = 0;
                    _dosAccum = 0f; _dosFadeAlpha = 1f;
                    _dosDone.Clear();
                }
                break;
        }
    }

    private void DrawDosShell(Graphics g)
    {
        if (_monoFont == null) return;
        float alpha = _dosPhase == 2 ? Math.Max(0f, _dosFadeAlpha) : 1f;
        var col = _settings.TextColor;
        using var br    = new SolidBrush(Color.FromArgb((int)(210 * alpha), col));
        using var dimBr = new SolidBrush(Color.FromArgb((int)(110 * alpha), col));

        int lineH = _monoCharH + 2;
        int y     = 18;

        foreach (var line in _dosDone)
        {
            // Highlight prompt lines
            var pen = line.StartsWith("C:\\") ? br : dimBr;
            g.DrawString(line, _monoFont, pen, 18, y);
            y += lineH;
        }

        // Current partially-typed line
        if (_dosPhase == 0 && _dosLineIdx < DosLines.Length)
        {
            string partial = DosLines[_dosLineIdx][.._dosCharIdx];
            g.DrawString(partial + (_dosCursor ? "_" : " "), _monoFont, br, 18, y);
        }
    }

    // =========================================================================
    // DiskMap
    // =========================================================================

    private void UpdateDiskMap(float speed)
    {
        // Advance the scanning head
        _diskHeadAccum += 0.04f * speed;
        if (_diskHeadAccum >= 1f)
        {
            _diskHeadAccum -= 1f;
            _diskHeadCol   += 1f;
            if (_diskHeadCol >= _diskCols)
            {
                _diskHeadCol = 0f;
                // When the head wraps, do a small reshuffle (simulate defrag progress)
                for (int i = 0; i < _rng.Next(3, 8); i++)
                {
                    int idx = _rng.Next(_diskSectors.Length);
                    if (_diskSectors[idx] == 3) _diskSectors[idx] = 1; // consolidate fragment
                }
            }
        }

        // Flash a random sector periodically
        _diskFlashTimer -= speed / 60f;
        if (_diskFlashTimer <= 0f)
        {
            _diskFlashSector = _rng.Next(_diskSectors.Length);
            _diskFlashTimer  = 0.08f + (float)_rng.NextDouble() * 0.25f;
        }
    }

    private void DrawDiskMap(Graphics g)
    {
        var col    = _settings.TextColor;
        int marginX = _previewMode ? 8 : 24;
        int marginT = _previewMode ? 8 : 20;
        int statsH  = (_monoFont != null ? _monoCharH : 16) * 2 + 12;
        int availW  = ClientSize.Width  - marginX * 2;
        int availH  = ClientSize.Height - marginT - statsH - marginX;

        float cellStep = Math.Min(availW / (float)_diskCols, availH / (float)_diskRows);
        float cellSize = Math.Max(2f, cellStep - 1f);
        float gridW    = _diskCols * cellStep;
        float gridH    = _diskRows * cellStep;
        float ox       = marginX + (availW - gridW) / 2f;
        float oy       = marginT;

        // Sector cells
        for (int row = 0; row < _diskRows; row++)
        {
            for (int col2 = 0; col2 < _diskCols; col2++)
            {
                int   idx = row * _diskCols + col2;
                float sx  = ox + col2 * cellStep;
                float sy  = oy + row  * cellStep;

                bool flashing = idx == _diskFlashSector && _diskFlashTimer > 0.04f;
                Color c = flashing ? Color.White :
                    _diskSectors[idx] switch
                    {
                        0 => Color.FromArgb(10, 28, 10),    // free
                        1 => Color.FromArgb(0,  110, 0),    // data
                        2 => Color.FromArgb(57, 255, 20),   // system
                        3 => Color.FromArgb(160, 80, 0),    // fragmented
                        _ => Color.Black
                    };

                using var cellBr = new SolidBrush(c);
                g.FillRectangle(cellBr, sx, sy, cellSize, cellSize);
            }
        }

        // Read/write head — vertical line + triangle pointer
        float hx = ox + _diskHeadCol * cellStep + cellSize / 2f;
        using var headPen = new Pen(Color.FromArgb(100, col), 1f);
        g.DrawLine(headPen, hx, oy - 2, hx, oy + gridH + 2);
        using var headBr = new SolidBrush(Color.White);
        g.FillPolygon(headBr, [
            new PointF(hx - 5, oy - 10),
            new PointF(hx + 5, oy - 10),
            new PointF(hx,     oy - 2)
        ]);

        // Stats
        if (_monoFont != null)
        {
            float sy2  = oy + gridH + 12;
            int used   = 0;
            int frags  = 0;
            foreach (var s in _diskSectors) { if (s > 0) used++; if (s == 3) frags++; }
            int pctUsed = used * 100 / Math.Max(1, _diskSectors.Length);
            int cyl     = (int)(_diskHeadCol / _diskCols * 612);
            int head    = ((int)_diskHeadCol / 3) % 6;
            int sec     = (int)_diskHeadCol % 17 + 1;

            using var statsBr = new SolidBrush(Color.FromArgb(160, col));
            using var dimBr   = new SolidBrush(Color.FromArgb(70, col));
            g.DrawString(
                $"CYL:{cyl:D4}  HEAD:{head}  SECT:{sec:D2}   " +
                $"USED:{pctUsed}%  FRAGS:{frags}  " +
                $"FREE:{_diskSectors.Length - used} sectors",
                _monoFont, statsBr, ox, sy2);

            // Capacity bar
            float barW    = gridW;
            int   barFill = (int)(barW * pctUsed / 100f);
            using var barBgBr = new SolidBrush(Color.FromArgb(20, col));
            using var barFgBr = new SolidBrush(Color.FromArgb(120, col));
            g.FillRectangle(barBgBr, ox, sy2 + _monoCharH + 4, barW, 6);
            g.FillRectangle(barFgBr, ox, sy2 + _monoCharH + 4, barFill, 6);
            g.DrawString("CARDIFF GIANT HDD  //  DISK ANALYZER v1.1",
                _monoFont, dimBr, ox + barW + 10, sy2 + _monoCharH + 2);
        }
    }

    // =========================================================================
    // AsteroidField
    // =========================================================================

    private Asteroid MakeAsteroid(float x, float y, int size)
    {
        float scale = size == 0 ? 44f : size == 1 ? 24f : 13f;
        int n = 7 + _rng.Next(3);
        var poly = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * MathF.PI * 2 / n;
            float r = 0.65f + (float)_rng.NextDouble() * 0.35f;
            poly[i] = new PointF(MathF.Cos(a) * r, MathF.Sin(a) * r);
        }
        return new Asteroid
        {
            X = x, Y = y,
            VX = (_rng.Next(2) == 0 ? 1 : -1) * (0.3f + (float)_rng.NextDouble() * 0.9f),
            VY = (_rng.Next(2) == 0 ? 1 : -1) * (0.3f + (float)_rng.NextDouble() * 0.9f),
            Angle   = (float)_rng.NextDouble() * MathF.PI * 2,
            AngVel  = ((float)_rng.NextDouble() - 0.5f) * 0.04f,
            Scale   = scale,
            Poly    = poly,
            Size    = size,
            Fade    = 1f,
            Dying   = false
        };
    }

    private void UpdateAsteroidField(float speed)
    {
        float w = ClientSize.Width, h = ClientSize.Height;

        // Rotate ship slowly, fire periodically
        _shipAngle  += 0.008f * speed;
        _shotCooldown -= speed;
        if (_shotCooldown <= 0f)
        {
            _shotX  = _shipX; _shotY = _shipY;
            _shotDX = MathF.Cos(_shipAngle) * 8f;
            _shotDY = MathF.Sin(_shipAngle) * 8f;
            _shotLife = 60f;
            _shotCooldown = 80f + _rng.Next(60);
        }
        if (_shotLife > 0) { _shotX += _shotDX * speed; _shotY += _shotDY * speed; _shotLife -= speed; }

        // Advance asteroids
        for (int i = 0; i < _asteroids.Length; i++)
        {
            ref var a = ref _asteroids[i];
            a.X     = ((a.X + a.VX * speed) % w + w) % w;
            a.Y     = ((a.Y + a.VY * speed) % h + h) % h;
            a.Angle += a.AngVel * speed;
            if (a.Dying) { a.Fade -= 0.04f * speed; if (a.Fade <= 0) a.Dying = false; }
        }

        // Periodically split a large asteroid
        _astSplitTimer -= speed;
        if (_astSplitTimer <= 0f)
        {
            _astSplitTimer = 120f + _rng.Next(180);
            // Find a large living asteroid
            for (int i = 0; i < _asteroids.Length; i++)
            {
                if (_asteroids[i].Size == 0 && !_asteroids[i].Dying)
                {
                    float sx = _asteroids[i].X, sy = _asteroids[i].Y;
                    _asteroids[i].Dying = true;
                    // Replace two medium ones in empty slots (or reuse the dying slot next frame)
                    for (int j = 0; j < _asteroids.Length; j++)
                    {
                        if (!_asteroids[j].Dying && _rng.Next(8) == 0)
                        {
                            _asteroids[j] = MakeAsteroid(sx + _rng.Next(40) - 20, sy + _rng.Next(40) - 20, 1);
                            break;
                        }
                    }
                    break;
                }
            }
        }
        // Respawn dead asteroids as new large ones at edge
        for (int i = 0; i < _asteroids.Length; i++)
        {
            if (!_asteroids[i].Dying && _asteroids[i].Fade <= 0)
            {
                float ex = _rng.Next(2) == 0 ? 0 : w;
                float ey = (float)_rng.NextDouble() * h;
                _asteroids[i] = MakeAsteroid(ex, ey, 0);
            }
        }
    }

    private void DrawAsteroidField(Graphics g)
    {
        var col = _settings.TextColor;

        // Ship — classic triangle
        float sa = _shipAngle;
        var shipPts = new PointF[]
        {
            new(_shipX + MathF.Cos(sa) * 14, _shipY + MathF.Sin(sa) * 14),
            new(_shipX + MathF.Cos(sa + 2.4f) * 9, _shipY + MathF.Sin(sa + 2.4f) * 9),
            new(_shipX + MathF.Cos(sa - 2.4f) * 9, _shipY + MathF.Sin(sa - 2.4f) * 9)
        };
        using var shipPen = new Pen(Color.FromArgb(220, col), 1.5f);
        using var shipGlow = new Pen(Color.FromArgb(35, col), 5f);
        g.DrawPolygon(shipGlow, shipPts);
        g.DrawPolygon(shipPen, shipPts);

        // Shot
        if (_shotLife > 0)
        {
            using var shotBr = new SolidBrush(Color.White);
            g.FillEllipse(shotBr, _shotX - 3, _shotY - 3, 6, 6);
        }

        // Asteroids
        foreach (ref var ast in _asteroids.AsSpan())
        {
            if (ast.Poly == null || (!ast.Dying && ast.Fade <= 0)) continue;
            float fade = ast.Dying ? Math.Max(0, ast.Fade) : 1f;
            int n = ast.Poly.Length;
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                float px = ast.Poly[i].X * MathF.Cos(ast.Angle) - ast.Poly[i].Y * MathF.Sin(ast.Angle);
                float py = ast.Poly[i].X * MathF.Sin(ast.Angle) + ast.Poly[i].Y * MathF.Cos(ast.Angle);
                pts[i] = new PointF(ast.X + px * ast.Scale, ast.Y + py * ast.Scale);
            }
            using var pen  = new Pen(Color.FromArgb((int)(200 * fade), col), 1.5f);
            using var glow = new Pen(Color.FromArgb((int)(28 * fade), col), 5f);
            g.DrawPolygon(glow, pts);
            g.DrawPolygon(pen,  pts);
        }

        // Label
        if (_monoFont != null)
        {
            using var lbBr = new SolidBrush(Color.FromArgb(70, col));
            g.DrawString("GIANT ARCADE  //  1 PLAYER  //  INSERT COIN",
                _monoFont, lbBr, 14f, ClientSize.Height - _monoCharH - 10f);
        }
    }

    // =========================================================================
    // TankWars
    // =========================================================================

    private void GenerateTerrain()
    {
        int w = Math.Max(1, ClientSize.Width);
        _terrain = new float[w];
        float baseH = ClientSize.Height * 0.68f;
        float a1 = ClientSize.Height * 0.07f, a2 = ClientSize.Height * 0.04f;
        float f1 = MathF.PI * 2 * 2.5f / w, f2 = MathF.PI * 2 * 5f / w;
        float p1 = (float)_rng.NextDouble() * MathF.PI * 2;
        float p2 = (float)_rng.NextDouble() * MathF.PI * 2;
        for (int x = 0; x < w; x++)
            _terrain[x] = baseH + a1 * MathF.Sin(x * f1 + p1) + a2 * MathF.Sin(x * f2 + p2);
    }

    private float TerrainAt(float x) =>
        _terrain.Length == 0 ? 0 : _terrain[Math.Clamp((int)x, 0, _terrain.Length - 1)];

    private void UpdateTankWars(float speed)
    {
        _twFireTimer -= speed;

        // Explosion decay
        if (_twExplTimer > 0) _twExplTimer -= speed;

        if (_twProjActive)
        {
            _twProjVY += 0.18f * speed;   // gravity
            _twProjX  += _twProjVX * speed;
            _twProjY  += _twProjVY * speed;

            float terY = TerrainAt(_twProjX);
            bool outOfBounds = _twProjX < 0 || _twProjX >= ClientSize.Width || _twProjY > ClientSize.Height;
            if (_twProjY >= terY || outOfBounds)
            {
                _twProjActive = false;
                _twExplX = _twProjX; _twExplY = MathF.Min(_twProjY, terY);
                _twExplR = 0f; _twExplTimer = 40f;

                // Check hit on the other tank
                float hitX = _twTurn == 0 ? _tw2X : _tw1X;
                if (MathF.Abs(_twProjX - hitX) < 30)
                {
                    if (_twTurn == 0) _twScore1++; else _twScore2++;
                }

                _twTurn     = 1 - _twTurn;
                _twFireTimer = 80f + _rng.Next(60);
            }
        }
        else if (_twFireTimer <= 0f)
        {
            // Fire
            float srcX  = _twTurn == 0 ? _tw1X : _tw2X;
            float dstX  = _twTurn == 0 ? _tw2X : _tw1X;
            float srcY  = TerrainAt(srcX);
            float dstY  = TerrainAt(dstX);
            float dir   = dstX > srcX ? 1f : -1f;
            float dist  = MathF.Abs(dstX - srcX);
            float spd   = 5f + dist * 0.012f + (float)_rng.NextDouble() * 2f;
            float angle = MathF.Atan2(srcY - dstY, dstX - srcX)
                          - (0.3f + (float)_rng.NextDouble() * 0.25f);
            _twProjX  = srcX; _twProjY = srcY - 14f;
            _twProjVX = MathF.Cos(angle) * spd * dir;
            _twProjVY = -MathF.Sin(angle) * spd;
            _twProjActive = true;
        }

        // Grow explosion radius
        if (_twExplTimer > 0) _twExplR = Math.Min(40f, _twExplR + 2f * speed);
    }

    private void DrawTankWars(Graphics g)
    {
        var col = _settings.TextColor;

        // Sky gradient hint (optional subtle bands)
        // Terrain polygon — fill below terrain line
        int w = ClientSize.Width;
        if (_terrain.Length >= w)
        {
            var terPts = new PointF[w + 2];
            for (int x = 0; x < w; x++) terPts[x] = new PointF(x, _terrain[x]);
            terPts[w]     = new PointF(w, ClientSize.Height);
            terPts[w + 1] = new PointF(0, ClientSize.Height);
            using var terFill = new SolidBrush(Color.FromArgb(18, col));
            g.FillPolygon(terFill, terPts);
            using var terPen = new Pen(Color.FromArgb(160, col), 1.5f);
            var linePts = new PointF[w];
            for (int x = 0; x < w; x++) linePts[x] = new PointF(x, _terrain[x]);
            g.DrawLines(terPen, linePts);
        }

        // Tank helper
        void DrawTank(float tx, bool right)
        {
            float ty = TerrainAt(tx);
            float bw = 28, bh = 11;
            using var bodyFill = new SolidBrush(Color.FromArgb(50, col));
            using var bodyPen  = new Pen(Color.FromArgb(200, col), 1.5f);
            g.FillRectangle(bodyFill, tx - bw / 2, ty - bh, bw, bh);
            g.DrawRectangle(bodyPen,  tx - bw / 2, ty - bh, bw, bh);
            // Barrel
            float ba = right ? -MathF.PI * 0.3f : -MathF.PI * 0.7f;
            float blen = 18;
            g.DrawLine(bodyPen, tx, ty - bh, tx + MathF.Cos(ba) * blen, ty - bh + MathF.Sin(ba) * blen);
            // Treads
            using var treadPen = new Pen(Color.FromArgb(130, col), 3f);
            g.DrawLine(treadPen, tx - bw / 2 - 3, ty, tx + bw / 2 + 3, ty);
        }

        DrawTank(_tw1X, true);
        DrawTank(_tw2X, false);

        // Projectile
        if (_twProjActive)
        {
            using var projBr = new SolidBrush(Color.White);
            g.FillEllipse(projBr, _twProjX - 3, _twProjY - 3, 6, 6);
        }

        // Explosion
        if (_twExplTimer > 0 && _twExplR > 0)
        {
            float alpha = _twExplTimer / 40f;
            using var exPen = new Pen(Color.FromArgb((int)(180 * alpha), col), 2f);
            using var exGlow = new Pen(Color.FromArgb((int)(50 * alpha), col), 8f);
            g.DrawEllipse(exGlow, _twExplX - _twExplR, _twExplY - _twExplR, _twExplR * 2, _twExplR * 2);
            g.DrawEllipse(exPen, _twExplX - _twExplR, _twExplY - _twExplR, _twExplR * 2, _twExplR * 2);
        }

        // Score
        if (_monoFont != null)
        {
            using var scoreBr = new SolidBrush(Color.FromArgb(170, col));
            g.DrawString($"TANK-1: {_twScore1:D2}", _monoFont, scoreBr, 20f, 14f);
            using var scoreBr2 = new SolidBrush(Color.FromArgb(170, col));
            var s2 = $"TANK-2: {_twScore2:D2}";
            using var measG = CreateGraphics();
            var sz = measG.MeasureString(s2, _monoFont);
            g.DrawString(s2, _monoFont, scoreBr2, ClientSize.Width - sz.Width - 20f, 14f);
            using var titleBr = new SolidBrush(Color.FromArgb(60, col));
            g.DrawString("TANKWARS  //  CARDIFF GIANT  //  1984",
                _monoFont, titleBr, 20f, ClientSize.Height - _monoCharH - 10f);
        }
    }

    // =========================================================================
    // MutinyBBS
    // =========================================================================

    private void UpdateMutinyBBS(float speed)
    {
        const float CharsPerSec = 48f;
        float dt = speed / 60f;

        _bbsCursorAccum += dt;
        if (_bbsCursorAccum > 0.5f) { _bbsCursor = !_bbsCursor; _bbsCursorAccum = 0f; }

        switch (_bbsPhase)
        {
            case 0:
                _bbsAccum += CharsPerSec * dt * speed * 0.75f;
                while (_bbsAccum >= 1f && _bbsLineIdx < BbsLines.Length)
                {
                    _bbsAccum -= 1f;
                    string line = BbsLines[_bbsLineIdx];
                    if (_bbsCharIdx >= line.Length)
                    {
                        _bbsDone.Add(line);
                        _bbsLineIdx++;
                        _bbsCharIdx = 0;
                        if (_bbsLineIdx >= BbsLines.Length) { _bbsPhase = 1; _bbsPauseAccum = 0f; }
                    }
                    else { _bbsCharIdx++; }
                }
                break;
            case 1:
                _bbsPauseAccum += dt;
                if (_bbsPauseAccum > 4f) { _bbsPhase = 2; _bbsFadeAlpha = 1f; }
                break;
            case 2:
                _bbsFadeAlpha -= dt * 0.6f;
                if (_bbsFadeAlpha <= 0f)
                {
                    _bbsPhase = 0; _bbsLineIdx = 0; _bbsCharIdx = 0;
                    _bbsAccum = 0f; _bbsFadeAlpha = 1f;
                    _bbsDone.Clear();
                }
                break;
        }
    }

    private void DrawMutinyBBS(Graphics g)
    {
        if (_monoFont == null) return;
        float alpha = _bbsPhase == 2 ? Math.Max(0f, _bbsFadeAlpha) : 1f;
        var col = _settings.TextColor;
        using var mainBr  = new SolidBrush(Color.FromArgb((int)(200 * alpha), col));
        using var dimBr   = new SolidBrush(Color.FromArgb((int)(100 * alpha), col));
        using var headerBr = new SolidBrush(Color.FromArgb((int)(230 * alpha), col));

        int lineH = _monoCharH + 2;
        int y = 14;
        foreach (var line in _bbsDone)
        {
            // Header lines (===) get extra brightness; prompt lines get full bright; rest dim
            var br = line.StartsWith("=") ? headerBr : line.StartsWith(">") || line.StartsWith("[") ? mainBr : dimBr;
            g.DrawString(line, _monoFont, br, 18, y);
            y += lineH;
        }
        if (_bbsPhase == 0 && _bbsLineIdx < BbsLines.Length)
        {
            string partial = BbsLines[_bbsLineIdx][.._bbsCharIdx];
            g.DrawString(partial + (_bbsCursor ? "_" : " "), _monoFont, mainBr, 18, y);
        }
    }

    // =========================================================================
    // SonarisGame  (Breakout — letters of "HALT AND CATCH FIRE" as bricks)
    // =========================================================================

    private void UpdateSonarisGame(float speed)
    {
        if (_sbrResetTimer > 0) { _sbrResetTimer -= speed; return; }

        float w = ClientSize.Width, h = ClientSize.Height;

        // Paddle tracks ball with slight lag
        float target = _sbrBallX;
        _sbrPaddleX += (target - _sbrPaddleX) * 0.08f * speed;
        _sbrPaddleX  = Math.Clamp(_sbrPaddleX, 50, w - 50);

        // Move ball
        _sbrBallX += _sbrBallVX * speed;
        _sbrBallY += _sbrBallVY * speed;

        // Wall bounces
        if (_sbrBallX < 6 || _sbrBallX > w - 6)  _sbrBallVX = -_sbrBallVX;
        if (_sbrBallY < 6)                          _sbrBallVY = -_sbrBallVY;

        // Paddle bounce
        float padW = 80, padH = 10;
        float padY = h - 50;
        if (_sbrBallY + 6 >= padY && _sbrBallY - 6 <= padY + padH &&
            _sbrBallX >= _sbrPaddleX - padW / 2 && _sbrBallX <= _sbrPaddleX + padW / 2)
        {
            _sbrBallVY = -MathF.Abs(_sbrBallVY);
            _sbrBallVX += (_sbrBallX - _sbrPaddleX) * 0.06f; // angle off paddle
        }

        // Ball lost — reset
        if (_sbrBallY > h + 20)
        {
            _sbrBallX = w / 2f; _sbrBallY = h * 0.55f;
            _sbrBallVX = 3f + (float)_rng.NextDouble(); _sbrBallVY = -3.5f;
        }

        // Brick collision — build brick rects on the fly
        var brickLayout = GetBrickRects();
        for (int i = 0; i < _sbrBricks.Length && i < brickLayout.Length; i++)
        {
            if (!_sbrBricks[i]) continue;
            var r = brickLayout[i];
            if (_sbrBallX + 6 >= r.Left && _sbrBallX - 6 <= r.Right &&
                _sbrBallY + 6 >= r.Top  && _sbrBallY - 6 <= r.Bottom)
            {
                _sbrBricks[i] = false;
                _sbrBallVY    = -_sbrBallVY;
                _sbrScore     += 10;
            }
        }

        // All bricks gone — reset after pause
        if (_sbrBricks.All(b => !b))
        {
            _sbrResetTimer = 90f;
            for (int i = 0; i < _sbrBricks.Length; i++) _sbrBricks[i] = true;
        }
    }

    private RectangleF[] GetBrickRects()
    {
        if (_monoFont == null) return [];
        float totalW = 0;
        using var measG = CreateGraphics();
        for (int i = 0; i < SbrWord.Length; i++)
            totalW += measG.MeasureString(SbrWord[i].ToString(), _monoFont,
                PointF.Empty, StringFormat.GenericTypographic).Width + 4;

        float startX = (ClientSize.Width - totalW) / 2f;
        float brickY = ClientSize.Height * 0.18f;
        float brickH = _monoCharH + 6;
        var rects = new RectangleF[SbrWord.Length];
        float x = startX;
        for (int i = 0; i < SbrWord.Length; i++)
        {
            float cw = measG.MeasureString(SbrWord[i].ToString(), _monoFont,
                PointF.Empty, StringFormat.GenericTypographic).Width + 8;
            rects[i] = new RectangleF(x, brickY, cw, brickH);
            x += cw + 2;
        }
        return rects;
    }

    private void DrawSonarisGame(Graphics g)
    {
        var col = _settings.TextColor;

        // Title
        if (_monoFont != null)
        {
            using var titleBr = new SolidBrush(Color.FromArgb(180, col));
            using var titleFont = new Font("Courier New", _previewMode ? 10f : 16f, FontStyle.Bold, GraphicsUnit.Point);
            g.DrawString("SONARIS  //  MUTINY ONLINE GAMING  //  1987",
                titleFont, titleBr, 18f, 14f);
            using var scoreBr = new SolidBrush(Color.FromArgb(140, col));
            g.DrawString($"SCORE: {_sbrScore:D6}", _monoFont, scoreBr,
                ClientSize.Width - 160f, 14f);
        }

        // Bricks (letters)
        var bricks = GetBrickRects();
        using var brickFont = _monoFont != null
            ? new Font("Courier New", _monoFont.Size, FontStyle.Bold, GraphicsUnit.Point)
            : new Font("Courier New", 11f, FontStyle.Bold, GraphicsUnit.Point);
        for (int i = 0; i < _sbrBricks.Length && i < bricks.Length; i++)
        {
            if (!_sbrBricks[i]) continue;
            var r = bricks[i];
            using var brickFill = new SolidBrush(Color.FromArgb(35, col));
            using var brickPen  = new Pen(Color.FromArgb(160, col), 1f);
            using var brickTxt  = new SolidBrush(Color.FromArgb(220, col));
            g.FillRectangle(brickFill, r);
            g.DrawRectangle(brickPen,  r.X, r.Y, r.Width, r.Height);
            g.DrawString(SbrWord[i].ToString(), brickFont, brickTxt, r.X + 3, r.Y + 2);
        }

        // Ball
        using var ballBr = new SolidBrush(Color.White);
        using var ballGlow = new SolidBrush(Color.FromArgb(50, col));
        g.FillEllipse(ballGlow, _sbrBallX - 8, _sbrBallY - 8, 16, 16);
        g.FillEllipse(ballBr,   _sbrBallX - 4, _sbrBallY - 4,  8,  8);

        // Paddle
        float padW = 80, padH = 10;
        float padY = ClientSize.Height - 50;
        using var padFill = new SolidBrush(Color.FromArgb(60, col));
        using var padPen  = new Pen(Color.FromArgb(200, col), 1.5f);
        g.FillRectangle(padFill, _sbrPaddleX - padW / 2, padY, padW, padH);
        g.DrawRectangle(padPen,  _sbrPaddleX - padW / 2, padY, padW, padH);
    }

    // =========================================================================
    // TokenRing
    // =========================================================================

    private void UpdateTokenRing(float speed)
    {
        int n = TokenNodes.Length;

        // Circulate the token
        _tokAngle += 0.012f * speed;
        if (_tokAngle > MathF.PI * 2) _tokAngle -= MathF.PI * 2;

        // Flash decay
        if (_tokFlashTimer > 0) _tokFlashTimer -= speed;

        // Periodically start a transmission
        _tokSendTimer -= speed;
        if (_tokSendTimer <= 0f && !_tokTransmitting)
        {
            _tokSrcNode    = _rng.Next(n);
            _tokDstNode    = (_tokSrcNode + 1 + _rng.Next(n - 1)) % n;
            _tokPacketAngle = NodeAngle(_tokSrcNode, n);
            _tokTransmitting = true;
            _tokSendTimer   = 120f + _rng.Next(80);
        }

        if (_tokTransmitting)
        {
            // Advance packet toward destination
            float dstAngle = NodeAngle(_tokDstNode, n);
            float diff     = NormalizeAngle(dstAngle - _tokPacketAngle);
            float step     = 0.025f * speed;
            if (MathF.Abs(diff) <= step)
            {
                _tokPacketAngle  = dstAngle;
                _tokTransmitting = false;
                _tokFlashNode    = _tokDstNode;
                _tokFlashTimer   = 30f;
            }
            else
            {
                _tokPacketAngle += MathF.Sign(diff) * step;
            }
        }
    }

    private static float NodeAngle(int idx, int total) =>
        -MathF.PI / 2f + idx * MathF.PI * 2f / total;

    private static float NormalizeAngle(float a)
    {
        while (a >  MathF.PI) a -= MathF.PI * 2;
        while (a < -MathF.PI) a += MathF.PI * 2;
        return a;
    }

    private void DrawTokenRing(Graphics g)
    {
        var col = _settings.TextColor;
        int n   = TokenNodes.Length;
        float cx = ClientSize.Width  / 2f;
        float cy = ClientSize.Height / 2f;
        float r  = Math.Min(ClientSize.Width, ClientSize.Height) * 0.32f;

        // Ring backbone
        using var ringPen  = new Pen(Color.FromArgb(50, col), 2f);
        using var ringGlow = new Pen(Color.FromArgb(12, col), 8f);
        g.DrawEllipse(ringGlow, cx - r, cy - r, r * 2, r * 2);
        g.DrawEllipse(ringPen,  cx - r, cy - r, r * 2, r * 2);

        // Token dot circling the ring
        float tx = cx + MathF.Cos(_tokAngle) * r;
        float ty = cy + MathF.Sin(_tokAngle) * r;
        using var tokBr   = new SolidBrush(Color.White);
        using var tokGlow = new SolidBrush(Color.FromArgb(60, col));
        g.FillEllipse(tokGlow, tx - 8, ty - 8, 16, 16);
        g.FillEllipse(tokBr,   tx - 4, ty - 4,  8,  8);

        // Active packet
        if (_tokTransmitting)
        {
            float px = cx + MathF.Cos(_tokPacketAngle) * r;
            float py = cy + MathF.Sin(_tokPacketAngle) * r;
            using var pktBr   = new SolidBrush(Color.FromArgb(255, 200, 0));
            using var pktGlow = new SolidBrush(Color.FromArgb(60, 200, 120, 0));
            g.FillEllipse(pktGlow, px - 10, py - 10, 20, 20);
            g.FillEllipse(pktBr,   px -  5, py -  5, 10, 10);
        }

        // Nodes
        for (int i = 0; i < n; i++)
        {
            float na  = NodeAngle(i, n);
            float nx  = cx + MathF.Cos(na) * r;
            float ny  = cy + MathF.Sin(na) * r;
            bool flash = i == _tokFlashNode && _tokFlashTimer > 0;
            bool src  = _tokTransmitting && i == _tokSrcNode;
            bool dst  = _tokTransmitting && i == _tokDstNode;

            Color nodeCol = flash ? Color.White :
                            src   ? Color.FromArgb(255, 200, 0) :
                            dst   ? Color.FromArgb(100, 255, 100) :
                                    col;
            int nodeA = flash ? 255 : 180;

            float nr = 14;
            using var nodeFill = new SolidBrush(Color.FromArgb(nodeA / 3, nodeCol));
            using var nodePen  = new Pen(Color.FromArgb(nodeA, nodeCol), 1.5f);
            g.FillRectangle(nodeFill, nx - nr, ny - nr / 2, nr * 2, nr);
            g.DrawRectangle(nodePen,  nx - nr, ny - nr / 2, nr * 2, nr);

            // Label
            if (_monoFont != null)
            {
                using var lblBr = new SolidBrush(Color.FromArgb(nodeA, nodeCol));
                float lblX = nx + (MathF.Cos(na) > 0 ? nr + 4 : -nr - 58);
                float lblY = ny - _monoCharH / 2f;
                g.DrawString(TokenNodes[i], _monoFont, lblBr, lblX, lblY);
            }
        }

        // Title + activity
        if (_monoFont != null)
        {
            using var lbBr = new SolidBrush(Color.FromArgb(70, col));
            string status = _tokTransmitting
                ? $"TX: {TokenNodes[_tokSrcNode]} -> {TokenNodes[_tokDstNode]}"
                : "TOKEN IDLE";
            g.DrawString($"CARDIFF TOKEN RING  //  4 MBPS  //  {status}",
                _monoFont, lbBr, 14f, ClientSize.Height - _monoCharH - 10f);
        }
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
