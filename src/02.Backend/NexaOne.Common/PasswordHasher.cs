using System.Security.Cryptography;
using System.Text;

namespace NexaOne.Common;

/// <summary>
/// 비밀번호 해시 — per-user salt + PBKDF2(HMAC-SHA256) 강화 해시(설계 §19.2.2).
/// 저장 형식: <c>pbkdf2$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>.
/// 구(舊) 무염 SHA-256 hex(64자, SmartUX 3.5 호환)도 <see cref="Verify"/>로 검증 가능하며,
/// 검증 성공 시 <see cref="NeedsRehash"/>가 true를 반환해 로그인 시 강화 해시로 재해싱(rehash-on-login)하도록 한다.
/// </summary>
/// <remarks>
/// 클라이언트가 보내는 자격증명(원문/클라이언트 SHA-256 무관)을 서버가 salt+stretching하여 저장하므로,
/// 전송 계약은 그대로 두고 저장 보안만 강화한다 — 무염 SHA-256 DB 저장(CWE-759/916)을 제거한다.
/// </remarks>
public static class PasswordHasher
{
    private const string Pbkdf2Prefix = "pbkdf2$";
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>비밀번호를 PBKDF2 강화 해시(저장 형식 문자열)로 만든다.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Pbkdf2Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>비밀번호가 저장된 해시와 일치하는지 검증한다. PBKDF2 형식과 구 무염 SHA-256 hex를 모두 지원한다.</summary>
    public static bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        if (stored.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal))
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
                return false;
            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException) { return false; }

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        // 구 무염 SHA-256 hex (SmartUX 3.5 호환). 길이가 다르면 FixedTimeEquals가 false를 반환한다.
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(legacy), Encoding.UTF8.GetBytes(stored));
    }

    /// <summary>저장된 해시가 구 형식(PBKDF2 아님)이면 true — 로그인 검증 성공 시 강화 해시로 재해싱해야 한다.</summary>
    public static bool NeedsRehash(string stored)
        => !string.IsNullOrEmpty(stored) && !stored.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal);
}
