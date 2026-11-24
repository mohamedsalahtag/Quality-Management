using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Who the test host "notifies": nobody real.
///
/// The suite runs against the live database, where the real lookup would
/// return the managers of a real branch -- and a test would then put a notice
/// in their bell and an e-mail in the outbox the deployed worker sends from.
/// These stand-ins have ids no portal user holds and an address that cannot
/// be delivered. The real lookup is tested directly, read-only, in
/// SurveyNotificationTests.
/// </summary>
public sealed class TestSurveyRecipients : ISurveyRecipients
{
    public const int DeciderId   = -4401;
    public const int RequesterId = -4402;
    /// <summary>The name the survey tests request under.</summary>
    public const string RequesterName = "requester";

    public static readonly Recipient Decider =
        new(DeciderId, "survey-test-decider", "Survey Test Decider", "survey-test-decider@example.invalid");

    public Task<IReadOnlyList<Recipient>> WithPermissionAsync(
        SqlConnection c, SqlTransaction tx, string permissionCode, string? plant) =>
        Task.FromResult<IReadOnlyList<Recipient>>(new[] { Decider });

    public Task<Recipient?> ByUsernameAsync(SqlConnection c, SqlTransaction tx, string? username) =>
        Task.FromResult(username == RequesterName
            ? new Recipient(RequesterId, RequesterName, "Survey Test Requester", null)
            : null);

    /// <summary>
    /// An internal committee member is "resolved" to a stand-in: a user id
    /// nobody holds and an address that cannot be delivered. This matters
    /// even when the member is a real account -- or when the live database has
    /// a real committee that a test's survey picks up: the notice must still
    /// land in nobody's bell.
    /// </summary>
    public Task<IReadOnlyList<Recipient>> ByUserIdsAsync(SqlConnection c, SqlTransaction tx, IEnumerable<int> userIds) =>
        Task.FromResult<IReadOnlyList<Recipient>>(userIds.Distinct()
            .Select(id => new Recipient(StandIn(id), "member" + id, "Committee Member " + id, MemberAddress(id))).ToList());

    public static int StandIn(int userId) => -100000 - Math.Abs(userId);
    public static string MemberAddress(int userId) => $"survey-test-member{Math.Abs(userId)}@example.invalid";

    /// <summary>Members with an even id "can open" the Surveys page, odd ones
    /// cannot, so both link targets are exercised.</summary>
    public Task<IReadOnlyList<int>> WhoCanOpenSurveysAsync(SqlConnection c, SqlTransaction tx, IEnumerable<int> userIds) =>
        Task.FromResult<IReadOnlyList<int>>(userIds.Where(id => id % 2 == 0).Distinct().Select(StandIn).ToList());
}

/// <summary>
/// The real outbox, except that a new message is parked in the year 2099.
/// The deployed worker only sends messages that are due, so it can never pick
/// up one a test wrote; the tests send theirs explicitly with DispatchOneAsync,
/// which the recording mail service then captures.
/// </summary>
public sealed class ParkedMailOutbox : MailOutbox
{
    public static readonly DateTime ParkedUntil = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public ParkedMailOutbox(IConfiguration config, IEmailService email, ILogger<MailOutbox> log)
        : base(config, email, log) { }

    protected override DateTime? FirstAttemptUtc => ParkedUntil;
}
