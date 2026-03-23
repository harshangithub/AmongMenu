using System.Windows.Media;

namespace AmongMenu.Utilities
{
    /// <summary>
    /// Maps a player distance value to a colour used for tracer lines.
    /// Red   = very close  (distance &lt; 2 units)
    /// Yellow = medium     (2 ≤ distance &lt; 5 units)
    /// Green  = far away   (distance ≥ 5 units)
    /// </summary>
    public static class ColorHelper
    {
        private const float CloseThreshold = 2f;
        private const float MediumThreshold = 5f;

        /// <summary>Returns the tracer <see cref="Color"/> for the supplied distance.</summary>
        public static Color GetTracerColor(float distance)
        {
            if (distance < CloseThreshold)
                return Colors.Red;

            if (distance < MediumThreshold)
                return Colors.Yellow;

            return Colors.LimeGreen;
        }

        /// <summary>Returns a <see cref="SolidColorBrush"/> for the supplied distance.</summary>
        public static SolidColorBrush GetTracerBrush(float distance) =>
            new SolidColorBrush(GetTracerColor(distance));
    }
}
