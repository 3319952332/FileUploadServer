namespace FileUploadServer.Auth.Dtos;

public record RegisterRequest(string Username, string Password);

public record LoginRequest(string Username, string Password, string? DeviceName);

public record ResetPasswordRequest(string Password);

public record UserDto(int Id, string Username, bool IsAdmin, string Status, DateTime? ApprovedAt);

public record LoginResponse(string Token, DateTime ExpiresAt, UserDto User, string FileKey, DateTime FileKeyExpiresAt);

public record VerifyResponse(bool Valid, int? UserId, string? Username);

public record ApiMessage(string Message);
