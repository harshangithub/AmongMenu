using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using AmongMenu.Models;

namespace AmongMenu.Utilities
{
    /// <summary>
    /// Reads live game data from the running Among Us process.
    ///
    /// Architecture overview
    /// ─────────────────────
    /// Among Us (IL2CPP build) keeps managed objects in heap memory that can be
    /// located through a chain of static-field pointers anchored in the game
    /// assembly image.
    ///
    /// Offset loading
    /// ──────────────
    /// Offsets are loaded from <c>config/offsets.json</c> at startup so that
    /// they can be updated after a game patch without recompiling.  If the file
    /// cannot be found or parsed, hardcoded defaults (v2024.6.18) are used and
    /// a warning is written to the debug log.
    ///
    /// Reading strategy
    /// ────────────────
    /// 1. Locate the Among Us process with Process.GetProcessesByName.
    /// 2. Open a read-only handle with ReadProcessMemory.
    /// 3. Walk the pointer chain:
    ///       GameData (static) → allPlayers → PlayerControl[] → PlayerInfo
    ///    to collect name, role, position and room for every slot.
    /// 4. Identify the local player via PlayerControl.LocalPlayer (static).
    /// 5. Compute per-player distances and populate <see cref="GameData"/>.
    ///
    /// Signature scanning fallback
    /// ───────────────────────────
    /// When the configured GameData or LocalPlayer static RVA resolves to a
    /// null pointer (a clear sign that the offset is wrong), the reader runs
    /// <see cref="OffsetScanner"/> to search for updated values using the AOB
    /// patterns defined in <c>config/offsets.json → signatures</c>.  Found
    /// candidates are logged to <see cref="DebugLog"/> for the user to put
    /// into the config file.
    ///
    /// When the game is not running, <see cref="ReadGameData"/> returns a
    /// placeholder <see cref="GameData"/> with <c>IsGameActive = false</c>.
    /// </summary>
    public class GameMemoryReader
    {
        // ── Win32 imports ──────────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr hProcess,
            IntPtr lpBaseAddress,
            byte[] lpBuffer,
            int dwSize,
            out int lpNumberOfBytesRead);

        private const int PROCESS_VM_READ          = 0x0010;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;

        // ── Default IL2CPP offsets (Steam v2024.6.18) ─────────────────────────
        // Used only when config/offsets.json is missing or unreadable.

        private const long DEFAULT_GAMEDATA_STATIC        = 0x01F890B0;
        private const int  DEFAULT_GAMEDATA_INSTANCE_OFF  = 0xB8;
        private const long DEFAULT_LOCAL_PLAYER_STATIC    = 0x01F89290;
        private const int  DEFAULT_LOCAL_PLAYER_OFF       = 0xB8;

        private const int DEFAULT_ALL_PLAYERS_OFFSET  = 0x24;
        private const int DEFAULT_LIST_ITEMS_OFFSET   = 0x10;
        private const int DEFAULT_LIST_COUNT_OFFSET   = 0x18;
        private const int DEFAULT_ARRAY_FIRST_ELEMENT = 0x20;
        private const int DEFAULT_ARRAY_ELEMENT_SIZE  = 0x08;

        private const int DEFAULT_INFO_NAME_OFFSET   = 0x10;
        private const int DEFAULT_INFO_ROLE_OFFSET   = 0x2C;
        private const int DEFAULT_INFO_ISDEAD_OFFSET = 0x30;
        private const int DEFAULT_INFO_OBJECT_OFFSET = 0x08;

        private const int DEFAULT_CTRL_POSITION_X     = 0x78;
        private const int DEFAULT_CTRL_POSITION_Y     = 0x7C;
        private const int DEFAULT_ROOM_TRACKER_OFFSET = 0xA0;
        private const int DEFAULT_ROOM_ID_OFFSET      = 0x10;

        // ── Room lookup ───────────────────────────────────────────────────────
        private static readonly Dictionary<int, string> RoomNames = new()
        {
            { 0,  "The Skeld – Cafeteria"  },
            { 1,  "The Skeld – Reactor"    },
            { 2,  "The Skeld – Upper Engine" },
            { 3,  "The Skeld – Lower Engine" },
            { 4,  "The Skeld – Security"   },
            { 5,  "The Skeld – MedBay"     },
            { 6,  "The Skeld – Electrical" },
            { 7,  "The Skeld – Storage"    },
            { 8,  "The Skeld – Admin"      },
            { 9,  "The Skeld – Navigation" },
            { 10, "The Skeld – Weapons"    },
            { 11, "The Skeld – Shields"    },
            { 12, "The Skeld – Communications" },
            { 13, "The Skeld – O2"         },
            { 14, "The Skeld – Comms"      },
            { 15, "MIRA HQ – Cafeteria"    },
            { 16, "MIRA HQ – Balcony"      },
            { 17, "MIRA HQ – Storage"      },
            { 18, "MIRA HQ – Admin"        },
            { 19, "MIRA HQ – Laboratory"   },
            { 20, "MIRA HQ – Reactor"      },
            { 21, "Polus – Security"       },
            { 22, "Polus – Electrical"     },
            { 23, "Polus – O2"             },
            { 24, "Polus – Communications" },
            { 25, "Polus – Office"         },
            { 26, "Polus – Admin"          },
            { 27, "Polus – Specimen Room"  },
            { 28, "Polus – Boiler Room"    },
            { 29, "Polus – Outside"        },
            { 30, "Polus – Dropship"       },
        };

        // ── Runtime offsets (loaded from JSON, or fall back to defaults) ───────

        private long _gameDataStaticRva;
        private int  _gameDataInstanceOff;
        private long _localPlayerStaticRva;
        private int  _localPlayerOff;

        private int _allPlayersOff;
        private int _listItemsOff;
        private int _listCountOff;
        private int _arrayFirstElement;
        private int _arrayElementSize;

        private int _infoNameOff;
        private int _infoRoleOff;
        private int _infoIsDeadOff;
        private int _infoObjectOff;

        private int _ctrlPosX;
        private int _ctrlPosY;
        private int _roomTrackerOff;
        private int _roomIdOff;

        // Signature scanning config (from offsets.json)
        private string _gameDataSigPattern   = string.Empty;
        private int    _gameDataSigRipDisp   = 3;
        private int    _gameDataSigInstrSize = 7;
        private bool   _gameDataSigEnabled;

        private string _localPlayerSigPattern   = string.Empty;
        private int    _localPlayerSigRipDisp   = 3;
        private int    _localPlayerSigInstrSize = 7;
        private bool   _localPlayerSigEnabled;

        // ── Process state ─────────────────────────────────────────────────────

        private IntPtr _processHandle = IntPtr.Zero;
        private long   _moduleBase    = 0;
        private int    _moduleSize    = 0;
        private int    _lastPid       = 0;

        // ── Debug support ─────────────────────────────────────────────────────

        /// <summary>
        /// When true, extra diagnostic messages are appended to <see cref="DebugLog"/>.
        /// Toggle via the Debug button in the overlay.
        /// </summary>
        public bool DebugMode { get; set; }

        /// <summary>
        /// Accumulated debug messages.  Cleared on each <see cref="ReadGameData"/> call
        /// when <see cref="DebugMode"/> is enabled.
        /// </summary>
        public string DebugLog { get; private set; } = string.Empty;

        // ── Construction ──────────────────────────────────────────────────────

        public GameMemoryReader()
        {
            LoadOffsets();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the current game state from memory.
        /// Returns a populated <see cref="GameData"/> on success, or a
        /// placeholder object when the game is not running / cannot be read.
        /// </summary>
        public GameData ReadGameData()
        {
            var result = new GameData();
            var dbg    = new StringBuilder();

            if (!EnsureProcess())
            {
                result.GameStatus = "Among Us not running";
                return result;
            }

            if (DebugMode)
                dbg.AppendLine($"Module base: 0x{_moduleBase:X}  size: 0x{_moduleSize:X}");

            try
            {
                IntPtr localCtrl = ReadLocalPlayer(dbg);
                if (localCtrl == IntPtr.Zero)
                {
                    result.GameStatus = "Waiting for game to start…";
                    if (DebugMode)
                    {
                        TrySignatureScan(dbg);
                        DebugLog = dbg.ToString();
                    }
                    return result;
                }

                var localPos = ReadPosition(localCtrl);

                if (DebugMode)
                    dbg.AppendLine($"LocalPlayer ctrl: 0x{localCtrl:X}  pos: ({localPos.X:F2}, {localPos.Y:F2})");

                IntPtr gameDataInst = ReadPointerChain(_moduleBase + _gameDataStaticRva, _gameDataInstanceOff);
                if (gameDataInst == IntPtr.Zero)
                {
                    result.GameStatus = "GameData not initialized";
                    if (DebugMode)
                    {
                        dbg.AppendLine($"GameData static RVA: 0x{_gameDataStaticRva:X}  →  instance ptr is null");
                        TrySignatureScan(dbg);
                        DebugLog = dbg.ToString();
                    }
                    return result;
                }

                if (DebugMode)
                    dbg.AppendLine($"GameData instance: 0x{gameDataInst:X}");

                IntPtr allPlayers = ReadPointerChain(gameDataInst, _allPlayersOff);
                if (allPlayers == IntPtr.Zero)
                {
                    result.GameStatus = "No player list found";
                    if (DebugMode)
                    {
                        dbg.AppendLine($"allPlayers offset 0x{_allPlayersOff:X}  →  null; GameData instance may be stale");
                        TrySignatureScan(dbg);
                        DebugLog = dbg.ToString();
                    }
                    return result;
                }

                IntPtr itemsArray = ReadPointerChain(allPlayers, _listItemsOff);
                int    count      = ReadInt(allPlayers + _listCountOff);

                if (DebugMode)
                    dbg.AppendLine($"allPlayers: 0x{allPlayers:X}  count: {count}  items: 0x{itemsArray:X}");

                if (count <= 0 || count > 15)
                {
                    result.GameStatus = "Unexpected player count";
                    if (DebugMode)
                    {
                        dbg.AppendLine($"count={count} out of range [1,15]");
                        DebugLog = dbg.ToString();
                    }
                    return result;
                }

                for (int i = 0; i < count; i++)
                {
                    long   elementOffset = _arrayFirstElement + (long)i * _arrayElementSize;
                    IntPtr infoPtr       = ReadPointerChain(itemsArray, (int)elementOffset);
                    if (infoPtr == IntPtr.Zero) continue;

                    Player player = ReadPlayerInfo(infoPtr, localPos, localCtrl);
                    result.Players.Add(player);

                    if (player.IsLocal)
                        result.LocalPlayer = player;
                    else if (player.IsImpostor)
                        result.Impostors.Add(player);
                    else
                        result.Crewmates.Add(player);

                    if (DebugMode)
                        dbg.AppendLine($"  [{i}] {player}");
                }

                result.IsGameActive = true;
                result.GameStatus   = $"Game active – {result.Players.Count} players";
            }
            catch (Exception ex)
            {
                result.GameStatus = $"Read error: {ex.Message}";
                if (DebugMode)
                    dbg.AppendLine($"EXCEPTION: {ex}");
            }

            if (DebugMode)
                DebugLog = dbg.ToString();

            return result;
        }

        // ── Offset loading ────────────────────────────────────────────────────

        /// <summary>
        /// Loads offsets from <c>config/offsets.json</c> next to the executable.
        /// Falls back to compiled-in defaults when the file is absent or invalid.
        /// </summary>
        private void LoadOffsets()
        {
            // Apply defaults first so any missing JSON key keeps a sane value.
            ApplyDefaultOffsets();

            string configPath = Path.Combine(
                AppContext.BaseDirectory, "config", "offsets.json");

            if (!File.Exists(configPath))
            {
                // Also try relative to current directory (useful during development)
                configPath = Path.Combine(
                    Directory.GetCurrentDirectory(), "config", "offsets.json");
            }

            if (!File.Exists(configPath))
                return;   // use defaults silently

            try
            {
                string json = File.ReadAllText(configPath);
                using JsonDocument doc = JsonDocument.Parse(json,
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("gameData", out JsonElement gd))
                {
                    _gameDataStaticRva   = ParseHex(gd, "staticRva",      DEFAULT_GAMEDATA_STATIC);
                    _gameDataInstanceOff = (int)ParseHex(gd, "instanceOffset", DEFAULT_GAMEDATA_INSTANCE_OFF);
                }

                if (root.TryGetProperty("localPlayer", out JsonElement lp))
                {
                    _localPlayerStaticRva = ParseHex(lp, "staticRva", DEFAULT_LOCAL_PLAYER_STATIC);
                    _localPlayerOff       = (int)ParseHex(lp, "offset",    DEFAULT_LOCAL_PLAYER_OFF);
                }

                if (root.TryGetProperty("playerList", out JsonElement pl))
                {
                    _allPlayersOff    = (int)ParseHex(pl, "allPlayersOffset",  DEFAULT_ALL_PLAYERS_OFFSET);
                    _listItemsOff     = (int)ParseHex(pl, "listItemsOffset",   DEFAULT_LIST_ITEMS_OFFSET);
                    _listCountOff     = (int)ParseHex(pl, "listCountOffset",   DEFAULT_LIST_COUNT_OFFSET);
                    _arrayFirstElement = (int)ParseHex(pl, "arrayFirstElement", DEFAULT_ARRAY_FIRST_ELEMENT);
                    _arrayElementSize  = (int)ParseHex(pl, "arrayElementSize",  DEFAULT_ARRAY_ELEMENT_SIZE);
                }

                if (root.TryGetProperty("playerInfo", out JsonElement pi))
                {
                    _infoNameOff   = (int)ParseHex(pi, "nameOffset",   DEFAULT_INFO_NAME_OFFSET);
                    _infoRoleOff   = (int)ParseHex(pi, "roleOffset",   DEFAULT_INFO_ROLE_OFFSET);
                    _infoIsDeadOff = (int)ParseHex(pi, "isDeadOffset", DEFAULT_INFO_ISDEAD_OFFSET);
                    _infoObjectOff = (int)ParseHex(pi, "objectOffset", DEFAULT_INFO_OBJECT_OFFSET);
                }

                if (root.TryGetProperty("playerControl", out JsonElement pc))
                {
                    _ctrlPosX        = (int)ParseHex(pc, "positionXOffset",   DEFAULT_CTRL_POSITION_X);
                    _ctrlPosY        = (int)ParseHex(pc, "positionYOffset",   DEFAULT_CTRL_POSITION_Y);
                    _roomTrackerOff  = (int)ParseHex(pc, "roomTrackerOffset", DEFAULT_ROOM_TRACKER_OFFSET);
                    _roomIdOff       = (int)ParseHex(pc, "roomIdOffset",      DEFAULT_ROOM_ID_OFFSET);
                }

                if (root.TryGetProperty("signatures", out JsonElement sigs))
                {
                    if (sigs.TryGetProperty("gameDataStatic", out JsonElement gds))
                    {
                        _gameDataSigPattern   = gds.TryGetProperty("pattern",         out var p) ? p.GetString() ?? string.Empty : string.Empty;
                        _gameDataSigRipDisp   = gds.TryGetProperty("ripDisplacement", out var r) ? r.GetInt32() : 3;
                        _gameDataSigInstrSize = gds.TryGetProperty("instrSize",        out var s) ? s.GetInt32() : 7;
                        _gameDataSigEnabled   = gds.TryGetProperty("enabled",          out var e) && e.GetBoolean();
                    }

                    if (sigs.TryGetProperty("localPlayerStatic", out JsonElement lps))
                    {
                        _localPlayerSigPattern   = lps.TryGetProperty("pattern",         out var p) ? p.GetString() ?? string.Empty : string.Empty;
                        _localPlayerSigRipDisp   = lps.TryGetProperty("ripDisplacement", out var r) ? r.GetInt32() : 3;
                        _localPlayerSigInstrSize = lps.TryGetProperty("instrSize",        out var s) ? s.GetInt32() : 7;
                        _localPlayerSigEnabled   = lps.TryGetProperty("enabled",          out var e) && e.GetBoolean();
                    }
                }
            }
            catch
            {
                // JSON parse failure – keep defaults already applied above
            }
        }

        private void ApplyDefaultOffsets()
        {
            _gameDataStaticRva     = DEFAULT_GAMEDATA_STATIC;
            _gameDataInstanceOff   = DEFAULT_GAMEDATA_INSTANCE_OFF;
            _localPlayerStaticRva = DEFAULT_LOCAL_PLAYER_STATIC;
            _localPlayerOff       = DEFAULT_LOCAL_PLAYER_OFF;

            _allPlayersOff     = DEFAULT_ALL_PLAYERS_OFFSET;
            _listItemsOff      = DEFAULT_LIST_ITEMS_OFFSET;
            _listCountOff      = DEFAULT_LIST_COUNT_OFFSET;
            _arrayFirstElement = DEFAULT_ARRAY_FIRST_ELEMENT;
            _arrayElementSize  = DEFAULT_ARRAY_ELEMENT_SIZE;

            _infoNameOff   = DEFAULT_INFO_NAME_OFFSET;
            _infoRoleOff   = DEFAULT_INFO_ROLE_OFFSET;
            _infoIsDeadOff = DEFAULT_INFO_ISDEAD_OFFSET;
            _infoObjectOff = DEFAULT_INFO_OBJECT_OFFSET;

            _ctrlPosX       = DEFAULT_CTRL_POSITION_X;
            _ctrlPosY       = DEFAULT_CTRL_POSITION_Y;
            _roomTrackerOff = DEFAULT_ROOM_TRACKER_OFFSET;
            _roomIdOff      = DEFAULT_ROOM_ID_OFFSET;
        }

        /// <summary>
        /// Parses a hex string property from a JSON element, returning
        /// <paramref name="fallback"/> when the property is missing or unparseable.
        /// Accepts both <c>"0x1A2B"</c> and plain decimal strings.
        /// </summary>
        private static long ParseHex(JsonElement element, string propertyName, long fallback)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement prop))
                return fallback;

            string? raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            raw = raw.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(raw.Substring(2), System.Globalization.NumberStyles.HexNumber,
                    null, out long hex) ? hex : fallback;
            }

            return long.TryParse(raw, out long dec) ? dec : fallback;
        }

        // ── Process management ────────────────────────────────────────────────

        private bool EnsureProcess()
        {
            var processes = Process.GetProcessesByName("Among Us");
            if (processes.Length == 0)
            {
                DisposeHandle();
                return false;
            }

            int pid = processes[0].Id;
            if (pid == _lastPid && _processHandle != IntPtr.Zero)
                return true;

            DisposeHandle();
            _lastPid       = pid;
            _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_processHandle == IntPtr.Zero)
                return false;

            (_moduleBase, _moduleSize) = GetModuleInfo(processes[0], "GameAssembly.dll");
            return _moduleBase != 0;
        }

        private static (long base_, int size) GetModuleInfo(Process proc, string moduleName)
        {
            foreach (ProcessModule mod in proc.Modules)
            {
                if (string.Equals(mod.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                    return ((long)mod.BaseAddress, mod.ModuleMemorySize);
            }
            return (0, 0);
        }

        private void DisposeHandle()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            _moduleBase = 0;
            _moduleSize = 0;
            _lastPid    = 0;
        }

        // ── Signature-scan fallback ───────────────────────────────────────────

        /// <summary>
        /// Runs the AOB scanner on the configured signatures and appends
        /// candidate RVAs to the debug log.  Candidates should be placed in
        /// <c>config/offsets.json</c> to fix the offset after a game patch.
        /// </summary>
        private void TrySignatureScan(StringBuilder dbg)
        {
            if (_processHandle == IntPtr.Zero || _moduleBase == 0 || _moduleSize == 0)
            {
                dbg.AppendLine("Signature scan skipped – no process handle or module info.");
                return;
            }

            var scanner = new OffsetScanner(_processHandle, _moduleBase, _moduleSize);

            dbg.AppendLine("─── Signature scan results ────────────────────────────────");

            if (_gameDataSigEnabled && !string.IsNullOrWhiteSpace(_gameDataSigPattern))
                dbg.Append(scanner.Diagnose(_gameDataSigPattern, _gameDataSigRipDisp, _gameDataSigInstrSize));
            else
                dbg.AppendLine("GameData signature scan disabled or pattern not set.");

            if (_localPlayerSigEnabled && !string.IsNullOrWhiteSpace(_localPlayerSigPattern))
                dbg.Append(scanner.Diagnose(_localPlayerSigPattern, _localPlayerSigRipDisp, _localPlayerSigInstrSize));
            else
                dbg.AppendLine("LocalPlayer signature scan disabled or pattern not set.");

            dbg.AppendLine("───────────────────────────────────────────────────────────");
            dbg.AppendLine("Copy any candidate RVAs above into config/offsets.json.");
        }

        // ── Player reading ────────────────────────────────────────────────────

        private IntPtr ReadLocalPlayer(StringBuilder dbg)
        {
            IntPtr ctrlPtr = ReadPointerChain((IntPtr)(_moduleBase + _localPlayerStaticRva), _localPlayerOff);
            if (DebugMode && ctrlPtr == IntPtr.Zero)
                dbg.AppendLine($"LocalPlayer static RVA 0x{_localPlayerStaticRva:X}  →  ptr is null");
            return ctrlPtr;
        }

        private Player ReadPlayerInfo(IntPtr infoPtr, Vector localPos, IntPtr localCtrl)
        {
            string name   = ReadString(infoPtr + _infoNameOff);
            int    role   = ReadInt(infoPtr    + _infoRoleOff);
            bool   isDead = ReadBool(infoPtr   + _infoIsDeadOff);

            IntPtr ctrlPtr = ReadPointerChain(infoPtr, _infoObjectOff);
            Vector pos     = ctrlPtr != IntPtr.Zero ? ReadPosition(ctrlPtr) : localPos;

            float dist = (float)Math.Sqrt(
                Math.Pow(pos.X - localPos.X, 2) +
                Math.Pow(pos.Y - localPos.Y, 2));

            int roomId = ReadRoomId(ctrlPtr);

            return new Player
            {
                Name            = string.IsNullOrWhiteSpace(name) ? "???" : name,
                IsImpostor      = role == 1,
                Position        = pos,
                Room            = RoomNames.TryGetValue(roomId, out var rn) ? rn : $"Room {roomId}",
                DistanceToLocal = dist,
                IsLocal         = ctrlPtr == localCtrl,
                IsDead          = isDead,
            };
        }

        private Vector ReadPosition(IntPtr ctrlPtr)
        {
            float x = ReadFloat(ctrlPtr + _ctrlPosX);
            float y = ReadFloat(ctrlPtr + _ctrlPosY);
            return new Vector(x, y);
        }

        private int ReadRoomId(IntPtr ctrlPtr)
        {
            if (ctrlPtr == IntPtr.Zero) return -1;
            IntPtr tracker = ReadPointerChain(ctrlPtr, _roomTrackerOff);
            if (tracker == IntPtr.Zero) return -1;
            return ReadInt(tracker + _roomIdOff);
        }

        // ── Raw memory readers ────────────────────────────────────────────────

        private IntPtr ReadPointerChain(IntPtr baseAddr, int offset)
        {
            byte[] buf = new byte[8];
            if (!ReadProcessMemory(_processHandle, baseAddr + offset, buf, 8, out int read) || read < 8)
                return IntPtr.Zero;
            return (IntPtr)BitConverter.ToInt64(buf, 0);
        }

        private IntPtr ReadPointerChain(long baseAddr, int offset) =>
            ReadPointerChain((IntPtr)baseAddr, offset);

        private int ReadInt(IntPtr addr)
        {
            byte[] buf = new byte[4];
            ReadProcessMemory(_processHandle, addr, buf, 4, out _);
            return BitConverter.ToInt32(buf, 0);
        }

        private float ReadFloat(IntPtr addr)
        {
            byte[] buf = new byte[4];
            ReadProcessMemory(_processHandle, addr, buf, 4, out _);
            return BitConverter.ToSingle(buf, 0);
        }

        private bool ReadBool(IntPtr addr)
        {
            byte[] buf = new byte[1];
            ReadProcessMemory(_processHandle, addr, buf, 1, out _);
            return buf[0] != 0;
        }

        private string ReadString(IntPtr strObjPtr)
        {
            if (strObjPtr == IntPtr.Zero) return string.Empty;

            // IL2CPP System.String layout:
            //   0x00  object header / klass ptr
            //   0x10  int32 length
            //   0x14  char[]  (UTF-16LE)
            byte[] lenBuf = new byte[4];
            if (!ReadProcessMemory(_processHandle, strObjPtr + 0x10, lenBuf, 4, out int r) || r < 4)
                return string.Empty;

            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 64) return string.Empty;   // sanity cap

            byte[] charBuf = new byte[len * 2];
            if (!ReadProcessMemory(_processHandle, strObjPtr + 0x14, charBuf, charBuf.Length, out _))
                return string.Empty;

            return Encoding.Unicode.GetString(charBuf);
        }
    }
}
