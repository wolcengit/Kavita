namespace Kavita.Importer.Cli;

public sealed class CommandLineOptions
{
    /// <summary>
    /// Kavita server root directory. It must contain the config/kavita.db file (i.e. the folder the server
    /// normally runs from). Defaults to the current working directory.
    /// </summary>
    public string KavitaDirectory { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Root folder that is being imported.
    /// </summary>
    public string? SourceDirectory { get; set; }

    /// <summary>
    /// When true, the name of the folder that contains each book is used as the owning series name.
    /// Directory depth is irrelevant; Kavita's folder/file parsing rules are not used to derive the series.
    /// </summary>
    public bool DirAsSerial { get; set; }

    /// <summary>
    /// Target library id. Mutually exclusive with LibraryName.
    /// </summary>
    public int? LibraryId { get; set; }

    /// <summary>
    /// Target library name. Mutually exclusive with LibraryId.
    /// </summary>
    public string? LibraryName { get; set; }

    public bool ForceUpdate { get; set; }

    public bool NoCovers { get; set; }

    public bool NoWordCount { get; set; }

    /// <summary>
    /// Parses and groups files but does not write anything to the database.
    /// </summary>
    public bool DryRun { get; set; }

    public bool ListLibraries { get; set; }

    public bool Verbose { get; set; }
}
