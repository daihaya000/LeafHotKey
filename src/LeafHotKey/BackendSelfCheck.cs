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

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
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
