using GM3P.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using GM3P.FileSystem;

namespace GM3P.Manager
{
    public interface IModManager
    {
        Task PlayGame(string executablePath, string? inputList, GM3PConfig config);
    }
    public class ModManager : IModManager
    {
        private readonly IDirectoryManager _directoryManager;
        public async Task PlayGame(string executablePath, string? inputList, GM3PConfig config)
        {
            try
            {
                string executableDirectory = Path.GetDirectoryName(executablePath);
                string executableName = Path.GetFileName(executablePath);
                // Implementation for playing the game goes here
                using (var process = new Process())
                {
                    if (OperatingSystem.IsWindows())
                    {
                        process.StartInfo.WorkingDirectory = executableDirectory;
                        process.StartInfo.FileName = $"{executablePath}";
                        process.StartInfo.Arguments =
                            $"";
                            //$"start /D \"{Path.GetDirectoryName(executablePath)}\" {Path.GetFileName(executablePath).Replace("\"","")}";
                        Console.WriteLine($"Starting Game: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        process.StartInfo.FileName = "/bin/bash";
                        process.StartInfo.Arguments =
                            $"-c \"{executablePath}\"";
                    }
                    process.StartInfo.CreateNoWindow = false;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();
                    string output = await process.StandardOutput.ReadToEndAsync();
                    Console.WriteLine(output);
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start game: {ex.Message}");
            }
        }



    }
}
