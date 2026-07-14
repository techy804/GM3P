using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using GM3P.Data;

namespace GM3P.GameMaker
{
    public record ScriptResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Succeeded => ExitCode == 0; // allow non-fatal warnings on stderr
    }

    public interface IUndertaleModTool
    {
        Task RunExportScripts(string dataWin, bool isVanilla, int modNumber, GM3PConfig config);
        Task RunImportScripts(string dataWin, string[] scriptNames, GM3PConfig config);

        // New non-breaking additions:
        Task<ScriptResult> RunImportScriptsAndCapture(string dataWin, string[] scriptNames, GM3PConfig config);
        Task RunScript(string dataWin, string scriptName, GM3PConfig config);
        Task<ScriptResult> RunScriptAndCapture(string dataWin, string scriptName, GM3PConfig config);
    }

    public class UndertaleModTool : IUndertaleModTool
    {
        // These two helpers check if sprite samples need refreshing based on data.win metadata.
        // This is a heuristic to avoid expensive re-exports.
        // This is not foolproof but works in practice.
        // And it should make the whole process faster when enabled.
        // Note: This only checks data.win size and mtime and content hashes.
        private static string ComputeFileSha1(string path)
        {
            using (var fs = System.IO.File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var h = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool NeedsSpriteSampleRefresh(string vanillaSlotRoot)
        {
            string dataWin = System.IO.Path.Combine(vanillaSlotRoot, "data.win");
            if (!System.IO.File.Exists(dataWin)) return false;

            string chapterDir = System.IO.Directory.GetParent(vanillaSlotRoot).FullName;      // .../xDeltaCombiner/<chapter>
            string xDeltaRoot = System.IO.Directory.GetParent(chapterDir).FullName;           // .../xDeltaCombiner
            string outputRoot = System.IO.Directory.GetParent(xDeltaRoot).FullName;           // .../output

            string sha1 = ComputeFileSha1(dataWin);
            string cacheDir = System.IO.Path.Combine(outputRoot, "Cache", "vanilla", sha1);
            string jsonPath = System.IO.Path.Combine(cacheDir, "_vanilla_sprite_ff_hash.json");
            string shaPath  = System.IO.Path.Combine(cacheDir, "_vanilla_datawin.sha1");

            // refresh if cache for this SHA doesn't exist or is incomplete
            return !(System.IO.File.Exists(jsonPath) && System.IO.File.Exists(shaPath));
        }

        public async Task RunExportScripts(string dataWin, bool isVanilla, int modNumber, GM3PConfig config)
        {
            if (!File.Exists(dataWin))
            {
                Console.WriteLine($"ERROR: data.win not found at {dataWin}");
                return;
            }

            // IMPORTANT: run UTMTCLI from the slot root so all exports land under that slot.
            var slotRoot = Path.GetDirectoryName(dataWin)!;

            using (var modToolProc = new Process())
            {
                string scriptsToRun;
                if (isVanilla)
                {
                    scriptsToRun =
                        " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAllTexturesGrouped.csx\"" +
                        " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAllCode.csx\"" +
                        " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAllRoomsWithCC.csx\"" +
                        " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAssetOrder.csx\"";

                    // Sprite-sample cache refresh (global cache under output/Cache/vanilla/<sha1>)
                    if (NeedsSpriteSampleRefresh(slotRoot))
                        scriptsToRun += " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportSpriteSamples.csx\"";
                }
                else
                {
                    // Prefer fast path if present
                    var modifiedOnly = Path.Combine(config.WorkingDirectory, "tools", "UTMTCLI", "Scripts", "ExportModifiedOnly.csx");
                    if (File.Exists(modifiedOnly) && config.EnableFastCombiner)
                    {
                        scriptsToRun =
                            " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportModifiedOnly.csx\"";
                    }
                    else
                    {
                        scriptsToRun =
                            " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAllTexturesGrouped.csx\"" +
                            " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAllCode.csx\"" +
                            " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAllRoomsWithCC.csx\"" +
                            " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportAssetOrder.csx\"" +
                            " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/ExportNewObjects.csx\"";
                    }
                }

                if (OperatingSystem.IsWindows())
                {
                    modToolProc.StartInfo.FileName = config.ModToolPath;
                    modToolProc.StartInfo.Arguments =
                        "load \"" + dataWin + "\" --verbose --output \"" + dataWin + "\"" + scriptsToRun;
                }
                else
                {
                    modToolProc.StartInfo.FileName = "/bin/bash";
                    modToolProc.StartInfo.Arguments =
                        "-c \"" + config.ModToolPath + " load '" + dataWin + "' --verbose --output '" + dataWin + "'" +
                        scriptsToRun.Replace("\"", "'") + "\"";
                }

                modToolProc.StartInfo.CreateNoWindow = false;
                modToolProc.StartInfo.UseShellExecute = false;
                modToolProc.StartInfo.RedirectStandardOutput = true;
                modToolProc.StartInfo.RedirectStandardError = true;

                // CRITICAL: run inside the slot so Export* scripts write into that slot's tree.
                modToolProc.StartInfo.WorkingDirectory = slotRoot;

                modToolProc.Start();

                var outputTask = modToolProc.StandardOutput.ReadToEndAsync();
                var errorTask  = modToolProc.StandardError.ReadToEndAsync();

                await Task.WhenAll(outputTask, errorTask);
                await modToolProc.WaitForExitAsync();

                var output = await outputTask;
                var error  = await errorTask;

                if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output);
                if (!string.IsNullOrWhiteSpace(error))  Console.WriteLine($"      Export warnings: {error}");
            }
        }


        public async Task RunImportScripts(string dataWin, string[] scriptNames, GM3PConfig config)
        {
            var _ = await RunImportScriptsInternal(dataWin, scriptNames, config, capture: false);
        }

        public async Task<ScriptResult> RunImportScriptsAndCapture(string dataWin, string[] scriptNames, GM3PConfig config)
        {
            return await RunImportScriptsInternal(dataWin, scriptNames, config, capture: true);
        }


        public async Task RunScript(string dataWin, string scriptName, GM3PConfig config)
        {
            await RunImportScripts(dataWin, new[] { scriptName }, config);
        }

        public async Task<ScriptResult> RunScriptAndCapture(string dataWin, string scriptName, GM3PConfig config)
        {
            return await RunImportScriptsAndCapture(dataWin, new[] { scriptName }, config);
        }

        // ------ Internal runner (shared) ------
        private async Task<ScriptResult> RunImportScriptsInternal(string dataWin, string[] scriptNames, GM3PConfig config, bool capture)
        {
            if (!File.Exists(dataWin))
            {
                Console.WriteLine($"ERROR: data.win not found at {dataWin}");
                return new ScriptResult(1, "", "data.win not found");
            }

            var workingDir = Path.GetDirectoryName(dataWin)!;
            var originalDir = Directory.GetCurrentDirectory();

            try
            {
                Directory.SetCurrentDirectory(workingDir);

                using (var proc = new Process())
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var args = "load \"" + dataWin + "\" --verbose --output \"" + dataWin + "\"";
                        foreach (var script in scriptNames)
                            args += " --scripts \"" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/" + script + "\"";

                        proc.StartInfo.FileName = config.ModToolPath;
                        proc.StartInfo.Arguments = args;
                    }
                    else
                    {
                        var args = config.ModToolPath + " load '" + dataWin + "' --verbose --output '" + dataWin + "'";
                        foreach (var script in scriptNames)
                            args += " --scripts '" + config.WorkingDirectory + "/tools/UTMTCLI/Scripts/" + script + "'";

                        proc.StartInfo.FileName = "/bin/bash";
                        proc.StartInfo.Arguments = "-c \"" + args + "\"";
                    }

                    proc.StartInfo.CreateNoWindow = false;
                    proc.StartInfo.UseShellExecute = false;
                    proc.StartInfo.RedirectStandardOutput = true;
                    proc.StartInfo.RedirectStandardError = true;
                    proc.StartInfo.WorkingDirectory = workingDir;

                    proc.Start();

                    var outputTask = proc.StandardOutput.ReadToEndAsync();
                    var errorTask = proc.StandardError.ReadToEndAsync();

                    await Task.WhenAll(outputTask, errorTask);
                    await proc.WaitForExitAsync();

                    var output = await outputTask;
                    var error = await errorTask;

                    if (!capture)
                    {
                        if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
                        if (!string.IsNullOrEmpty(error)) Console.WriteLine($"STDERR: {error}");
                        if (proc.ExitCode != 0) Console.WriteLine($"WARNING: UndertaleModTool exited with code {proc.ExitCode}");
                    }

                    return new ScriptResult(proc.ExitCode, output ?? "", error ?? "");
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDir);
            }
        }
    }
}
