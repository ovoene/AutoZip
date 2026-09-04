using Microsoft.Win32;

namespace NewAutoZip.Core.OneDrive;

/// <param name="DisplayName">给人看的名字，例如"OneDrive - 个人"或"OneDrive - 某公司"。</param>
/// <param name="Path">本地根目录。</param>
/// <param name="IsBusiness">是否商业版（OneDrive for Business / SharePoint）。</param>
/// <param name="Source">这条是从哪儿发现的，用于日志里交代来源。</param>
public sealed record OneDriveAccount(string DisplayName, string Path, bool IsBusiness, string Source)
{
    /// <summary>目录当前是否真的存在。</summary>
    public bool Exists => Directory.Exists(Path);
}

/// <summary>
/// 找出这台机器上所有 OneDrive 本地根目录。
///
/// 旧版只认一个固定环境变量，个人版 + 商业版并存、或者用户把 OneDrive 挪过位置的机器上就找不着。
/// 新版枚举三个来源并合并去重：
///   1. <c>HKCU\Software\Microsoft\OneDrive\Accounts\*</c> 下每个账号的 UserFolder（最可靠）
///   2. <c>%OneDrive%</c> / <c>%OneDriveConsumer%</c> / <c>%OneDriveCommercial%</c>
///   3. <c>%USERPROFILE%\OneDrive</c> 兜底
///
/// 全部方法都不抛异常：注册表读不到就跳过，返回空列表由界面提示用户手动选目录。
/// </summary>
public static class OneDriveLocator
{
    private const string AccountsKey = @"Software\Microsoft\OneDrive\Accounts";

    public static IReadOnlyList<OneDriveAccount> Discover()
    {
        List<OneDriveAccount> found = [];

        AddRange(found, FromRegistry());
        AddRange(found, FromEnvironment());
        AddRange(found, FromUserProfile());

        // 存在的排前面，商业版排后面（个人版更常用），其余按名字稳定排序。
        return
        [
            .. found
                .OrderByDescending(a => a.Exists)
                .ThenBy(a => a.IsBusiness)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCulture)
        ];
    }

    /// <summary>猜一个默认值给设置界面用。没有任何候选时返回 null，界面据此提示手动选择。</summary>
    public static OneDriveAccount? Preferred() =>
        Discover().FirstOrDefault(a => a.Exists);

    /// <summary>
    /// 判断一个目录是否真的在 OneDrive 的同步范围内。
    /// 比"路径里有 OneDrive 字样"可靠得多 —— 用户完全可以建一个叫 OneDrive 的普通文件夹，
    /// 那里面的东西永远不会被上传。<b>同步根之下的任意层子目录都算在范围内。</b>
    /// </summary>
    public static bool LooksSynced(string directory) => OneDriveScope.InScope(directory);

    // ==================================================================

    private static IEnumerable<OneDriveAccount> FromRegistry()
    {
        RegistryKey? accounts = null;

        try
        {
            accounts = Registry.CurrentUser.OpenSubKey(AccountsKey);
        }
        catch (Exception)
        {
            // 注册表读不到就当没有
        }

        if (accounts is null)
        {
            yield break;
        }

        using (accounts)
        {
            string[] names;

            try
            {
                names = accounts.GetSubKeyNames();
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (string name in names)
            {
                OneDriveAccount? account = ReadAccount(accounts, name);

                if (account is not null)
                {
                    yield return account;
                }
            }
        }
    }

    private static OneDriveAccount? ReadAccount(RegistryKey accounts, string subKeyName)
    {
        try
        {
            using RegistryKey? key = accounts.OpenSubKey(subKeyName);

            if (key?.GetValue("UserFolder") is not string folder || string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            // Business1 / Business2 … 是商业版；Personal 是个人版。
            bool business = subKeyName.StartsWith("Business", StringComparison.OrdinalIgnoreCase);

            string label = key.GetValue("DisplayName") as string
                           ?? (business ? "OneDrive（工作或学校）" : "OneDrive（个人）");

            return new OneDriveAccount(
                label,
                Normalize(folder),
                business,
                $@"注册表 HKCU\{AccountsKey}\{subKeyName}");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<OneDriveAccount> FromEnvironment()
    {
        (string Variable, string Label, bool Business)[] candidates =
        [
            ("OneDriveConsumer", "OneDrive（个人）", false),
            ("OneDrive", "OneDrive", false),
            ("OneDriveCommercial", "OneDrive（工作或学校）", true),
        ];

        foreach ((string variable, string label, bool business) in candidates)
        {
            string? value = null;

            try
            {
                value = Environment.GetEnvironmentVariable(variable);
            }
            catch (Exception)
            {
                // 忽略
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return new OneDriveAccount(label, Normalize(value), business, $"环境变量 %{variable}%");
            }
        }
    }

    private static IEnumerable<OneDriveAccount> FromUserProfile()
    {
        string? profile = null;

        try
        {
            profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify);
        }
        catch (Exception)
        {
            // 忽略
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            yield break;
        }

        string guess = Path.Combine(profile, "OneDrive");

        if (Directory.Exists(guess))
        {
            yield return new OneDriveAccount("OneDrive", Normalize(guess), false, @"%USERPROFILE%\OneDrive");
        }
    }

    private static void AddRange(List<OneDriveAccount> into, IEnumerable<OneDriveAccount> items)
    {
        foreach (OneDriveAccount item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            // 同一个目录可能被多个来源发现，保留第一个（注册表优先，来源信息更准）。
            if (into.Any(existing => string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            into.Add(item);
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path.Trim();
        }
    }
}
