using System.ComponentModel.DataAnnotations;

namespace SharbatlyQMS.Web.Models;

/// <summary>
/// View-model for the My Profile page. Surfaces only the fields a user is
/// allowed to change about themselves -- role / IsActive are admin-only.
/// </summary>
public class ProfileVm
{
    public int UserId { get; set; }
    public string Username { get; set; } = "";

    [Required, StringLength(200)]
    public string FullName { get; set; } = "";

    [EmailAddress, StringLength(200)]
    public string? Email { get; set; }

    [StringLength(200)]
    public string? Department { get; set; }

    public string? EmployeeId { get; set; }   // read-only on the page
    public string  Role       { get; set; } = ""; // read-only on the page
    public string? ProfilePicture { get; set; }   // current avatar path
    public bool    IsAdUser  { get; set; }    // hide password change for AD users
}

public class ChangePasswordVm
{
    [Required]
    public string CurrentPassword { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 10, ErrorMessage = "Password must be at least 10 characters.")]
    public string NewPassword { get; set; } = "";

    [Required, Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
}
