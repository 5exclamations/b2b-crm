using Crm.Application.Common;
using Crm.Domain.Users;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Crm.Application.Users;

public sealed record UserDto(Guid Id, string Email, string FullName, UserRole Role, bool IsActive,
    DateTimeOffset? LastLoginAt);
public sealed record CreateUserRequest(string Email, string FullName, string Password, UserRole Role);
public sealed record UpdateUserRequest(string FullName, UserRole Role, bool IsActive);
public sealed record ResetPasswordRequest(string NewPassword);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User);

public interface IUserService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<UserDto> GetCurrentAsync(CancellationToken ct);
    Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? isActive, CancellationToken ct);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct);
    Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct);
    Task ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken ct);
}

internal sealed class UserService(ICrmDbContext db, IPasswordHasher hasher, IAccessTokenIssuer tokens,
    ICurrentUser currentUser, TimeProvider clock) : IUserService
{
    // Pre-computed so that unknown-user logins cost the same as wrong-password logins.
    private readonly string _dummyHash = hasher.Hash(Guid.NewGuid().ToString());

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var ok = hasher.Verify(request.Password, user?.PasswordHash ?? _dummyHash);
        if (user is null || !ok || !user.IsActive)
            throw new UnauthorizedAccessException("Invalid email or password.");

        user.RecordLogin(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        var token = tokens.Issue(user);
        return new LoginResponse(token.Token, token.ExpiresAt, ToDto(user));
    }

    public async Task<UserDto> GetCurrentAsync(CancellationToken ct)
    {
        var id = currentUser.RequireId();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw new NotFoundException("User", id);
        return ToDto(user);
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(UserRole? role, bool? isActive, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (role is { } r) query = query.Where(u => u.Role == r);
        if (isActive is { } a) query = query.Where(u => u.IsActive == a);
        var users = await query.OrderBy(u => u.FullName).ToListAsync(ct);
        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        var email = User.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException($"A user with email '{email}' already exists.", "duplicate_email");
        var user = new User(email, request.FullName, hasher.Hash(request.Password), request.Role);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task<UserDto> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
        if (id == currentUser.UserId && (!request.IsActive || request.Role != UserRole.Admin))
            throw new Crm.Domain.Common.DomainException("You cannot demote or deactivate your own account.", "invalid_self_change");
        user.UpdateProfile(request.FullName, request.Role);
        user.SetActive(request.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(user);
    }

    public async Task ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User", id);
        user.ChangePasswordHash(hasher.Hash(request.NewPassword));
        await db.SaveChangesAsync(ct);
    }

    internal static UserDto ToDto(User u) => new(u.Id, u.Email, u.FullName, u.Role, u.IsActive, u.LastLoginAt);
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(200);
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).PasswordPolicy();
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator() => RuleFor(x => x.NewPassword).PasswordPolicy();
}

internal static class PasswordRules
{
    public static IRuleBuilderOptions<T, string> PasswordPolicy<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MinimumLength(10).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain an upper-case letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lower-case letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
}
