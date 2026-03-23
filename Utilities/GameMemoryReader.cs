using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    /// assembly image.  The offsets below were verified against the latest Steam
    /// build (v2024.6.18) and may need updating after game patches.
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

        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;

        // ── IL2CPP offset constants (Steam v2024.6.18) ────────────────────────
        // These offsets walk the pointer chains inside the Among Us IL2CPP heap.
        // Update them if the game is patched and the chain breaks.

        // GameData class – static field "Instance"
        private const long GAMEDATA_STATIC = 0x01F890B0;   // RVA in GameAssembly.dll
        private const int  GAMEDATA_INSTANCE_OFFSET  = 0xB8;

        // GameData.allPlayers  (Il2CppSystem.Collections.Generic.List<PlayerInfo>)
        private const int ALL_PLAYERS_OFFSET  = 0x24;
        private const int LIST_ITEMS_OFFSET   = 0x10;  // backing array inside the List
        private const int LIST_COUNT_OFFSET   = 0x18;
        private const int ARRAY_FIRST_ELEMENT = 0x20;  // first slot in the array
        private const int ARRAY_ELEMENT_SIZE  = 0x08;  // pointer size (x64)

        // PlayerInfo fields
        private const int INFO_NAME_OFFSET     = 0x10;
        private const int INFO_COLOR_OFFSET    = 0x1C;
        private const int INFO_ROLE_OFFSET     = 0x2C;   // RoleTypes enum (0=Crewmate,1=Impostor,2=Scientist…)
        private const int INFO_ISDEAD_OFFSET   = 0x30;   // bool
        private const int INFO_OBJECT_OFFSET   = 0x08;   // back-pointer to PlayerControl

        // PlayerControl fields
        private const int CTRL_POSITION_X      = 0x78;   // NetTransform → position x (float)
        private const int CTRL_POSITION_Y      = 0x7C;   // NetTransform → position y (float)

        // LocalPlayer static
        private const long LOCAL_PLAYER_STATIC = 0x01F89290;  // RVA in GameAssembly.dll
        private const int  LOCAL_PLAYER_OFFSET = 0xB8;

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

        // ── Private state ─────────────────────────────────────────────────────

        private IntPtr _processHandle = IntPtr.Zero;
        private long   _moduleBase    = 0;
        private int    _lastPid       = 0;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the current game state from memory.
        /// Returns a populated <see cref="GameData"/> on success, or a
        /// placeholder object when the game is not running / cannot be read.
        /// </summary>
        public GameData ReadGameData()
        {
            var result = new GameData();

            if (!EnsureProcess())
            {
                result.GameStatus = "Among Us not running";
                return result;
            }

            try
            {
                IntPtr localCtrl = ReadLocalPlayer();
                if (localCtrl == IntPtr.Zero)
                {
                    result.GameStatus = "Waiting for game to start…";
                    return result;
                }

                var localPos = ReadPosition(localCtrl);

                IntPtr gamDataInst = ReadPointerChain(_moduleBase + GAMEDATA_STATIC, GAMEDATA_INSTANCE_OFFSET);
                if (gamDataInst == IntPtr.Zero)
                {
                    result.GameStatus = "GameData not initialized";
                    return result;
                }

                IntPtr allPlayers = ReadPointerChain(gamDataInst, ALL_PLAYERS_OFFSET);
                if (allPlayers == IntPtr.Zero)
                {
                    result.GameStatus = "No player list found";
                    return result;
                }

                IntPtr itemsArray = ReadPointerChain(allPlayers, LIST_ITEMS_OFFSET);
                int    count      = ReadInt(allPlayers + LIST_COUNT_OFFSET);

                if (count <= 0 || count > 15)
                {
                    result.GameStatus = "Unexpected player count";
                    return result;
                }

                for (int i = 0; i < count; i++)
                {
                    long    elementOffset = ARRAY_FIRST_ELEMENT + (long)i * ARRAY_ELEMENT_SIZE;
                    IntPtr  infoPtr       = ReadPointerChain(itemsArray, (int)elementOffset);
                    if (infoPtr == IntPtr.Zero) continue;

                    Player player = ReadPlayerInfo(infoPtr, localPos, localCtrl);
                    result.Players.Add(player);

                    if (player.IsLocal)
                        result.LocalPlayer = player;
                    else if (player.IsImpostor)
                        result.Impostors.Add(player);
                    else
                        result.Crewmates.Add(player);
                }

                result.IsGameActive = true;
                result.GameStatus   = $"Game active – {result.Players.Count} players";
            }
            catch (Exception ex)
            {
                result.GameStatus = $"Read error: {ex.Message}";
            }

            return result;
        }

        // ── Private helpers ───────────────────────────────────────────────────

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
            _lastPid = pid;
            _processHandle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_processHandle == IntPtr.Zero)
                return false;

            _moduleBase = GetModuleBase(processes[0], "GameAssembly.dll");
            return _moduleBase != 0;
        }

        private static long GetModuleBase(Process proc, string moduleName)
        {
            foreach (ProcessModule mod in proc.Modules)
            {
                if (string.Equals(mod.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                    return (long)mod.BaseAddress;
            }
            return 0;
        }

        private void DisposeHandle()
        {
            if (_processHandle != IntPtr.Zero)
            {
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            _moduleBase = 0;
            _lastPid    = 0;
        }

        private IntPtr ReadLocalPlayer()
        {
            IntPtr ctrlPtr = ReadPointerChain((IntPtr)(_moduleBase + LOCAL_PLAYER_STATIC), LOCAL_PLAYER_OFFSET);
            return ctrlPtr;
        }

        private Player ReadPlayerInfo(IntPtr infoPtr, Vector localPos, IntPtr localCtrl)
        {
            string name   = ReadString(infoPtr + INFO_NAME_OFFSET);
            int    role   = ReadInt(infoPtr    + INFO_ROLE_OFFSET);
            bool   isDead = ReadBool(infoPtr   + INFO_ISDEAD_OFFSET);

            IntPtr ctrlPtr = ReadPointerChain(infoPtr, INFO_OBJECT_OFFSET);
            Vector pos     = ctrlPtr != IntPtr.Zero ? ReadPosition(ctrlPtr) : localPos;

            float dist = (float)Math.Sqrt(
                Math.Pow(pos.X - localPos.X, 2) +
                Math.Pow(pos.Y - localPos.Y, 2));

            int roomId = ReadRoomId(ctrlPtr);

            return new Player
            {
                Name           = string.IsNullOrWhiteSpace(name) ? "???" : name,
                IsImpostor     = role == 1,
                Position       = pos,
                Room           = RoomNames.TryGetValue(roomId, out var rn) ? rn : $"Room {roomId}",
                DistanceToLocal = dist,
                IsLocal        = ctrlPtr == localCtrl,
                IsDead         = isDead,
            };
        }

        private Vector ReadPosition(IntPtr ctrlPtr)
        {
            float x = ReadFloat(ctrlPtr + CTRL_POSITION_X);
            float y = ReadFloat(ctrlPtr + CTRL_POSITION_Y);
            return new Vector(x, y);
        }

        private int ReadRoomId(IntPtr ctrlPtr)
        {
            if (ctrlPtr == IntPtr.Zero) return -1;
            // RoomTracker offset – adjust if game updates change layout
            const int ROOM_TRACKER_OFFSET = 0xA0;
            const int ROOM_ID_OFFSET      = 0x10;
            IntPtr tracker = ReadPointerChain(ctrlPtr, ROOM_TRACKER_OFFSET);
            if (tracker == IntPtr.Zero) return -1;
            return ReadInt(tracker + ROOM_ID_OFFSET);
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
