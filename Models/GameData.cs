using System.Collections.Generic;

namespace AmongMenu.Models
{
    /// <summary>Snapshot of the current Among Us game state read each refresh tick.</summary>
    public class GameData
    {
        /// <summary>All players currently in the lobby / game (alive and dead).</summary>
        public List<Player> Players { get; set; } = new List<Player>();

        /// <summary>Convenience list of impostor players.</summary>
        public List<Player> Impostors { get; set; } = new List<Player>();

        /// <summary>Convenience list of crewmate players.</summary>
        public List<Player> Crewmates { get; set; } = new List<Player>();

        /// <summary>Reference to the local (human-controlled) player.</summary>
        public Player? LocalPlayer { get; set; }

        /// <summary>True while a game round is active (not in lobby / between rounds).</summary>
        public bool IsGameActive { get; set; }

        /// <summary>Human-readable status line shown in the overlay header.</summary>
        public string GameStatus { get; set; } = "Waiting for game…";
    }
}
