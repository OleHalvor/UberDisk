using System.Diagnostics;
using UberDisk.Core.Scanning;
using UberDisk.Core.Viz;

string root = args.Length > 0 ? args[0] : (OperatingSystem.IsWindows() ? @"C:\" : "/usr");
int[] threadCounts = args.Length > 1 ? args[1].Split(',').Select(int.Parse).ToArray() : new[] { 4, 8, 16, 24, 32 };
Console.WriteLine($"Scanning {root} on {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}");

// Warm-up run so the OS cache is in the same state for every measurement.
ScanJob.ScanNow(root, options: new ScanOptions { Threads = 16 });

foreach (int t in threadCounts)
{
    var times = new List<double>();
    UberDisk.Core.Model.DirNode? tree = null;
    int gc0 = GC.CollectionCount(0);
    for (int run = 0; run < 3; run++)
    {
        tree = null;
        GC.Collect();
        var sw = Stopwatch.StartNew();
        tree = ScanJob.ScanNow(root, options: new ScanOptions { Threads = t });
        times.Add(sw.Elapsed.TotalSeconds);
    }
    GC.Collect();
    long heap = GC.GetTotalMemory(true);
    Console.WriteLine($"{t,3} threads: best {times.Min():0.00} s, median {times.OrderBy(x => x).ElementAt(1):0.00} s, " +
                      $"{tree!.FileCount:N0} files, {tree.DirCount:N0} folders, {Format.Bytes(tree.Size)}, " +
                      $"heap with tree {heap / 1048576.0:0} MB, gen0 GCs {(GC.CollectionCount(0) - gc0) / 3}");
    GC.KeepAlive(tree);
}

{
    var tree = ScanJob.ScanNow(root, options: new ScanOptions { Threads = 16 });
    var sw = Stopwatch.StartNew();
    int n = 0;
    for (int i = 0; i < 20; i++) n = SunburstLayout.Compute(tree, 7, ring => 3.0 / (120 + ring * 40)).Count;
    Console.WriteLine($"Layout of the drive root: {sw.Elapsed.TotalMilliseconds / 20:0.0} ms for {n:N0} segments");
}
