using System.Security.Cryptography;
using System.Text;

namespace NewAutoZip.Core.Configuration;

/// <summary>
/// 用 DPAPI（CurrentUser 作用域）保护配置里的秘密。
///
/// 旧版把 Webhook 明文写进 webhook.txt、把压缩密码明文写进日志，两者都与压缩包同机存放。
///
/// 说明：DPAPI 只能防"别人拿到文件"，不能防"以同一个 Windows 用户身份运行的进程"。
/// 对本地备份工具来说这个强度是合适的；要更强就得引入用户每次输入的主密码，
/// 那会破坏无人值守自动运行，所以不采用。
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NewAutoZip.v2.settings");

    /// <summary>加密。空值原样返回，便于 JSON 里保持空字符串。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain),
                Entropy,
                DataProtectionScope.CurrentUser);

            return Prefix + Convert.ToBase64String(cipher);
        }
        catch (CryptographicException)
        {
            // DPAPI 不可用（极少见）。宁可不加密也要让程序能用，但要让调用方知道。
            return plain;
        }
    }

    /// <summary>
    /// 解密。对不带前缀的值原样返回 —— 这样手工编辑过 settings.json 的明文值仍然可用，
    /// 并会在下一次保存时自动被加密。
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // 换了 Windows 用户或配置文件被拷到别的机器 —— 解不开就当作空，
            // 让启动前校验提示"请重新填写密码"，而不是拿着乱码去打包。
            return string.Empty;
        }
    }

    public static bool IsProtected(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);
}
