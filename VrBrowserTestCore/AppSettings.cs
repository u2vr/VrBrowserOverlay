using System;
using System.Collections.Generic;

namespace VrBrowserTestCore
{
    public class AppSettings
    {
        public float Size { get; set; } = 0.9f;
        public float Offset { get; set; } = 0.45f;
        public float Curvature { get; set; } = 0.1f;
        public int ColorFormat { get; set; } = 0;

        // 0 = 1280x720, 1 = 1600x900, 2 = 1920x1080, 3 = 2560x1440
        public int ResolutionIndex { get; set; } = 0;
        public int Fps { get; set; } = 30;

        public bool MinimizeOnStart { get; set; } = false;
        public bool MinimizeToTray { get; set; } = false;

        public List<Tab> Tabs { get; set; } = new List<Tab>();

        public (int Width, int Height) GetResolution()
        {
            return ResolutionIndex switch
            {
                0 => (1280, 720),
                1 => (1600, 900),
                2 => (1920, 1080),
                3 => (2560, 1440),
                _ => (1280, 720)
            };
        }
    }

    public class Tab
    {
        public string Url { get; set; } = "";
        public string Css { get; set; } = "";
        public bool AudioEnabled { get; set; } = false;

        public Guid Tag { get; set; } = Guid.NewGuid();
    }
}
