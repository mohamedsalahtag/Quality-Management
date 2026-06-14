namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Typed wrapper around the two TempData banners (<c>Success</c> + <c>Error</c>)
/// that <c>_Layout.cshtml</c> renders globally. Controllers historically wrote
/// <c>TempData["Success"] = "..."</c> directly; the same string keys + spelling
/// drifted across the codebase as a result. <see cref="BannerExtensions.Banner"/>
/// is the typed entry point going forward -- it still writes through TempData
/// so the existing Razor render path keeps working, but compile-time naming
/// catches typos. Existing TempData call sites continue to work; this is an
/// additive shim, not a breaking change.
/// </summary>
public sealed class BannerVm
{
    public string? Success { get; init; }
    public string? Error   { get; init; }

    public static BannerVm Ok(string msg)    => new() { Success = msg };
    public static BannerVm Fail(string msg)  => new() { Error   = msg };
}

public static class BannerExtensions
{
    public const string KeySuccess = "Success";
    public const string KeyError   = "Error";

    /// <summary>
    /// Persist a typed banner into TempData so the next page render shows it.
    /// Call from any controller via <c>this.Banner(BannerVm.Ok("Saved."));</c>.
    /// </summary>
    public static void Banner(this Microsoft.AspNetCore.Mvc.Controller ctl, BannerVm banner)
    {
        if (!string.IsNullOrWhiteSpace(banner.Success))
            ctl.TempData[KeySuccess] = banner.Success;
        if (!string.IsNullOrWhiteSpace(banner.Error))
            ctl.TempData[KeyError] = banner.Error;
    }
}
