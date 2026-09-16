using System.Text;

namespace LeafHotKey;

/// <summary>
/// GUI なしで Phase 1 の骨格を検証する。
/// WinExe はコンソールへ出力できないため、結果はレポートファイルと終了コードで返す。
/// </summary>
public static class SelfCheck
{
    public static string DefaultReportPath =>
        Path.Combine(Path.GetTempPath(), "leafhotkey-selfcheck.txt");

    public static int Run(string? reportPath = null)
    {
        var path = reportPath ?? DefaultReportPath;
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        // 途中で停止した場合に、どの検査まで進んだか分かるよう逐次追記する。
        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        // 実行中の本体へ影響しないよう、検証専用のパイプ名を使う。
        var pipeName = "LeafHotKey.selfcheck." + Guid.NewGuid().ToString("N");
        var state = new HostState();
        using var server = new ControlServer(state, pipeName);
        var shutdownRequested = 0;
        server.ShutdownRequested += () => Interlocked.Increment(ref shutdownRequested);
        server.Start();

        // 応答内容を逐次残す。停止した場合もどのコマンドで止まったか分かる。
        ControlClient.Trace = stage => File.AppendAllText(path, $"  TRACE {stage}{Environment.NewLine}", encoding);

        string? Call(string name, string command)
        {
            File.AppendAllText(path, $"CALL {name}{Environment.NewLine}", encoding);
            var response = ControlClient.Send(command, 2000, pipeName);
            File.AppendAllText(path, $"RECV {name}: {response ?? "<null>"}{Environment.NewLine}", encoding);
            return response;
        }

        Check("ping", Call("ping", ControlProtocol.Ping) == ControlProtocol.Pong, "PING へ PONG が返る");
        Check("status.initial", Call("status.initial", ControlProtocol.Status) == "OK RUNNING", "初期状態は RUNNING");
        Check("pause", Call("pause", ControlProtocol.Pause) == "OK PAUSED", "PAUSE で停止する");
        Check("status.paused", Call("status.paused", ControlProtocol.Status) == "OK PAUSED", "停止状態が保持される");
        Check("pause.twice", Call("pause.twice", ControlProtocol.Pause) == "ERR NOT_RUNNING", "停止中の再停止は拒否する");
        Check("resume", Call("resume", ControlProtocol.Resume) == "OK RUNNING", "RESUME で再開する");
        Check("unknown", Call("unknown", "NOPE") == "ERR UNKNOWN_COMMAND", "未知コマンドを拒否する");

        Check(
            "disconnect",
            ControlClient.Send(ControlProtocol.Ping, 300, "LeafHotKey.absent." + Guid.NewGuid().ToString("N")) is null,
            "接続できない場合は null を返す");
        Check(
            "disconnect.isresponding",
            !ControlClient.IsHostResponding(300, "LeafHotKey.absent." + Guid.NewGuid().ToString("N")),
            "接続断は応答ありと判定しない");

        Check("shutdown.unknownreason", Call("shutdown.unknownreason", "SHUTDOWN OOPS") == "ERR UNKNOWN_REASON", "未知の終了理由を拒否する");
        Check("shutdown", Call("shutdown", ControlProtocol.Shutdown) == "OK SHUTTINGDOWN MANUAL", "引数なし SHUTDOWN は手動終了");
        Check("shutdown.reason", state.ExitReason == ExitReason.Manual, "手動終了として記録する（自動再起動しない）");
        Check("shutdown.event", Volatile.Read(ref shutdownRequested) == 1, "終了要求イベントが 1 回発火する");

        // ゲーム保護による退避は別の終了理由として記録される必要がある。
        var gamePipeName = "LeafHotKey.selfcheck." + Guid.NewGuid().ToString("N");
        var gameState = new HostState();
        using var gameServer = new ControlServer(gameState, gamePipeName);
        gameServer.Start();
        var gameResponse = ControlClient.Send(
            ControlProtocol.Shutdown + " " + ControlProtocol.ReasonGame,
            2000,
            gamePipeName);
        Check("shutdown.game", gameResponse == "OK SHUTTINGDOWN GAME", "ゲーム保護の退避を受け付ける");
        Check("shutdown.game.reason", gameState.ExitReason == ExitReason.GameProtection, "退避理由を GameProtection として記録する");

        var role = "selfcheck." + Guid.NewGuid().ToString("N");
        using (var first = SingleInstance.TryAcquire(role))
        {
            using var second = SingleInstance.TryAcquire(role);
            Check("singleinstance.first", first.Acquired, "1つ目は所有権を取得する");
            Check("singleinstance.second", !second.Acquired, "2つ目は起動を拒否される");
        }

        using (var afterRelease = SingleInstance.TryAcquire(role))
        {
            Check("singleinstance.release", afterRelease.Acquired, "解放後は再取得できる");
        }

        File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
        return failures == 0 ? 0 : 1;
    }
}
