using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.API.Services.Plus;
using Kavita.API.Services.ReadingLists;
using Kavita.API.Services.SignalR;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.Collection;
using Kavita.Models.DTOs.KavitaPlus.ExternalMetadata;
using Kavita.Models.DTOs.KavitaPlus.ExternalMetadata.Covers;
using Kavita.Models.DTOs.KavitaPlus.Metadata;
using Kavita.Models.DTOs.Metadata.Matching;
using Kavita.Models.DTOs.ReadingLists;
using Kavita.Models.DTOs.ReadingLists.Request;
using Kavita.Models.DTOs.SeriesDetail;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Enums.Audit;
using Kavita.Models.Entities.ReadingLists;
using Kavita.Models.Entities.User;

namespace Kavita.Importer.Infrastructure;

/// <summary>
/// No-op IEventHub: there is no SignalR client connected during a CLI import.
/// </summary>
public sealed class NoOpEventHub : IEventHub
{
    public Task SendMessageAsync(string method, SignalRMessage message, bool onlyAdmins = true,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task SendMessageToAsync(string method, SignalRMessage message, int userId,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// No-op IMediaErrorService: real implementation enqueues background jobs via Hangfire which is not
/// initialized in the CLI. Errors are simply ignored; import progress is surfaced through the logger.
/// </summary>
public sealed class NoOpMediaErrorService : IMediaErrorService
{
    public void ReportMediaIssue(string filename, MediaErrorProducer producer, string errorMessage, string details)
    {
    }

    public void ReportMediaIssue(string filename, MediaErrorProducer producer, string errorMessage, Exception ex)
    {
    }

    public Task ReportMediaIssueAsync(string filename, MediaErrorProducer producer, string errorMessage,
        string details, CancellationToken ct = default) => Task.CompletedTask;

    public Task ReportMediaIssueAsync(string filename, MediaErrorProducer producer, string errorMessage, Exception ex,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// No-op IReadingListService: reading-list generation is a Kavita+ / UI side feature and not part of a
/// plain import. Only the method invoked from ProcessSeries (when the library opts into reading lists)
/// is implemented as a no-op so imports never fail on that path.
/// </summary>
public sealed class NoOpReadingListService : IReadingListService
{
    public Task<ReadingList> CreateReadingListForUser(AppUser userWithReadingList, string title) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task UpdateReadingList(ReadingList readingList, UpdateReadingListDto dto) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<bool> RemoveFullyReadItems(int readingListId, AppUser user) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<bool> UpdateReadingListItemPosition(UpdateReadingListPosition dto) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<bool> DeleteReadingListItem(UpdateReadingListPosition dto) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<AppUser?> UserHasReadingListAccess(int readingListId, string username) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<bool> DeleteReadingList(int readingListId, AppUser user) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task CalculateReadingListAgeRating(ReadingList readingList) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<bool> AddChaptersToReadingList(int seriesId, IList<int> chapterIds, ReadingList readingList) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task CalculateStartAndEndDates(ReadingList readingListWithItems) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task CreateReadingListsFromSeries(Series series, Library library) => Task.CompletedTask;

    public Task<string> GenerateReadingListCoverImage(int readingListId, bool commit = false) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<string> GenerateReadingListCoverImage(ReadingList readingList) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task UpdateReadingListCoverImage(ReadingList readingList) => Task.CompletedTask;

    public Task UpdateReadingListAgeRatingForSeries(int seriesId, AgeRating ageRating) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");

    public Task<IList<ReadingListItemDto>> GetReadingListItems(int readingListId, int userId,
        UserParams? userParams = null) =>
        throw new NotSupportedException("Reading list management is not available in Kavita.Importer.");
}

/// <summary>
/// No-op IExternalMetadataService: Kavita+ / external metadata endpoints must never be contacted from the
/// CLI. Match/load helpers report nothing matched so the importer never touches the network.
/// </summary>
public sealed class NoOpExternalMetadataService : IExternalMetadataService
{
    public Task<ExternalSeriesDetailDto?> GetExternalSeriesDetail(int seriesId, MetadataRequest request,
        int? recommendedSeriesId, CancellationToken ct = default) =>
        Task.FromResult<ExternalSeriesDetailDto?>(null);

    public Task FetchExternalDataTask(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IList<MalStackDto>> GetStacksForUser(int userId, CancellationToken ct = default) =>
        Task.FromResult<IList<MalStackDto>>([]);

    public Task<MatchSeriesResultDto?> MatchSeries(MatchSeriesDto dto, CancellationToken ct = default) =>
        Task.FromResult<MatchSeriesResultDto?>(null);

    public Task FixSeriesMatch(int seriesId, ExternalMetadataIdsDto ids, MetadataProvider? provider = null,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateSeriesDontMatch(int seriesId, bool dontMatch, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task UpdateSeriesMetadataProviderOverride(int seriesId, MetadataProvider? metadataProviderOverride,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> WriteExternalMetadataToSeries(ExternalSeriesDetailDto externalMetadata, int seriesId,
        MetadataFetchTrigger trigger = MetadataFetchTrigger.OnDemand, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<IList<ExternalCoverResponseDto>> GetExternalCovers(int seriesId, int? volumeId = null,
        int? chapterId = null, CancellationToken ct = default) =>
        Task.FromResult<IList<ExternalCoverResponseDto>>([]);

    public Task<SeriesDetailPlusDto?> TryMatchAndLoadMetadataForSeries(int seriesId, LibraryType libraryType,
        MetadataFetchTrigger trigger, CancellationToken ct = default) =>
        Task.FromResult<SeriesDetailPlusDto?>(null);

    public Task<bool> WouldNameChangeOrphanMergedFiles(Series series, string? proposedName,
        CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> WouldLocalizedNameChangeOrphanMergedFiles(Series series, string? proposedLocalizedName,
        CancellationToken ct = default) => Task.FromResult(false);
}
