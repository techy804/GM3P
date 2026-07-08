using GM3P.Data;
using System.Reflection;
using System.Text.Json;

namespace GM3P.Core
{
    public interface IConfigurationService
    {
        GM3PConfig Config { get; }
        void LoadConfiguration(string? configPath = null);
        void SaveConfiguration(string? configPath = null);
        void UpdateConfiguration(Action<GM3PConfig> updateAction);
    }

    public class ConfigurationService : IConfigurationService
    {
        private GM3PConfig _config;
        private readonly string _defaultConfigPath;

        public GM3PConfig Config => _config;

        public ConfigurationService()
        {
            var pwd = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            _defaultConfigPath = Path.Combine(pwd, "gm3p.config.json");
            _config = GM3PConfig.LoadFromEnvironment();

            // Set defaults
            _config.WorkingDirectory = pwd;
            _config.OutputPath = Path.Combine(pwd, "output");

            // Platform-specific defaults
            if (OperatingSystem.IsWindows())
            {
                _config.DeltaPatcherPath = Path.Combine(pwd, "tools", "xdelta3.exe");
                _config.G3MToolPath = Path.Combine(pwd, "tools", "G3MTool", "G3MTool.exe");
                _config.ModToolPath = Path.Combine(pwd, "tools", "UTMTCLI", "UndertaleModCli.exe");
            }
            else if (OperatingSystem.IsLinux())
            {
                _config.DeltaPatcherPath = "xdelta3";
                _config.G3MToolPath = $"dotnet \"{Path.Combine(pwd, "tools", "G3MTool", "G3MTool")}\"";
                _config.ModToolPath = $"dotnet \"{Path.Combine(pwd, "tools", "UTMTCLI", "UndertaleModCli.dll")}\"";
            }
        }

        public void LoadConfiguration(string? configPath = null)
        {
            configPath ??= _defaultConfigPath;

            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var loaded = JsonSerializer.Deserialize<GM3PConfig>(json);
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load configuration: {ex.Message}");
                }
            }
        }

        public void SaveConfiguration(string? configPath = null)
        {
            configPath ??= _defaultConfigPath;

            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save configuration: {ex.Message}");
            }
        }

        public void UpdateConfiguration(Action<GM3PConfig> updateAction)
        {
            updateAction(_config);
        }
    }
}