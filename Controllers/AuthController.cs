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
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly LoginAttemptService _loginAttemptService;

        public AuthController(AppDbContext db, IConfiguration config, LoginAttemptService loginAttemptService)
        {
            _db = db;
            _config = config;
            _loginAttemptService = loginAttemptService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            // Normalisera email för konsekvent register/login och unikhetkontroll
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var password = request.Password;

            // Kolla om user redan finns
            var exists = await _db.Users.AnyAsync(x => x.Email == normalizedEmail);

            //Skydda mot dubbla konton med samma email
            if (exists)
            {
                return BadRequest(new ErrorMessageResponseDto
                {
                    Message = "User already exists."
                });
            }

            // HASHA lösenordet innan det sparas i database
            var hash = BCrypt.Net.BCrypt.HashPassword(password);

            var user = new User
            {
                Email = normalizedEmail,
                IsVerified = true,
                PasswordHash = hash,

                // Rimliga defaults för ny användare
                CreatedAtUtc = DateTime.UtcNow,
                LikesRemaining = 50,
                LikesResetAtUtc = DateTime.UtcNow.AddHours(12),
                MatchRadiusKm = 50
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            // Skapa token först efter att user finns och är sparad
            var token = GenerateJwt(user);

            // Register returnerar token direkt så frontend kan logga in utan extra steg
            return Ok(new AuthResponseDto
            {
                Message = "User created",
                Token = token,
                UserId = user.Id,
                Email = user.Email
            });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        {
          

            // Samma normalisering och email som vid register
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var password = request.Password;

            var tooFast = await _loginAttemptService.IsTryingTooFastAsync(normalizedEmail);
            if (tooFast)
            {
                return BadRequest(new ErrorMessageResponseDto
                {
                    Message = "Too many login attempts. Please wait a moment."
                });
            }

            var user = await _db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail);
            if (user == null)
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid email or password."
                });
            }

            //  skydd mot gamla null-rader
            if (string.IsNullOrWhiteSpace(user.PasswordHash))
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "User has no password set. Re-register this user."
                });
            }

            var ok = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
            //samma felmeddelande oavsett vad som var fel för att inte läcka onödig auth-info
            if (!ok)
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