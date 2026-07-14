// ModCombiner.cs  — revised for NewObjects merge + import order robustness
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GM3P.Cache;
using GM3P.Data;
using GM3P.FileSystem;
using GM3P.GameMaker;

namespace GM3P.Merging
{
    // Result class to avoid ref parameters in async method
    public class ProcessFileResult
    {
        public int ChangedCount { get; set; }
        public bool CodeChanged { get; set; }
        public bool RoomChanged { get; set; }
        public bool SpriteChanged { get; set; }
    }

    public interface IModCombiner
    {
        Task CompareCombine(GM3PConfig config);
        Task HandleNewObjects(GM3PConfig config);
        Task ImportWithNewObjects(GM3PConfig config);
        List<string> GetModifiedAssets();
    }

    public class ModCombiner : IModCombiner
    {
        private readonly IDirectoryManager _directoryManager;
        private readonly IFileLinker _fileLinker;
        private readonly IHashCache _hashCache;
        private readonly IPngUtils _pngUtils;
        private readonly IAssetHelper _assetHelper;
        private readonly IAssetOrderMerger _assetOrderMerger;
        private readonly IGitService _gitService;
        private readonly IUndertaleModTool _modTool;

        private int _usedAsBase = -1;

        private readonly List<string> _modifiedAssets = new List<string>
        {
            "Asset Name                       Hash (SHA1 in Base64)"
        };

        public ModCombiner(
            IDirectoryManager directoryManager,
            IFileLinker fileLinker,
            IHashCache hashCache,
            IPngUtils pngUtils,
            IAssetHelper assetHelper,
            IAssetOrderMerger assetOrderMerger,
            IGitService gitService,
            IUndertaleModTool modTool)
        {
            _directoryManager = directoryManager;
            _fileLinker = fileLinker;
            _hashCache = hashCache;
            _pngUtils = pngUtils;
            _assetHelper = assetHelper;
            _assetOrderMerger = assetOrderMerger;
            _gitService = gitService;
            _modTool = modTool;
        }

        private class ModChangeInfo
        {
            public int NewObjects { get; set; }
            public int ModifiedSprites { get; set; }
            public int ModifiedRooms { get; set; }
            public int ModifiedCode { get; set; }
            public bool HasAssetOrderChanges { get; set; }

            public bool HasAnyChanges =>
                NewObjects > 0 || ModifiedSprites > 0 || ModifiedCode > 0 || HasAssetOrderChanges;

            // AssetOrder not scored on purpose (structure-only)
            public int TotalScore => NewObjects * 100 + ModifiedSprites * 10 + ModifiedCode * 5;

            public override string ToString()
            {
                return $"{NewObjects} new objects, {ModifiedSprites} sprites, {ModifiedCode} code files"
                       + (HasAssetOrderChanges ? " (+AssetOrder)" : "");
            }
        }

        private ModChangeInfo AnalyzeModChanges(int chapter, int modNumber, GM3PConfig config)
        {
            var info = new ModChangeInfo();

            // LEGACY signal: NewObjects.txt
            string legacyNewObjectsFile = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), modNumber.ToString(), "Objects", "NewObjects.txt");

            if (File.Exists(legacyNewObjectsFile))
            {
                info.NewObjects += File.ReadAllLines(legacyNewObjectsFile)
                    .Count(l => !string.IsNullOrWhiteSpace(l));
            }

            // NEW signal: explicit ObjectDefinitions
            string newObjDefsDir = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), modNumber.ToString(), "Objects", "NewObjects", "ObjectDefinitions");

            if (Directory.Exists(newObjDefsDir))
            {
                info.NewObjects += Directory.GetFiles(newObjDefsDir, "*.txt", SearchOption.TopDirectoryOnly).Length;
            }

            // Modified sprites
            string spritesPath = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), modNumber.ToString(), "Objects", "Sprites");

            if (Directory.Exists(spritesPath))
                info.ModifiedSprites = Directory.GetFiles(spritesPath, "*.png", SearchOption.AllDirectories).Length;

            // Modified code (top-level CodeEntries only; NewObjects code handled separately during import)
            string roomPath = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), modNumber.ToString(), "Objects", "Rooms");

            if (Directory.Exists(roomPath))
                info.ModifiedCode = Directory.GetFiles(roomPath, "*.json", SearchOption.AllDirectories).Length;

            // Modified code (top-level CodeEntries only; NewObjects code handled separately during import)
            string codePath = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), modNumber.ToString(), "Objects", "CodeEntries");

            if (Directory.Exists(codePath))
                info.ModifiedCode = Directory.GetFiles(codePath, "*.gml", SearchOption.AllDirectories).Length;

            // AssetOrder present?
            string modAO = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), modNumber.ToString(), "Objects", "AssetOrder.txt");
            if (File.Exists(modAO))
                info.HasAssetOrderChanges = true;

            return info;
        }

        private int SelectBestBase(Dictionary<int, ModChangeInfo> modsWithChanges, int chapter, GM3PConfig config)
        {
            var scores = modsWithChanges.Select(kvp => new
            {
                ModNumber = kvp.Key,
                Score = kvp.Value.TotalScore
            })
            .OrderByDescending(x => x.Score)
            .ToList();

            Console.WriteLine("  Base selection scores:");
            foreach (var score in scores)
                Console.WriteLine($"    Mod {score.ModNumber - 1}: score {score.Score}");

            return scores.First().ModNumber;
        }

        private static IEnumerable<string> EnumerateSlotFiles(string slotRoot)
        {
            return Directory.Exists(slotRoot)
                ? Directory.EnumerateFiles(slotRoot, "*", SearchOption.AllDirectories)
                : Enumerable.Empty<string>();
        }

        public async Task CompareCombine(GM3PConfig config)
        {
            for (int chapter = 0; chapter < config.ChapterAmount; chapter++)
            {
                Console.WriteLine(chapter == 0 ? "Processing Root Chapter:" : $"Processing Chapter {chapter}:");

                int changedThisChapter = 0;
                bool anyCodeChanged = false;
                bool anyRoomChanged = false;
                bool anySpriteChanged = false;
                bool assetOrderChanged = false;

                var chapterModified = new List<string>();

                string vanillaObjectsPath = _directoryManager.GetXDeltaCombinerPath(
                    config, chapter.ToString(), "0", "Objects");

                if (!Directory.Exists(vanillaObjectsPath))
                {
                    Console.WriteLine($"  WARNING: No vanilla Objects folder for chapter {chapter}");
                    continue;
                }

                // Clear stale merged files
                string mergedObjectsPath = _directoryManager.GetXDeltaCombinerPath(
                    config, chapter.ToString(), "1", "Objects");
                if (Directory.Exists(mergedObjectsPath))
                {
                    try { Directory.Delete(mergedObjectsPath, true); } catch { }
                }
                Directory.CreateDirectory(mergedObjectsPath);

                var vanillaFiles = Directory.GetFiles(vanillaObjectsPath, "*", SearchOption.AllDirectories);
                Console.WriteLine($"  Found {vanillaFiles.Length} vanilla files");

                // Build dictionaries
                var vanillaFileDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var allFileVersions = new Dictionary<string, List<ModFileInfo>>(StringComparer.OrdinalIgnoreCase);
                var allKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Add vanilla
                foreach (var vf in vanillaFiles)
                {
                    string relKey = _assetHelper.NormalizeKey(Path.GetRelativePath(vanillaObjectsPath, vf));
                    vanillaFileDict[relKey] = vf;
                    allKnown.Add(relKey);

                    if (!allFileVersions.TryGetValue(relKey, out var list))
                        allFileVersions[relKey] = list = new List<ModFileInfo>();
                    list.Add(new ModFileInfo { ModNumber = 0, FilePath = vf, ModName = "Vanilla" });
                }

                // Add mod versions
                for (int modNumber = 2; modNumber < (config.ModAmount + 2); modNumber++)
                {
                    string modObjectsPath = _directoryManager.GetXDeltaCombinerPath(
                        config, chapter.ToString(), modNumber.ToString(), "Objects");
                    if (!Directory.Exists(modObjectsPath)) continue;

                    var modFiles = Directory.GetFiles(modObjectsPath, "*", SearchOption.AllDirectories);
                    Console.WriteLine($"  Mod {modNumber - 1} has {modFiles.Length} files");

                    foreach (var mf in modFiles)
                    {
                        string relKey = _assetHelper.NormalizeKey(Path.GetRelativePath(modObjectsPath, mf));
                        allKnown.Add(relKey);

                        if (!allFileVersions.TryGetValue(relKey, out var list))
                            allFileVersions[relKey] = list = new List<ModFileInfo>();
                        list.Add(new ModFileInfo { ModNumber = modNumber, FilePath = mf, ModName = $"Mod {modNumber - 1}" });
                    }
                }

                Console.WriteLine($"Chapter {chapter}: Found {allKnown.Count} unique files across vanilla and {config.ModAmount} mod(s)");

                // Process everything except AssetOrder.txt (handled later)
                foreach (string relKey in allKnown)
                {
                    if (relKey.Equals("assetorder.txt", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var result = await ProcessFile(relKey, allFileVersions[relKey], vanillaFileDict,
                                                   mergedObjectsPath, chapterModified);

                    changedThisChapter += result.ChangedCount;
                    anyCodeChanged |= result.CodeChanged;
                    anyRoomChanged |= result.RoomChanged;
                    anySpriteChanged |= result.SpriteChanged;
                }

                // AssetOrder merge
                _assetOrderMerger.HandleAssetOrderFile(chapter, allFileVersions, vanillaFileDict, config);

                // Detect actual AO change
                var mergedAO = _directoryManager.GetXDeltaCombinerPath(
                    config, chapter.ToString(), "1", "Objects", "AssetOrder.txt");
                if (File.Exists(mergedAO))
                {
                    var vanillaAO = _directoryManager.GetXDeltaCombinerPath(
                        config, chapter.ToString(), "0", "Objects", "AssetOrder.txt");
                    if (!File.Exists(vanillaAO) || !FilesEqual(mergedAO, vanillaAO))
                    {
                        assetOrderChanged = true;
                        changedThisChapter++;
                    }
                }

                // Validate sprites
                ValidateSprites(chapter, config);

                // Save change stamps
                SaveChangeStamp(chapter, changedThisChapter, anyCodeChanged, anySpriteChanged, assetOrderChanged, config);

                // Write modified assets list
                var modListPath = _directoryManager.GetXDeltaCombinerPath(
                    config, chapter.ToString(), "1", "modifiedAssets.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(modListPath)!);
                File.WriteAllLines(modListPath, chapterModified);

                Console.WriteLine($"\nChapter {chapter} Summary:");
                Console.WriteLine($"  Total files processed: {allKnown.Count}");
                Console.WriteLine($"  Files changed: {changedThisChapter}");
                Console.WriteLine($"  Code changes: {(anyCodeChanged ? "Yes" : "No")}");
                Console.WriteLine($"  Room changes: {(anyRoomChanged ? "Yes" : "No")}");
                Console.WriteLine($"  Sprite changes: {(anySpriteChanged ? "Yes" : "No")}");
                Console.WriteLine($"  AssetOrder changes: {(assetOrderChanged ? "Yes" : "No")}");

                if (changedThisChapter == 0)
                    Console.WriteLine("  WARNING: No changes detected! Check if mods were properly dumped.");
            }
        }

        private async Task<ProcessFileResult> ProcessFile(string relKey, List<ModFileInfo> versions,
            Dictionary<string, string> vanillaFileDict, string mergedObjectsPath, List<string> chapterModified)
        {
            var result = new ProcessFileResult();
            var vanillaVersion = versions.FirstOrDefault(v => v.ModNumber == 0);
            var modVersions = versions.Where(v => v.ModNumber > 0).ToList();

            if (modVersions.Count > 0)
                Console.WriteLine($"  File {Path.GetFileName(relKey)}: {modVersions.Count} mod version(s)");

            var different = ComputeDifferences(vanillaVersion, modVersions, relKey);

            if (different.Count == 0)
            {
                // New file (no vanilla) → include all mod versions for merge policy
                if (vanillaVersion == null && modVersions.Count > 0)
                {
                    Console.WriteLine($"    NEW FILE from mods: {relKey}");
                    different = modVersions;
                }
                else
                {
                    return result; // truly unchanged
                }
            }

            string targetPath = Path.Combine(mergedObjectsPath, relKey);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (different.Count == 1)
            {
                File.Copy(different[0].FilePath, targetPath, true);
                Console.WriteLine($"    Using {different[0].ModName}'s version");
            }
            else
            {
                Console.WriteLine($"    Merging {different.Count} versions");
                await MergeMultipleVersions(relKey, different, vanillaVersion, targetPath);
            }

            // Track
            string hash = _hashCache.GetSha1Base64(targetPath);
            _modifiedAssets.Add($"{relKey}        {hash}");
            chapterModified.Add($"{relKey}        {hash}");

            result.ChangedCount = 1;

            string ext = Path.GetExtension(relKey);
            if (relKey.StartsWith("sprites/", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                result.SpriteChanged = true;
            }

            if (relKey.Contains("rooms/", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                result.RoomChanged = true;
            }

            if (relKey.Contains("/codeentries/", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".gml", StringComparison.OrdinalIgnoreCase))
            {
                result.CodeChanged = true;
            }

            return result;
        }

        private List<ModFileInfo> ComputeDifferences(ModFileInfo? vanillaVersion, List<ModFileInfo> modVersions, string relKey)
        {
            var different = new List<ModFileInfo>();

            if (vanillaVersion != null && modVersions.Count > 0)
            {
                string ext = Path.GetExtension(relKey);
                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var mv in modVersions)
                    {
                        bool isDifferent;
                        try
                        {
                            isDifferent = _pngUtils.AreSpritesDifferent(mv.FilePath, vanillaVersion.FilePath);
                        }
                        catch
                        {
                            string vHash = _hashCache.GetSha1Base64(vanillaVersion.FilePath);
                            string mHash = _hashCache.GetSha1Base64(mv.FilePath);
                            isDifferent = !string.Equals(vHash, mHash, StringComparison.Ordinal);
                        }

                        if (isDifferent)
                        {
                            different.Add(mv);
                            Console.WriteLine($"    Sprite different: {Path.GetFileName(mv.FilePath)} from {mv.ModName}");
                        }
                    }
                }
                else
                {
                    string vHash = _hashCache.GetSha1Base64(vanillaVersion.FilePath);
                    foreach (var mv in modVersions)
                    {
                        string mHash = _hashCache.GetSha1Base64(mv.FilePath);
                        if (!string.Equals(vHash, mHash, StringComparison.Ordinal))
                        {
                            different.Add(mv);
                            if (ext.Equals(".gml", StringComparison.OrdinalIgnoreCase))
                                Console.WriteLine($"    Code different: {Path.GetFileName(mv.FilePath)} from {mv.ModName}");
                            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                                Console.WriteLine($"    Room different: {Path.GetFileName(mv.FilePath)} from {mv.ModName}");
                        }
                    }
                }
            }
            else if (vanillaVersion == null && modVersions.Count > 0)
            {
                different = modVersions; // brand-new file
                Console.WriteLine($"    New file from mods: {Path.GetFileName(modVersions[0].FilePath)}");
            }

            return different;
        }

        private async Task MergeMultipleVersions(string relKey, List<ModFileInfo> different,
                                                ModFileInfo? vanillaVersion, string targetPath)
        {
            string lcKey = relKey.Replace('\\', '/').ToLowerInvariant();
            string ext = Path.GetExtension(relKey).ToLowerInvariant();

            // SPECIAL-CASE: Objects/NewObjects/** -> last-mod-wins (deterministic; definitions shouldn't be git-merged)
            if (lcKey.StartsWith("newobjects/") || lcKey.Contains("/newobjects/"))
            {
                var last = different.OrderBy(d => d.ModNumber).Last();
                File.Copy(last.FilePath, targetPath, true);
                Console.WriteLine($"    [NewObjects] Using last mod's version: {last.ModName}");
                return;
            }

            if (ext == ".png")
            {
                var best = SelectBestSprite(different.OrderBy(d => d.ModNumber).ToList(), vanillaVersion);
                File.Copy(best.FilePath, targetPath, true);
                return;
            }

            if (ext == ".ogg" || ext == ".wav" || ext == ".mp3")
            {
                var last = different.OrderBy(d => d.ModNumber).Last();
                File.Copy(last.FilePath, targetPath, true);
                return;
            }

            bool ok = false;
            if (vanillaVersion != null)
                ok = _gitService.PerformGitMerge(vanillaVersion.FilePath, different, targetPath, relKey);

            if (!ok)
            {
                Console.WriteLine($"    WARNING: Git merge failed for {relKey}, using last mod");
                File.Copy(different.OrderBy(d => d.ModNumber).Last().FilePath, targetPath, true);

                var skipped = different.Take(different.Count - 1);
                foreach (var skip in skipped)
                    Console.WriteLine($"      SKIPPED: {skip.ModName}'s changes to {Path.GetFileName(relKey)}");
            }
        }

        private ModFileInfo SelectBestSprite(List<ModFileInfo> sprites, ModFileInfo? vanillaVersion)
        {
            Console.WriteLine($"    Selecting best sprite from {sprites.Count} version(s)");

            // Prefer valid PNGs from later mods
            for (int i = sprites.Count - 1; i >= 0; i--)
            {
                if (_pngUtils.IsValidPNG(sprites[i].FilePath))
                {
                    var info = new FileInfo(sprites[i].FilePath);
                    Console.WriteLine($"      Selected valid PNG from {sprites[i].ModName} ({info.Length} bytes)");
                    return sprites[i];
                }
                Console.WriteLine($"      WARNING: Invalid PNG from {sprites[i].ModName}");
            }

            if (vanillaVersion != null && _pngUtils.IsValidPNG(vanillaVersion.FilePath))
            {
                Console.WriteLine($"      All mod sprites invalid, using vanilla");
                return vanillaVersion;
            }

            Console.WriteLine($"      WARNING: No valid sprites found, using {sprites[0].ModName} anyway");
            return sprites[0];
        }

        private bool FilesEqual(string file1, string file2)
        {
            if (!File.Exists(file1) || !File.Exists(file2)) return false;

            var info1 = new FileInfo(file1);
            var info2 = new FileInfo(file2);
            if (info1.Length != info2.Length) return false;

            using var fs1 = File.OpenRead(file1);
            using var fs2 = File.OpenRead(file2);

            byte[] buffer1 = new byte[4096];
            byte[] buffer2 = new byte[4096];

            while (true)
            {
                int bytes1 = fs1.Read(buffer1, 0, buffer1.Length);
                int bytes2 = fs2.Read(buffer2, 0, buffer2.Length);

                if (bytes1 != bytes2) return false;
                if (bytes1 == 0) return true;

                for (int i = 0; i < bytes1; i++)
                    if (buffer1[i] != buffer2[i]) return false;
            }
        }

        private void ValidateSprites(int chapter, GM3PConfig config)
        {
            Console.WriteLine("\nValidating sprites after merge...");

            string spritesPath = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), "1", "Objects", "Sprites");

            if (!Directory.Exists(spritesPath))
            {
                Console.WriteLine("  No sprites directory found in merged output");
                return;
            }

            int total = 0;
            var invalidSprites = new List<string>();

            foreach (var pngFile in Directory.EnumerateFiles(spritesPath, "*.png", SearchOption.AllDirectories))
            {
                total++;
                if (!_pngUtils.IsValidPNG(pngFile))
                    invalidSprites.Add(Path.GetFileName(pngFile));
            }

            if (total == 0)
            {
                Console.WriteLine("  WARNING: No sprites found in merged output!");
            }
            else if (invalidSprites.Count > 0)
            {
                Console.WriteLine($"  WARNING: {invalidSprites.Count} invalid sprites detected after merge:");
                foreach (var sprite in invalidSprites.Take(10))
                    Console.WriteLine($"    - {sprite}");
                if (invalidSprites.Count > 10)
                    Console.WriteLine($"    ... and {invalidSprites.Count - 10} more");
            }
            else
            {
                Console.WriteLine($"  ✓ All {total} sprites validated successfully");
            }
        }

        private void SaveChangeStamp(int chapter, int changedThisChapter, bool anyCodeChanged,
                                    bool anySpriteChanged, bool assetOrderChanged, GM3PConfig config)
        {
            var stampDir = _directoryManager.GetCachePath(config, "running");
            Directory.CreateDirectory(stampDir);

            File.WriteAllText(
                Path.Combine(stampDir, $"chapter_{chapter}_changes.txt"),
                $"{changedThisChapter}|{(anyCodeChanged?1:0)}|{(anySpriteChanged?1:0)}|{(assetOrderChanged?1:0)}");
        }

        public async Task HandleNewObjects(GM3PConfig config)
        {
            for (int chapter = 0; chapter < config.ChapterAmount; chapter++)
            {
                Console.WriteLine($"Checking for new objects in chapter {chapter}...");

                string vanillaCodePath = _directoryManager.GetXDeltaCombinerPath(
                    config, chapter.ToString(), "0", "Objects", "CodeEntries");

                for (int modNumber = 2; modNumber < (config.ModAmount + 2); modNumber++)
                {
                    string modCodePath = _directoryManager.GetXDeltaCombinerPath(
                        config, chapter.ToString(), modNumber.ToString(), "Objects", "CodeEntries");

                    var newObjects = _assetHelper.FindNewObjects(vanillaCodePath, modCodePath);

                    if (newObjects.Count > 0)
                    {
                        Console.WriteLine($"Mod {modNumber - 1} adds {newObjects.Count} new objects: {string.Join(", ", newObjects)}");

                        string newObjectsFile = _directoryManager.GetXDeltaCombinerPath(
                            config, chapter.ToString(), modNumber.ToString(), "Objects", "NewObjects.txt");
                        File.WriteAllLines(newObjectsFile, newObjects);
                    }
                }
            }

            await Task.CompletedTask;
        }

        public async Task ImportWithNewObjects(GM3PConfig config)
        {
            for (int chapter = 0; chapter < config.ChapterAmount; chapter++)
            {
                _usedAsBase = -1;
                Console.WriteLine($"Processing Chapter {chapter}...");

                string workingDataWin = _directoryManager.GetXDeltaCombinerPath(
                    config, chapter.ToString(), "1", "data.win");

                // Identify mods that actually changed anything
                var modsWithChanges = new Dictionary<int, ModChangeInfo>();
                for (int modNumber = 2; modNumber < (config.ModAmount + 2); modNumber++)
                {
                    var changeInfo = AnalyzeModChanges(chapter, modNumber, config);
                    if (changeInfo.HasAnyChanges)
                    {
                        modsWithChanges[modNumber] = changeInfo;
                        Console.WriteLine($"  Mod {modNumber - 1}: {changeInfo}");
                    }
                }

                // Choose base
                if (modsWithChanges.Count == 0)
                {
                    Console.WriteLine("No changes detected, using vanilla");
                    _fileLinker.LinkOrCopy(
                        _directoryManager.GetXDeltaCombinerPath(config, chapter.ToString(), "0", "data.win"),
                        workingDataWin);
                }
                else if (modsWithChanges.Count == 1)
                {
                    var singleMod = modsWithChanges.First();
                    Console.WriteLine($"  Only Mod {singleMod.Key - 1} has changes, using it directly");
                    _fileLinker.LinkOrCopy(
                        _directoryManager.GetXDeltaCombinerPath(config, chapter.ToString(), singleMod.Key.ToString(), "data.win"),
                        workingDataWin);
                }
                else
                {
                    var bestBase = SelectBestBase(modsWithChanges, chapter, config);
                    Console.WriteLine($"  Using Mod {bestBase - 1} as base (most comprehensive)");
                    _fileLinker.LinkOrCopy(
                        _directoryManager.GetXDeltaCombinerPath(config, chapter.ToString(), bestBase.ToString(), "data.win"),
                        workingDataWin);
                    _usedAsBase = bestBase;
                }

                await ImportDifferences(chapter, workingDataWin, modsWithChanges, config);
            }
        }

        private async Task ImportDifferences(int chapter, string workingDataWin,
            Dictionary<int, ModChangeInfo> modsWithChanges, GM3PConfig config)
        {
            if (_usedAsBase > 0 && modsWithChanges.ContainsKey(_usedAsBase))
                Console.WriteLine($"  Note: Mod {_usedAsBase - 1}'s changes already in base data.win");

            var scripts = new List<string>();

            string mergedObjects = _directoryManager.GetXDeltaCombinerPath(
                config, chapter.ToString(), "1", "Objects");

            // Script presence probing (robust across forks)
            string scriptsRoot = Path.Combine(config.WorkingDirectory, "tools", "UTMTCLI", "Scripts");
            bool hasImportGraphics = File.Exists(Path.Combine(scriptsRoot, "ImportGraphics.csx"));
            bool hasImportNewObjects = File.Exists(Path.Combine(scriptsRoot, "ImportNewObjects.csx"));
            bool hasImportRooms = File.Exists(Path.Combine(scriptsRoot, "ImportRoomsBulk.csx"));
            bool hasImportGml = File.Exists(Path.Combine(scriptsRoot, "ImportGML.csx"));
            bool hasImportAssetOrder = File.Exists(Path.Combine(scriptsRoot, "ImportAssetOrder.csx"));

            bool hasSprites = Directory.Exists(Path.Combine(mergedObjects, "Sprites")) &&
                              Directory.GetFiles(Path.Combine(mergedObjects, "Sprites"), "*.png", SearchOption.AllDirectories).Any();

            bool hasRooms = Directory.Exists(Path.Combine(mergedObjects, "Rooms")) &&
                           Directory.GetFiles(Path.Combine(mergedObjects, "Rooms"), "*.json", SearchOption.AllDirectories).Any();

            bool hasCode = Directory.Exists(Path.Combine(mergedObjects, "CodeEntries")) &&
                           Directory.GetFiles(Path.Combine(mergedObjects, "CodeEntries"), "*.gml", SearchOption.AllDirectories).Any();

            bool hasAssetOrder = File.Exists(Path.Combine(mergedObjects, "AssetOrder.txt"));

            string newObjDefsDir = Path.Combine(mergedObjects, "NewObjects", "ObjectDefinitions");
            string newObjCodeDir = Path.Combine(mergedObjects, "NewObjects", "CodeEntries");
            bool hasNewObjDefs = Directory.Exists(newObjDefsDir) &&
                                 Directory.GetFiles(newObjDefsDir, "*.txt", SearchOption.TopDirectoryOnly).Any();
            bool hasNewObjCode = Directory.Exists(newObjCodeDir) &&
                                 Directory.GetFiles(newObjCodeDir, "*.gml", SearchOption.TopDirectoryOnly).Any();

            // 1) Sprites first
            if (hasSprites)
            {
                if (hasImportGraphics) scripts.Add("ImportGraphics.csx");
                else Console.WriteLine("  WARNING: No ImportGraphics script found; skipping sprite import.");

                var count = Directory.GetFiles(Path.Combine(mergedObjects, "Sprites"), "*.png", SearchOption.AllDirectories).Length;
                Console.WriteLine($"  Importing {count} merged sprites");
            }

            if (hasRooms)
            {
                if (hasImportRooms) scripts.Add("ImportRoomsBulk.csx");
                else Console.WriteLine("  WARNING: No ImportRoomsBulk script found; skipping sprite import.");

                var count = Directory.GetFiles(Path.Combine(mergedObjects, "Rooms"), "*.json", SearchOption.AllDirectories).Length;
                Console.WriteLine($"  Importing {count} merged rooms");
            }

            if (hasCode || hasNewObjCode)
            {
                // ImportGML now works properly with case fixes
                await _modTool.RunScript(workingDataWin, "ImportGML.csx", config);
                Console.WriteLine($"  Imported code using fixed ImportGML");
            }

            // 4) Asset order last
            if (hasAssetOrder)
            {
                if (hasImportAssetOrder)
                {
                    scripts.Add("ImportAssetOrder.csx");
                    Console.WriteLine($"  Importing merged AssetOrder.txt");
                }
                else
                {
                    Console.WriteLine("  WARNING: ImportAssetOrder.csx not found; AssetOrder changes will not be applied.");
                }
            }

            if (scripts.Count > 0)
                await _modTool.RunImportScripts(workingDataWin, scripts.ToArray(), config);
        }

        public List<string> GetModifiedAssets() => _modifiedAssets;
    }
}
