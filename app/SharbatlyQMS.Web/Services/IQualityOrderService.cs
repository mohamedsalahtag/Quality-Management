using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services;

public interface IQualityOrderService
{
    Task<IReadOnlyList<QualityOrder>> ListAsync(string? status, string? search, string? plant = null);
    Task<QualityOrder?> GetAsync(long qualityOrderId);
    Task<QualityOrder?> GetByArrivalAsync(long arrivalId);

    /// <summary>Plant code inherited via the QO's arrival (denormalized on
    /// qms_arrival.plant). Null when the QO doesn't exist. Used by the
    /// controller-level plant-scope gate on Details / Edit endpoints.</summary>
    Task<string?> GetPlantForQoAsync(long qualityOrderId);
    Task<IReadOnlyList<QualityOrderMaterial>> GetMaterialsAsync(long qualityOrderId);

    /// <summary>The parent quality_order_id for a QO material, or null if not found.</summary>
    Task<long?> GetQoIdForMaterialAsync(long qoMaterialId);

    /// <summary>
    /// Creates a Quality Order in Initial state, copying every arrival item
    /// onto the QO as a qo_material row (the inspector samples per material).
    /// Caller must have already verified the arrival is Completed.
    /// </summary>
    Task<long> CreateForArrivalAsync(long arrivalId, string user);

    Task<(bool ok, string? error)> OpenAsync(long qualityOrderId, string user);
    /// <summary>V31 (2026-06-20): operator marks data entry complete; locks
    /// the QO until a Supervisor finishes (Close) or cancel-submits.</summary>
    Task<(bool ok, string? error)> SubmitAsync(long qualityOrderId, string user);
    /// <summary>V31: Supervisor returns a Submitted QO to Open so the operator
    /// can fix mistakes. Reason optional but recorded in the audit log.</summary>
    Task<(bool ok, string? error)> CancelSubmitAsync(long qualityOrderId, string user, string? reason);
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
    /// <summary>Active defect categories (V22+), ordered by sort_order; drives the dynamic per-category sections + colours.</summary>
    Task<IReadOnlyList<DefectCategory>> GetActiveCategoriesAsync();

    // ----- Sample header fields (configurable, global, V20+) -----
    /// <summary>Active sample header field catalog (everything except
    /// `sample_size` -- that's still a first-class column on qms_sample).
    /// Used by the sample form to render dynamic inputs.</summary>
    Task<IReadOnlyList<SampleHeaderField>> GetActiveSampleHeaderFieldsAsync();

    /// <summary>Saved header values for one sample, joined with the
    /// catalog so the caller has field_code / field_name / value_kind.</summary>
    Task<IReadOnlyList<SampleHeaderValue>> GetSampleHeaderValuesAsync(long sampleId);

    /// <summary>Batch variant for the PDF builder -- one query for the
    /// whole QO so per-sample cards don't issue N round-trips.</summary>
    Task<ILookup<long, SampleHeaderValue>> GetSampleHeaderValuesBatchAsync(IEnumerable<long> sampleIds);

    /// <summary>Replace the entire header-value set for one sample.
    /// Atomic (DELETE + bulk INSERT in a single transaction). Empty/null
    /// values are dropped -- no need to keep blank rows around.</summary>
    Task SaveSampleHeaderValuesAsync(long sampleId, IEnumerable<SampleHeaderValue> values, string user);

    /// <summary>Material-scoped header values for one QO material (joined with
    /// the catalog). Entered once per material and inherited by every sample.</summary>
    Task<IReadOnlyList<MaterialHeaderValue>> GetMaterialHeaderValuesAsync(long qoMaterialId);

    /// <summary>Batch variant keyed by qo_material_id for the Details page /
    /// PDF builder.</summary>
    Task<ILookup<long, MaterialHeaderValue>> GetMaterialHeaderValuesBatchAsync(IEnumerable<long> qoMaterialIds);

    /// <summary>Save a material's Material-scoped header values (replace whole
    /// set, copy down to every sample). Sample size is set elsewhere via the
    /// Override Size flow (SaveOverrideAsync).</summary>
    Task SaveMaterialHeaderValuesAsync(long qoMaterialId,
        IEnumerable<MaterialHeaderValue> values, string user);

    /// <summary>V31 (2026-06-20): returns qoMaterialId -> isComplete, where
    /// "complete" means every active Material-scoped header field that is
    /// mandatory has a value on that material. Used by the QO Details page to
    /// gate the "Add sample" button and by the Submit precondition.</summary>
    Task<IReadOnlyDictionary<long, bool>> GetMaterialHeaderCompleteMapAsync(long qualityOrderId);

    /// <summary>V31 (2026-06-20): streams one row per (sample × catalog defect)
    /// for the flat data-hub report at /Reports/FlatDefects. Includes zero-value
    /// rows so pivot tables can compute coverage. Filters are AND'd; see
    /// <see cref="FlatDefectFilter"/> for the available fields. Use the
    /// async-enumerable hot path so the Excel export never materialises the
    /// full result set in memory.</summary>
    IAsyncEnumerable<FlatDefectRow> StreamFlatDefectRowsAsync(
        FlatDefectFilter filter, CancellationToken ct);

    // ----- Quality Order PDF: grouped summary -----
    /// <summary>
    /// Rolls every material in the QO up into (MaterialGroup, Brand, Variety,
    /// Grade) groups for the PDF's page-1 summary. Each group carries:
    /// Σ sample_size, Σ gross (qty × MARA weight) and Σ tara (TARA readings)
    /// → derived Net; the FULL active defect catalog for the group's
    /// material_group with per-defect Σ value / Σ size × 100 percentages
    /// bucketed Major (Major+Critical) / Minor (everything else); and one
    /// pre-rendered display value per active reading type honoring its
    /// display_mode (text|count|sum|sum_over_size|formula). Brand / Variety
    /// / Grade / MARA weight come live from <see cref="IMaraService"/> via
    /// the supplied materials enriched with ApplyMara.
    /// </summary>
    Task<IReadOnlyList<MaterialGroupSummary>> BuildGroupSummariesAsync(
        long qualityOrderId, IReadOnlyList<QualityOrderMaterial> materials);
}
