# AmongMenu — Among Us Overlay

A **C# WPF standalone overlay** for *Among Us* that shows impostor/crewmate
positions, real-time distances, room names, and colour-coded tracers — all in a
transparent, always-on-top window you can capture in OBS without it appearing
inside the game itself.

---

## Features

| Feature | Description |
|---|---|
| **Impostor detection** | Lists every impostor with name, room, and distance |
| **Crewmate display** | Same info for crewmates (useful when *you* are the impostor) |
| **Distance-based tracers** | Lines drawn from your position to every player |
| **Colour coding** | 🔴 Red &lt; 2 u · 🟡 Yellow 2–5 u · 🟢 Green &gt; 5 u |
| **Transparent overlay** | `AllowsTransparency=True`, draggable, always-on-top |
| **Real-time refresh** | Reads game memory every 500 ms |
| **Configurable offsets** | Edit `config/offsets.json` — no recompilation needed |
| **Signature scanning** | Auto-detects updated pointer addresses after patches |
| **Debug mode** | Toggle the **DBG** button to log pointer chains + scan results |

---

## Project structure

```
AmongMenu/
├── config/
│   └── offsets.json        # IL2CPP offsets (edit after game patches)
├── Models/
│   ├── Player.cs           # Per-player data (name, role, position, room, distance)
│   └── GameData.cs         # Snapshot of the full game state
├── Utilities/
│   ├── GameMemoryReader.cs # Reads Among Us process memory via ReadProcessMemory
│   ├── OffsetScanner.cs    # AOB signature scanner for finding updated offsets
│   └── ColorHelper.cs      # Maps distance → tracer colour
├── Views/
│   ├── OverlayWindow.xaml  # Transparent WPF overlay with Canvas + player list
│   └── OverlayWindow.xaml.cs
├── App.xaml / App.xaml.cs  # WPF application entry point
├── AmongMenu.csproj        # .NET 8 SDK project (x64, WPF)
└── README.md
```

---

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 8.0 or later (Windows) |
| Windows | 10 / 11 x64 |
| Among Us | Steam build (tested on v2024.6.18) |

---

## Building

```bash
# Restore & build (Release, x64)
dotnet build -c Release -r win-x64

# Publish a self-contained single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/
```

The output executable is `publish/AmongMenu.exe`.

---

## Usage

1. Launch **Among Us** (Steam).
2. Start a lobby / game.
3. Run `AmongMenu.exe` as **Administrator** (required for cross-process memory
   access via `ReadProcessMemory`).
4. The overlay appears in the top-left corner — drag the title bar to move it.
5. Click **✕** to close.

---

## Fixing "No player list found" (updating offsets after a game patch)

When Among Us is updated, the IL2CPP static-field addresses shift and the
overlay shows *"No player list found"* or *"GameData not initialized"*.

### Quick fix — edit the config file

Open `config/offsets.json` (next to `AmongMenu.exe`) in any text editor and
update the hex values.  **No recompilation is needed.**

```jsonc
{
  "gameVersion": "2024.x.x",        // ← update this for your version
  "gameData": {
    "staticRva": "0x01F890B0",      // ← RVA of the GameData static pointer
    "instanceOffset": "0xB8"
  },
  "localPlayer": {
    "staticRva": "0x01F89290",      // ← RVA of the LocalPlayer static pointer
    "offset": "0xB8"
  }
  // … other offsets
}
```

### How to find the new RVAs

#### Option A — use the built-in signature scanner (easiest)

1. Run the overlay and click the **DBG** button in the title bar.
2. Wait for the debug panel to appear at the bottom of the window.
3. When an error occurs (e.g. *"No player list found"*) the scanner
   automatically runs and logs candidate RVAs:

   ```
   ─── Signature scan results ─────────────────────────────────
   Scanning for: 48 8B 05 ?? ?? ?? ?? 48 8B 80 B8 00 00 00
     Match @ RVA 0x01F890B0  →  target RVA 0x020A1234
   ```

4. Copy the **target RVA** (e.g. `0x020A1234`) into `config/offsets.json`
   under `gameData.staticRva`.
5. Restart the overlay — no rebuild required.

#### Option B — use IL2CPP dumper + dnSpy / Ghidra

1. Download [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) and run it
   against `GameAssembly.dll` + `global-metadata.dat` from your Among Us
   install folder (`…\Among Us\Among Us_Data\il2cpp\`).
2. Load the generated `dump.cs` or import the script into Ghidra / IDA.
3. Search for `GameData$$get_Instance` and note the first
   `MOV RAX, [RIP + <disp>]` instruction.
4. Compute: `RVA = instruction_offset + 7 + sign_extend_32(disp)`.
5. Update `config/offsets.json`.

#### Option C — use Cheat Engine

1. Open Cheat Engine, attach to `Among Us.exe`.
2. Search for the string `"GameData"` in memory (UTF-16).
3. Follow cross-references in the disassembly view to find the static field
   accessor.
4. Note the RIP-relative address and compute the RVA as above.

### Updating signature patterns

If the auto-scanner also fails (no matches), the byte pattern in
`config/offsets.json → signatures` may need updating too:

```jsonc
"signatures": {
  "gameDataStatic": {
    "pattern": "48 8B 05 ?? ?? ?? ?? 48 8B 80 B8 00 00 00",
    "ripDisplacement": 3,
    "instrSize": 7,
    "enabled": true
  }
}
```

Use a disassembler to locate the new byte sequence around
`GameData$$get_Instance` and update `pattern` accordingly.

---

## Distance colour thresholds

Thresholds are defined in `Utilities/ColorHelper.cs`:

```csharp
private const float CloseThreshold  = 2f;   // Red  below this
private const float MediumThreshold = 5f;   // Yellow between the two
                                             // Green above MediumThreshold
```

Change the constants and rebuild to adjust them.

---

## Disclaimer

This project is for **educational and personal use only**.  Using overlays that
read game memory may violate Among Us' Terms of Service.  Use responsibly and
only in private lobbies with consenting players.

---

## License

MIT