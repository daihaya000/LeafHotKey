using System.Text;

namespace LeafHotKey;

/// <summary>
/// 設定の保存・検証・競合検出を確認する。
/// 実際の保存先には触れず、一時ディレクトリで検証する。
/// </summary>
public static class SettingsSelfCheck
{
    public static int Run(string? reportPath, string? defaultsPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-settingscheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        var defaults = defaultsPath ?? FindDefaultSettings();
        if (defaults is null)
        {
            Check("defaults.found", false, "defaults/settings.json が見つからない");
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "leafhotkey-settingscheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var settingsPath = Path.Combine(workDirectory, "settings.json");
            var store = new SettingsStore(settingsPath, defaults);

            Check("seed.missing", !File.Exists(settingsPath), "初期状態では保存先が存在しない");

            var first = store.Load();
            Check("seed.created", File.Exists(settingsPath), "初回読み込みで既定設定から作られる");
            Check("seed.contents", first.Profiles.Count == 13 && first.GameProtection.StopTriggerProcessNames.Count == 6, $"プロファイル {first.Profiles.Count} 件・停止対象 {first.GameProtection.StopTriggerProcessNames.Count} 件を読み込む");

            var again = store.Load();
            Check("revision.stable", first.Revision == again.Revision, "内容が変わらなければ版も変わらない");

            // 正常な更新。
            var updated = first.Json
                .Replace("\"pollIntervalMs\": 1000", "\"pollIntervalMs\": 750", StringComparison.Ordinal)
                .Replace("\"imeDisableBeforeSend\": true", "\"imeDisableBeforeSend\": false", StringComparison.Ordinal);
            Check("update.prepared", updated != first.Json, "更新用の内容を用意できる");
            Check("input.ime.default", first.ImeDisableBeforeSend, "既定では送信前に IME を無効化する");

            var saved = store.Save(updated, first.Revision);
            Check("save.ok", saved.Success, $"正しい内容を保存できる（{saved.Message}）");

            var afterSave = store.Load();
            Check("save.applied", afterSave.GameProtection.PollIntervalMs == 750, $"保存内容が反映される（pollIntervalMs={afterSave.GameProtection.PollIntervalMs}）");
            Check("input.ime.saved", !afterSave.ImeDisableBeforeSend, "保存した IME 設定を読み直せる");
            Check("save.revision", afterSave.Revision != first.Revision && afterSave.Revision == saved.Revision, "保存で版が更新される");
            Check("save.backup", File.Exists(store.BackupPath), "直前の内容がバックアップに残る");
            Check("save.no-temp", !File.Exists(settingsPath + ".tmp"), "一時ファイルが残らない");

            var bom = File.ReadAllBytes(settingsPath);
            Check("save.encoding", !(bom.Length >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF), "UTF-8 BOM なしで保存する");

            // 版が古い保存は拒否する。
            var stale = store.Save(first.Json, first.Revision);
            Check("conflict.rejected", stale.Status == SaveStatus.Conflict, $"古い版での保存を拒否する（{stale.Message}）");
            Check("conflict.unchanged", store.Load().Revision == afterSave.Revision, "拒否した場合は内容を変えない");

            // 壊れた JSON は拒否する。
            var broken = store.Save("{ \"profiles\": [", afterSave.Revision);
            Check("invalid.json", broken.Status == SaveStatus.Invalid, "壊れた JSON を拒否する");
            Check("invalid.json.unchanged", store.Load().Revision == afterSave.Revision, "拒否後も内容が残る");

            // 設定として成立しない内容も拒否する。
            // 改行コードに依存しないよう、文字列置換ではなく JSON を編集する。
            var mutated = System.Text.Json.Nodes.JsonNode.Parse(afterSave.Json)!;
            mutated["gameProtection"]!["stopTriggerProcessNames"] = new System.Text.Json.Nodes.JsonArray("");
            var emptyTriggers = mutated.ToJsonString();
            var invalidRule = store.Save(emptyTriggers, store.Load().Revision);
            Check("invalid.rule", invalidRule.Status == SaveStatus.Invalid, $"空のプロセス名を拒否する（{invalidRule.Message}）");

            var unknownAction = afterSave.Json.Replace("\"type\": \"send\"", "\"type\": \"explode\"", StringComparison.Ordinal);
            var invalidAction = store.Save(unknownAction, store.Load().Revision);
            Check("invalid.action", invalidAction.Status == SaveStatus.Invalid, "未知の action.type を拒否する");

            var unknownKey = afterSave.Json.Replace("\"sequence\": [\"{Right}\"]", "\"sequence\": [\"{NoSuchKey}\"]", StringComparison.Ordinal);
            var invalidKey = store.Save(unknownKey, store.Load().Revision);
            Check("invalid.key", invalidKey.Status == SaveStatus.Invalid, "未知のキー名を拒否する");

            Check("invalid.unchanged", store.Load().GameProtection.PollIntervalMs == 750, "拒否された保存は反映されない");

            // 正本が壊れても起動できるよう、バックアップ → 既定設定の順で復旧する。
            // バックアップ側に識別可能な内容（pollIntervalMs=321）を残してから正本を壊す。
            var marker = store.Load().Json.Replace("\"pollIntervalMs\": 750", "\"pollIntervalMs\": 321", StringComparison.Ordinal);
            Check("recover.marker", store.Save(marker, store.Load().Revision).Success, "復旧検証用の内容を用意できる");

            File.WriteAllText(settingsPath, "{ \"profiles\": [", encoding);
            var fromBackup = store.Load();
            Check("recover.backup", fromBackup.GameProtection.PollIntervalMs == 750, $"壊れた正本はバックアップから復旧する（pollIntervalMs={fromBackup.GameProtection.PollIntervalMs}）");
            Check("recover.backup.file", File.ReadAllText(settingsPath, encoding) == fromBackup.Json, "復旧内容を正本へ書き戻す");

            File.WriteAllText(settingsPath, "not json", encoding);
            File.WriteAllText(store.BackupPath, "not json", encoding);
            var fromDefaults = store.Load();
            Check(
                "recover.defaults",
                fromDefaults.GameProtection.PollIntervalMs == 1000 && fromDefaults.Profiles.Count == 13,
                $"バックアップも壊れている場合は既定設定で復旧する（profiles={fromDefaults.Profiles.Count}）");
            Check("recover.defaults.file", File.ReadAllText(settingsPath, encoding) == fromDefaults.Json, "復旧内容を正本へ書き戻す");

            var afterRecovery = fromDefaults.Json.Replace("\"pollIntervalMs\": 1000", "\"pollIntervalMs\": 500", StringComparison.Ordinal);
            Check("recover.save", store.Save(afterRecovery, fromDefaults.Revision).Success, "復旧後は正本と版が一致して保存できる");

            // 既定へ戻す。
            var restored = store.RestoreDefaults();
            Check("restore.ok", restored.Success, "既定設定へ戻せる");
            Check(
                "restore.contents",
                store.Load().Revision == SettingsStore.RevisionOf(File.ReadAllText(defaults, encoding)),
                "戻した内容が既定設定と一致する");

            // 版を指定しない保存は上書きを許す（初期化などの用途）。
            var forced = store.Save(updated, expectedRevision: null);
            Check("save.force", forced.Success, "版を指定しない保存は通る");
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
    }

    private static string? FindDefaultSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "defaults", "settings.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
