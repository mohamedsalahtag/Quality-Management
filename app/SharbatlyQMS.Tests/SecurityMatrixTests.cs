using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Guards the rule the permission model exists to enforce: <b>every access
/// decision goes through the matrix</b>. Nothing may decide access by comparing
/// a role name.
///
/// This was written after a review found four places doing exactly that. Three
/// of them compared against the PRE-OVERHAUL role names ("SiteAdmin",
/// "Manager") while the role claim carries codes ("QcAdmin", "QcManager"), so
/// they silently matched nobody — one of them, <c>CanShare()</c>, meant no user
/// alive could publish a report perspective. A hardcoded role check does not
/// merely bypass the Security screen; it rots, and nothing tells you.
/// </summary>
[Collection("workflow")]
public class SecurityMatrixTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SecurityMatrixTests(QmsAppFactory factory) => _factory = factory;

    private IPermissionResolver Resolver()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
    }

    // ---------------------------------------------------------------- source scan

    /// <summary>
    /// Walks up from the test binary to the repository, so the scan keeps
    /// working wherever the output lands. Fails loudly rather than skipping —
    /// a guard that quietly finds nothing to guard is worse than none.
    /// </summary>
    private static DirectoryInfo WebProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "SharbatlyQMS.Web");
            if (Directory.Exists(candidate)) return new DirectoryInfo(candidate);
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate SharbatlyQMS.Web above {AppContext.BaseDirectory}. " +
            "The hardcoded-role scan cannot run, so it is failing rather than passing vacuously.");
    }

    /// <summary>
    /// Strips comments before scanning. Without this the guard reports the very
    /// comments that explain why a hardcoded check was removed, which would
    /// push the next person to delete the explanation rather than keep the rule.
    /// </summary>
    private static string WithoutComments(string text)
    {
        text = Regex.Replace(text, @"@\*.*?\*@", "", RegexOptions.Singleline);   // Razor
        text = Regex.Replace(text, @"/\*.*?\*/",  "", RegexOptions.Singleline);   // block
        // Line comments, but keep the newlines so reported line numbers stay true.
        text = Regex.Replace(text, "//[^\n]*", "");
        return text;
    }

    /// <summary>
    /// <c>IsInRole</c> anywhere in the web project is a hardcoded access
    /// decision. The sanctioned alternatives are an attribute
    /// ([RequirePermission] / [RequireScreen]) or, when the permission qualifies
    /// how an action behaves rather than whether it runs, a declaration in
    /// <see cref="Perm.CodeOnly"/> checked through <c>IUserPermissions.Can</c>.
    /// </summary>
    [Fact]
    public void No_source_file_decides_access_by_role_name()
    {
        var web = WebProjectDir();
        var offenders = new List<string>();

        foreach (var file in web.EnumerateFiles("*.cs", SearchOption.AllDirectories)
                                .Concat(web.EnumerateFiles("*.cshtml", SearchOption.AllDirectories)))
        {
            // obj/bin hold generated copies of the same source; scanning them
            // would report every offence twice and outlive a real fix.
            var rel = Path.GetRelativePath(web.FullName, file.FullName).Replace('\\', '/');
            if (rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.StartsWith("wwwroot/")) continue;

            var text = WithoutComments(File.ReadAllText(file.FullName));
            foreach (Match m in Regex.Matches(text, @"IsInRole\s*\("))
            {
                var line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
                offenders.Add($"{rel}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Access must be decided by the permission matrix, never by a role name. " +
            "Use [RequirePermission] / [RequireScreen], or declare the permission in " +
            "Perm.CodeOnly and check it with IUserPermissions.Can. Offenders:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// The pre-overhaul role names in <c>UserRoles</c> ("SiteAdmin", "Manager",
    /// …) are not the role codes the claim carries ("QcAdmin", "QcManager"), so
    /// comparing against them is always false. They survive only for legacy
    /// display; nothing in the security path may reference them.
    /// </summary>
    [Fact]
    public void Legacy_role_names_are_not_used_for_decisions()
    {
        var web = WebProjectDir();
        var offenders = new List<string>();

        foreach (var file in web.EnumerateFiles("*.cs", SearchOption.AllDirectories)
                                .Concat(web.EnumerateFiles("*.cshtml", SearchOption.AllDirectories)))
        {
            var rel = Path.GetRelativePath(web.FullName, file.FullName).Replace('\\', '/');
            if (rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.StartsWith("wwwroot/")) continue;
            // The declaration itself, and the Users screen's legacy-name column.
            if (rel is "Models/User.cs") continue;

            var text = WithoutComments(File.ReadAllText(file.FullName));
            foreach (Match m in Regex.Matches(text, @"UserRoles\.(SiteAdmin|Manager|Supervisor|Operator|ClaimManager|Viewer)"))
            {
                var line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
                offenders.Add($"{rel}:{line} -> {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "UserRoles.* are pre-overhaul display names, not the role codes the claim carries, " +
            "so any comparison against them is always false. Offenders:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ---------------------------------------------------------------- the matrix

    /// <summary>
    /// Claims was seeded to every built-in role, which handed the screen to
    /// Operator, Supervisor and Viewer — none of which hold a single Claims.*
    /// action. They could read every claim decision in the business.
    /// </summary>
    [Theory]
    [InlineData(RoleCodes.Operator)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Viewer)]
    public void Claims_screen_is_closed_to_roles_that_cannot_act_on_a_claim(string role)
    {
        var perms = Resolver();
        Assert.False(perms.RoleHas(role, Screens.Claims, AccessLevel.Read, isScreen: true),
            $"{role} can still open the Claims screen.");
    }

    [Theory]
    [InlineData(RoleCodes.Admin)]
    [InlineData(RoleCodes.Manager)]
    [InlineData(RoleCodes.ClaimManager)]
    public void Claims_screen_stays_open_to_the_roles_that_own_claims(string role)
    {
        var perms = Resolver();
        Assert.True(perms.RoleHas(role, Screens.Claims, AccessLevel.Read, isScreen: true),
            $"{role} lost access to the Claims screen.");
    }

    /// <summary>
    /// A permission the resolver has never heard of denies — so a code-only
    /// permission that never reached the catalogue would silently lock its
    /// feature for everyone, which is exactly how the dead IsInRole checks
    /// behaved. Prove each one is really registered and really granted.
    /// </summary>
    [Fact]
    public void Code_only_permissions_reach_the_catalogue_and_their_seed_roles()
    {
        var perms = Resolver();
        var known = perms.Permissions.Select(p => p.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var declared in Perm.CodeOnly)
        {
            Assert.True(known.Contains(declared.Code),
                $"{declared.Code} is declared in Perm.CodeOnly but never reached qms_permission.");

            // Seeding happens once, on first discovery. Every role the seed
            // names must hold it, or the feature is dark for its intended users.
            foreach (var role in Seeds.RolesFor(declared.SeedFor))
                Assert.True(perms.RoleHas(role, declared.Code, AccessLevel.Edit, isScreen: false),
                    $"{role} was seeded {declared.Code} but does not hold it.");
        }
    }

    /// <summary>
    /// The administrator floor is exactly two codes. If it ever widened, a bad
    /// grant could no longer lock an administrator out — but neither could an
    /// administrator's role be meaningfully restricted, which is the same
    /// hardcoded-access problem wearing a different hat.
    /// </summary>
    [Fact]
    public void The_administrator_floor_covers_only_the_security_screen()
    {
        Assert.Equal(
            new[] { Screens.AdminSecurity, Perm.Admin.SecurityEdit },
            Perm.AdminFloor);

        var perms = Resolver();
        // A permission outside the floor must be decidable for the admin role
        // through the grant table, not conjured by the floor. (The built-in
        // Administrator is is_super, so it legitimately holds everything —
        // assert the floor list itself rather than the super role's grants.)
        Assert.DoesNotContain(Perm.Qo.Delete, Perm.AdminFloor);
        Assert.DoesNotContain(Screens.Claims, Perm.AdminFloor);
    }
}
