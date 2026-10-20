namespace GameServer.Gateway.Tests;

public class GameDataFilesTests
{
    [Fact]
    public void Warning_names_every_missing_file_and_points_at_extract_data()
    {
        string dir = Directory.CreateTempSubdirectory("gamedata").FullName;
        try
        {
            Assert.NotNull(GameDataFiles.Warning(dir));
            foreach (var (file, _) in GameDataFiles.All.Where(f => f.File != "data/hideouts.json"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, file))!);
                File.WriteAllText(Path.Combine(dir, file), "{}");
            }
            string? warning = GameDataFiles.Warning(dir);
            Assert.NotNull(warning);
            Assert.Contains("1 game data file(s) missing", warning);
            Assert.Contains("data/hideouts.json (hideouts)", warning);
            Assert.Contains("extract-data.ps1", warning);
            File.WriteAllText(Path.Combine(dir, "data/hideouts.json"), "{}");
            Assert.Null(GameDataFiles.Warning(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Every_file_the_Gateway_checks_has_an_entry_in_the_extract_data_manifest()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "scripts", "dev", "extract-data.ps1"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        string script = File.ReadAllText(Path.Combine(root, "scripts", "dev", "extract-data.ps1"));
        foreach (var (file, _) in GameDataFiles.All)
            Assert.Contains($"'{file.Replace('/', '\\')}'", script);
    }
}
