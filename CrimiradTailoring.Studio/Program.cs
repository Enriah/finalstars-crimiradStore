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
            bool screenshotMode = args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase);

            var gwSettings = GameWindowSettings.Default;
            var nwSettings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1500, 900),
                MinimumClientSize = new Vector2i(1080, 680),
                Title = "Crimirad Cloth Sculpt Editor (OpenGL + ImGui)",
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                NumberOfSamples = 4,
                StartFocused = true
            };

            using var win = new StudioWindow(gwSettings, nwSettings, screenshotMode);
            win.Run();
        }
    }
}
