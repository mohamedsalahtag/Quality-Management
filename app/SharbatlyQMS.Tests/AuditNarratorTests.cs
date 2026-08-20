using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The reading layer of the audit log. Pure functions over a row, so these run
/// without a database.
///
/// What they are guarding: the log is the only record of who changed what, and
/// it is read under pressure. A value rendered as "2" when it means "Full
/// access", or a role row rendered as "#0", is not a cosmetic problem — it is
/// the difference between an answer and a dead end.
/// </summary>
public class AuditNarratorTests
{
    private static AuditEntryListRow Row(
        string entityType, long entityId, string action,
        string? oldJson = null, string? newJson = null, string changedBy = "mohamed.tag")
    {
        var r = new AuditEntryListRow
        {
            EntityType    = entityType,
            EntityId      = entityId,
            ActionCode    = action,
            OldValuesJson = oldJson,
            NewValuesJson = newJson,
            ChangedBy     = changedBy,
            ChangedAt     = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc)
        };
        r.DiffRows = AuditService.ParseDiffs(oldJson, newJson);
        return r;
    }

    // ---- Values a person can read -----------------------------------------

    [Theory]
    [InlineData("isActive", "true",  "Yes")]
    [InlineData("isActive", "false", "No")]
    [InlineData("anything", "",      "(empty)")]
    [InlineData("anything", null,    "(empty)")]
    [InlineData("anything", "null",  "(empty)")]
    [InlineData("statusCode", "Closed", "Closed")]
    public void ValueLabel_renders_values_as_words(string field, string? raw, string expected)
        => Assert.Equal(expected, AuditNarrator.ValueLabel(field, raw));

    [Theory]
    [InlineData("[]",                  "(empty)")]
    [InlineData("[\"JD01\"]",           "JD01")]
    [InlineData("[\"BH01\",\"JD01\"]",  "BH01, JD01")]
    public void ValueLabel_reads_a_list_as_a_list(string raw, string expected)
        // Plant access is stored as a JSON array. Rendered raw it reaches the
        // screen as ["BH01","JD01"] — punctuation the reader has to decode
        // before they can compare the two sides of the change.
        => Assert.Equal(expected, AuditNarrator.ValueLabel("plants", raw));

    [Fact]
    public void Expand_treats_a_first_plant_as_an_addition()
    {
        var row = Row(EntityTypes.User, 42, ActionCodes.Updated,
            oldJson: """{"plants":[]}""",
            newJson: """{"plants":["JD01"]}""");

        var change = Assert.Single(AuditNarrator.Expand(row));
        Assert.Equal("Plants", change.Field);
        Assert.Equal("JD01", change.New);
        Assert.Equal(AuditNarrator.DiffKind.Added, change.Kind);
    }

    [Fact]
    public void ValueLabel_never_shows_a_password_hash()
    {
        var hash = "AQAAAAIAAYagAAAAEJ8k2p+Zk1hV3rWl0mQ==";
        Assert.Equal("(not shown)", AuditNarrator.ValueLabel("PasswordHash", hash));
        Assert.DoesNotContain("AQAA", AuditNarrator.ValueLabel("passwordHash", hash));
    }

    [Fact]
    public void ValueLabel_renders_a_utc_timestamp_in_local_time()
    {
        // Written as round-trip UTC; the table shows local, and a value that
        // disagrees with the column beside it is worse than no value.
        var utc    = new DateTime(2026, 8, 20, 22, 30, 0, DateTimeKind.Utc);
        var actual = AuditNarrator.ValueLabel("closedAt", utc.ToString("o"));
        Assert.Equal(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), actual);
    }

    [Theory]
    [InlineData("0", "No access")]
    [InlineData("1", "Read only")]
    [InlineData("2", "Full access")]
    [InlineData("",  "No access")]
    public void AccessLevelLabel_spells_out_the_stored_number(string raw, string expected)
        => Assert.Equal(expected, AuditNarrator.AccessLevelLabel(raw));

    // ---- Field names ------------------------------------------------------

    [Theory]
    [InlineData("quality_order_no", "QC number")]
    [InlineData("statusCode",       "Status")]
    [InlineData("closeReason",      "Reason for finishing")]
    [InlineData("someOtherField",   "Some other field")]
    [InlineData("plain_snake_case", "Plain snake case")]
    public void FieldLabel_reads_as_english(string field, string expected)
        => Assert.Equal(expected, AuditNarrator.FieldLabel(field));

    // ---- Permission maps --------------------------------------------------

    [Fact]
    public void Expand_opens_a_role_grant_map_into_one_row_per_permission()
    {
        // This is the shape SecurityAdminService writes: the whole grant map on
        // both sides. Rendered raw it is a 200-key JSON blob in a table cell.
        var row = Row(EntityTypes.Role, 0, ActionCodes.Updated,
            oldJson: """{"roleCode":"QcOperator","grants":{"Arrivals.Details":1,"Claims.Approve":0}}""",
            newJson: """{"roleCode":"QcOperator","grants":{"Arrivals.Details":2,"Claims.Approve":0}}""");

        var changes = AuditNarrator.Expand(row);

        var moved = Assert.Single(changes);
        Assert.Equal("Arrivals.Details", moved.Field);
        Assert.Equal("Read only",   moved.Old);
        Assert.Equal("Full access", moved.New);
        Assert.Equal(AuditNarrator.DiffKind.Changed, moved.Kind);
    }

    [Fact]
    public void Expand_marks_a_newly_granted_permission_as_added()
    {
        var row = Row(EntityTypes.Role, 0, ActionCodes.Updated,
            oldJson: """{"grants":{"Claims.Approve":0}}""",
            newJson: """{"grants":{"Claims.Approve":2}}""");

        var change = Assert.Single(AuditNarrator.Expand(row));
        Assert.Equal(AuditNarrator.DiffKind.Added, change.Kind);
        Assert.Equal("Full access", change.New);
    }

    [Fact]
    public void Expand_marks_a_revoked_permission_as_removed()
    {
        var row = Row(EntityTypes.Role, 0, ActionCodes.Updated,
            oldJson: """{"grants":{"Claims.Approve":2}}""",
            newJson: """{"grants":{"Claims.Approve":0}}""");

        var change = Assert.Single(AuditNarrator.Expand(row));
        Assert.Equal(AuditNarrator.DiffKind.Removed, change.Kind);
    }

    [Fact]
    public void Expand_hides_fields_that_only_look_different()
    {
        // "true" and "True" are the same answer. Showing them as a change sends
        // someone hunting for a difference that is not there.
        var row = Row(EntityTypes.User, 12, ActionCodes.Updated,
            oldJson: """{"isActive":true,"role":"QcOperator"}""",
            newJson: """{"isActive":true,"role":"QcSupervisor"}""");

        var change = Assert.Single(AuditNarrator.Expand(row));
        Assert.Equal("Role", change.Field);
    }

    // ---- Sentences --------------------------------------------------------

    [Fact]
    public void Sentence_uses_the_resolved_business_key()
    {
        var row = Row(EntityTypes.Sample, 91204, ActionCodes.Updated,
            oldJson: """{"weight":"12.0"}""", newJson: """{"weight":"12.4"}""");
        row.DisplayLabel = AuditNarrator.RecordLabel(EntityTypes.Sample, "QO-2026-000353", "MSCU1234567", "3");

        var sentence = AuditNarrator.Sentence(row);

        Assert.Contains("sample 3 of QO-2026-000353", sentence);
        Assert.DoesNotContain("91204", sentence);
    }

    [Theory]
    [InlineData(ActionCodes.Approved,     "approved the claim on QO-2026-000442.")]
    [InlineData(ActionCodes.ClaimRequest, "raised a claim on QO-2026-000442.")]
    public void Sentence_does_not_say_the_claim_twice(string action, string expectedTail)
    {
        // "approved the claim on" + "the claim on QO-…" produced "approved the
        // claim on the claim on QO-2026-000442", which shipped to the screen.
        var row = Row(EntityTypes.Claim, 13, action);
        row.DisplayLabel = AuditNarrator.RecordLabel(EntityTypes.Claim, "QO-2026-000442", null, null);

        Assert.EndsWith(expectedTail, AuditNarrator.Sentence(row));
    }

    [Fact]
    public void Sentence_still_names_the_claim_for_a_plain_edit()
    {
        // Only the claim-specific verbs repeat it; "changed" must still say what
        // was changed.
        var row = Row(EntityTypes.Claim, 13, ActionCodes.Updated);
        row.DisplayLabel = AuditNarrator.RecordLabel(EntityTypes.Claim, "QO-2026-000442", null, null);

        Assert.Contains("the claim on QO-2026-000442", AuditNarrator.Sentence(row));
    }

    [Fact]
    public void Sentence_falls_back_to_the_id_when_nothing_resolved()
    {
        // An honest "#91204" beats a confident half-sentence about a record whose
        // parent has been deleted.
        var row = Row(EntityTypes.Sample, 91204, ActionCodes.Updated);
        Assert.Contains("#91204", AuditNarrator.Sentence(row));
    }

    [Fact]
    public void Sentence_never_says_hash_zero_for_a_role()
    {
        // Every Role row is written with entity_id 0, so the fallback must not
        // reach for the id — "#0" is a real string that has appeared on screen.
        var row = Row(EntityTypes.Role, 0, ActionCodes.Created,
            newJson: """{"roleCode":"QcAuditor","displayName":"Auditor"}""");
        row.DisplayLabel = AuditNarrator.RecordLabel(
            EntityTypes.Role, null, null, AuditNarrator.RoleCodeFromJson(row.NewValuesJson));

        var sentence = AuditNarrator.Sentence(row);

        Assert.DoesNotContain("#0", sentence);
        Assert.Contains("QcAuditor role", sentence);
    }

    [Fact]
    public void RoleCodeFromJson_reads_the_code_out_of_either_casing()
    {
        Assert.Equal("QcOperator", AuditNarrator.RoleCodeFromJson("""{"roleCode":"QcOperator"}"""));
        Assert.Equal("QcOperator", AuditNarrator.RoleCodeFromJson("""{"RoleCode":"QcOperator"}"""));
        Assert.Null(AuditNarrator.RoleCodeFromJson("""{"somethingElse":1}"""));
        Assert.Null(AuditNarrator.RoleCodeFromJson("not json"));
    }

    [Fact]
    public void RecordLabel_returns_empty_when_the_keys_could_not_be_resolved()
    {
        Assert.Equal("", AuditNarrator.RecordLabel(EntityTypes.QualityOrder, null, null, null));
        Assert.Equal("", AuditNarrator.RecordLabel(EntityTypes.Claim, "", "", ""));
    }

    [Fact]
    public void NoisyEntityTypes_are_the_per_sample_ones()
    {
        Assert.True(AuditNarrator.IsNoisy(EntityTypes.SampleReading));
        Assert.True(AuditNarrator.IsNoisy(EntityTypes.Sample));
        Assert.False(AuditNarrator.IsNoisy(EntityTypes.User));
        Assert.False(AuditNarrator.IsNoisy(EntityTypes.Role));
        Assert.False(AuditNarrator.IsNoisy(null));
    }
}
