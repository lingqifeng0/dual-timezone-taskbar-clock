using System;
using System.IO;
using NativeTaskbarClock;

internal static class StorageTest
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Main()
    {
        string basePath = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string testRoot = Path.GetFullPath(Path.Combine(basePath, "dual-clock-test-" + Guid.NewGuid().ToString("N")));
        Check(testRoot.StartsWith(basePath, StringComparison.OrdinalIgnoreCase), "test directory containment");
        string legacy = Path.Combine(testRoot, "old"); string root = Path.Combine(testRoot, "new");
        try {
            Directory.CreateDirectory(legacy);
            string oldPath = Path.Combine(legacy, "config.json");
            ClockSettings old = new ClockSettings(); old.ChinaLabel = "Old"; old.FirstRun = false; old.Save(oldPath);
            ClockStorage.InitializeConfig(root, new[] { oldPath });
            ClockSettings migrated = ClockSettings.Load(Path.Combine(root, "config.json"));
            Check(migrated.ChinaLabel == "Old" && migrated.FirstRun && migrated.TargetZoneId == "China Standard Time", "migrate legacy config");
            Check(!ClockSettings.Load(oldPath).FirstRun, "keep old config unchanged");
            migrated.ChinaLabel = "Edited"; migrated.Save(Path.Combine(root, "config.json"));
            ClockStorage.InitializeConfig(root, new[] { oldPath });
            Check(ClockSettings.Load(Path.Combine(root, "config.json")).ChinaLabel == "Edited", "do not replace existing config");
            byte[] payload = { 10, 20, 30, 40 };
            string path = ClockStorage.ExtractModule(root, payload);
            Check(File.Exists(path) && ClockStorage.ExtractModule(root, payload) == path, "extract and reuse module");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            bool rejected = false;
            try { ClockStorage.ExtractModule(root, payload); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "reject corrupted cached module");
        } finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
        Console.WriteLine("PASS: migration, embedded module extraction and corrupt-cache rejection");
    }
}
