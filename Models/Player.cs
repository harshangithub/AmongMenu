using System.Windows;

namespace AmongMenu.Models
{
    /// <summary>Represents a single player in the current Among Us game.</summary>
    public class Player
    {
        /// <summary>In-game display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>True when the player has the Impostor role.</summary>
        public bool IsImpostor { get; set; }

        /// <summary>Player position in world-space units.</summary>
        public Vector Position { get; set; }

        /// <summary>Name of the room / map area the player is currently in.</summary>
        public string Room { get; set; } = "Unknown";

        /// <summary>Euclidean distance to the local (controlled) player, updated each tick.</summary>
        public float DistanceToLocal { get; set; }

        /// <summary>True when this entry represents the local (human-controlled) player.</summary>
        public bool IsLocal { get; set; }

        /// <summary>True when the player has been reported as dead.</summary>
        public bool IsDead { get; set; }

        public override string ToString() =>
            $"{Name} | {(IsImpostor ? "Impostor" : "Crewmate")} | Room: {Room} | Dist: {DistanceToLocal:F1}";
    }
}
