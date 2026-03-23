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

---

## Project structure

```
AmongMenu/
├── Models/
│   ├── Player.cs           # Per-player data (name, role, position, room, distance)
│   └── GameData.cs         # Snapshot of the full game state
├── Utilities/
│   ├── GameMemoryReader.cs # Reads Among Us process memory via ReadProcessMemory
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
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish/
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

## Distance colour thresholds

Thresholds are defined in `Utilities/ColorHelper.cs`:

```csharp
private const float CloseThreshold  = 2f;   // Red  below this
private const float MediumThreshold = 5f;   // Yellow between the two
                                             // Green above MediumThreshold
```

Change the constants and rebuild to adjust them.

---

## Memory offsets

`GameMemoryReader.cs` contains IL2CPP pointer offsets verified against the
Steam v2024.6.18 build of Among Us.  If the game is patched, the offsets may
need updating.  They are clearly documented as constants at the top of the file.

---

## Disclaimer

This project is for **educational and personal use only**.  Using overlays that
read game memory may violate Among Us' Terms of Service.  Use responsibly and
only in private lobbies with consenting players.

---

## License

MIT