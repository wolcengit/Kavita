namespace Kavita.Importer.Cli;

public static class CliParser
{
    public const string HelpText = """
        Kavita.Importer - 将书籍文件从命令行导入到现有的 Kavita 服务器数据库中。

        用法:
          Kavita.Importer --dir <path> [选项]

        选项:
          --kavita-dir <path>   Kavita 服务器根目录（需包含 config/kavita.db）。
                                默认为当前工作目录。
          --dir <path>          要导入的书籍所在根目录（必填，除非使用 --list-libraries）。
          --dir-as-serial       将每本书所在文件夹的名称作为所属系列名使用。
                                忽略目录层级，不依赖 Kavita 的默认文件夹解析规则。
          --library-id <id>     目标 Library 的数据库 id。
          --library-name <name> 目标 Library 的名称（按名称查找）。
          --force-update        强制重新生成封面与字数统计（相当于服务器重新扫描）。
          --no-covers           跳过封面生成。
          --no-wordcount        跳过字数统计。
          --dry-run             仅解析并预览分组结果，不写入数据库。
          --list-libraries      列出当前数据库中的所有 Library。
          --verbose             输出更多日志信息。
          -h, --help            显示帮助。

        示例:
          Kavita.Importer --dir /mnt/books --dir-as-serial --library-name Manga
          Kavita.Importer --list-libraries --kavita-dir /srv/kavita

        注意事项:
          - 目标数据库（config/kavita.db）通常处于 SQLite 模式。运行本工具前请先停止 Kavita 服务器，
            或对数据库文件进行备份/复制，以避免数据库锁冲突。
          - 导入的文件/文件夹需要能被 Kavita 服务器继续管理时，请确保 --dir 位于目标 Library
            已配置的扫描文件夹下，否则后续服务器执行全库扫描时可能会将导入的系列视为缺失。
        """;

    public static CommandLineOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var options = new CommandLineOptions();
        var showHelp = args.Length == 0;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--kavita-dir":
                    if (!TryGetValue(args, ref i, out var kavitaDir)) return null;
                    options.KavitaDirectory = Path.GetFullPath(kavitaDir!);
                    break;
                case "--dir":
                    if (!TryGetValue(args, ref i, out var sourceDir)) return null;
                    options.SourceDirectory = Path.GetFullPath(sourceDir!);
                    break;
                case "--dir-as-serial":
                    options.DirAsSerial = true;
                    break;
                case "--library-id":
                    if (!TryGetValue(args, ref i, out var libraryId)) return null;
                    if (!int.TryParse(libraryId, out var id) || id <= 0)
                    {
                        error = $"--library-id 需要为正整数，当前值: '{libraryId}'";
                        return null;
                    }
                    options.LibraryId = id;
                    break;
                case "--library-name":
                    if (!TryGetValue(args, ref i, out var libraryName)) return null;
                    options.LibraryName = libraryName;
                    break;
                case "--force-update":
                    options.ForceUpdate = true;
                    break;
                case "--no-covers":
                    options.NoCovers = true;
                    break;
                case "--no-wordcount":
                    options.NoWordCount = true;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--list-libraries":
                    options.ListLibraries = true;
                    break;
                case "-v":
                case "--verbose":
                    options.Verbose = true;
                    break;
                default:
                    error = $"未知参数: '{arg}'";
                    return null;
            }
        }

        if (showHelp)
        {
            return null;
        }

        if (options.ListLibraries)
        {
            return options;
        }

        if (string.IsNullOrEmpty(options.SourceDirectory))
        {
            error = "缺少必填参数 --dir";
            return null;
        }

        if (!Directory.Exists(options.SourceDirectory))
        {
            error = $"目录不存在: {options.SourceDirectory}";
            return null;
        }

        if (options.LibraryId.HasValue && !string.IsNullOrEmpty(options.LibraryName))
        {
            error = "--library-id 与 --library-name 不能同时使用";
            return null;
        }

        if (!options.LibraryId.HasValue && string.IsNullOrEmpty(options.LibraryName))
        {
            error = "必须提供 --library-id 或 --library-name 指定目标 Library";
            return null;
        }

        return options;
    }

    private static bool TryGetValue(string[] args, ref int i, out string? value)
    {
        value = null;
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"参数 '{args[i]}' 缺少值");
            return false;
        }

        value = args[++i];
        return true;
    }
}
