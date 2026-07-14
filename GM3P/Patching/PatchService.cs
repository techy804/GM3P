using System.Diagnostics;
using GM3P.Data;
using GM3P.FileSystem;

namespace GM3P.Patching
{
    public interface IPatchService
    {
        Task ApplyPatches(string[] patchPaths, GM3PConfig config);
        Task ApplyPatch(string sourceFile, string patchFile, string targetFile, GM3PConfig config);
        Task CreatePatch(string originalFile, string modifiedFile, string patchFile, GM3PConfig config);
    }

    public class PatchService : IPatchService
    {
        private readonly IDirectoryManager _directoryManager;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly object _modNumbersCacheLock = new object();

        public PatchService(IDirectoryManager directoryManager)
        {
            _directoryManager = directoryManager;
            var maxConcurrency = Environment.ProcessorCount;
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrency);
        }

        public async Task ApplyPatches(string[] patchPaths, GM3PConfig config)
        {
            var tasks = new List<Task>();

            for (int chapter = 0; chapter < config.ChapterAmount; chapter++)
            {
                string chapterMods = chapter < patchPaths.Length ? patchPaths[chapter] : "";
                if (string.IsNullOrEmpty(chapterMods))
                    continue;

                string[] parts = chapterMods.Split(',');
                var actualPatches = parts.Skip(0).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                Console.WriteLine(actualPatches);
                for (int i = 0; i < actualPatches.Length && i < config.ModAmount; i++)
                {
                    int modNumber = i + 2;
                    string patchFile = actualPatches[i].Trim();
                    if (!File.Exists(patchFile))
                    {
                        Console.WriteLine($"WARNING: Patch file not found: {patchFile}");
                        continue;
                    }

                    tasks.Add(ApplyPatchAsync(chapter, modNumber, patchFile, config));
                }
            }

            await Task.WhenAll(tasks);
        }

        private async Task ApplyPatchAsync(int chapter, int modNumber, string patchFile, GM3PConfig config)
        {
            await _concurrencySemaphore.WaitAsync();
            try
            {
                string dataPath = _directoryManager.GetXDeltaCombinerPath(
                    config,
                    chapter.ToString(),
                    modNumber.ToString(),
                    "data.win");

                string extension = Path.GetExtension(patchFile).ToLower();

                switch (extension)
                {
                    case ".csx":
                        await ApplyScriptPatch(dataPath, patchFile, config);
                        break;

                    case ".win":
                        File.Copy(patchFile, dataPath, overwrite: true);
                        Console.WriteLine($"Copied: {patchFile} to {dataPath}");
                        break;

                    case ".xdelta":
                    case ".vcdiff":
                        await ApplyXDeltaPatch(dataPath, patchFile, config);
                        break;
                    case ".g3mpatch":
                        await ApplyG3MPatch(dataPath, patchFile, config);
                        break;
                    default:
                        Console.WriteLine($"Unknown patch format: {extension}");
                        break;
                }

                Console.WriteLine($"Patched: {patchFile}");
            }
            finally
            {
                _concurrencySemaphore.Release();
            }
        }

        private async Task ApplyScriptPatch(string dataPath, string scriptPath, GM3PConfig config)
        {
            string tmpPath = Path.ChangeExtension(dataPath, ".tmp.win");

            using (var process = new Process())
            {
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = config.ModToolPath;
                    process.StartInfo.Arguments =
                        $"load \"{dataPath}\" --verbose --output \"{tmpPath}\" --scripts \"{scriptPath}\"";
                }
                else if (OperatingSystem.IsLinux())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments =
                        $"-c \"{config.ModToolPath} load '{dataPath}' --verbose --output '{tmpPath}' --scripts '{scriptPath}'\"";
                }

                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine(output);

                await process.WaitForExitAsync();
            }

            // Atomically replace after the tool is done
            if (File.Exists(tmpPath))
            {
                try { File.Delete(dataPath); } catch { }
                File.Move(tmpPath, dataPath);
            }
        }
        private async Task ApplyG3MPatch(string dataPath, string patchPath, GM3PConfig config)
        {
            string tmpPath = Path.ChangeExtension(dataPath, ".tmp.win");
            using (var process = new Process())
            {
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = config.G3MToolPath;
                    process.StartInfo.Arguments =
                        $"patch apply \"{dataPath}\" \"{patchPath}\" \"{tmpPath}\" --xdelta-fallback";
                }
                else if (OperatingSystem.IsLinux())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments =
                        $"-c \"{config.G3MToolPath} patch apply '{dataPath}' '{patchPath}' '{tmpPath}' --xdelta-fallback\"";
                }
                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine(output);
                await process.WaitForExitAsync();
            }
            // Atomically replace after the tool is done
            if (File.Exists(tmpPath))
            {
                try { File.Delete(dataPath); } catch { }
                File.Move(tmpPath, dataPath);
            }
        }
        private async Task ApplyXDeltaPatch(string dataPath, string patchPath, GM3PConfig config)
        {
            string tmpPath = Path.ChangeExtension(dataPath, ".tmp.win");

            lock (_modNumbersCacheLock)
            {
                var cacheFile = _directoryManager.GetCachePath(config, "modNumbersCache.txt");
                File.WriteAllText(cacheFile, Path.GetFileNameWithoutExtension(dataPath));
            }

            using (var process = new Process())
            {
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = config.DeltaPatcherPath;
                    process.StartInfo.Arguments =
                        $"-v -d -f -s \"{dataPath}\" \"{patchPath}\" \"{tmpPath}\"";
                }
                else if (OperatingSystem.IsLinux())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments =
                        $"-c \"{config.DeltaPatcherPath} -v -d -f -s '{dataPath}' '{patchPath}' '{tmpPath}'\"";
                }

                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine(output);

                await process.WaitForExitAsync();
            }

            if (File.Exists(tmpPath))
            {
                File.Delete(dataPath);
                File.Move(tmpPath, dataPath);
            }
        }

        public async Task ApplyPatch(string sourceFile, string patchFile, string targetFile, GM3PConfig config)
        {
            using (var process = new Process())
            {
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = config.DeltaPatcherPath;
                    process.StartInfo.Arguments =
                        $"-v -d -f -s \"{sourceFile}\" \"{patchFile}\" \"{targetFile}\"";
                }
                else if (OperatingSystem.IsLinux())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments =
                        $"-c \"{config.DeltaPatcherPath} -v -d -f -s '{sourceFile}' '{patchFile}' '{targetFile}'\"";
                }

                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine(output);
                await process.WaitForExitAsync();
            }
        }

        public async Task CreatePatch(string originalFile, string modifiedFile, string patchFile, GM3PConfig config)
        {
            // First xDelta3
            using (var process = new Process())
            {
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = config.DeltaPatcherPath;
                    process.StartInfo.Arguments =
                        $"-v -e -f -s \"{originalFile}\" \"{modifiedFile}\" \"{patchFile}.xdelta\"";
                }
                else if (OperatingSystem.IsLinux())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments =
                        $"-c \"{config.DeltaPatcherPath} -v -e -f -s '{originalFile}' '{modifiedFile}' '{patchFile}.xdelta'\"";
                }

                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine(output);
                await process.WaitForExitAsync();
            }
            // Then G3MTool
            using (var process = new Process())
            {
                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = config.G3MToolPath;
                    process.StartInfo.Arguments =
                        $"patch create \"{originalFile}\" \"{modifiedFile}\" \"{patchFile}.g3mpatch\" --xdelta-fallback";
                }
                else if (OperatingSystem.IsLinux())
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.Arguments =
                        $"-c \"{config.G3MToolPath} patch apply '{originalFile}' '{modifiedFile}' '{patchFile}.g3mpatch' --xdelta-fallback\"";
                }
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                Console.WriteLine(output);
                await process.WaitForExitAsync();
            }
        }
    }
}