using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public interface IQualityOrderService
{
    Task<IReadOnlyList<QualityOrder>> ListAsync(string? status, string? search);
    Task<QualityOrder?> GetAsync(long qualityOrderId);
    Task<QualityOrder?> GetByArrivalAsync(long arrivalId);
    Task<IReadOnlyList<QualityOrderMaterial>> GetMaterialsAsync(long qualityOrderId);

    /// <summary>
    /// Creates a Quality Order in Initial state, copying every arrival item
    /// onto the QO as a qo_material row (the inspector samples per material).
    /// Caller must have already verified the arrival is Completed.
    /// </summary>
    Task<long> CreateForArrivalAsync(long arrivalId, string user);

    Task<(bool ok, string? error)> OpenAsync(long qualityOrderId, string user);
    Task<(bool ok, string? error)> CloseAsync(long qualityOrderId, string user, string? reason);
    Task<(bool ok, string? error)> ReopenAsync(long qualityOrderId, string user, string? reason);
    Task<(bool ok, string? error)> CancelAsync(long qualityOrderId, string user, string? reason);

    Task SaveOverrideAsync(long qoMaterialId, string newSize, string? reason, string user);
    Task ClearOverrideAsync(long qoMaterialId, string user);

    // ----- Samples -----
    Task<IReadOnlyList<Sample>> ListSamplesAsync(long qualityOrderId);
    Task<Sample?> GetSampleAsync(long sampleId);
    Task<long> CreateSampleAsync(Sample sample);
    Task UpdateSampleAsync(Sample sample);
    Task SoftDeleteSampleAsync(long sampleId, string user);

    // ----- Readings + Defects (full replace per save) -----
    Task<IReadOnlyList<SampleReading>> GetReadingsAsync(long sampleId);
    Task<ILookup<long, SampleReading>> GetReadingsBatchAsync(IEnumerable<long> sampleIds);
    Task SaveReadingsAsync(long sampleId, IEnumerable<SampleReading> readings, string user);
    Task<IReadOnlyList<SampleDefect>> GetDefectsAsync(long sampleId);
    Task<ILookup<long, SampleDefect>> GetDefectsBatchAsync(IEnumerable<long> sampleIds);
    Task SaveDefectsAsync(long sampleId, IEnumerable<SampleDefect> defects, string user);

    // ----- Catalogs (read for forms) -----
    Task<IReadOnlyList<ReadingTypeEntry>> GetActiveReadingTypesAsync();
    Task<IReadOnlyList<ReadingTypeEntry>> GetActiveReadingTypesForGroupAsync(string? materialGroup);
    Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsAsync();
    Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsForGroupAsync(string? materialGroup);
    Task<IReadOnlyDictionary<string, string>> GetDisplaySectionMapAsync(string? materialGroup, string? majorCategory);
}
