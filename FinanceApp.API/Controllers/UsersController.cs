using System.Security.Claims;
using FinanceApp.API.Services;
using FinanceApp.Core.Models;
using FinanceApp.Data.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IUserSecurityService _securityService;

    public UsersController(AppDbContext context, IUserSecurityService securityService)
    {
        _context = context;
        _securityService = securityService;
    }

    [HttpGet("me")]
    public async Task<ActionResult<MeUserDto>> GetMe()
    {
        var user = await GetCurrentUser();
        if (user == null)
        {
            return NotFound();
        }

        return ToMeDto(user);
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<MeUserDto>> Register(RegisterDto dto)
    {
        if (!_securityService.TryNormalizeEmail(dto.Email, out var normalizedEmail, out var trimmedEmail))
        {
            return BadRequest("Некорректный email.");
        }

        if (!_securityService.TryValidateAndNormalizeUsername(dto.Username, out var normalizedUsername, out var trimmedUsername, out var usernameError))
        {
            return BadRequest(usernameError);
        }

        if (string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 8)
        {
            return BadRequest("Пароль должен содержать минимум 8 символов.");
        }

        if (normalizedUsername == normalizedEmail)
        {
            return BadRequest("Логин не должен совпадать с email.");
        }

        if (await _context.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
        {
            return BadRequest("Email already exists");
        }

        if (await _context.Users.AnyAsync(u => u.NormalizedUsername == normalizedUsername))
        {
            return BadRequest("Логин уже занят.");
        }

        if (await _context.Users.AnyAsync(u => u.NormalizedEmail == normalizedUsername))
        {
            return BadRequest("Логин не должен совпадать с email другого пользователя.");
        }

        var user = new User
        {
            Username = trimmedUsername,
            NormalizedUsername = normalizedUsername,
            Email = trimmedEmail,
            NormalizedEmail = normalizedEmail,
            CreatedAt = DateTime.UtcNow
        };

        user.PasswordHash = _securityService.HashPassword(user, dto.Password);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetMe), ToMeDto(user));
    }

    [HttpPatch("me/profile")]
    public async Task<ActionResult<MeUserDto>> UpdateProfile(UpdateProfileDto dto)
    {
        var user = await GetCurrentUser();
        if (user == null)
        {
            return NotFound();
        }

        if (!_securityService.TryValidateAndNormalizeUsername(dto.Username, out var normalizedUsername, out var trimmedUsername, out var usernameError))
        {
            return BadRequest(usernameError);
        }

        if (string.IsNullOrWhiteSpace(dto.CurrentPassword))
        {
            return BadRequest("Введите текущий пароль.");
        }

        var verificationResult = _securityService.VerifyAndUpgradePassword(user, dto.CurrentPassword, out var upgradedHash);
        if (verificationResult == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            return BadRequest("Неверный текущий пароль.");
        }

        if (normalizedUsername == user.NormalizedUsername)
        {
            user.Username = trimmedUsername;
            user.NormalizedUsername = normalizedUsername;
            if (!string.IsNullOrWhiteSpace(upgradedHash))
            {
                user.PasswordHash = upgradedHash;
            }

            await _context.SaveChangesAsync();
            return Ok(ToMeDto(user));
        }

        if (await _context.Users.AnyAsync(u => u.Id != user.Id && u.NormalizedUsername == normalizedUsername))
        {
            return Conflict("Логин уже занят.");
        }

        if (await _context.Users.AnyAsync(u => u.Id != user.Id && u.NormalizedEmail == normalizedUsername))
        {
            return Conflict("Логин не должен совпадать с email другого пользователя.");
        }

        user.Username = trimmedUsername;
        user.NormalizedUsername = normalizedUsername;
        if (!string.IsNullOrWhiteSpace(upgradedHash))
        {
            user.PasswordHash = upgradedHash;
        }

        await _context.SaveChangesAsync();
        return Ok(ToMeDto(user));
    }

    [HttpPost("me/change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        var user = await GetCurrentUser();
        if (user == null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(dto.CurrentPassword))
        {
            return BadRequest("Введите текущий пароль.");
        }

        if (string.IsNullOrWhiteSpace(dto.NewPassword) || dto.NewPassword.Length < 8)
        {
            return BadRequest("Новый пароль должен содержать минимум 8 символов.");
        }

        if (dto.NewPassword != dto.ConfirmPassword)
        {
            return BadRequest("Подтверждение пароля не совпадает.");
        }

        var verificationResult = _securityService.VerifyAndUpgradePassword(user, dto.CurrentPassword, out _);
        if (verificationResult == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
        {
            return BadRequest("Неверный текущий пароль.");
        }

        user.PasswordHash = _securityService.HashPassword(user, dto.NewPassword);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    [HttpDelete("me")]
    public async Task<IActionResult> DeleteMe()
    {
        var user = await GetCurrentUser();
        if (user == null)
        {
            return NotFound();
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private async Task<User?> GetCurrentUser()
    {
        var userIdRaw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdRaw, out var userId))
        {
            return null;
        }

        return await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
    }

    private static MeUserDto ToMeDto(User user) =>
        new(user.Id, user.Username, user.Email, user.CreatedAt);
}

public class RegisterDto
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class UpdateProfileDto
{
    public string Username { get; set; } = string.Empty;
    public string CurrentPassword { get; set; } = string.Empty;
}

public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

public record MeUserDto(int Id, string Username, string Email, DateTime CreatedAt);
