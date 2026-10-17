// usage: dotnet run --project scripts/re/ggdump -- <Content.ggpk> <outDir> <path>...   (e.g. Data/Inventories.datc64)
using GameServer.Data.Ggpk;
using GgDump;
// usage: GgDump <ggpk> <outDir> <path>...   (or "list <substring>" as first path to search the index)
string root = @"C:\Users\Frost\Desktop\Projects\Game";
using var oodle = new NodeOodleDecoder("node", Path.Combine(root, @"Tools\ooz\ooz_worker.mjs"));
using var fs = GameFileSystem.Open(args[0], oodle);
foreach (var path in args.Skip(2))
{
    if (!fs.TryReadFile(path, out var d)) { Console.WriteLine($"{path}: not found"); continue; }
    string o = Path.Combine(args[1], path.Replace('/', '_'));
    File.WriteAllBytes(o, d);
    Console.WriteLine($"{path} -> {o} ({d.Length} bytes)");
}
