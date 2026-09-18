using System.Text.Json;

namespace LeafHotKey;

/// <summary>入力変換を担当するバックエンド。</summary>
public enum InputBackend
{
    /// <summary>本体に内蔵した入力エンジン（既定）。</summary>
    Builtin,

    /// <summary>外部の AutoHotkey スクリプトに任せる。内蔵エンジンと同時には動かさない。</summary>
    Ahk,
}

/// <summary>settings.json の backend セクション。</summary>
public sealed class BackendSettings
{
    public required InputBackend Mode { get; init; }

    /// <summary>実行する AHK スクリプト。空なら未設定。</summary>
    public required string AhkScript { get; init; }

    /// <summary>AutoHotkey 本体。空ならスクリプトと同じフォルダーなどから自動検出する。</summary>
    public required string AhkExecutable { get; init; }

    /// <summary>設定画面の内容から AHK スクリプトを生成して使うか。</summary>
    public bool GenerateScript { get; init; } = true;

    /// <summary>実際に起動するスクリプト（生成を使う場合は生成物）。</summary>
    public string ScriptToRun => GenerateScript ? AhkScriptWriter.PathFor(AhkScript) : AhkScript;

    public static BackendSettings Default { get; } = new()
    {
        Mode = InputBackend.Builtin,
        AhkScript = string.Empty,
        AhkExecutable = string.Empty,
    };

    /// <summary>設定として成立しているかを確かめる。</summary>
    public void Validate()
    {
        if (Mode == InputBackend.Ahk && AhkScript.Length == 0)
        {
            throw new InvalidDataException("AHK バックエンドを使う場合は backend.ahkScript にスクリプトを指定してください。");
        }
    }

    /// <summary>JSON のルートから backend セクションを読む。無い場合は既定（内蔵エンジン）。</summary>
    public static BackendSettings Read(JsonElement root)
    {
        if (!root.TryGetProperty("backend", out var backend) || backend.ValueKind != JsonValueKind.Object) return Default;

        var mode = InputBackend.Builtin;
        if (backend.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String)
        {
            mode = modeValue.GetString() switch
            {
                "ahk" => InputBackend.Ahk,
                "" or "builtin" => InputBackend.Builtin,
                var other => throw new InvalidDataException($"未知の backend.mode です: {other}"),
            };
        }

        var settings = new BackendSettings
        {
            Mode = mode,
            AhkScript = ReadString(backend, "ahkScript"),
            AhkExecutable = ReadString(backend, "ahkExecutable"),
            GenerateScript = !backend.TryGetProperty("generateScript", out var generate) || generate.ValueKind != JsonValueKind.False,
        };

        settings.Validate();
        return settings;
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;
}
