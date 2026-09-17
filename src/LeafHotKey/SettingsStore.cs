using System.Security.Cryptography;
using System.Text;

namespace LeafHotKey;

/// <summary>読み込んだ設定のスナップショット。</summary>
public sealed class SettingsSnapshot
{
    public required string Json { get; init; }

    /// <summary>保存時の競合検出に使う版。内容が変わると変わる。</summary>
    public required string Revision { get; init; }

    public required GameProtectionSettings GameProtection { get; init; }

    public required IReadOnlyList<HotkeyProfile> Profiles { get; init; }
}

/// <summary>保存要求の結果。</summary>
public enum SaveStatus
{
    Saved,

    /// <summary>JSON として読めない、または設定として成立しない。</summary>
    Invalid,

    /// <summary>他の更新が先に入っている（版が一致しない）。</summary>
    Conflict,

    /// <summary>書き込みに失敗した。</summary>
    WriteFailed,
}

public sealed class SaveResult
{
    public required SaveStatus Status { get; init; }

    public required string Message { get; init; }

    /// <summary>保存に成功した場合の新しい版。</summary>
    public string? Revision { get; init; }

    public bool Success => Status == SaveStatus.Saved;
}

/// <summary>
/// 設定ファイルの正本を管理する。
/// 検証してから原子的に置き換え、直前の内容をバックアップとして残す。
/// </summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string _defaultsPath;

    public SettingsStore(string settingsPath, string defaultsPath)
    {
        SettingsPath = settingsPath;
        _defaultsPath = defaultsPath;
    }

    public string SettingsPath { get; }

    public string BackupPath => SettingsPath + ".bak";

    /// <summary>ユーザーごとの保存先。</summary>
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LeafHotKey",
        "settings.json");

    /// <summary>保存先が無ければ既定設定から作る。</summary>
    public void EnsureExists()
    {
        lock (_gate)
        {
            if (File.Exists(SettingsPath)) return;

            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.Copy(_defaultsPath, SettingsPath);
        }
    }

    public SettingsSnapshot Load()
    {
        lock (_gate)
        {
            EnsureExists();

            if (TryParse(ReadText(SettingsPath)) is { } current) return current;

            // 壊れた内容でも起動できるよう、バックアップ → 既定設定の順で復旧する。
            // 復旧内容は正本へ書き戻し、壊れていた内容はバックアップ側へ退避する。
            var recovered = TryParse(File.Exists(BackupPath) ? ReadText(BackupPath) : string.Empty)
                ?? TryParse(ReadText(_defaultsPath));
            if (recovered is null) throw new InvalidDataException($"設定を読み込めません: {SettingsPath}");

            WriteAtomic(recovered.Json);
            return recovered;
        }
    }

    /// <summary>内容が設定として成立すればスナップショットを返す。壊れている場合は null。</summary>
    private SettingsSnapshot? TryParse(string json)
    {
        try
        {
            return Parse(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 設定を差し替える。検証に通らない、または版が一致しない場合は既存ファイルを変更しない。
    /// </summary>
    public SaveResult Save(string json, string? expectedRevision)
    {
        lock (_gate)
        {
            EnsureExists();

            var currentJson = ReadText(SettingsPath);
            var currentRevision = RevisionOf(currentJson);

            if (expectedRevision is not null && !string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal))
            {
                return new SaveResult
                {
                    Status = SaveStatus.Conflict,
                    Message = "他の更新が先に保存されています。再読み込みしてからやり直してください。",
                };
            }

            SettingsSnapshot parsed;
            try
            {
                parsed = Parse(json);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or FormatException)
            {
                return new SaveResult { Status = SaveStatus.Invalid, Message = ex.Message };
            }

            try
            {
                WriteAtomic(json);
            }
            catch (IOException ex)
            {
                return new SaveResult { Status = SaveStatus.WriteFailed, Message = ex.Message };
            }
            catch (UnauthorizedAccessException ex)
            {
                return new SaveResult { Status = SaveStatus.WriteFailed, Message = ex.Message };
            }

            return new SaveResult
            {
                Status = SaveStatus.Saved,
                Message = $"プロファイル {parsed.Profiles.Count} 件を保存しました。",
                Revision = RevisionOf(json),
            };
        }
    }

    /// <summary>既定設定へ戻す。現在の内容はバックアップに残す。</summary>
    public SaveResult RestoreDefaults() => Save(ReadText(_defaultsPath), expectedRevision: null);

    /// <summary>内容から版を計算する。</summary>
    public static string RevisionOf(string json)
    {
        var hash = SHA256.HashData(new UTF8Encoding(false).GetBytes(json));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>設定として成立するかを確かめ、スナップショットを作る。</summary>
    private SettingsSnapshot Parse(string json)
    {
        var temp = Path.Combine(Path.GetTempPath(), "leafhotkey-validate-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            var gameProtection = GameProtectionSettings.Load(temp);
            var profiles = HotkeyProfileLoader.Load(temp);

            return new SettingsSnapshot
            {
                Json = json,
                Revision = RevisionOf(json),
                GameProtection = gameProtection,
                Profiles = profiles,
            };
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>一時ファイルへ書いてから置き換える。途中で壊れた内容を残さない。</summary>
    private void WriteAtomic(string json)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));

        if (File.Exists(SettingsPath))
        {
            File.Replace(temp, SettingsPath, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, SettingsPath);
        }
    }

    private static string ReadText(string path) => File.ReadAllText(path, new UTF8Encoding(false));
}
