using System.IO.Abstractions;
using AutoMapper;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.Scanner;
using Kavita.Database;
using Kavita.Models.Parser;
using Kavita.Services;
using Kavita.Services.Helpers;
using Kavita.Services.Metadata;
using Kavita.Services.Reading;
using Kavita.Services.Scanner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kavita.Importer.Infrastructure;

/// <summary>
/// Builds the (offline) dependency graph that mirrors how the Kavita server composes its scan pipeline:
///
/// ReadingItemService (per-file parse) + ProcessSeries (series/volume/chapter upsert + commit)
/// + MetadataService.GenerateCoversForSeries (covers) + WordCountAnalyzerService (word counts).
///
/// Everything that would normally reach out to the network / SignalR / Hangfire is replaced with a
/// no-op stub. The only external state used is the SQLite database the caller points us at.
/// </summary>
public static class ServiceComposer
{
    private static readonly Lazy<IMapper> MapperLazy = new(() =>
    {
        var configuration = new MapperConfiguration(cfg =>
        {
            // The server registers AutoMapper with AddAutoMapper(typeof(Program).Assembly). In this split
            // solution the entity/DTO profiles can live in any of the Kavita.* assemblies, so scan them all.
            cfg.AddMaps(typeof(DataContext).Assembly);
            cfg.AddMaps(typeof(IProcessSeries).Assembly);
            cfg.AddMaps(typeof(MetadataService).Assembly);
            cfg.AddMaps(typeof(ParserInfo).Assembly);
        });

        return configuration.CreateMapper();
    });

    /// <summary>
    /// Creates a completely new offline session (new DataContext + UnitOfWork + dependent services).
    /// </summary>
    /// <param name="dbConnectionString">SQLite connection string pointing to Kavita's config/kavita.db</param>
    /// <param name="loggerFactory">Shared logger factory</param>
    /// <param name="fileSystem">Shared file system abstraction</param>
    public static ImporterSession CreateSession(string dbConnectionString, ILoggerFactory loggerFactory,
        IFileSystem fileSystem)
    {
        var options = new DbContextOptionsBuilder<DataContext>()
            .UseSqlite(dbConnectionString)
            .Options;

        var context = new DataContext(options);
        var unitOfWork = new UnitOfWork(context, MapperLazy.Value, null);

        var directoryService = new DirectoryService(loggerFactory.CreateLogger<DirectoryService>(), fileSystem);
        var imageService = new ImageService(loggerFactory.CreateLogger<ImageService>(), directoryService);
        var mediaErrorService = new NoOpMediaErrorService();

        var archiveService = new ArchiveService(loggerFactory.CreateLogger<ArchiveService>(), directoryService,
            imageService, mediaErrorService);
        var bookService = new BookService(loggerFactory.CreateLogger<BookService>(), directoryService, imageService,
            mediaErrorService, unitOfWork);
        var readingItemService = new ReadingItemService(archiveService, bookService, imageService, directoryService,
            loggerFactory.CreateLogger<ReadingItemService>(), mediaErrorService);

        var eventHub = new NoOpEventHub();
        var fileService = new FileService(fileSystem);
        var cacheHelper = new CacheHelper(fileService);

        var processSeries = new ProcessSeries(unitOfWork, loggerFactory.CreateLogger<ProcessSeries>(), eventHub,
            directoryService, cacheHelper, readingItemService, fileService, new NoOpReadingListService(),
            new NoOpExternalMetadataService());

        // MetadataService creates a child scope per unit of work; provide a bare root scope factory so it can
        // do so offline (it never resolves network/SignalR dependents thanks to the no-op stubs above).
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var metadataService = new MetadataService(serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            unitOfWork, loggerFactory.CreateLogger<MetadataService>(), eventHub, cacheHelper, readingItemService,
            directoryService, imageService);

        var wordCountAnalyzerService = new WordCountAnalyzerService(
            loggerFactory.CreateLogger<WordCountAnalyzerService>(), unitOfWork, eventHub, cacheHelper,
            mediaErrorService);

        return new ImporterSession(context, unitOfWork, readingItemService, processSeries, metadataService,
            wordCountAnalyzerService);
    }
}
