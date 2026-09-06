using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Metadata;
using Kavita.API.Services.Scanner;
using Kavita.Database;

namespace Kavita.Importer.Infrastructure;

/// <summary>
/// A single unit of work against the Kavita database.
///
/// One session owns one <see cref="DataContext"/> / <see cref="IUnitOfWork"/> pair together with all the
/// services bound to it (mirrors how the server creates a DI scope for each parsed series). A fresh
/// session must be created per series so changes committed by one series never bleed into another.
/// </summary>
public sealed class ImporterSession : IDisposable
{
    internal ImporterSession(DataContext context, IUnitOfWork unitOfWork, IReadingItemService readingItemService,
        IProcessSeries processSeries, IMetadataService metadataService,
        IWordCountAnalyzerService wordCountAnalyzerService)
    {
        Context = context;
        UnitOfWork = unitOfWork;
        ReadingItemService = readingItemService;
        ProcessSeries = processSeries;
        MetadataService = metadataService;
        WordCountAnalyzerService = wordCountAnalyzerService;
    }

    public DataContext Context { get; }

    public IUnitOfWork UnitOfWork { get; }

    public IReadingItemService ReadingItemService { get; }

    public IProcessSeries ProcessSeries { get; }

    public IMetadataService MetadataService { get; }

    public IWordCountAnalyzerService WordCountAnalyzerService { get; }

    public void Dispose()
    {
        Context.Dispose();
    }
}
