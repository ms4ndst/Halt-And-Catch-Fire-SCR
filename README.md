# Halt and Catch Fire — Windows Screensaver

A Windows screensaver inspired by the opening titles of the AMC television series
*[Halt and Catch Fire](https://en.wikipedia.org/wiki/Halt_and_Catch_Fire_(TV_series))* (2014–2017).
The show follows engineers and entrepreneurs navigating the personal computer revolution of the 1980s.
This screensaver recreates that era's CRT-terminal aesthetic: phosphor-green characters, scanlines,
circuit-board traces, and the glitchy digital feel of machines that were pushed past their limits.

> **"Halt and Catch Fire"** — an undocumented x86 instruction rumoured to cause a processor to
> halt execution and literally catch fire. Whether myth or reality, it perfectly captures the spirit
> of pushing hardware to its absolute edge.

---

## Animations

Seventeen distinct animations, all rendered in the phosphor-green-on-black palette of period-authentic
80s computer terminals.

| Animation | Description |
|---|---|
| **PhosphorDrift** *(default)* | Cascading columns of hex and ASCII characters with a bright white head and a fading green trail — the show's signature look |
| **BootSequence** | A fake IBM PC boot sequence types itself out line by line, revealing a Cardiff Giant Computing OS, then fades in the title card in large phosphor text |
| **CircuitTrace** | PCB circuit-board traces are drawn in real time across the screen, branching at 90° angles with solder pads at junctions, before fading and restarting |
| **HexStream** | A continuously scrolling hex dump — the title `HALT AND CATCH FIRE` is embedded in the ASCII side-panel so it drifts past in the data stream |
| **CrtTitle** | The show title centred on screen with full CRT treatment: scanline overlay, multi-pass phosphor glow, screen-edge vignette, random brightness flicker, and occasional noise bars |
| **BinaryRain** | Like PhosphorDrift but restricted to `0` and `1` — rendered in period-authentic **amber** phosphor |
| **DataCorrupt** | RGB channel splitting, random character corruption, horizontal screen tears (clipped region displacement), and scattered noise blocks |
| **Interference** | Analogue TV static (hundreds of noise pixels per frame) with moving interference bands and the title pulsing in and out through the snow |
| **VectorSpin** | A perspective-projected 3D wireframe cube and tetrahedron rotating simultaneously with phosphor glow — evoking 80s CAD software and vector arcade games |
| **OscilloScope** | Lissajous figures drawn on a CRT oscilloscope display complete with amber bezel, phosphor grid, and a rotating phase angle that continuously morphs the figure through 8 ratio presets |
| **DosShell** | A full DOS terminal session plays out — `DIR`, `TYPE ROADMAP.TXT`, and a `DEBUG` session that disassembles the actual `HLT` + illegal opcode sequence at address `1A3F:0110` |
| **DiskMap** | A hard-disk sector map showing data, system, fragmented, and free sectors with a scanning read/write head, cylinder/head/sector readout, and a live capacity bar |
| **MutinyBBS** | A dial-up modem connection sequence plays out — `ATDT`, handshake tones, `CONNECT 2400` — landing on the Mutiny BBS login screen from *Halt and Catch Fire* season 2 |
| **SonarisGame** | A Breakout clone where the bricks spell **HALT AND CATCH FIRE** — a nod to Cameron's Sonaris game featured throughout the series |
| **TokenRing** | Cardiff Giant's token-ring LAN in motion: six nodes arranged in a ring with named stations (`GIANT-01`, `ROUTER`, `MODEM`, …) and animated packets racing around the loop |
| **PixieGame** | A Pixie dungeon-crawler text adventure plays out on screen — rooms, monsters, and commands, evoking Cameron's RPG game built on the Mutiny platform |
| **GiantCalc** | A Lotus 1-2-3 style spreadsheet fills with Cardiff Giant Computing's Q3 1983 financials — the killer-app dream that drove the whole show |

---

## Requirements

- Windows 10 or 11 (x64)
- **.NET 8 SDK** — only required to *build*; the published `.scr` is self-contained with no runtime dependency
  - Download: <https://dotnet.microsoft.com/download>

---

## Quick Install

Run the installer from the repository root (self-elevates to Administrator):

```powershell
.\Install-HcfScreensaver.ps1
```

The script will:

1. Verify the .NET 8 SDK is installed
2. Publish a self-contained single-file Release build
3. Copy and rename the output to `HcfScreensaver.scr` in the repo root
4. Install it to `%WINDIR%\System32\`
5. Offer to open the Windows Screen Saver Settings dialog

To uninstall:

```powershell
Remove-Item "$env:WINDIR\System32\HcfScreensaver.scr" -Force
```

---

## Build from Source

```powershell
cd HcfScreensaver

# Debug build (fast, for development)
dotnet build

# Release publish — creates the deployable .scr
dotnet publish --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output bin\Release\publish
```

The published executable is a standard Windows PE binary. Rename it to `.scr` and place it in
`System32` to register it as a screensaver, or double-click a `.scr` file directly to preview it.

---

## Configuration

Double-clicking the `.scr` file (or clicking **Settings…** in the Windows Screen Saver dialog)
opens the configuration panel.

![Config form screenshot — terminal aesthetic: phosphor green on black](docs/config-screenshot.png)

| Setting | Options | Default |
|---|---|---|
| Animation mode | 17 animations (see table above) | PhosphorDrift |
| Speed | 1 (slow) – 20 (fast) | 8 |
| CRT intensity | 1 (subtle) – 5 (heavy) | 3 |
| Phosphor colour | Green · Amber · White · Cyan · Custom | Green `#39FF14` |

---

## Screensaver Arguments

Windows invokes screensavers with standard arguments that this application handles:

| Argument | Behaviour |
|---|---|
| *(none)* or `/c` | Opens the configuration dialog |
| `/s` | Runs the screensaver fullscreen across all monitors |
| `/p <HWND>` | Renders a preview inside the Windows Screen Saver Settings panel |

---

## Project Structure

```
HCF_screensaver/
├── HcfScreensaver/
│   ├── HcfScreensaver.csproj          .NET 8 WinForms, single-file self-contained
│   ├── Program.cs                     Entry point — parses /s /c /p arguments
│   ├── ScreensaverApplicationContext.cs  Multi-monitor form lifecycle
│   ├── Models/
│   │   └── ScreensaverSettings.cs     Persisted settings model
│   ├── Services/
│   │   └── SettingsService.cs         JSON persistence → %LOCALAPPDATA%\HcfScreensaver\
│   └── Forms/
│       ├── ScreensaverForm.cs         Animation engine — all 17 animations
│       └── ConfigForm.cs              Terminal-aesthetic configuration UI
└── Install-HcfScreensaver.ps1         Self-elevating build + install script
```

Settings are saved to `%LOCALAPPDATA%\HcfScreensaver\settings.json`.

---

## Technical Notes

- **Rendering:** GDI+ with `OptimizedDoubleBuffer` to eliminate flicker. The animation timer runs
  at 16 ms intervals (~60 fps).
- **Performance:** Character columns in PhosphorDrift and BinaryRain are computed per-frame with a
  seeded pseudo-random function (`col × 137 + trailPos × 31 + frame/4 × 17`) so chars change
  slowly without heap allocations.
- **CircuitTrace** traces grow at 3 px/frame along a 24 px grid; completed segments accumulate
  until the scene fades and resets at 200 segments.
- **CRT flicker** is implemented as a random brightness multiplier (0.88–1.0) applied to the text
  alpha every 10–50 frames.
- The `.scr` file is a standard Win32 PE executable — no installer registry writes are needed
  beyond copying it to `System32`.

---

## Inspiration

- *Halt and Catch Fire* (AMC, 2014–2017) — created by Christopher Cantwell and Christopher C. Rogers
- The show's title sequence, designed to evoke the feel of early personal computers and the
  obsessive energy of the people who built them
- The `HCF` (Halt and Catch Fire) mnemonic — a real Intel 8088 undocumented opcode (`F4 AF C0`)
  that was said to generate bus contention severe enough to damage hardware

---

## See Also

- [TYT_Screensaver](https://github.com/ms4ndst/TYT_Screensaver) — sibling project:
  *"TRUST YOUR TECHNOLUST"* screensaver with 18 animations, the architectural template for this project

---

## License

MIT — do whatever you like with it.
