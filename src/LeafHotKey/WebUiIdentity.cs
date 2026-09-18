using System.Security.Cryptography;
using System.Text;

namespace LeafHotKey;

/// <summary>
/// WebUI の URL を起動ごとに変えないための、固定ポートと保存したトークンを扱う。
/// トークンはユーザーごとの保存先に置き、ブックマークした URL をそのまま使い回せるようにする。
/// </summary>
public static class WebUiIdentity
{
    /// <summary>設定画面の既定ポート。ここが埋まっている場合だけ空きポートへ退避する。</summary>
    public const int DefaultPort = 17832;

    /// <summary>トークンの保存先。</summary>
    public static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LeafHotKey",
        "webui.token");

    /// <summary>保存済みのトークンを返す。無ければ作って保存する。</summary>
    public static string LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path, new UTF8Encoding(false)).Trim();
                if (saved.Length >= 32) return saved;
            }

            var created = NewToken();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, created, new UTF8Encoding(false));
            return created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 保存できない環境では起動ごとのトークンに戻る（URL は起動ごとに変わる）。
            return NewToken();
        }
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');
}
