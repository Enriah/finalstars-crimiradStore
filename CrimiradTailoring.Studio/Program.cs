using System;
using System.Linq;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace CrimiradTailoring.Studio
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool screenshotChar = args.Contains("--screenshot-char", StringComparer.OrdinalIgnoreCase);
            bool screenshotMode = screenshotChar || args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase);

            var gwSettings = new GameWindowSettings
            {
                UpdateFrequency = 0.0
            };
            var nwSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1500, 900),
                MinimumClientSize = new Vector2i(1080, 680),
                Title = "Crimirad Tailoring Studio",
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                NumberOfSamples = 0,
                StartFocused = true
            };

            using var win = new StudioWindow(gwSettings, nwSettings, screenshotMode, screenshotChar);
            win.Run();
        }
    }
}
