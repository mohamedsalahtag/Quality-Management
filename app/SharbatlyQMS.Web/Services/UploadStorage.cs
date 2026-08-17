namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Resolves where uploaded inspection photos physically live.
///
/// They used to sit in <c>wwwroot/uploads</c> -- inside the publish output
/// directory -- which makes production data a casualty of anything that
/// rebuilds or replaces the deploy folder: a clean publish, a wiped output
/// directory, or swapping in one of the deploy\rollback-* snapshots. Those
/// snapshots run to ~4 GB each precisely because the photos were inside them.
///
/// Set <see cref="ConfigKey"/> to an absolute path outside the deploy folder.
/// When it is unset the historical wwwroot/uploads location is used, so
/// development machines need no configuration.
///
/// Note: photos went missing twice during the 2026-07-23 migration and this
/// comment previously blamed the publish pipeline. That was incorrect -- they
/// had been deleted by hand. dotnet publish and Republish.ps1 were both tested
/// with the full set in place and preserved it.
/// </summary>
public static class UploadStorage
{
    /// <summary>appsettings key holding the absolute uploads path.</summary>
    public const string ConfigKey = "QMS:UploadsPhysicalRoot";

    /// <summary>URL prefix the photos are served under. Stored image URLs in
    /// qms_image_asset are of the form /uploads/{ownerType}/{ownerId}/{file},
    /// so this must not change or every existing link breaks.</summary>
    public const string RequestPath = "/uploads";

    /// <summary>Admin-configured uploads folder (Site Configuration → Storage),
    /// cached from the settings DB. Set ONCE at service startup (see Program.cs)
    /// so every <see cref="Root"/> caller — including the static-file provider —
    /// agrees on one location for the whole process lifetime. Changing the
    /// setting requires a restart to take effect, which is intentional: the
    /// static-file provider is bound only at boot, and photos must be moved to
    /// the new folder before it serves them.</summary>
    private static string? _configuredRoot;

    /// <summary>Called once at startup with the DB-configured uploads root
    /// (blank/null → fall back to appsettings/default).</summary>
    public static void SetRoot(string? root)
        => _configuredRoot = string.IsNullOrWhiteSpace(root) ? null : root;

    public static string Root(IWebHostEnvironment env, IConfiguration config)
    {
        // Precedence: DB setting (startup cache) → appsettings key → default.
        var configured = !string.IsNullOrWhiteSpace(_configuredRoot)
            ? _configuredRoot
            : config[ConfigKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.WebRootPath, "uploads")
            : Path.GetFullPath(configured);
    }
}
