using GM3P.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using GM3P.FileSystem;
using InputSimulatorPro;
using InputSimulatorPro.Resources.Natives;
using InputSimulatorPro.Resources;


namespace GM3P.Manager
{
    public interface IModManager
    {
        Task PlayGame(string executablePath, string? inputList, GM3PConfig config);
    }
    public class ModManager : IModManager
    {
        public async Task PlayGame(string executablePath, string? inputList, GM3PConfig config)
        {
            try
            {
                string executableDirectory = Path.GetDirectoryName(executablePath);
                string executableName = Path.GetFileName(executablePath);
                // Implementation for playing the game goes here
                using (var process = new Process())
                {
                    process.StartInfo.WorkingDirectory = executableDirectory;
                    process.StartInfo.FileName = $"{executablePath}";
                    process.StartInfo.Arguments =
                            $"";
                    process.StartInfo.CreateNoWindow = false;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();
                    char[] inputs = ['a'];
                    Console.WriteLine($"Input List: {Path.GetFileName(inputList)}");
                    if (File.Exists(inputList))
                    {
                        string inputContent = File.ReadAllText(inputList);
                        Console.WriteLine(inputContent);
                        char[] a = inputContent.ToCharArray();
                        Array.Resize(ref inputs, a.Length);
                        inputs = a;
                    }
                    if (inputs?.Length>10)
                    {
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            InputSimulator.Keyboard.TextEntry($"{inputs[i]}");
                            Thread.Sleep(33);
                        }
                    }
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
