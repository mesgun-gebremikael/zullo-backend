using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;


namespace Zullo.Api.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public AuthController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            // Normalisera email så register/login blir mindre känsligt för case och mellanslag
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            // Kolla om user redan finns
            var exists = await _db.Users.AnyAsync(x => x.Email == normalizedEmail);
            if (exists)
                return BadRequest("User already exists.");

            // HASHA lösenordet
            var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);

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
          

            // Samma normalisering som vid register
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var user = await _db.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail);
            if (user == null)
                return Unauthorized("Invalid email or password.");

            //  skydd mot gamla null-rader
            if (string.IsNullOrWhiteSpace(user.PasswordHash))
                return Unauthorized("User has no password set. Re-register this user.");

            var ok = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
            if (!ok)
                return Unauthorized("Invalid email or password.");

            var token = GenerateJwt(user);

            return Ok(new AuthResponseDto
            {
                Token = token,
                UserId = user.Id,
                Email = user.Email
            });
        }

        private string GenerateJwt(User user)
        {
            var key = Encoding.UTF8.GetBytes(_config["Jwt:Key"]!);

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