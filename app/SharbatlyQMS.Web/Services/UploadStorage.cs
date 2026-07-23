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

    public static string Root(IWebHostEnvironment env, IConfiguration config)
    {
        var configured = config[ConfigKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.WebRootPath, "uploads")
            : Path.GetFullPath(configured);
    }
}
