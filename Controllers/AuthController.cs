using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Controllers;

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

    // DEV-login: Email räcker (password ignoreras i MVP)
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email is required");

        var email = request.Email.Trim().ToLowerInvariant();

        // ✅ hitta eller skapa user i DB
        var user = await _db.User.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
        {
            user = new User
            {
                Email = email,
                IsVerified = true
            };
            _db.User.Add(user);
            await _db.SaveChangesAsync();
        }

        var token = CreateJwt(user.Id, user.Email);

        return Ok(new
        {
            token,
            userId = user.Id,
            email = user.Email
        });
    }

    // DEV-register: skapar user om email inte finns
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Email is required");

        var email = request.Email.Trim().ToLowerInvariant();

        var exists = await _db.User.AnyAsync(u => u.Email == email);
        if (exists) return BadRequest("Email already exists");

        var user = new User
        {
            Email = email,
            IsVerified = true
        };

        _db.User.Add(user);
        await _db.SaveChangesAsync();

        var token = CreateJwt(user.Id, user.Email);

        return Ok(new
        {
            token,
            userId = user.Id,
            email = user.Email
        });
    }

    private string CreateJwt(Guid userId, string? email)
    {
        var key = _config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key))
            throw new Exception("Jwt:Key is missing in appsettings.json");

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256
        );

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        };

        if (!string.IsNullOrWhiteSpace(email))
            claims.Add(new Claim(ClaimTypes.Email, email));

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = ""; // ignoreras i DEV
}

public class RegisterRequest
{
    public string Name { get; set; } = ""; // ignoreras i DEV
    public string Email { get; set; } = "";
    public string Password { get; set; } = ""; // ignoreras i DEV
}