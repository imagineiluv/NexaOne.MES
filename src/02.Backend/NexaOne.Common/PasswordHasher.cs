using System.Security.Cryptography;
using System.Text;

namespace NexaOne.Common;

/// <summary>비밀번호 해시 — 현행 SmartUX 3.5와 동일한 무염 SHA-256 lowercase hex 64자.
/// 설계서 §19.2.2의 호환 기간 정책으로, 강화 해시(PBKDF2 등) 전환 시 이 클래스만 교체한다.</summary>
public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
