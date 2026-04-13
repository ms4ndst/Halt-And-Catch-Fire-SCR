using System.Drawing;
using System.Text.Json.Serialization;

namespace HcfScreensaver.Models;

public enum AnimationStyle
{
    PhosphorDrift,   // Cascading columns of phosphor-green hex/ASCII characters
    BootSequence,    // 80s PC boot messages typing out, revealing the title
    CircuitTrace,    // PCB circuit board traces being drawn across the screen
    HexStream,       // Scrolling hex dump of machine code
    CrtTitle,        // Title card with CRT scanlines, phosphor glow and flicker
    BinaryRain,      // Columns of binary 0s and 1s (amber palette)
    DataCorrupt,     // Glitch/data-corruption effect on the title
    Interference,    // TV static with the title visible through the noise
    VectorSpin,      // Rotating 3D wireframe objects with perspective projection
    OscilloScope,    // Lissajous figures on a CRT oscilloscope display
    DosShell,        // Extended DOS terminal session with commands and DEBUG
    DiskMap          // Hard-disk sector map with scanning read/write head
}

public sealed class ScreensaverSettings
{
    public const string DefaultText = "HALT AND CATCH FIRE";

    // Text color defaults to phosphor green; background to black
    public int TextColorArgb        { get; set; } = Color.FromArgb(57, 255, 20).ToArgb();
    public int BackgroundColorArgb  { get; set; } = Color.Black.ToArgb();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnimationStyle AnimationStyle { get; set; } = AnimationStyle.PhosphorDrift;

    public int Speed { get; set; } = 8;   // 1–20

    // CRT effect intensity 1–5 (affects scanline density, glow, flicker)
    public int CrtIntensity { get; set; } = 3;

    [JsonIgnore] public Color TextColor       => Color.FromArgb(TextColorArgb);
    [JsonIgnore] public Color BackgroundColor => Color.FromArgb(BackgroundColorArgb);

    public Font CreateTitleFont(float size = 64f)
    {
        return new Font("Courier New", size, FontStyle.Bold, GraphicsUnit.Point);
    }
}
