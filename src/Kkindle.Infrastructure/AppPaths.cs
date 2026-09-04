namespace Kkindle.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? AppContext.BaseDirectory;
        Data = Path.Combine(Root, "data");
        Library = Path.Combine(Data, "library");
        Covers = Path.Combine(Data, "covers");
        Logs = Path.Combine(Data, "logs");
        ReaderCache = Path.Combine(Data, "reader-cache");
        EmbeddingModels = Path.Combine(Data, "embedding-models");
        Fonts = Path.Combine(Data, "fonts");
        Dictionaries = Path.Combine(Data, "dictionaries");
        Backups = Path.Combine(Root, "backups");
        Database = Path.Combine(Data, "kkindle.db");
        Settings = Path.Combine(Data, "app-settings.json");
    }

    public string Root { get; }
    public string Data { get; }
    public string Library { get; }
    public string Covers { get; }
    public string Logs { get; }
    public string ReaderCache { get; }
    public string EmbeddingModels { get; }
    public string Fonts { get; }
    public string Dictionaries { get; }
    public string Backups { get; }
    public string Database { get; }
    public string Settings { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Library);
        Directory.CreateDirectory(Covers);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(ReaderCache);
        Directory.CreateDirectory(EmbeddingModels);
        Directory.CreateDirectory(Fonts);
        Directory.CreateDirectory(Dictionaries);
        Directory.CreateDirectory(Backups);
    }
}
