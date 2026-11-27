using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using Zullo.Api.Services;


namespace Zullo.Api.Controllers
{
    public sealed class VerifyEmailRequest
    {
        public Guid ChallengeId { get; set; }
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.RegularExpression("^[0-9]{6}$")]
        public string Code { get; set; } = "";
    }
    public sealed class ResendEmailRequest { public Guid ChallengeId { get; set; } }

    [ApiController]
    [Route("api/auth")]
    [EnableRateLimiting(AuthenticationProtection.Policy)]
    public class AuthController : ControllerBase
    {
        private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword("not-a-user-password");
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly LoginAttemptService _loginAttemptService;
        private readonly PendingEmailRegistrationService _verification;

        public AuthController(AppDbContext db, IConfiguration config, LoginAttemptService loginAttemptService, PendingEmailRegistrationService? verification = null)
        {
            _db = db;
            _config = config;
            _loginAttemptService = loginAttemptService;
            _verification = verification ?? new PendingEmailRegistrationService(db, config, new SmtpVerificationEmailSender(config));
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            if (!EmailAddressPolicy.TryNormalize(request.Email, out var email))
                return BadRequest(new { code = "invalid_email", message = "Invalid email address." });
            var suggestion = EmailAddressPolicy.SuggestAddress(email);
            if (suggestion is not null) return BadRequest(new { code = "email_domain_typo", suggestion, message = "Check the email domain." });
            try
            {
                return StatusCode(StatusCodes.Status202Accepted,
                    await _verification.Begin(email, request.Password, HttpContext?.RequestAborted ?? default));
            }
            catch (EmailVerificationFailure failure) { return VerificationError(failure); }
        }

        [HttpPost("verify-email")]
        public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request)
        {
            try
            {
                var user = await _verification.Verify(request.ChallengeId, request.Code, HttpContext?.RequestAborted ?? default);
                return Ok(new AuthResponseDto { Token = GenerateJwt(user), UserId = user.Id, Email = user.Email });
            }
            catch (EmailVerificationFailure failure) { return VerificationError(failure); }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_User_Email" })
            { return Conflict(new { code = "email_in_use", message = "User already exists." }); }
        }

        [HttpPost("resend-verification")]
        public async Task<IActionResult> ResendVerification([FromBody] ResendEmailRequest request)
        {
            try { return StatusCode(StatusCodes.Status202Accepted, await _verification.Resend(request.ChallengeId, HttpContext?.RequestAborted ?? default)); }
            catch (EmailVerificationFailure failure) { return VerificationError(failure); }
        }

        private IActionResult VerificationError(EmailVerificationFailure failure)
        {
            if (failure.RetryAfter is int seconds && HttpContext is not null) Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(failure.Status, new { code = failure.Code,
                message = failure.Code == "email_in_use" ? "User already exists." : "Email verification could not be completed." });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        {
          

            // Samma normalisering och email som vid register
            if (!EmailAddressPolicy.TryNormalize(request.Email, out var normalizedEmail))
                return BadRequest(new { code = "invalid_email", message = "Invalid email address." });

            var password = request.Password;

            if (!_loginAttemptService.TryAcquire(normalizedEmail, out var retryAfter))
            {
                Response.Headers.RetryAfter = AuthenticationProtection.RetrySeconds(retryAfter);
                return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorMessageResponseDto
                {
                    Message = "Too many authentication attempts. Try again later."
                });
            }

            var user = await _db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail);
            // Perform password work for unknown/passwordless accounts as well.
            var hash = string.IsNullOrWhiteSpace(user?.PasswordHash) ? DummyPasswordHash : user.PasswordHash;
            var validPassword = BCrypt.Net.BCrypt.Verify(password, hash);
            if (user is null)
            {
                var pending = await _verification.Resume(normalizedEmail, password, HttpContext?.RequestAborted ?? default);
                if (pending is not null) return StatusCode(StatusCodes.Status202Accepted, pending);
            }
            if (user == null || user.IsClosed || string.IsNullOrWhiteSpace(user.PasswordHash) || !validPassword)
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid email or password."
                });
            }

            var token = GenerateJwt(user);

            // Login returnerar samma auth-shape som register, men utan message-fält
            return Ok(new AuthResponseDto
            {
                Token = token,
                UserId = user.Id,
                Email = user.Email
            });
        }

        // Skapar JWT som frontend använder för alla skyddade endpoints
        private string GenerateJwt(User user)
        {
            // JWT-nyckeln måste finnas, annars kan jag inte skapa säkra tokens
            var jwtKey = _config["Jwt:Key"];

            if (string.IsNullOrWhiteSpace(jwtKey))
                throw new InvalidOperationException("Jwt:Key is missing in configuration.");

            var key = Encoding.UTF8.GetBytes(jwtKey);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                 new Claim(ClaimTypes.Email, user.Email ?? "")
    };

            var creds = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

   
}