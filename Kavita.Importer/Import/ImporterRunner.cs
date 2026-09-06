using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Kavita.API.Repositories;
using Kavita.API.Services.Scanner;
using Kavita.Common.Extensions;
using Kavita.Importer.Cli;
using Kavita.Importer.Infrastructure;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.Entities;
using Kavita.Models.Parser;
using Kavita.Services.Extensions;
using Microsoft.Extensions.Logging;

namespace Kavita.Importer.Import;

public static class ImporterRunner
{
    public static async Task<int> RunAsync(CommandLineOptions options)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(configure =>
            {
                configure.SingleLine = true;
                configure.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                configure.UseUtcTimestamp = false;
                configure.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
            });
            builder.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("Kavita.Importer");
        var fileSystem = new FileSystem();

        var kavitaDir = Path.GetFullPath(options.KavitaDirectory);
        if (!Directory.Exists(kavitaDir))
        {
            logger.LogError("Kavita 根目录不存在: {KavitaDirectory}", kavitaDir);
            return 1;
        }

        var dbPath = Path.Combine(kavitaDir, "config", "kavita.db");
        if (!fileSystem.File.Exists(dbPath))
        {
            logger.LogError("未找到数据库文件 {DbPath}。请确认 --kavita-dir 指向 Kavita 服务器根目录（内含 config/kavita.db）。", dbPath);
            return 1;
        }

        var connectionString = $"Data Source={dbPath}";

        // DirectoryService resolves all relative paths (config/, config/covers, ...) against the current
        // directory, exactly like the running server. Point this process at the Kavita root up front.
        if (!string.Equals(Path.GetFullPath(Directory.GetCurrentDirectory()), kavitaDir,
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.SetCurrentDirectory(kavitaDir);
        }

        if (options.ListLibraries)
        {
            return await ListLibrariesAsync(connectionString, loggerFactory, fileSystem, logger);
        }

        // Resolve the target library (by id, or by name) so we can report it even before parsing.
        var libraryId = options.LibraryId;
        if (libraryId is null)
        {
            var resolved = await ResolveLibraryByNameAsync(options.LibraryName!, connectionString, loggerFactory,
                fileSystem, logger);
            if (resolved is null)
            {
                return 1;
            }

            libraryId = resolved.Value;
        }

        logger.LogInformation("目标 Library: Id={LibraryId}", libraryId);
        logger.LogInformation("导入根目录: {SourceDirectory}", options.SourceDirectory);
        logger.LogInformation("系列归属规则: {Rule}", options.DirAsSerial ? "目录名作为系列 (--dir-as-serial)" : "Kavita 默认解析规则");
        logger.LogInformation("封面生成: {Covers}, 字数统计: {WordCount}, 强制更新: {ForceUpdate}",
            options.NoCovers ? "跳过" : "启用",
            options.NoWordCount ? "跳过" : "启用",
            options.ForceUpdate ? "是" : "否");

        // ---- Phase 1: enumerate + parse files ---------------------------------------------
        var files = fileSystem.Directory
            .EnumerateFiles(options.SourceDirectory!, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation("共发现 {FileCount} 个候选文件", files.Count);

        var parsedInfos = new List<ParserInfo>();
        var parsedFiles = new List<string>();

        using (var session = ServiceComposer.CreateSession(connectionString, loggerFactory, fileSystem))
        {
            var library = await session.UnitOfWork.LibraryRepository.GetLibraryForIdAsync(libraryId.Value,
                LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns);
            if (library is null)
            {
                logger.LogError("Library Id={LibraryId} 不存在。", libraryId);
                return 1;
            }

            logger.LogInformation("Library: {LibraryName} (Id={LibraryId}, Type={LibraryType})", library.Name,
                library.Id, library.Type);

            // ---- Directory-level format filter -------------------------------------------
            // Only files whose extension belongs to the library's configured file types are imported.
            // If a directory contains no such file at all, that directory is ignored entirely. This
            // mirrors the server scanner, which filters candidates by library.LibraryFileTypes before
            // any parser runs (otherwise generic parsers would turn unsupported files into series).
            var fileTypeRegex = BuildLibraryFileTypeRegex(library);
            if (fileTypeRegex is null)
            {
                logger.LogError(
                    "Library Id={LibraryId} 未配置任何可导入的文件类型（LibraryFileTypes 为空），无法判断哪些文件满足导入格式要求。",
                    library.Id);
                return 1;
            }

            var candidates = new List<string>();
            var ignoredDirectories = new List<(string Directory, int FileCount)>();
            foreach (var directoryGroup in files.GroupBy(Path.GetDirectoryName))
            {
                if (string.IsNullOrEmpty(directoryGroup.Key))
                {
                    candidates.AddRange(directoryGroup);
                    continue;
                }

                var eligibleFiles = directoryGroup
                    .Where(file => fileTypeRegex.IsMatch(Path.GetExtension(file)))
                    .ToList();

                if (eligibleFiles.Count == 0)
                {
                    ignoredDirectories.Add((directoryGroup.Key, directoryGroup.Count()));
                    continue;
                }

                candidates.AddRange(eligibleFiles);
            }

            foreach (var (directory, fileCount) in ignoredDirectories)
            {
                logger.LogInformation(
                    "忽略文件夹 {Directory}: 其中没有任何文件的格式满足导入要求（{FileCount} 个文件已跳过）",
                    directory, fileCount);
            }

            logger.LogInformation(
                "满足导入格式的文件共 {CandidateCount} 个（忽略 {IgnoredDirectoryCount} 个没有有效格式文件的文件夹）",
                candidates.Count, ignoredDirectories.Count);
            if (candidates.Count == 0)
            {
                logger.LogError("没有找到任何满足导入格式要求的文件。");
                return 1;
            }

            foreach (var file in candidates)
            {
                try
                {
                    // ParseFile mirrors exactly what the server scanner does for a single file
                    // (ReadingItemService.Parse). enableMetadata=true pulls ComicInfo/EPUB metadata.
                    var info = session.ReadingItemService.ParseFile(file, options.SourceDirectory!,
                        options.SourceDirectory!, library.Type, enableMetadata: true);
                    if (info is null)
                    {
                        logger.LogDebug("跳过不支持的或无法解析的文件: {File}", file);
                        continue;
                    }

                    if (options.DirAsSerial)
                    {
                        var parentDir = Path.GetDirectoryName(file);
                        var seriesName = string.IsNullOrEmpty(parentDir)
                            ? string.Empty
                            : Path.GetFileName(parentDir);
                        if (string.IsNullOrWhiteSpace(seriesName))
                        {
                            logger.LogWarning("跳过文件（无法从路径确定所在目录名）: {File}", file);
                            continue;
                        }

                        if (!string.Equals(info.Series, seriesName, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogDebug("--dir-as-serial: 将 '{Old}' 改写为目录名 '{New}' ({File})", info.Series,
                                seriesName, file);
                        }

                        info.Series = seriesName.Trim();
                    }

                    if (string.IsNullOrEmpty(info.Series))
                    {
                        logger.LogDebug("跳过无法确定系列名的文件: {File}", file);
                        continue;
                    }

                    parsedInfos.Add(info);
                    parsedFiles.Add(file);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "解析文件失败: {File}", file);
                }
            }
        }

        logger.LogInformation("解析成功 {ParsedCount} 个满足格式要求的文件", parsedInfos.Count);
        if (parsedInfos.Count == 0)
        {
            logger.LogError("没有找到任何可导入的文件。");
            return 1;
        }

        // ---- Phase 2: group files into series ---------------------------------------------
        var groups = parsedInfos
            .GroupBy(info => info.Series.ToNormalized(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var infos = g.OrderBy(i => i.Filename, StringComparer.OrdinalIgnoreCase).ToList();
                return new SeriesGroup(infos[0].Series, infos);
            })
            .OrderBy(g => g.SeriesName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        logger.LogInformation("解析出 {GroupCount} 个系列分组:", groups.Count);
        foreach (var group in groups)
        {
            logger.LogInformation("  - {SeriesName} ({FileCount} 个文件)", group.SeriesName, group.Files.Count);
        }

        if (options.DryRun)
        {
            logger.LogInformation("--dry-run 模式：以上为将要导入的分组结果，未写入任何数据。");
            return 0;
        }

        // ---- Phase 3: process each series ------------------------------------------------
        logger.LogInformation("开始导入 {GroupCount} 个系列...", groups.Count);

        var index = 0;
        foreach (var group in groups)
        {
            index++;
            var failed = false;

            using (var session = ServiceComposer.CreateSession(connectionString, loggerFactory, fileSystem))
            {
                try
                {
                    var library = await session.UnitOfWork.LibraryRepository.GetLibraryForIdAsync(libraryId.Value,
                        LibraryIncludes.Folders | LibraryIncludes.FileTypes | LibraryIncludes.ExcludePatterns);
                    if (library is null)
                    {
                        logger.LogError("[{Index}/{Total}] 系列 '{Series}' 处理失败: Library Id={LibraryId} 不存在。",
                            index, groups.Count, group.SeriesName, libraryId);
                        failed = true;
                    }
                    else
                    {
                        logger.LogInformation("[{Index}/{Total}] 正在处理系列 '{Series}' ({FileCount} 个文件)...",
                            index, groups.Count, group.SeriesName, group.Files.Count);

                        var metadataSettings = await session.UnitOfWork.SettingsRepository.GetMetadataSettingDto();
                        var serverSettings = await session.UnitOfWork.SettingsRepository.GetSettingsDtoAsync();

                        var seriesId = await session.ProcessSeries.ProcessSeriesAsync(
                            metadataSettings ?? new MetadataSettingsDto(),
                            group.Files,
                            new ProcessSeriesArgs
                            {
                                Library = library,
                                TotalToProcess = groups.Count,
                                LeftToProcess = groups.Count - index,
                                ForceUpdate = options.ForceUpdate
                            });

                        if (seriesId is null)
                        {
                            logger.LogWarning("[{Index}/{Total}] 系列 '{Series}' 没有产生任何变更（或未能处理），跳过封面/字数。",
                                index, groups.Count, group.SeriesName);
                            continue;
                        }

                        if (!options.NoCovers && serverSettings is not null)
                        {
                            await session.MetadataService.GenerateCoversForSeries(serverSettings, library.Id,
                                seriesId.Value, options.ForceUpdate, forceColorScape: false);
                        }
                        else if (!options.NoCovers)
                        {
                            logger.LogWarning("[{Index}/{Total}] 未能加载服务器设置，跳过封面生成。", index, groups.Count);
                        }

                        if (!options.NoWordCount)
                        {
                            await session.WordCountAnalyzerService.ScanSeries(library.Id, seriesId.Value,
                                options.ForceUpdate);
                        }

                        logger.LogInformation("[{Index}/{Total}] 完成系列 '{Series}' (SeriesId={SeriesId})。", index,
                            groups.Count, group.SeriesName, seriesId.Value);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[{Index}/{Total}] 系列 '{Series}' 处理时发生异常。", index, groups.Count,
                        group.SeriesName);
                    failed = true;
                }
            }

            if (failed)
            {
                logger.LogWarning("第 {Index} 个系列处理失败，继续下一个...", index);
            }
        }

        logger.LogInformation("导入流程结束。");
        return 0;
    }

    /// <summary>
    /// Builds the regex used to decide whether a file's extension satisfies the library's configured
    /// file types. Mirrors how the server scanner constructs its candidate filter
    /// (ParseScannedFiles.ScanFiles): each configured <see cref="FileTypeGroup"/> contributes its
    /// extensions and the resulting pattern is matched against the file extension case-insensitively.
    /// </summary>
    /// <param name="library">Library with LibraryFileTypes loaded</param>
    /// <returns>Regex or null when the library has no file types configured</returns>
    private static Regex? BuildLibraryFileTypeRegex(Library library)
    {
        var fileExtensions = string.Join("|", library.LibraryFileTypes.Select(l => l.FileTypeGroup.GetRegex()));
        if (string.IsNullOrWhiteSpace(fileExtensions))
        {
            return null;
        }

        return new Regex(fileExtensions,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(500));
    }

    private static async Task<int> ListLibrariesAsync(string connectionString, ILoggerFactory loggerFactory,
        IFileSystem fileSystem, ILogger logger)
    {
        using var session = ServiceComposer.CreateSession(connectionString, loggerFactory, fileSystem);
        var libraries = (await session.UnitOfWork.LibraryRepository.GetLibrariesAsync()).ToList();
        if (libraries.Count == 0)
        {
            logger.LogInformation("当前数据库中没有 Library。");
            return 0;
        }

        logger.LogInformation("当前数据库中的 Library:");
        foreach (var library in libraries)
        {
            logger.LogInformation("  Id={LibraryId}  Name={LibraryName}  Type={LibraryType}", library.Id, library.Name,
                library.Type);
        }

        return 0;
    }

    private static async Task<int?> ResolveLibraryByNameAsync(string libraryName, string connectionString,
        ILoggerFactory loggerFactory, IFileSystem fileSystem, ILogger logger)
    {
        using var session = ServiceComposer.CreateSession(connectionString, loggerFactory, fileSystem);
        var libraries = (await session.UnitOfWork.LibraryRepository.GetLibrariesAsync()).ToList();
        var match = libraries.FirstOrDefault(l =>
            string.Equals(l.Name, libraryName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match.Id;
        }

        logger.LogError("未找到名为 '{LibraryName}' 的 Library。可用 Library:", libraryName);
        foreach (var library in libraries)
        {
            logger.LogInformation("  Id={LibraryId}  Name={LibraryName}  Type={LibraryType}", library.Id, library.Name,
                library.Type);
        }

        return null;
    }

    private sealed record SeriesGroup(string SeriesName, IList<ParserInfo> Files);
}
