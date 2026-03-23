using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AmongMenu.Models;
using AmongMenu.Utilities;

namespace AmongMenu.Views
{
    /// <summary>
    /// Main overlay window.
    /// Refreshes game data at <see cref="RefreshIntervalMs"/> ms and redraws
    /// the tracer canvas and player list on every tick.
    ///
    /// Debug mode
    /// ──────────
    /// Click the <b>DBG</b> toggle in the title bar to enable debug logging.
    /// When active, a green-on-dark log panel expands at the bottom of the
    /// window showing:
    ///   • Module base address and size
    ///   • Resolved pointer values at each step of the reading chain
    ///   • Signature-scan candidates when a pointer resolves to null
    /// Copy candidate RVAs from the scan output into <c>config/offsets.json</c>
    /// to fix reading after a game patch.
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private const int RefreshIntervalMs = 500;
        private const int DebugPanelHeight  = 150;

        private readonly GameMemoryReader _memReader = new GameMemoryReader();
        private readonly DispatcherTimer  _timer     = new DispatcherTimer();

        public OverlayWindow()
        {
            InitializeComponent();
            _timer.Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs);
            _timer.Tick    += Timer_Tick;
            _timer.Start();
        }

        // ── Timer tick ────────────────────────────────────────────────────────

        private void Timer_Tick(object? sender, EventArgs e)
        {
            GameData data = _memReader.ReadGameData();
            StatusText.Text = data.GameStatus;
            RedrawTracers(data);
            RedrawPlayerList(data);

            if (_memReader.DebugMode)
                UpdateDebugPanel();
        }

        // ── Debug mode toggle ─────────────────────────────────────────────────

        private void DebugToggle_Checked(object sender, RoutedEventArgs e)
        {
            _memReader.DebugMode  = true;
            DebugRow.Height       = new GridLength(DebugPanelHeight);
            DebugToggle.Foreground = new SolidColorBrush(Colors.LimeGreen);
        }

        private void DebugToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _memReader.DebugMode  = false;
            DebugRow.Height       = new GridLength(0);
            DebugText.Text        = string.Empty;
            DebugToggle.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        private void UpdateDebugPanel()
        {
            string log = _memReader.DebugLog;
            if (DebugText.Text == log) return;
            DebugText.Text = log;
            // Auto-scroll to bottom
            DebugScrollViewer.ScrollToEnd();
        }

        // ── Tracer canvas ─────────────────────────────────────────────────────

        private void RedrawTracers(GameData data)
        {
            TracerCanvas.Children.Clear();

            double cx = TracerCanvas.ActualWidth  / 2.0;
            double cy = TracerCanvas.ActualHeight / 2.0;

            // Draw a small white dot at the centre representing the local player
            DrawDot(cx, cy, Colors.White, 5);

            if (!data.IsGameActive || data.LocalPlayer is null)
                return;

            foreach (Player player in data.Players)
            {
                if (player.IsLocal || player.IsDead) continue;

                Color lineColor = ColorHelper.GetTracerColor(player.DistanceToLocal);

                // Map world-space offset to screen pixels.
                // Scale factor: 1 world unit ≈ 15 canvas pixels (tunable).
                const double Scale = 15.0;
                double tx = cx + (player.Position.X - data.LocalPlayer.Position.X) * Scale;
                double ty = cy - (player.Position.Y - data.LocalPlayer.Position.Y) * Scale; // Y is inverted on screen

                // Clamp to canvas bounds
                tx = Math.Clamp(tx, 4, TracerCanvas.ActualWidth  - 4);
                ty = Math.Clamp(ty, 4, TracerCanvas.ActualHeight - 4);

                // Tracer line
                var line = new Line
                {
                    X1              = cx,
                    Y1              = cy,
                    X2              = tx,
                    Y2              = ty,
                    Stroke          = new SolidColorBrush(lineColor),
                    StrokeThickness = player.IsImpostor ? 2.0 : 1.2,
                    Opacity         = player.IsImpostor ? 1.0 : 0.7,
                };
                TracerCanvas.Children.Add(line);

                // Endpoint dot
                Color dotColor = player.IsImpostor ? Colors.OrangeRed : Colors.CornflowerBlue;
                DrawDot(tx, ty, dotColor, player.IsImpostor ? 6 : 5);

                // Name label
                var label = new TextBlock
                {
                    Text       = player.Name,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize   = 10,
                    FontFamily = new FontFamily("Consolas"),
                };
                Canvas.SetLeft(label, tx + 6);
                Canvas.SetTop(label, ty - 8);
                TracerCanvas.Children.Add(label);
            }
        }

        private void DrawDot(double x, double y, Color color, double radius)
        {
            var ellipse = new Ellipse
            {
                Width  = radius * 2,
                Height = radius * 2,
                Fill   = new SolidColorBrush(color),
            };
            Canvas.SetLeft(ellipse, x - radius);
            Canvas.SetTop(ellipse,  y - radius);
            TracerCanvas.Children.Add(ellipse);
        }

        // ── Player list panel ─────────────────────────────────────────────────

        private void RedrawPlayerList(GameData data)
        {
            PlayerListPanel.Children.Clear();

            AddSectionHeader("🔴  IMPOSTORS");
            if (data.Impostors.Count == 0)
            {
                AddPlayerRow("  (none detected)", Colors.Gray);
            }
            else
            {
                foreach (Player p in data.Impostors)
                    AddPlayerRow(FormatPlayer(p), ColorHelper.GetTracerColor(p.DistanceToLocal));
            }

            AddSeparator();
            AddSectionHeader("🔵  CREWMATES");
            if (data.Crewmates.Count == 0)
            {
                AddPlayerRow("  (none detected)", Colors.Gray);
            }
            else
            {
                foreach (Player p in data.Crewmates)
                    AddPlayerRow(FormatPlayer(p), ColorHelper.GetTracerColor(p.DistanceToLocal));
            }

            if (data.LocalPlayer is not null)
            {
                AddSeparator();
                AddSectionHeader("⚪  LOCAL PLAYER");
                AddPlayerRow($"  {data.LocalPlayer.Name}  |  {data.LocalPlayer.Room}", Colors.White);
            }
        }

        private static string FormatPlayer(Player p)
        {
            string dead = p.IsDead ? " ☠" : "";
            return $"  {p.Name,-15} {p.DistanceToLocal,5:F1}u  |  {p.Room}{dead}";
        }

        private void AddSectionHeader(string text)
        {
            PlayerListPanel.Children.Add(new TextBlock
            {
                Text       = text,
                Style      = (Style)FindResource("SectionHeader"),
            });
        }

        private void AddPlayerRow(string text, Color color)
        {
            PlayerListPanel.Children.Add(new TextBlock
            {
                Text      = text,
                Foreground = new SolidColorBrush(color),
                Style     = (Style)FindResource("PlayerRow"),
            });
        }

        private void AddSeparator()
        {
            PlayerListPanel.Children.Add(new Rectangle
            {
                Style = (Style)FindResource("Sep"),
            });
        }

        // ── Window chrome ─────────────────────────────────────────────────────

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
