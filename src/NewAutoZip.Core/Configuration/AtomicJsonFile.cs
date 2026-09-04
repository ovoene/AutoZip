using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewAutoZip.Core.Configuration;

/// <summary>
/// 原子写 JSON：先写 .tmp，再 File.Move 覆盖目标。
/// 断电或进程被杀不会留下半截 JSON —— 旧版 Checkpoint.Save 直接 File.WriteAllText，
/// 而且连 try/catch 都没有。
/// </summary>
public static class AtomicJsonFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 让中文路径在文件里保持可读，而不是被转义成 \uXXXX
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>读取。文件不存在、内容损坏、无权限，统一返回 null，并把原因写进 <paramref name="error"/>。</summary>
    public static T? TryRead<T>(string path, out string? error)
        where T : class
    {
        error = null;

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>原子写入。抛出的异常由调用方决定如何处理（配置保存失败需要让用户知道）。</summary>
    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        string json = JsonSerializer.Serialize(value, Options);

        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // File.Move(overwrite:true) 在同目录内是原子替换。
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>尽力写入，失败只返回 false（用于状态文件这类"写不进去也要继续跑"的场景）。</summary>
    public static bool TryWrite<T>(string path, T value, out string? error)
    {
        error = null;

        try
        {
            Write(path, value);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
