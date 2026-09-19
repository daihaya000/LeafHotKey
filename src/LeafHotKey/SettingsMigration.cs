using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeafHotKey;

/// <summary>
/// AHK 時代の設定ファイルを現行の形へ移行する。
/// - 送信内容をアプリの表記（Ctrl+Alt+g）へ変換する
/// - 廃止した backend セクションと action.blind を外す
/// - schemaVersion を現行へ上げる
/// 読み込みと保存の両方で通すため、何度実行しても結果は変わらない。
/// </summary>
public static class SettingsMigration
{
    /// <summary>現行の設定形式。2 で送信内容の表記をアプリ独自のものへ変えた。</summary>
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>移行が必要なら書き換えた JSON を返す。不要なら入力をそのまま返す。</summary>
    public static string Apply(string json)
    {
        if (IsCurrent(json)) return json;

        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("設定を読み込めません。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // 壊れた内容は移行せず、検証側に判断させる。
            return json;
        }

        var changed = root.Remove("backend");

        if (root["profiles"] is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                if (profile["rules"] is not JsonArray rules) continue;

                foreach (var rule in rules.OfType<JsonObject>())
                {
                    if (rule["action"] is not JsonObject action) continue;

                    // 動作に影響しない AHK 時代の指定は残さない。
                    if (action.Remove("blind")) changed = true;

                    if (action["sequence"] is not JsonArray sequence) continue;

                    for (var index = 0; index < sequence.Count; index++)
                    {
                        if (sequence[index] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                        if (!TryConvert(text, out var converted)) continue;

                        sequence[index] = converted;
                        changed = true;
                    }
                }
            }
        }

        // ここへ来た時点で形式が現行ではないため、版を上げて書き戻す。
        root["schemaVersion"] = CurrentSchemaVersion;
        return root.ToJsonString(WriteOptions);
    }

    /// <summary>現行形式かどうか。書き方の違いで取りこぼさないよう、値と backend の有無で判定する。</summary>
    private static bool IsCurrent(string json)
        => !json.Contains("\"backend\"", StringComparison.Ordinal) &&
           (json.Contains($"\"schemaVersion\": {CurrentSchemaVersion}", StringComparison.Ordinal) ||
            json.Contains($"\"schemaVersion\":{CurrentSchemaVersion}", StringComparison.Ordinal));

    /// <summary>旧表記なら現行表記へ変換する。現行表記や解釈できない内容はそのままにする。</summary>
    private static bool TryConvert(string text, out string converted)
    {
        converted = text;
        if (!LooksLikeLegacyNotation(text)) return false;

        try
        {
            converted = SendNotation.Format(LegacyAhkNotation.Parse(text));
            return !string.Equals(converted, text, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>AHK の記号を含む、または「f5」のように現行表記ではキー名になる旧表記か。</summary>
    private static bool LooksLikeLegacyNotation(string text)
    {
        if (text.IndexOfAny(new[] { '^', '!', '#', '{', '}' }) >= 0) return true;
        if (text.StartsWith('+')) return true;

        // 「f5」は旧表記では文字 f と 5、現行表記では F5 キーになるため、旧表記として扱う。
        return text.Length > 1 && KeyNames.TryNormalize(text, out var canonical) &&
               !string.Equals(text, canonical, StringComparison.Ordinal);
    }
}
