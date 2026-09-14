using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using NewAutoZip.Core.Configuration;
using NewAutoZip.Core.Diagnostics;

namespace NewAutoZip.Core.Packing;

/// <summary>
/// 归档清单 —— 回答"这个包里到底装了什么、它还是不是原来那个包"。
///
/// <b>刻意分成两层，两层装的东西不一样。</b>
///
/// <list type="number">
/// <item>
/// <b>旁挂层</b>（<see cref="ArchiveManifestSidecar"/>）：明文 JSON，文件名是
/// <c>&lt;归档名&gt;.manifest.json</c>，跟着归档一起进云盘。里面<b>只有校验用的元数据</b> ——
/// 包的 SHA-256、字节数、文件数、生成时间。<b>一个源文件名都没有。</b>
/// </item>
/// <item>
/// <b>包内层</b>（<see cref="ArchiveManifestDocument"/>）：逐文件明细（相对路径、字节数、
/// 修改时间、SHA-256），作为 <see cref="EntryName"/> 打进归档内部，随 AES-256 一起加密。
/// </item>
/// </list>
///
/// <b>为什么旁挂那层不能写文件名。</b>
/// 默认开着的 <c>EncryptFileNames</c>（<c>-mhe=on</c>）唯一的作用就是隐藏归档里的文件名表 ——
/// 让拿到包的人连"里面有什么"都看不出来。若把同一份文件名清单以明文放在包旁边、
/// 再跟着包一起上云，那道加密就等于白开：攻击者不必解包，读一个 JSON 就够了。
///
/// 这个项目对"把敏感信息写在压缩包旁边"有过教训 —— <see cref="SecretRedactor"/> 存在的
/// 全部理由，就是旧版把压缩密码明文写进了与压缩包同机存放的日志。
///
/// 所以：**旁挂层证明"包没变"，包内层说明"包里有什么"。想知道有什么，就得先有密码。**
/// </summary>
public static class ArchiveManifest
{
    /// <summary>包内清单的条目名。放在归档根部，恢复演练解开后按这个名字找。</summary>
    public const string EntryName = "__autozip_manifest.json";

    /// <summary>旁挂清单的后缀，接在<b>完整归档名之后</b>（<c>Backup_x.7z.manifest.json</c>）。</summary>
    public const string SidecarSuffix = ".manifest.json";

    /// <summary>
    /// 写旁挂清单时用的临时后缀。
    ///
    /// <b>不能用 <see cref="AtomicJsonFile"/> 的 <c>.tmp</c></b>：
    /// <see cref="Storage.ZipTempManager.SweepIntermediates"/> 会删 ZipTemp 下所有 <c>*.tmp</c>，
    /// 而清单恰恰写在 ZipTemp 里。两者撞上就是清单被自己人删掉。
    /// </summary>
    private const string WritingSuffix = ".writing";

    /// <summary>当前清单格式版本。改了字段语义就 +1，读的一方据此决定认不认。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>由归档路径推出旁挂清单路径。<b>唯一的推导入口</b>，各处不要自己拼字符串。</summary>
    public static string SidecarPathFor(string archivePath) => archivePath + SidecarSuffix;

    /// <summary>这个路径本身是不是一份旁挂清单。</summary>
    public static bool IsSidecar(string path) =>
        path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 算一个文件的 SHA-256（小写十六进制）。流式读取，缓冲区 1 MB ——
    /// 备份包动辄几个 GB，整个读进内存是不行的。
    /// </summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            // 允许别人同时读写：源文件可能正被写入方持有，独占打开会把业务进程搞崩 ——
            // 这正是旧版对被监控文件用 FileShare.None 犯过的错。
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            useAsync: true);

        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 为一批源文件生成包内清单，写到 <paramref name="outputPath"/>。
    ///
    /// 逐个文件算 SHA-256，读不到的（被独占、中途消失）记 <c>null</c> 并在
    /// <see cref="ArchiveManifestEntry.Note"/> 里说明，<b>不抛异常</b>：
    /// 一个文件读不出哈希不该让整轮备份失败 —— 那个文件本来也会被 7za 以退出码 1 跳过。
    /// </summary>
    /// <param name="files">源文件绝对路径。</param>
    /// <param name="rootPath">用于算相对路径的根（监控目录）。算不出就退回文件名。</param>
    public static async Task<ArchiveManifestDocument> BuildAsync(
        IReadOnlyList<string> files,
        string rootPath,
        string archiveFileName,
        DateTimeOffset createdLocal,
        AppSettings settings,
        IAppLogger log,
        CancellationToken ct)
    {
        long startedAt = Stopwatch.GetTimestamp();
        List<ArchiveManifestEntry> entries = new(files.Count);
        long totalBytes = 0;
        int hashed = 0;
        int skipped = 0;

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            string relative = MakeRelative(file, rootPath);
            long size = 0;
            DateTimeOffset? modified = null;
            string? sha = null;
            string? note = null;

            try
            {
                FileInfo info = new(file);

                if (!info.Exists)
                {
                    note = "打包前已消失。";
                    skipped++;
                }
                else
                {
                    size = info.Length;
                    modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    totalBytes += size;

                    sha = await ComputeSha256Async(file, ct).ConfigureAwait(false);
                    hashed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 最常见的是被别的进程独占。7za 那边同样会跳过它并以退出码 1 收尾。
                note = $"无法读取，未能计算校验值：{ex.Message}";
                skipped++;
            }

            entries.Add(new ArchiveManifestEntry
            {
                Path = relative,
                Bytes = size,
                ModifiedUtc = modified,
                Sha256 = sha,
                Note = note,
            });
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        log.Debug($"包内清单：{hashed} 个文件已计算校验值" +
                  (skipped > 0 ? $"，{skipped} 个未能读取" : string.Empty) +
                  $"，耗时 {elapsed.TotalSeconds:0.#} 秒。");

        return new ArchiveManifestDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            ArchiveFileName = archiveFileName,
            CreatedLocal = createdLocal,
            MonitorRoot = rootPath,
            FileCount = entries.Count,
            TotalBytes = totalBytes,
            CompressionLevel = settings.CompressionLevel,
            EncryptFileNames = settings.EncryptFileNames,
            Machine = SafeMachineName(),
            AppVersion = AppPaths.ProductVersion,
            Entries = entries,
        };
    }

    /// <summary>
    /// 生成旁挂清单并写盘。必须在归档<b>原子晋级之后</b>调用 —— 它要算最终 .7z 的哈希。
    /// 失败只返回 null 并记日志：清单是辅助设施，写不出来不该让一次成功的备份变成失败。
    /// </summary>
    public static async Task<ArchiveManifestSidecar?> WriteSidecarAsync(
        string archivePath,
        ArchiveManifestDocument document,
        IAppLogger log,
        CancellationToken ct)
    {
        try
        {
            long bytes = new FileInfo(archivePath).Length;
            string sha = await ComputeSha256Async(archivePath, ct).ConfigureAwait(false);

            ArchiveManifestSidecar sidecar = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                ArchiveFileName = Path.GetFileName(archivePath),
                ArchiveBytes = bytes,
                ArchiveSha256 = sha,
                CreatedLocal = document.CreatedLocal,
                FileCount = document.FileCount,
                SourceBytes = document.TotalBytes,
                CompressionLevel = document.CompressionLevel,
                EncryptFileNames = document.EncryptFileNames,
                HasInnerManifest = true,
                Machine = document.Machine,
                AppVersion = document.AppVersion,
            };

            WriteSidecarFile(SidecarPathFor(archivePath), sidecar);

            return sidecar;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"写旁挂清单失败（不影响归档本身）：{Path.GetFileName(archivePath)}", ex);
            return null;
        }
    }

    /// <summary>
    /// 写旁挂清单文件。走 <c>.writing</c> → <c>File.Move</c> 的原子替换，
    /// 但<b>不</b>用 <see cref="AtomicJsonFile.Write"/> —— 它的 <c>.tmp</c> 会被
    /// <see cref="Storage.ZipTempManager.SweepIntermediates"/> 当成残骸删掉。
    /// </summary>
    public static void WriteSidecarFile(string sidecarPath, ArchiveManifestSidecar sidecar)
    {
        string? directory = Path.GetDirectoryName(sidecarPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = sidecarPath + WritingSuffix;
        string json = System.Text.Json.JsonSerializer.Serialize(sidecar, AtomicJsonFile.Options);

        File.WriteAllText(temp, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, sidecarPath, overwrite: true);
    }

    /// <summary>
    /// 把包内清单写到磁盘，交给 <see cref="PackRequest.ExtraFiles"/> 随包压进去。
    ///
    /// 文件名必须<b>正好</b>是 <see cref="EntryName"/> —— 演练解开后是按这个名字找它的。
    /// 因此它不能像旁挂清单那样带临时后缀，也就没法做原子替换；
    /// 但这不要紧：它是个一次性的中转文件，打包结束就在 finally 里删掉，
    /// 而这个程序同一时刻只打一个包。
    /// </summary>
    public static void WriteInnerManifest(string path, ArchiveManifestDocument document)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = System.Text.Json.JsonSerializer.Serialize(document, AtomicJsonFile.Options);

        File.WriteAllText(path, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>读旁挂清单。读不到 / 格式不对一律返回 null，不抛。</summary>
    public static ArchiveManifestSidecar? TryReadSidecar(string archivePath, out string? error) =>
        AtomicJsonFile.TryRead<ArchiveManifestSidecar>(SidecarPathFor(archivePath), out error);

    /// <summary>
    /// 在解压出来的目录树里找包内清单。
    ///
    /// <b>为什么要搜而不是直接拼路径</b>：7-Zip 存条目时会把绝对路径的盘符与根斜杠剥掉
    /// （<c>D:\ZipTemp\__autozip_manifest.json</c> 存成 <c>ZipTemp\__autozip_manifest.json</c>），
    /// 具体剥成什么样取决于这一批文件的公共前缀 —— 同一个包里换一批源文件，
    /// 清单的落点就不一样。写死路径必然有对不上的那天，搜文件名则永远对得上。
    /// </summary>
    public static ArchiveManifestDocument? TryFindInnerManifest(string extractedRoot, out string? error)
    {
        error = null;

        try
        {
            string? found = Directory
                .EnumerateFiles(extractedRoot, EntryName, SearchOption.AllDirectories)
                .FirstOrDefault();

            if (found is null)
            {
                error = "解压结果里没有找到包内清单。";
                return null;
            }

            return AtomicJsonFile.TryRead<ArchiveManifestDocument>(found, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>算相对路径。跨盘符或算不出来时退回文件名 —— 清单里宁可粗一点也不能崩。</summary>
    private static string MakeRelative(string fullPath, string rootPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return Path.GetFileName(fullPath);
            }

            string relative = Path.GetRelativePath(rootPath, fullPath);

            // GetRelativePath 在跨盘符时原样返回绝对路径；那种情况下相对化没有意义。
            return Path.IsPathRooted(relative) ? Path.GetFileName(fullPath) : relative;
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }

    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// <b>旁挂层</b> —— 明文放在归档旁边，随包进云盘。
///
/// <b>这里绝对不能出现任何源文件名或路径。</b>见 <see cref="ArchiveManifest"/> 的说明：
/// 写了就等于把 <c>-mhe=on</c> 保护的东西原样交出去。
/// 有一条测试专门钉死这件事，别为了"方便排查"往里加文件名。
/// </summary>
public sealed class ArchiveManifestSidecar
{
    public int SchemaVersion { get; set; } = ArchiveManifest.CurrentSchemaVersion;

    /// <summary>归档文件名（<b>不含</b>目录，也不是源文件名）。用来确认清单没跟错包。</summary>
    public string ArchiveFileName { get; set; } = string.Empty;

    public long ArchiveBytes { get; set; }

    /// <summary>归档的 SHA-256。判断"云端那个包还是不是当初上传的那个"就靠它。</summary>
    public string ArchiveSha256 { get; set; } = string.Empty;

    public DateTimeOffset CreatedLocal { get; set; }

    /// <summary>包里有几个源文件。<b>只有数量，没有名字。</b></summary>
    public int FileCount { get; set; }

    /// <summary>源文件总字节数（压缩前）。</summary>
    public long SourceBytes { get; set; }

    public int CompressionLevel { get; set; }

    public bool EncryptFileNames { get; set; }

    /// <summary>包里是否带了逐文件明细。带了才能做逐文件比对。</summary>
    public bool HasInnerManifest { get; set; }

    public string Machine { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;
}

/// <summary>
/// <b>包内层</b> —— 打进归档、随 AES-256 一起加密的逐文件明细。
/// 这一层可以放文件名：想读它就得先有密码，而有密码的人本来就能列出整个包。
/// </summary>
public sealed class ArchiveManifestDocument
{
    public int SchemaVersion { get; set; } = ArchiveManifest.CurrentSchemaVersion;

    public string ArchiveFileName { get; set; } = string.Empty;

    public DateTimeOffset CreatedLocal { get; set; }

    /// <summary>算相对路径时用的根目录（监控目录）。恢复时告诉用户这些文件原本在哪。</summary>
    public string MonitorRoot { get; set; } = string.Empty;

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public int CompressionLevel { get; set; }

    public bool EncryptFileNames { get; set; }

    public string Machine { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public List<ArchiveManifestEntry> Entries { get; set; } = [];
}

public sealed class ArchiveManifestEntry
{
    /// <summary>相对于监控目录的路径。</summary>
    public string Path { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public DateTimeOffset? ModifiedUtc { get; set; }

    /// <summary>小写十六进制 SHA-256。<c>null</c> = 打包时没能读到这个文件，见 <see cref="Note"/>。</summary>
    public string? Sha256 { get; set; }

    /// <summary>异常情况的说明（文件消失、被独占）。正常条目为 null。</summary>
    public string? Note { get; set; }

    [JsonIgnore]
    public bool Verifiable => Sha256 is { Length: > 0 };
}
