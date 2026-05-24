#nullable enable
using System;
using System.Security.Cryptography;

namespace RemotePlayServer.Server;

public static class GuestIdManager
{
    private const string AlphaChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string NumericChars = "0123456789";
    private const int IdLength = 6;
    private const int PasswordLength = 6;

    private static string? _currentId;
    private static string? _currentPassword;

    public static string CurrentId => _currentId ?? throw new InvalidOperationException("ID not generated");
    public static string CurrentPassword => _currentPassword ?? throw new InvalidOperationException("Password not generated");

    /// Display format: "A3K-9PF" (dash in middle)
    public static string DisplayId => _currentId != null
        ? $"{_currentId[..3]}-{_currentId[3..]}"
        : "---";

    public static void Generate()
    {
        _currentId = GenerateString(AlphaChars, IdLength);
        _currentPassword = GenerateString(NumericChars, PasswordLength);
    }

    public static void RegeneratePassword()
    {
        _currentPassword = GenerateString(NumericChars, PasswordLength);
    }

    private static string GenerateString(string chars, int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var result = new char[length];
        for (int i = 0; i < length; i++)
            result[i] = chars[bytes[i] % chars.Length];
        return new string(result);
    }
}
