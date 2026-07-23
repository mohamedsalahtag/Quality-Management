namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Resolves where uploaded inspection photos physically live.
///
/// They used to sit in <c>wwwroot/uploads</c> -- i.e. inside the publish output
/// directory. That is unsafe: <c>dotnet publish</c> re-runs the static web
/// assets step whenever the build actually produces new output, and that step
/// prunes files under wwwroot that the project does not know about. On
/// 2026-07-23 it silently deleted 2,816 of 3,006 production photos, twice --
/// once during the host migration and again on the first deploy that carried
/// real code changes. (A publish with no rebuild leaves them alone, which is
/// why it looked intermittent.)
///
/// Keeping the photos outside the publish target removes the failure mode
/// rather than working around it. Set <see cref="ConfigKey"/> to an absolute
/// path outside the deploy folder. When it is unset the historical
/// wwwroot/uploads location is used, so development machines need no config.
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
