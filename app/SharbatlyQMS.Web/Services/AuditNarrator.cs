using System.Globalization;
using System.Text.Json;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Turns an audit row into a sentence a non-technical reader understands.
///
/// The log stores <c>entity_type</c> + <c>action_code</c> + two JSON blobs,
/// which is the right storage shape and the wrong reading shape: "Sample /
/// Updated / #48213" tells a quality manager nothing. Everything here is
/// presentation only — no stored data changes, and the raw JSON stays one
/// click away for anyone troubleshooting.
/// </summary>
public static class AuditNarrator
{
    /// <summary>
    /// Entity types that are per-sample bookkeeping. They are 89% of the log
    /// (58,664 of 65,764 rows on 2026-08-20), so showing them by default buries
    /// the user, role and order changes people actually come here to find.
    /// Hidden unless the reader asks for technical detail.
    /// </summary>
    public static readonly string[] NoisyEntityTypes =
    {
        EntityTypes.Sample,
        EntityTypes.SampleReading,
        EntityTypes.SampleDefect,
        EntityTypes.QualityOrderMaterial,
        EntityTypes.ArrivalItem,
    };

    public static bool IsNoisy(string? entityType) =>
        entityType != null &&
        NoisyEntityTypes.Contains(entityType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Entity types whose changes are about people and access rather
    /// than fruit. These are what the Users &amp; security tab collects.</summary>
    public static readonly string[] SecurityEntityTypes =
    {
        EntityTypes.User,
        EntityTypes.Role,
    };

    /// <summary>Plain name for a record type. The raw constants leak developer
    /// vocabulary ("QualityOrderMaterial", "SampleHeaderField").</summary>
    public static string EntityLabel(string? t) => t switch
    {
        EntityTypes.Arrival              => "Arrival",
        EntityTypes.ArrivalItem          => "Arrival line",
        EntityTypes.ArrivalChecklist     => "Arrival checklist",
        EntityTypes.QualityOrder         => "Quality order",
        EntityTypes.QualityOrderMaterial => "Order material",
        EntityTypes.Sample               => "Sample",
        EntityTypes.SampleReading        => "Sample reading",
        EntityTypes.SampleDefect         => "Sample defect",
        EntityTypes.Claim                => "Claim",
        EntityTypes.ClaimNote            => "Claim note",
        EntityTypes.User                 => "User",
        EntityTypes.Configuration        => "Settings",
        EntityTypes.DefectCatalog        => "Defect",
        EntityTypes.DefectCategory       => "Defect category",
        EntityTypes.ReadingType          => "Reading type",
        EntityTypes.SampleHeaderField    => "Sample field",
        EntityTypes.ArrivalField         => "Arrival field",
        EntityTypes.ReportUnit           => "Report unit",
        EntityTypes.CodeDescription      => "Code description",
        EntityTypes.Role                 => "Role",
        EntityTypes.System               => "System",
        _                                => t ?? ""
    };

    /// <summary>Past-tense verb for an action, as a person would say it.</summary>
    public static string ActionLabel(string? a) => a switch
    {
        ActionCodes.Created         => "created",
        ActionCodes.Updated         => "changed",
        ActionCodes.Deleted         => "deleted",
        ActionCodes.Opened          => "opened",
        ActionCodes.Submitted       => "submitted",
        ActionCodes.CancelSubmit    => "withdrew the submission of",
        ActionCodes.Closed          => "finished",
        ActionCodes.Reopened        => "reopened",
        ActionCodes.Cancelled       => "cancelled",
        ActionCodes.ClaimRequest    => "raised a claim on",
        ActionCodes.PassedQC        => "cleared",
        ActionCodes.Approved        => "approved the claim on",
        ActionCodes.Hold            => "put the claim on hold for",
        ActionCodes.Override        => "overrode the sample size on",
        ActionCodes.OverrideCleared => "cleared the sample-size override on",
        ActionCodes.PasswordReset   => "reset the password for",
        ActionCodes.Purged          => "purged",
        _                           => (a ?? "changed").ToLowerInvariant()
    };

    /// <summary>Short chip label for the action filter.</summary>
    public static string ActionChip(string? a) => a switch
    {
        ActionCodes.CancelSubmit    => "Submission withdrawn",
        ActionCodes.Closed          => "Finished",
        ActionCodes.PassedQC        => "Passed QC",
        ActionCodes.ClaimRequest    => "Claim raised",
        ActionCodes.Override        => "Size override",
        ActionCodes.OverrideCleared => "Override cleared",
        ActionCodes.PasswordReset   => "Password reset",
        _                           => a ?? ""
    };

    // ---- Record identity ---------------------------------------------------

    /// <summary>
    /// The subject phrase for a record, built from the business keys the
    /// service resolved: "sample 3 of QO-2026-000353" rather than "#91204".
    ///
    /// Returns "" when the keys could not be resolved (a deleted parent, a
    /// pre-migration row) so the caller falls back to the type and id — an
    /// honest "#91204" beats a confident half-sentence.
    /// </summary>
    public static string RecordLabel(string? entityType, string? qoNo, string? container, string? extra)
    {
        var qo    = (qoNo ?? "").Trim();
        var cn    = (container ?? "").Trim();
        var ex    = (extra ?? "").Trim();
        var onQo  = qo.Length > 0 ? $" of {qo}" : "";

        return entityType switch
        {
            EntityTypes.QualityOrder         => qo.Length > 0 ? $"quality order {qo}" : "",
            EntityTypes.QualityOrderMaterial => ex.Length > 0 ? $"material {ex}{onQo}" : Trim($"a material line{onQo}"),
            EntityTypes.Sample               => ex.Length > 0 ? $"sample {ex}{onQo}" : Trim($"a sample{onQo}"),
            EntityTypes.SampleReading        => ex.Length > 0 ? $"the readings on sample {ex}{onQo}" : Trim($"sample readings{onQo}"),
            EntityTypes.SampleDefect         => ex.Length > 0 ? $"the defects on sample {ex}{onQo}" : Trim($"sample defects{onQo}"),
            EntityTypes.Arrival              => ArrivalPhrase("arrival", ex, cn),
            EntityTypes.ArrivalChecklist     => ArrivalPhrase("the checklist on arrival", ex, cn),
            EntityTypes.ArrivalItem          => ArrivalPhrase("a line on arrival", ex, cn),
            EntityTypes.Claim                => qo.Length > 0 ? $"the claim on {qo}" : "",
            EntityTypes.ClaimNote            => qo.Length > 0 ? $"a note on the claim for {qo}" : "",
            EntityTypes.User                 => ex.Length > 0 ? $"user {ex}" : "",
            EntityTypes.Role                 => ex.Length > 0 ? $"the {ex} role" : "",
            _                                => ""
        };

        static string ArrivalPhrase(string lead, string arrivalNo, string container)
        {
            if (arrivalNo.Length == 0 && container.Length == 0) return "";
            var id = arrivalNo.Length > 0 ? arrivalNo : container;
            return container.Length > 0 && arrivalNo.Length > 0
                ? $"{lead} {id} ({container})"
                : $"{lead} {id}";
        }

        static string Trim(string s) => s.Trim();
    }

    /// <summary>
    /// Recovers a role code from an audit payload. Role rows are written with
    /// entity_id 0, so the code in the JSON is the only identity they carry.
    /// </summary>
    public static string? RoleCodeFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "roleCode", "RoleCode", "role_code" })
                if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }
        catch (JsonException) { return null; }
    }

    // ---- The sentence ------------------------------------------------------

    /// <summary>
    /// The sentence. Uses <see cref="AuditEntryListRow.DisplayLabel"/> when the
    /// service resolved a business key, and falls back to "&lt;type&gt; #&lt;id&gt;" when
    /// it did not — never a bare "#0", which is what every role row would
    /// otherwise read as.
    /// </summary>
    public static string Sentence(AuditEntryListRow e)
    {
        var what = Subject(e);
        var verb = ActionLabel(e.ActionCode);

        // The claim transitions name the claim in the verb ("approved the claim
        // on"), and the subject for a claim row names it again — which read as
        // "approved the claim on the claim on QO-2026-000442".
        const string claimPrefix = "the claim on ";
        if (verb.Contains("claim", StringComparison.OrdinalIgnoreCase) &&
            what.StartsWith(claimPrefix, StringComparison.OrdinalIgnoreCase))
            what = what[claimPrefix.Length..];

        var sentence = $"{e.ChangedBy} {verb} {what}";

        // One trailing clause when a single field moved — the commonest case by
        // far, and it saves opening the detail for things like a status flip.
        var diffs = Expand(e);
        if (e.ActionCode == ActionCodes.Updated && diffs.Count == 1)
        {
            var d = diffs[0];
            sentence += $" — {d.Field}: {Short(d.Old)} → {Short(d.New)}";
        }
        else if (e.ActionCode == ActionCodes.Updated && diffs.Count > 1)
        {
            sentence += $" — {diffs.Count} changes";
        }

        return sentence + ".";
    }

    private static string Subject(AuditEntryListRow e)
    {
        var label = (e.DisplayLabel ?? "").Trim();
        if (label.Length > 0) return label;

        // entity_id is 0 for every Role row, so "#0" would be actively misleading.
        var type = EntityLabel(e.EntityType).ToLowerInvariant();
        return e.EntityId > 0 ? $"{type} #{e.EntityId}" : type;
    }

    // ---- Field and value presentation --------------------------------------

    /// <summary>De-snake and de-camel a JSON property name for display, with a
    /// short table of names whose mechanical de-camelling still reads badly.</summary>
    public static string FieldLabel(string? field)
    {
        if (string.IsNullOrWhiteSpace(field)) return "";

        var known = field.ToLowerInvariant() switch
        {
            "qono" or "quality_order_no" or "qualityorderno" => "QC number",
            "statuscode" or "status_code"                    => "Status",
            "rolecode"   or "role_code"                      => "Role",
            "plantcode"  or "plant_code" or "plant"          => "Plant",
            "containerno" or "container_no"                  => "Container",
            "isactive"   or "is_active"                      => "Active",
            "isplantscoped" or "is_plant_scoped"             => "Plant-scoped",
            "passwordhash" or "password_hash"                => "Password",
            "grants"                                         => "Permissions",
            "closereason" or "close_reason"                  => "Reason for finishing",
            "reopenreason" or "reopen_reason"                => "Reason for reopening",
            "displayname" or "display_name"                  => "Name",
            "fullname"    or "full_name"                     => "Full name",
            _ => null
        };
        if (known != null) return known;

        // A permission code is already a name — "Arrivals.Details" de-camelled
        // becomes "Arrivals. Details", which reads as a typo and no longer
        // matches what the Security screen shows.
        if (field.Contains('.')) return field;

        var spaced = field.Replace('_', ' ');
        var chars = new List<char>(spaced.Length + 8);
        for (int i = 0; i < spaced.Length; i++)
        {
            if (i > 0 && char.IsUpper(spaced[i]) && char.IsLetterOrDigit(spaced[i - 1]) && !char.IsUpper(spaced[i - 1]))
                chars.Add(' ');
            chars.Add(spaced[i]);
        }

        // Sentence case, not Title Case: these sit beside hand-written labels
        // like "Reason for finishing", and a column mixing the two looks broken.
        // Acronyms stay shouting — "IP" must not become "Ip".
        var words = new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var isAcronym = w.Length >= 2 && w.All(char.IsUpper);
            if (isAcronym) continue;
            words[i] = i == 0
                ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()
                : w.ToLowerInvariant();
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// A stored value as a person would read it: true/false become Yes/No, a
    /// UTC timestamp becomes a local date and time, an access level becomes
    /// "Read only" instead of "1", and an empty value says so rather than
    /// rendering as a blank cell you cannot tell apart from a layout bug.
    /// </summary>
    public static string ValueLabel(string? field, string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0 || v == "null") return "(empty)";

        // A list-valued field (a user's plants, say) is stored as a JSON array
        // and would otherwise reach the screen as ["BH01","JD01"] — punctuation
        // a reader has to decode before they can compare the two sides.
        if (v.Length >= 2 && v[0] == '[')
        {
            var list = TryParseArray(v);
            if (list != null) return list.Count == 0 ? "(empty)" : string.Join(", ", list);
        }

        // Access levels: stored as the tinyint behind AccessLevel.
        if (IsAccessLevelField(field) && v.Length == 1 && v[0] is >= '0' and <= '9')
            return AccessLevelLabel(v);

        if (bool.TryParse(v, out var b)) return b ? "Yes" : "No";

        // Timestamps are written as round-trip UTC. Render them where the reader
        // lives, in the same format the table's When column uses.
        if (v.Length >= 16 && (v.Contains('T') || v.Contains(':'))
            && DateTime.TryParse(v, CultureInfo.InvariantCulture,
                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        // A password hash is never worth showing, and showing part of one is worse.
        if (field != null && field.Contains("password", StringComparison.OrdinalIgnoreCase))
            return "(not shown)";

        return v;
    }

    private static bool IsAccessLevelField(string? field) =>
        field != null &&
        (field.Contains("access", StringComparison.OrdinalIgnoreCase) ||
         field.Contains("level",  StringComparison.OrdinalIgnoreCase) ||
         field.StartsWith("perm", StringComparison.OrdinalIgnoreCase));

    public static string AccessLevelLabel(string? raw) => (raw ?? "").Trim() switch
    {
        "0" or "None" => "No access",
        "1" or "Read" => "Read only",
        "2" or "Edit" => "Full access",
        ""            => "No access",
        var other     => other
    };

    // ---- Expanded, readable diffs ------------------------------------------

    /// <summary>How a value moved, so the view can colour it without re-deriving.</summary>
    public enum DiffKind { Added, Removed, Changed }

    /// <summary>One change, already in reading form: labelled field, labelled
    /// values, and which way it went.</summary>
    public sealed record FriendlyDiff(string Field, string Old, string New, DiffKind Kind);

    private const int MapExpansionLimit = 200;

    /// <summary>
    /// The row's changes, in the form the screen shows them.
    ///
    /// Two things happen here that the raw diff cannot do. Nested objects — the
    /// role permission map above all — are opened up so a grant change reads as
    /// one line per permission instead of one line holding 200 keys of JSON.
    /// And every value goes through <see cref="ValueLabel"/>, so nobody has to
    /// know that access level 2 means full access.
    /// </summary>
    public static IReadOnlyList<FriendlyDiff> Expand(AuditEntryListRow e)
    {
        var outRows = new List<FriendlyDiff>();

        foreach (var d in e.DiffRows)
        {
            if (TryExpandMap(d, outRows)) continue;

            var oldV = ValueLabel(d.FieldName, d.OldValue);
            var newV = ValueLabel(d.FieldName, d.NewValue);
            if (oldV == newV) continue;
            outRows.Add(new FriendlyDiff(FieldLabel(d.FieldName), oldV, newV, KindOf(d.OldValue, d.NewValue)));
        }

        return outRows;
    }

    /// <summary>
    /// Opens a nested JSON object on either side of a diff into one row per key
    /// that actually moved. Returns false when the field is not a map, so the
    /// caller falls back to treating it as a plain value.
    /// </summary>
    private static bool TryExpandMap(DiffRow d, List<FriendlyDiff> sink)
    {
        var oldMap = TryParseMap(d.OldValue);
        var newMap = TryParseMap(d.NewValue);
        if (oldMap == null && newMap == null) return false;

        oldMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        newMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var keys = new SortedSet<string>(oldMap.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in newMap.Keys) keys.Add(k);

        // A role's permission map is ~200 keys. If a save somehow moved all of
        // them, listing every one would bury the page — say so instead and leave
        // the raw JSON, which is still one click away, to carry the detail.
        if (keys.Count > MapExpansionLimit)
        {
            sink.Add(new FriendlyDiff(FieldLabel(d.FieldName),
                $"{oldMap.Count} entries", $"{newMap.Count} entries", DiffKind.Changed));
            return true;
        }

        var isGrants = d.FieldName.Contains("grant", StringComparison.OrdinalIgnoreCase)
                       || d.FieldName.Contains("perm", StringComparison.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            oldMap.TryGetValue(key, out var o);
            newMap.TryGetValue(key, out var n);
            if (string.Equals(o, n, StringComparison.Ordinal)) continue;

            var oldV = isGrants ? AccessLevelLabel(o) : ValueLabel(key, o);
            var newV = isGrants ? AccessLevelLabel(n) : ValueLabel(key, n);
            if (oldV == newV) continue;

            sink.Add(new FriendlyDiff(FieldLabel(key), oldV, newV, KindOf(o, n)));
        }
        return true;
    }

    private static Dictionary<string, string>? TryParseMap(string? json)
    {
        var s = (json ?? "").TrimStart();
        if (!s.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // A map of maps is beyond what a table row can show; refuse it so
                // the caller renders the raw JSON rather than a misleading flattening.
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return null;
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null   => "",
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    _                    => prop.Value.GetRawText()
                };
            }
            return map;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads a JSON array of scalars. Returns null for anything else,
    /// including an array of objects — flattening one of those would invent a
    /// reading that is not in the data.</summary>
    private static List<string>? TryParseArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var items = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return null;
                items.Add(el.ValueKind switch
                {
                    JsonValueKind.Null   => "",
                    JsonValueKind.String => el.GetString() ?? "",
                    JsonValueKind.True   => "Yes",
                    JsonValueKind.False  => "No",
                    _                    => el.GetRawText()
                });
            }
            return items;
        }
        catch (JsonException) { return null; }
    }

    private static DiffKind KindOf(string? oldV, string? newV)
    {
        // An empty list and an empty object are "nothing was there", the same as
        // a null — otherwise granting a first plant renders as a change from a
        // struck-through "(empty)" rather than as an addition.
        static bool Present(string? v)
        {
            v = (v ?? "").Trim();
            return v.Length > 0 && v != "0" && v != "null" && v != "[]" && v != "{}";
        }

        var hadOld = Present(oldV);
        var hasNew = Present(newV);
        if (!hadOld && hasNew) return DiffKind.Added;
        if (hadOld && !hasNew) return DiffKind.Removed;
        return DiffKind.Changed;
    }

    private const int InlineValueChars = 48;

    private static string Short(string? v)
    {
        v = (v ?? "").Trim();
        return v.Length <= InlineValueChars ? v : v[..InlineValueChars] + "…";
    }
}
