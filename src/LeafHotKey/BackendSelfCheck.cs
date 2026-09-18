using System.Text;
using System.Text.Json;

namespace LeafHotKey;

/// <summary>
/// AHK バックエンドの設定解釈とパス解決を、実際に起動せずに検証する。
/// </summary>
public static class BackendSelfCheck
{
    public static int Run(string? reportPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-backendcheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        // 既定（backend セクションなし）は内蔵エンジン。
        var plain = BackendSettings.Read(JsonDocument.Parse("{}").RootElement);
        Check("backend.default", plain.Mode == InputBackend.Builtin, "backend が無ければ内蔵エンジンを使う");

        var ahkJson = """{ "backend": { "mode": "ahk", "ahkScript": "C:/tmp/MySet.ahk", "ahkExecutable": "" } }""";
        var ahk = BackendSettings.Read(JsonDocument.Parse(ahkJson).RootElement);
        Check(
            "backend.ahk",
            ahk.Mode == InputBackend.Ahk && ahk.AhkScript == "C:/tmp/MySet.ahk" && ahk.AhkExecutable.Length == 0,
            "AHK バックエンドの設定を読む");

        var emptyScript = """{ "backend": { "mode": "ahk" } }""";
        Check("backend.script.required", Throws(() => BackendSettings.Read(JsonDocument.Parse(emptyScript).RootElement)), "AHK 指定でスクリプト未設定は拒否する");

        var unknownMode = """{ "backend": { "mode": "python" } }""";
        Check("backend.mode.unknown", Throws(() => BackendSettings.Read(JsonDocument.Parse(unknownMode).RootElement)), "未知のバックエンドを拒否する");

        // パス解決。実体は一時ファイルで作る。
        var work = Path.Combine(Path.GetTempPath(), "leafhotkey-backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "MySet.ahk");
            File.WriteAllText(script, "; test", encoding);

            Check("backend.script.found", AhkBackend.ResolveScript(script) == script, "存在するスクリプトを解決する");
            Check("backend.script.missing", AhkBackend.ResolveScript(Path.Combine(work, "absent.ahk")) is null, "存在しないスクリプトは解決しない");
            Check("backend.script.empty", AhkBackend.ResolveScript(string.Empty) is null, "未設定のスクリプトは解決しない");

            var portable = Path.Combine(work, "AutoHotkeyU64.exe");
            File.WriteAllText(portable, string.Empty);
            Check("backend.exe.portable", AhkBackend.ResolveExecutable(string.Empty, script) == portable, "スクリプトと同じフォルダーの AutoHotkey を使う");

            var configured = Path.Combine(work, "custom.exe");
            File.WriteAllText(configured, string.Empty);
            Check("backend.exe.configured", AhkBackend.ResolveExecutable(configured, script) == configured, "指定された AutoHotkey を優先する");

            File.Delete(portable);
            Check("backend.exe.missing", AhkBackend.ResolveExecutable(Path.Combine(work, "absent.exe"), script) is null, "見つからない場合は起動しない");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        using (var backend = new AhkBackend())
        {
            Check("backend.idle", !backend.IsRunning && backend.Status == "停止中", "起動前は停止状態を報告する");
            var missing = new BackendSettings { Mode = InputBackend.Ahk, AhkScript = "Z:/absent/MySet.ahk", AhkExecutable = string.Empty };
            Check("backend.start.missing", !backend.Start(missing) && backend.Status == "スクリプトが見つかりません", "スクリプトが無ければ起動せず理由を返す");

            // 監視の判断（落ちたら戻し、外部インスタンスがあれば何もしない）。
            Check("backend.restart.exited", AhkBackend.ShouldRestart(true, false, false, 0, 3), "落ちていれば再起動する");
            Check("backend.restart.external", !AhkBackend.ShouldRestart(true, true, false, 0, 3), "外部の AutoHotkey が動いていれば起動しない");
            Check("backend.restart.released", !AhkBackend.ShouldRestart(true, false, true, 0, 3), "ホスト終了で手放したものは再起動しない");
            Check("backend.restart.limit", !AhkBackend.ShouldRestart(true, false, false, 3, 3), "再試行の上限で止める");
            Check("backend.restart.alive", !AhkBackend.ShouldRestart(false, false, false, 0, 3), "動いている間は何もしない");

            // 別のスクリプトへ切り替える指示では、古いものを止めて起動し直す。
            var switchWork = Path.Combine(Path.GetTempPath(), "leafhotkey-backend-switch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(switchWork);
            try
            {
                var first = Path.Combine(switchWork, "first.ahk");
                var second = Path.Combine(switchWork, "second.ahk");
                File.WriteAllText(first, "; first", encoding);
                File.WriteAllText(second, "; second", encoding);
                Check(
                    "backend.switch.script",
                    AhkBackend.ResolveScript(second) != AhkBackend.ResolveScript(first),
                    "スクリプトの切り替えを別物として扱う");
            }
            finally
            {
                Directory.Delete(switchWork, recursive: true);
            }

            // ホスト終了時はプロセスを止めずに手放す。
            backend.Release();
            Check("backend.release", !backend.IsRunning && backend.Status.Contains("ホスト終了", StringComparison.Ordinal), "ホスト終了時は手放して状態を残す");
        }

        // 設定画面の内容から AHK スクリプトを生成する。
        var sample = """"
{
  "profiles": [
    { "id": "chrome", "name": "Chrome", "enabled": true, "processNames": ["chrome.exe"], "rules": [
      { "trigger": { "key": "PgDn" }, "action": { "type": "send", "sequence": ["!{Left}"] } },
      { "trigger": { "key": "e", "modifiers": ["Ctrl", "Shift"] }, "action": { "type": "send", "sequence": ["f5", "{Esc}"] } }
    ] },
    { "id": "clip", "name": "Clip", "enabled": true, "processNames": ["CLIPStudioPaint.exe"], "rules": [
      { "trigger": { "key": "MButton", "anyModifier": true, "passThroughNative": true }, "action": { "type": "passthrough" } },
      { "trigger": { "prefix": "MButton", "key": "f13" }, "action": { "type": "hold", "modifier": "Ctrl", "releaseOn": "f13", "blind": false } }
    ] },
    { "id": "unreal", "name": "UE", "enabled": true, "processNames": ["UE4Editor.exe", "UnrealEditor.exe"], "rules": [
      { "trigger": { "key": "PgDn" }, "action": { "type": "send", "sequence": ["^z"] } }
    ] }
  ]
}
"""";

        var generated = AhkScriptWriter.Build(sample);
        Check("script.section", generated.Contains("#IfWinActive, ahk_exe chrome.exe", StringComparison.Ordinal), "プロファイルごとの対象を出す");
        Check("script.send", generated.Contains("PgDn::Snd(\"!{Left}\")", StringComparison.Ordinal), "送信文字列を元の表記のまま出す");
        Check(
            "script.split",
            generated.Contains("Snd(\"f5\")", StringComparison.Ordinal) && generated.Contains("Snd(\"{Esc}\")", StringComparison.Ordinal),
            "Snd の第2引数は別の送信として出す");
        Check("script.modifiers", generated.Contains("^+e::", StringComparison.Ordinal), "修飾キー付きの割り当てを出す");
        Check("script.passthrough", generated.Contains("*~MButton::return", StringComparison.Ordinal), "元入力を通す前置キーを出す");
        Check(
            "script.hold",
            generated.Contains("MButton & f13::Hold(\"Ctrl\", \"f13\", 0)", StringComparison.Ordinal),
            "保持ルールを {Blind} なしで出す");
        Check(
            "script.group",
            generated.Contains("GroupAdd, LeafHotKeyGroup_unreal, ahk_exe UE4Editor.exe", StringComparison.Ordinal) &&
            generated.Contains("#IfWinActive, ahk_group LeafHotKeyGroup_unreal", StringComparison.Ordinal),
            "対象が複数ならグループにする");
        Check(
            "script.helpers",
            generated.Contains("IME_SET(SetSts, WinTitle=\"A\")", StringComparison.Ordinal) && generated.Contains("Hold(mod, trigger, blind=1)", StringComparison.Ordinal),
            "元 AHK と同じ共通処理を埋め込む");
        Check("script.path", AhkScriptWriter.PathFor("C:/x/MySet.ahk").EndsWith("MySet.generated.ahk", StringComparison.Ordinal), "生成先は元スクリプトと同じフォルダー");

        try
        {
            var quoted = """"
{ "profiles": [ { "id": "x", "name": "X", "enabled": true, "processNames": ["x.exe"], "rules": [
  { "trigger": { "key": "q" }, "action": { "type": "send", "sequence": ["a\"b"] } } ] } ] }
"""";
            Check("script.escape", AhkScriptWriter.Build(quoted).Contains("`\"", StringComparison.Ordinal), "引用符をバッククォートで逃がす");

            // 既定の全プロファイルを生成できる（構文上の穴を検出する）。
            var defaults = FindDefaultSettings();
            if (defaults is null)
            {
                Check("script.defaults", false, "defaults/settings.json が見つからない");
            }
            else
            {
                var full = AhkScriptWriter.Build(File.ReadAllText(defaults, encoding));
                var sections = full.Split("#IfWinActive").Length - 1;
                Check(
                    "script.defaults",
                    full.Contains("Snd(", StringComparison.Ordinal) && full.Contains("MButton & f13", StringComparison.Ordinal) && sections > 20,
                    $"既定の台帳から生成できる（#IfWinActive {sections} 箇所）");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            Check("script.build", false, $"生成に失敗した（{ex.Message}）");
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

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return true;
        }
    }
}
