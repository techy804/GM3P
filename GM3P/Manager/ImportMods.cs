using GM3P.Data;
using System;
using System.Collections.Generic;
using System.Text;
using GM3P.FileSystem;

namespace GM3P.Manager
{
    public interface IInstall
    {
        Task InstallMods(string patchFile, string game, GM3PConfig config);
        Task InstallInstance(string modName, string gamePath, string game, string version, GM3PConfig config);
    }
    // This class is responsible for importing mods into the mod manager. It is called "Install" to avoid confusion with the "ExecuteImport()" Task and similarly named varibles and tasks in "UndertaleModTool.cs" and "GM3POrchestrator.cs", which runs the import scripts on legacy UTMT.
    public class Install: IInstall
    {
        private readonly IDirectoryManager _directoryManager;
        public Install(IDirectoryManager directoryManager)
        {
            _directoryManager = directoryManager;
        }
        public async Task InstallMods(string patchFile, string game, GM3PConfig config)
        {
            // Implementation for importing mods goes here
        }
        public async Task InstallInstance(string modName, string gamePath, string game, string version, GM3PConfig config)
        {
            // Implementation for importing vanilla files goes here
            try
            {
                _directoryManager.CopyDirectory(gamePath, Path.Combine(config.OutputPath,"DeltamodLite","instances",modName,game,version,game) , true);
            }
            catch (Exception ex) {
                Console.WriteLine($"Error copying vanilla files for mod {modName}: {ex.Message}");

            }
        }
    }
}
