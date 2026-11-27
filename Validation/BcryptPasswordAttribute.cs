using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Zullo.Api.Validation;

// BCrypt only incorporates the first 72 UTF-8 bytes. Reject longer new
// passwords rather than silently accepting an interchangeable suffix.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BcryptPasswordAttribute : ValidationAttribute
{
    public BcryptPasswordAttribute() : base("Password must not exceed 72 UTF-8 bytes.") { }

    public override bool IsValid(object? value) =>
        value is null || value is string password && Encoding.UTF8.GetByteCount(password) <= 72;
}
