using System.Diagnostics;
using UberDisk.Core.Drives;
using UberDisk.Core.Duplicates;
using UberDisk.Core.Model;
using UberDisk.Core.Scanning;

namespace UberDisk.Core.Demo;

/// <summary>
/// Made-up drives for screenshots and demos (<c>UberDisk.exe --demo</c>): plausible folders and files with invented
/// names, generated in memory from fixed seeds, so every run looks the same. Nothing is read from the real disks.
/// </summary>
public static class DemoDisk
{
    private const long MB = 1024L * 1024, GB = MB * 1024;

    private static readonly Lazy<Data> _data = new(Build);

    /// <summary>The demo drive list, used instead of the PC's drives.</summary>
    public static IReadOnlyList<DriveMeta> Drives => _data.Value.Drives;

    /// <summary>Lists the demo folders. Each folder takes a little time, so a scan animates like a real one.</summary>
    public static IDirectoryReader Reader => _data.Value.Reader;

    /// <summary>
    /// Duplicate groups for a demo tree: files with the same name and size (the demo planted a few on purpose).
    /// The real finder would read the files' content, which don't exist.
    /// </summary>
    public static DuplicateResult FindDuplicates(DirNode root, long minSize)
    {
        var files = new List<FileNode>();
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var c in d.Children ?? Array.Empty<Node>())
            {
                if (c is DirNode sub) stack.Push(sub);
                else if (c is FileNode { IsAggregate: false } f && f.Size >= minSize) files.Add(f);
            }
        }
        var groups = files
            .GroupBy(f => (f.Name.ToLowerInvariant(), f.Size))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.First().Name, g.Key.Size, g.Key.Size,
                g.Select(f => f.FullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(g => g.Wasted)
            .ToList();
        for (int i = 0; i < groups.Count; i++) groups[i].Index = i;
        return new DuplicateResult(root.FullPath, minSize, groups);
    }

    // ======================= The drives =======================

    private sealed record Data(IReadOnlyList<DriveMeta> Drives, IDirectoryReader Reader);

    private static Data Build()
    {
        var drives = new List<DriveMeta>();
        var folders = new Dictionary<string, RawEntry[]>(StringComparer.OrdinalIgnoreCase);
        var delay = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void Add(string letter, string label, MediaKind kind, double totalGb, double unseenGb, double scanSeconds, VDir root)
        {
            string rootPath = letter + "\\";
            int count = Index(root, rootPath, folders);
            long used = root.Bytes + (long)(unseenGb * GB);
            long total = (long)(totalGb * GB);
            drives.Add(new DriveMeta(rootPath, letter, label, kind, total, total - used, true));
            // Spread the scan time over the folders, for the 8 threads the demo scans with.
            delay[letter] = (long)(scanSeconds * Stopwatch.Frequency * 8 / Math.Max(1, count));
        }

        Add("C:", "Windows", MediaKind.NvmeSsd, 953.9, 9.2, 4.0, DriveC());
        Add("D:", "Games", MediaKind.Ssd, 1863, 0, 3.0, DriveD());
        Add("E:", "Backup", MediaKind.Hdd, 3726, 0, 4.0, DriveE());
        Add("F:", "USB stick", MediaKind.Removable, 57.6, 0, 1.0, DriveF());
        return new Data(drives, new DemoReader(folders, delay));
    }

    private static VDir DriveC() => Dir("C:",
        Fill("Windows", 31, Kind.System, 4),
        Dir("Program Files",
            Fill("Photon Studio", 7.4, Kind.App),
            Fill("Arcwave Audio", 11.2, Kind.App),
            Fill("Spectra Video Editor", 9.8, Kind.App),
            Fill("Northwind Office", 4.6, Kind.App),
            Fill("Common Files", 2.9, Kind.App),
            Fill("Kestrel Browser", 1.3, Kind.App),
            Fill("Graphics Driver", 3.1, Kind.App),
            Fill("WindowsApps", 14.5, Kind.App)),
        Dir("Program Files (x86)",
            Fill("GameLauncher", 4.2, Kind.App),
            Fill("Legacy Tools", 1.8, Kind.App),
            Fill("Common Files", 0.9, Kind.App),
            Fill("Printer Suite", 0.6, Kind.App)),
        Dir("ProgramData",
            Fill("Package Cache", 6.8, Kind.Cache),
            Fill("GameLauncher", 9.5, Kind.Cache),
            Fill("Antivirus", 2.1, Kind.App),
            Fill("Microsoft", 5.2, Kind.System, 3)),
        Dir("Users",
            Dir("demo",
                Dir("Videos",
                    Fill("Iceland 2025", 42, Kind.Video).With(F("DJI_0042.MP4", 3.8)),
                    Fill("Wedding", 36, Kind.Video),
                    Fill("Drone footage", 28, Kind.Video),
                    Fill("Screen recordings", 17, Kind.Video),
                    Fill("Birthday 2024", 6, Kind.Video),
                    F("Family movie night.mkv", 7.9)),
                Fill("Pictures", 64, Kind.Photo, 3),
                Fill("Music", 21, Kind.Music, 2),
                Fill("Documents", 14, Kind.Docs).With(Dir("ISOs", F("ubuntu-24.04-desktop-amd64.iso", 5.8))),
                Fill("Downloads", 12, Kind.Downloads, 1).With(
                    F("ubuntu-24.04-desktop-amd64.iso", 5.8),
                    F("Windows11_x64.iso", 6.3),
                    F("DJI_0042.MP4", 3.8),
                    F("Family movie night.mkv", 7.9),
                    F("StarfallCourier_Setup.exe", 1.2)),
                Fill("Projects", 26, Kind.Code, 4),
                Dir("AppData",
                    Dir("Local",
                        Fill("Temp", 9.4, Kind.Cache),
                        Fill("Packages", 7.7, Kind.App),
                        Fill("Kestrel Browser", 3.2, Kind.Cache),
                        Fill("GameLauncher", 1.1, Kind.Cache),
                        Dir("WSL", F("ext4.vhdx", 38)),
                        Fill("Spectra Video Editor", 12, Kind.Cache),
                        Fill("Programs", 4.4, Kind.App)),
                    Dir("Roaming",
                        Fill("Arcwave Audio", 2.6, Kind.App),
                        Fill("Chat", 1.9, Kind.Cache),
                        Fill("Code Editor", 1.2, Kind.App)),
                    Fill("LocalLow", 0.8, Kind.Cache)),
                Fill("Desktop", 2.3, Kind.Downloads, 1)),
            Fill("Public", 0.6, Kind.Docs)),
        Dir("Games",
            Fill("Starfall Courier", 92, Kind.Game),
            Fill("Iron Tides", 64, Kind.Game),
            Fill("Hollow Peaks", 38, Kind.Game),
            Dir("Installers", F("StarfallCourier_Setup.exe", 1.2))),
        Dir("$Recycle.Bin", Dir("S-1-5-21-1001", F("$RK2QX7A.zip", 2.4), F("$R8KQ2LM.mp4", 0.9))),
        F("pagefile.sys", 16),
        F("hiberfil.sys", 12.7),
        F("swapfile.sys", 0.25));

    private static VDir DriveD() => Dir("D:",
        Dir("GameLibrary",
            Dir("common",
                Fill("Skybound Legends", 142, Kind.Game),
                Fill("Night Harbor", 118, Kind.Game),
                Fill("Copper Frontier", 96, Kind.Game),
                Fill("Velocity Rush 4", 88, Kind.Game),
                Fill("Ashen Crown", 74, Kind.Game),
                Fill("Deep Signal", 51, Kind.Game),
                Fill("Riverlands", 33, Kind.Game),
                Fill("Tiny Kingdoms", 12, Kind.Game),
                Fill("Pixel Outpost", 3, Kind.Game)),
            Fill("shadercache", 14, Kind.Cache),
            Fill("downloading", 22, Kind.Game)),
        Fill("Recordings", 186, Kind.Video),
        Dir("Virtual Machines", F("dev-linux.vhdx", 64), F("win-test.vhdx", 48), F("retro-os.vmdk", 9)),
        Fill("Emulation", 41, Kind.Game),
        Fill("Mods", 18, Kind.Game));

    private static VDir DriveE() => Dir("E:",
        Dir("PC backups",
            Fill("2025-11-01", 420, Kind.Backup),
            Fill("2025-08-01", 390, Kind.Backup),
            Fill("2025-05-01", 361, Kind.Backup)),
        Fill("Movies", 780, Kind.Movies, 1),
        Fill("Photo archive", 520, Kind.Photo),
        Fill("Music archive", 140, Kind.Music, 2));

    private static VDir DriveF() => Dir("F:",
        Fill("Install media", 9, Kind.Downloads, 1),
        Fill("Transfer", 21, Kind.Docs));

    // ======================= Building blocks =======================

    private abstract class VItem
    {
        protected VItem(string name) => Name = name;
        public string Name { get; }
        public abstract long Bytes { get; }
    }

    private sealed class VFile(string name, long size) : VItem(name)
    {
        public override long Bytes => size;
    }

    private sealed class VDir(string name) : VItem(name)
    {
        public List<VItem> Items { get; } = new();
        public override long Bytes => Items.Sum(i => i.Bytes);

        public VDir With(params VItem[] items)
        {
            Items.AddRange(items);
            return this;
        }
    }

    private static VDir Dir(string name, params VItem[] items) => new VDir(name).With(items);

    private static VFile F(string name, double gb) => new(name, Round(gb * GB));

    private static long Round(double bytes) => Math.Max(4096, (long)Math.Ceiling(bytes / 4096) * 4096);

    /// <summary>Puts every folder of <paramref name="dir"/> in <paramref name="map"/> by path. Returns the folder count.</summary>
    private static int Index(VDir dir, string path, Dictionary<string, RawEntry[]> map)
    {
        map[Key(path)] = dir.Items.Select(i => new RawEntry(i.Name, i is VDir, i is VFile ? i.Bytes : 0, false)).ToArray();
        int count = 1;
        foreach (var sub in dir.Items.OfType<VDir>()) count += Index(sub, Path.Join(path, sub.Name), map);
        return count;
    }

    private static string Key(string path) => path.TrimEnd('\\', '/');

    private sealed class DemoReader(Dictionary<string, RawEntry[]> folders, Dictionary<string, long> delay) : IDirectoryReader
    {
        public bool TryRead(string path, List<RawEntry> sink)
        {
            if (!folders.TryGetValue(Key(path), out var entries)) return false;
            sink.AddRange(entries);
            // A short busy wait (Sleep is far too coarse for this) so the scan takes a few seconds.
            if (path.Length >= 2 && delay.TryGetValue(path[..2], out long ticks) && ticks > 0)
            {
                long until = Stopwatch.GetTimestamp() + ticks;
                while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
            }
            return true;
        }
    }

    // ======================= Generated content =======================

    private enum Kind { System, App, Game, Video, Movies, Photo, Music, Code, Docs, Backup, Cache, Downloads }

    private sealed record Profile(
        double MinFolderGb,          // below this a folder holds only files
        double FileShare,            // part of a folder's bytes in its own files (the rest goes to sub-folders)
        (int min, int max) Dirs,
        (int min, int max) LeafFiles,
        int NodeFiles,
        double Skew,                 // how unequal sizes are
        Func<Random, int, int, string> DirName,   // (rng, level, index)
        Func<Random, int, string> FileName);      // (rng, index)

    /// <summary>A folder of invented content of about <paramref name="gb"/> GB, <paramref name="depth"/> levels deep.</summary>
    private static VDir Fill(string name, double gb, Kind kind, int depth = 3)
    {
        var rng = new Random(Seed(name + "|" + kind + "|" + gb));
        var d = new VDir(name);
        FillInto(d, gb * GB, Profiles[kind], depth, rng, 0);
        return d;
    }

    private static void FillInto(VDir d, double bytes, Profile p, int depth, Random rng, int level)
    {
        bool leaf = depth <= 0 || bytes < p.MinFolderGb * GB;
        double fileBytes = leaf ? bytes : bytes * Math.Min(1, p.FileShare * (0.5 + rng.NextDouble()));
        int nFiles = leaf ? rng.Next(p.LeafFiles.min, p.LeafFiles.max + 1) : rng.Next(0, p.NodeFiles + 1);
        if (nFiles == 0) fileBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var w = Weights(rng, nFiles, p.Skew);
        for (int i = 0; i < nFiles; i++)
            d.Items.Add(new VFile(Unique(p.FileName(rng, i), names), Round(fileBytes * w[i])));

        double rest = bytes - fileBytes;
        if (leaf || rest <= 0) return;
        int nDirs = rng.Next(p.Dirs.min, p.Dirs.max + 1);
        var wd = Weights(rng, nDirs, p.Skew);
        for (int i = 0; i < nDirs; i++)
        {
            var sub = new VDir(Unique(p.DirName(rng, level, i), names));
            FillInto(sub, rest * wd[i], p, depth - 1, rng, level + 1);
            d.Items.Add(sub);
        }
    }

    private static double[] Weights(Random rng, int n, double skew)
    {
        var w = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            // Log-normal: a few big ones and many small ones, like real folders.
            double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
            double normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            w[i] = Math.Exp(skew * normal);
            sum += w[i];
        }
        for (int i = 0; i < n; i++) w[i] /= sum;
        return w;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            string n = $"{stem} ({i}){ext}";
            if (taken.Add(n)) return n;
        }
    }

    /// <summary>FNV-1a: the same seed in every run (string.GetHashCode changes per process).</summary>
    private static int Seed(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return (int)(h & 0x7FFFFFFF);
    }

    private static T Pick<T>(Random r, IReadOnlyList<T> list) => list[r.Next(list.Count)];

    private static readonly string[] SysPre = { "win", "nt", "sys", "dx", "core", "shell", "net", "ui", "crypt", "audio", "print", "media", "input", "ole", "gfx", "dev", "wlan", "font", "task", "sec" };
    private static readonly string[] SysPost = { "svc", "core", "api", "host", "mgr", "util", "lib", "res", "cfg", "32", "64", "ex", "srv", "prov", "ext", "data" };
    private static readonly string[] SysExt = { ".dll", ".dll", ".dll", ".sys", ".mui", ".exe", ".cat", ".manifest", ".dat" };
    private static readonly string[] SysDirs = { "System32", "SysWOW64", "WinSxS", "Installer", "assembly", "Fonts", "servicing", "SoftwareDistribution", "drivers", "DriverStore", "FileRepository", "en-US", "catroot2", "wbem", "Speech", "migration", "spool", "LogFiles", "Tasks", "config", "Microsoft.NET", "Resources", "SystemApps", "IME", "INF", "Logs", "Prefetch", "Temp", "WindowsPowerShell", "downlevel", "amd64_components" };

    private static readonly string[] Words = { "render", "engine", "core", "assets", "plugin", "update", "locale", "cache", "index", "shader", "model", "preset", "library", "sample", "theme", "font", "icon", "help", "runtime", "bundle", "codec", "filter", "export", "import", "media", "player", "sync", "store", "profile", "template" };
    private static readonly string[] AppDirs = { "bin", "lib", "resources", "plugins", "locales", "assets", "data", "modules", "presets", "templates", "fonts", "help", "extensions", "runtime", "codecs", "samples", "themes", "x64", "updates", "redist" };
    private static readonly string[] AppExt = { ".dll", ".dll", ".exe", ".pak", ".bin", ".dat", ".json", ".xml", ".png", ".db" };

    private static readonly string[] GameDirs = { "Content", "Paks", "Movies", "Binaries", "Win64", "Audio", "Banks", "Shaders", "Localization", "Maps", "Textures", "Engine", "Data", "Cinematics", "Characters", "Worlds", "Music", "Voice", "DLC", "Patches" };

    private static readonly string[] VideoDirs = { "Raw", "Edited", "Exports", "Proxies", "Day 1", "Day 2", "Day 3", "Camera A", "Camera B", "Drone", "Phone", "Clips", "Timelapse", "B-roll" };
    private static readonly string[] Events = { "Beach day", "Ski trip", "Concert", "Timelapse", "Christmas", "Road trip", "Hiking", "Garden", "Graduation", "City walk", "Camping", "Lake house", "Football", "Kids", "Snow", "Sunset", "Museum", "Festival", "Morning run", "Harbour" };
    private static readonly string[] Months = { "01 January", "02 February", "03 March", "04 April", "05 May", "06 June", "07 July", "08 August", "09 September", "10 October", "11 November", "12 December" };
    private static readonly string[] Adj = { "Velvet", "Neon", "Paper", "Silver", "Quiet", "Electric", "Golden", "Hollow", "Crimson", "Northern", "Lazy", "Wild", "Glass", "Midnight", "Copper", "Lucky" };
    private static readonly string[] Noun = { "Owls", "Harbor", "Lanterns", "Foxes", "Satellites", "Tides", "Echoes", "Pines", "Rivers", "Machines", "Comets", "Gardens", "Signals", "Islands", "Clouds", "Wolves" };

    private static readonly string[] Projects = { "weather-app", "pixel-editor", "home-dashboard", "budget-tracker", "game-jam-2025", "blog", "scripts", "ml-experiments", "api-server", "dotfiles", "photo-sorter", "chess-engine" };
    private static readonly string[] CodeDirs = { "src", "node_modules", "build", "dist", ".git", "assets", "tests", "docs", "packages", "bin", "obj", "lib", "components", "utils", "models", "data", "public", "vendor" };
    private static readonly string[] CodeExt = { ".ts", ".js", ".json", ".map", ".cs", ".py", ".png", ".md", ".pack", ".lock", ".dll", ".pt" };

    private static readonly string[] DocDirs = { "Work", "Taxes", "Receipts", "School", "Manuals", "Scans", "Letters", "Templates", "Invoices", "Recipes", "Car", "House", "Travel", "Insurance", "Notes", "Presentations" };
    private static readonly string[] DocWords = { "Report", "Invoice", "Notes", "Budget", "Plan", "Manual", "Scan", "Contract", "Summary", "Slides", "Receipt", "Letter", "Checklist", "Minutes", "Draft" };
    private static readonly string[] DocExt = { ".pdf", ".pdf", ".docx", ".xlsx", ".pptx", ".jpg", ".txt" };

    private static readonly string[] BackupDirs = { "Documents", "Pictures", "Videos", "Projects", "Mail", "Settings", "Music", "Desktop", "Games saves", "Archive" };

    private static readonly string[] DownloadNames = { "setup-x64.exe", "installer.msi", "drivers.zip", "manual.pdf", "wallpapers.zip", "photos.zip", "invoice.pdf", "portable.zip", "fonts.zip", "sample-pack.zip", "tutorial.mp4", "slides.pdf", "dataset.csv", "firmware.bin", "patch.exe" };

    private static string Hex(Random r, int digits) => r.NextInt64(1L << (4 * digits)).ToString("x" + digits);

    private static readonly Dictionary<Kind, Profile> Profiles = new()
    {
        [Kind.System] = new(0.05, 0.25, (4, 9), (15, 160), 40, 1.3,
            (r, lvl, i) => lvl == 0 ? SysDirs[i % SysDirs.Length] : r.Next(3) == 0 ? $"amd64_{Pick(r, SysPre)}{Pick(r, SysPost)}_{Hex(r, 8)}" : Pick(r, SysPre) + Pick(r, SysPost),
            (r, i) => Pick(r, SysPre) + Pick(r, SysPost) + Pick(r, SysExt)),
        [Kind.App] = new(0.08, 0.3, (3, 7), (10, 90), 25, 1.2,
            (r, lvl, i) => lvl == 0 ? AppDirs[(i * 3 + r.Next(3)) % AppDirs.Length] : Pick(r, AppDirs),
            (r, i) => Pick(r, Words) + (r.Next(2) == 0 ? "_" + Pick(r, Words) : "") + Pick(r, AppExt)),
        [Kind.Game] = new(0.6, 0.25, (3, 7), (6, 60), 12, 1.3,
            (r, lvl, i) => GameDirs[(lvl * 7 + i * 3 + r.Next(3)) % GameDirs.Length],
            (r, i) => r.Next(3) switch
            {
                0 => $"pakchunk{i}-Windows.pak",
                1 => $"data{i:000}.bin",
                _ => $"{Pick(r, Words)}_{i}.bank",
            }),
        [Kind.Video] = new(1.5, 0.15, (3, 6), (12, 60), 6, 1.0,
            (r, lvl, i) => lvl == 0 ? VideoDirs[(i * 2 + r.Next(2)) % VideoDirs.Length] : Pick(r, VideoDirs),
            (r, i) => r.Next(3) switch
            {
                0 => $"C{r.Next(10000):0000}.MP4",
                1 => $"DJI_{r.Next(10000):0000}.MP4",
                _ => $"VID_2025{1 + r.Next(12):00}{1 + r.Next(28):00}_{r.Next(1000000):000000}.mp4",
            }),
        [Kind.Movies] = new(1, 0.0, (30, 60), (1, 2), 0, 0.5,
            (r, lvl, i) => $"The {Pick(r, Adj)} {Pick(r, Noun)} ({1984 + r.Next(41)})",
            (r, i) => i == 0 ? "movie.mkv" : "extras.mkv"),
        [Kind.Photo] = new(0.4, 0.05, (4, 8), (40, 300), 4, 0.8,
            (r, lvl, i) => lvl == 0 ? (2018 + i).ToString() : lvl == 1 ? Months[(i * 2 + r.Next(2)) % 12] : Pick(r, Events),
            (r, i) => r.Next(3) switch
            {
                0 => $"IMG_{r.Next(10000):0000}.CR3",
                1 => $"IMG_{r.Next(10000):0000}.JPG",
                _ => $"DSC0{r.Next(10000):0000}.ARW",
            }),
        [Kind.Music] = new(0.15, 0.0, (6, 16), (8, 16), 0, 0.6,
            (r, lvl, i) => lvl == 0 ? (r.Next(2) == 0 ? "The " : "") + Pick(r, Adj) + " " + Pick(r, Noun) : Pick(r, Adj) + " " + Pick(r, Noun),
            (r, i) => $"{i + 1:00} {Pick(r, Adj)} {Pick(r, Noun)}.flac"),
        [Kind.Code] = new(0.02, 0.1, (3, 8), (10, 150), 15, 1.4,
            (r, lvl, i) => lvl == 0 ? Projects[i % Projects.Length] : CodeDirs[(lvl * 5 + i * 3 + r.Next(4)) % CodeDirs.Length],
            (r, i) => Pick(r, Words) + (r.Next(2) == 0 ? "-" + Pick(r, Words) : "") + Pick(r, CodeExt)),
        [Kind.Docs] = new(0.05, 0.2, (4, 9), (10, 70), 20, 1.4,
            (r, lvl, i) => lvl == 0 ? DocDirs[(i * 3 + r.Next(3)) % DocDirs.Length] : Pick(r, DocDirs),
            (r, i) => $"{Pick(r, DocWords)} {2019 + r.Next(7)}{(r.Next(3) == 0 ? $" ({r.Next(2, 9)})" : "")}{Pick(r, DocExt)}"),
        [Kind.Backup] = new(4, 0.1, (4, 8), (10, 60), 8, 1.2,
            (r, lvl, i) => BackupDirs[(lvl * 3 + i * 3 + r.Next(3)) % BackupDirs.Length],
            (r, i) => r.Next(2) == 0 ? $"part{i + 1:000}.zip" : $"image-{i + 1:00}.vhdx"),
        [Kind.Cache] = new(0.05, 0.2, (3, 8), (20, 200), 30, 1.5,
            (r, lvl, i) => r.Next(3) switch { 0 => "Cache_Data", 1 => Pick(r, new[] { "Code Cache", "GPUCache", "shadercache", "blob_storage", "IndexedDB", "Service Worker", "logs", "thumbnails" }), _ => Hex(r, 8) },
            (r, i) => r.Next(2) == 0 ? $"f_{Hex(r, 6)}" : $"data_{i}"),
        [Kind.Downloads] = new(0.5, 0.9, (1, 3), (25, 60), 50, 1.6,
            (r, lvl, i) => Pick(r, new[] { "old", "unzipped", "drivers", "to sort" }),
            (r, i) =>
            {
                var n = Pick(r, DownloadNames);
                return Path.GetFileNameWithoutExtension(n) + (r.Next(2) == 0 ? $"-{r.Next(1, 9)}.{r.Next(0, 20)}" : $" ({r.Next(1, 6)})") + Path.GetExtension(n);
            }),
    };
}
