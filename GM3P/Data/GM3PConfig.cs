namespace GM3P.Data
{
    public class GM3PConfig
    {
        public string? VanillaPath { get; set; }
        public string? OutputPath { get; set; }
        public string? DeltaPatcherPath { get; set; }
        public string? ModToolPath { get; set; }
        public string? G3MToolPath { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? GameEngine { get; set; }
        public int ModAmount { get; set; }
        public int ChapterAmount { get; set; } = 1;
        public bool Combined { get; set; }
        public bool EnableFastCombiner { get; set; } = false;
        public int CombinerTool { get; set; } = 0;

        // Cache settings
        public bool CacheEnabled { get; set; } = true;
        public bool CacheSpritesEnabled { get; set; } = true;
        public int ExportCacheCapMB { get; set; } = 2048;
        public int XDeltaConcurrency { get; set; } = 3;

        // Debug mode for Roslyn scripts.
        public bool DebugMode { get; set; } = false;

        public static GM3PConfig LoadFromEnvironment()
        {
            var config = new GM3PConfig();

            // Load from environment variables
            if (int.TryParse(Environment.GetEnvironmentVariable("GM3P_EXPORT_CACHE_CAP_MB"), out var cap))
                config.ExportCacheCapMB = cap;

            if (int.TryParse(Environment.GetEnvironmentVariable("GM3P_XDELTA_CONCURRENCY"), out var concurrency))
                config.XDeltaConcurrency = Math.Max(1, Math.Min(Environment.ProcessorCount, concurrency));

            config.CacheEnabled = Environment.GetEnvironmentVariable("GM3P_EXPORT_CACHE") != "0";
            config.CacheSpritesEnabled = Environment.GetEnvironmentVariable("GM3P_EXPORT_CACHE_SPRITES") != "0";

            return config;
        }
    }
}