// HOW IT FIRES: Auto-create-on-login (Trigger gated by AutoCreateAdUsers config)
// =============================================================================
// This block belongs at the START of the AccountController.Login POST,
// right after the DB lookup returned null. Pre-conditions:
//   - SiteConfiguration.AutoCreateAdUsers == "true"
//   - AdDomain + AdLdapPath are populated
// If AD bind succeeds, INSERT the user as Requester and continue with
// the rest of the login flow.

if (user == null)
{
    var autoCreate = (await _db.GetConfigAsync("AutoCreateAdUsers") ?? "false") == "true";
    var adDomain   = await _db.GetConfigAsync("AdDomain") ?? "";
    var adLdapPath = await _db.GetConfigAsync("AdLdapPath") ?? "";
    var adReady    = !string.IsNullOrWhiteSpace(adDomain)
                  && !string.IsNullOrWhiteSpace(adLdapPath);

    if (autoCreate && adReady)
    {
        var adUser = await _ad.AuthenticateAsync(model.Username, model.Password);
        if (adUser != null)
        {
            // Use the SAM portion (strip @domain) as the canonical AdUsername
            var sam = model.Username.Contains('@')
                ? model.Username.Split('@')[0]
                : model.Username;

            var newUser = new User
            {
                EmployeeId   = "",
                AdUsername   = sam,
                FullName     = string.IsNullOrWhiteSpace(adUser.FullName) ? sam : adUser.FullName,
                Email        = adUser.Email ?? "",
                Department   = adUser.Department ?? "",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password), // local fallback
                Role         = UserRoles.Requester,
                IsActive     = true
            };
            var newId = await _db.CreateUserAsync(newUser);
            newUser.UserId = newId;
            user = newUser;     // continue the login flow with the brand-new user

            _logger.LogInformation(
                "Auto-created AD user '{Sam}' as Requester (UserId={Id})",
                sam, newId);
        }
    }

    if (user == null)
    {
        ModelState.AddModelError("", "Invalid credentials or account not registered.");
        return View(model);
    }
}
