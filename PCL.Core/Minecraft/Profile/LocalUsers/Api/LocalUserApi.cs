using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PCL.Core.Minecraft.Profile.LocalUsers.Api;

/// <summary>
/// 本地用户服务 API 的请求与响应模型。
/// </summary>
public static class LocalUserApi
{
    /// <summary>
    /// 注册请求。
    /// </summary>
    public sealed class RegisterRequest
    {
        [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
        [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }

    /// <summary>
    /// 登录请求。
    /// </summary>
    public sealed class LoginRequest
    {
        [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
        [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// 修改密码请求。
    /// </summary>
    public sealed class ChangePasswordRequest
    {
        [JsonPropertyName("userId")] public string UserId { get; set; } = string.Empty;
        [JsonPropertyName("oldPassword")] public string OldPassword { get; set; } = string.Empty;
        [JsonPropertyName("newPassword")] public string NewPassword { get; set; } = string.Empty;
    }

    /// <summary>
    /// 用户信息响应。绝不包含任何密码材料。
    /// </summary>
    public sealed class UserInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("userName")] public string UserName { get; set; } = string.Empty;
        [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
        [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("lastLoginAt")] public string? LastLoginAt { get; set; }
        [JsonPropertyName("isAdmin")] public bool IsAdmin { get; set; }
        [JsonPropertyName("isDisabled")] public bool IsDisabled { get; set; }
        [JsonPropertyName("dataDirectory")] public string DataDirectory { get; set; } = string.Empty;
    }

    /// <summary>
    /// 通用响应。
    /// </summary>
    public sealed class Response
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("code")] public string Code { get; set; } = string.Empty;
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
        [JsonPropertyName("user")] public UserInfo? User { get; set; }
        [JsonPropertyName("users")] public List<UserInfo>? Users { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }

        public static Response Success(string code = "ok", string message = "")
            => new() { Ok = true, Code = code, Message = message };

        public static Response Fail(string code, string message)
            => new() { Ok = false, Code = code, Message = message };
    }
}
