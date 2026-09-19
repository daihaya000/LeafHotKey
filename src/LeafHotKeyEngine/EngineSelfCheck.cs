using System.Text;

namespace LeafHotKey;

/// <summary>
/// 実際のフックを使わずに、入力エンジンの判定（抑止・通過・保持）を検証する。
/// 台帳から読み込んだ実ルールをそのまま使う。
/// </summary>
public static class EngineSelfCheck
{
    /// <summary>送信内容を記録するだけの差し替え送信先。</summary>
    private sealed class RecordingSink : IKeySink
    {
        public List<string> Sent { get; } = new();

        public SendResult Send(IReadOnlyList<SendToken> tokens)
        {
            Sent.Add(string.Join(" ", tokens.Select(token => token.ToString())));
            return new SendResult { SentEvents = tokens.Count, Unresolved = Array.Empty<string>() };
        }
    }

    /// <summary>解放が一度だけ届かない環境を再現する送信先。</summary>
    private sealed class StubbornSink : IKeySink
    {
        private bool _released;

        public List<string> Sent { get; } = new();

        public SendResult Send(IReadOnlyList<SendToken> tokens)
        {
            Sent.Add(string.Join(" ", tokens.Select(token => token.ToString())));
            return new SendResult { SentEvents = tokens.Count, Unresolved = Array.Empty<string>() };
        }

        public bool VerifyKeyDown(string keyName) => true;

        public bool VerifyKeyUp(string keyName)
        {
            if (_released) return true;
            _released = true;
            return false;
        }
    }

    public static int Run(string? reportPath, string? settingsPath)
    {
        var path = reportPath ?? Path.Combine(Path.GetTempPath(), "leafhotkey-enginecheck.txt");
        var encoding = new UTF8Encoding(false);
        var failures = 0;

        File.WriteAllText(path, string.Empty, encoding);

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            File.AppendAllText(path, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}", encoding);
        }

        var settings = settingsPath ?? FindDefaultSettings();
        if (settings is null)
        {
            Check("settings.found", false, "defaults/settings.json が見つからない");
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        IReadOnlyList<HotkeyProfile> profiles;
        try
        {
            profiles = HotkeyProfileLoader.Load(settings);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or FormatException or IOException)
        {
            Check("settings.readable", false, $"設定を読み込めない（{ex.Message}）");
            File.AppendAllText(path, $"failures={failures}{Environment.NewLine}", encoding);
            return 1;
        }

        var clipStudio = profiles.Single(profile => profile.Id == "clipstudio");
        var explorer = profiles.Single(profile => profile.Id == "explorer");
        var photoshop = profiles.Single(profile => profile.Id == "photoshop");
        var chrome = profiles.Single(profile => profile.Id == "chrome");

        var sink = new RecordingSink();
        var engine = new InputEngineCore(sink);

        // プロファイル未選択では何も変換しない。
        Check("idle.passthrough", engine.OnKeyDown("f13", SendModifiers.None) == InputDecision.PassThrough, "対象アプリでなければ通過させる");
        Check("idle.nosend", sink.Sent.Count == 0, "対象外では送信しない");

        // 単純な送信ルール（Chrome PgDn → !{Left}）。
        engine.SetActiveProfile(chrome);
        sink.Sent.Clear();
        var pgdn = engine.OnKeyDown("PgDn", SendModifiers.None);
        Check("send.suppress", pgdn == InputDecision.Suppress, "変換したキーは元の入力を抑止する");
        Check("send.payload", sink.Sent.Count == 1 && sink.Sent[0] == "Alt+Left", $"PgDn は Alt+Left を送る（実際: {string.Join(" / ", sink.Sent)}）");

        // 修飾キーが違えばルールに一致しない。
        sink.Sent.Clear();
        var ctrlPgdn = engine.OnKeyDown("PgDn", SendModifiers.Ctrl);
        Check("send.modifier-mismatch", ctrlPgdn == InputDecision.PassThrough && sink.Sent.Count == 0, "Ctrl+PgDn は割り当て対象外として通過する");

        // 分割送信（Photoshop PgDn → ^z, {Esc}）。
        engine.SetActiveProfile(photoshop);
        sink.Sent.Clear();
        engine.OnKeyDown("PgDn", SendModifiers.None);
        Check("send.split", sink.Sent.Count == 2 && sink.Sent[0] == "Ctrl+z" && sink.Sent[1] == "Esc", $"2回に分けて送る（実際: {string.Join(" / ", sink.Sent)}）");

        // 修飾キー付きトリガ（Photoshop ^+e）。
        sink.Sent.Clear();
        var ctrlShiftE = engine.OnKeyDown("e", SendModifiers.Ctrl | SendModifiers.Shift);
        Check("send.modified-trigger", ctrlShiftE == InputDecision.Suppress && sink.Sent.Count == 2, "^+e を認識する");

        // Hold（Explorer f13 → Shift 保持）。
        engine.SetActiveProfile(explorer);
        sink.Sent.Clear();
        var holdDown = engine.OnKeyDown("f13", SendModifiers.None);
        Check("hold.down", holdDown == InputDecision.Suppress && sink.Sent.Count == 1 && sink.Sent[0] == "Shift↓", $"f13 で Shift を押す（実際: {string.Join(" / ", sink.Sent)}）");
        Check("hold.active", engine.ActiveHoldModifiers.Contains("Shift"), "保持中の修飾キーを把握している");

        sink.Sent.Clear();
        var holdRepeat = engine.OnKeyDown("f13", SendModifiers.None);
        Check("hold.repeat", holdRepeat == InputDecision.Suppress && sink.Sent.Count == 0, "キーリピートで Shift を二重に押さない");

        sink.Sent.Clear();
        var holdUp = engine.OnKeyUp("f13", SendModifiers.None);
        Check("hold.up", holdUp == InputDecision.Suppress && sink.Sent.Count == 1 && sink.Sent[0] == "Shift↑", $"離したら Shift を解放する（実際: {string.Join(" / ", sink.Sent)}）");
        Check("hold.cleared", engine.ActiveHoldModifiers.Count == 0, "解放後は保持状態が残らない");

        // 保持中にアプリが切り替わっても解放する。
        sink.Sent.Clear();
        engine.OnKeyDown("f14", SendModifiers.None);
        engine.SetActiveProfile(chrome);
        Check("hold.profile-switch", sink.Sent.Count == 2 && sink.Sent[1] == "Ctrl↑", $"アプリ切替時に保持キーを解放する（実際: {string.Join(" / ", sink.Sent)}）");
        Check("hold.switch.cleared", engine.ActiveHoldModifiers.Count == 0, "切替後に保持が残らない");

        // 停止・終了時の一括解放。
        engine.SetActiveProfile(explorer);
        sink.Sent.Clear();
        engine.OnKeyDown("f16", SendModifiers.None);
        engine.ReleaseAll();
        Check("hold.release-all", sink.Sent.Count == 2 && sink.Sent[1] == "Space↑", $"ReleaseAll で解放する（実際: {string.Join(" / ", sink.Sent)}）");

        // 前置キー（Clip Studio の MButton）。
        engine.SetActiveProfile(clipStudio);
        sink.Sent.Clear();
        var prefixDown = engine.OnKeyDown("MButton", SendModifiers.None);
        Check("prefix.down", prefixDown == InputDecision.PassThrough, "*~MButton があるため中ボタンの元動作は通す");
        Check(
            "prefix.down.send",
            sink.Sent.Count == 1 && sink.Sent[0] == "Enter",
            $"~ 付き前置キーの単体動作は押下時に発火する（実際: {string.Join(" / ", sink.Sent)}）");
        Check("prefix.held", engine.HeldPrefixes.Contains("MButton"), "前置キーを押下状態として保持する");

        sink.Sent.Clear();
        var combo = engine.OnKeyDown("f14", SendModifiers.None);
        Check("prefix.combo", combo == InputDecision.Suppress && sink.Sent.Count == 1 && sink.Sent[0] == "s", $"MButton & f14 は s を送る（実際: {string.Join(" / ", sink.Sent)}）");

        sink.Sent.Clear();
        var prefixUpAfterCombo = engine.OnKeyUp("MButton", SendModifiers.None);
        Check("prefix.up.consumed", sink.Sent.Count == 0, "組み合わせを使った後は単体動作を発火しない");
        Check("prefix.up.passthrough", prefixUpAfterCombo == InputDecision.PassThrough, "元の中ボタン解放は通す");
        Check("prefix.released", engine.HeldPrefixes.Count == 0, "離したら前置状態を消す");

        // 組み合わせを使わなければ単体動作（MButton → {Enter}）は押下時に 1 回だけ。
        sink.Sent.Clear();
        engine.OnKeyDown("MButton", SendModifiers.None);
        Check("prefix.standalone.down", sink.Sent.Count == 1 && sink.Sent[0] == "Enter", $"単体の中ボタンは Enter を送る（実際: {string.Join(" / ", sink.Sent)}）");
        sink.Sent.Clear();
        engine.OnKeyUp("MButton", SendModifiers.None);
        Check("prefix.standalone.up", sink.Sent.Count == 0, "解放時には同じ単体動作を繰り返さない");

        // 前置キー + Hold（MButton & f13 は {Blind} なしの Ctrl 保持）。
        sink.Sent.Clear();
        engine.OnKeyDown("MButton", SendModifiers.None);
        Check("prefix.hold.press", sink.Sent.Count == 1 && sink.Sent[0] == "Enter", "組み合わせ前の押下で単体動作が出る");
        sink.Sent.Clear();
        engine.OnKeyDown("f13", SendModifiers.None);
        Check("prefix.hold", sink.Sent.Count == 1 && sink.Sent[0] == "Ctrl↓", $"MButton & f13 は Ctrl を保持する（実際: {string.Join(" / ", sink.Sent)}）");
        sink.Sent.Clear();
        engine.OnKeyUp("f13", SendModifiers.None);
        Check("prefix.hold.release", sink.Sent.Count == 1 && sink.Sent[0] == "Ctrl↑", "f13 を離すと Ctrl を解放する");
        engine.OnKeyUp("MButton", SendModifiers.None);

        // f16 単体は Space を保持し、組み合わせ（f16 & Home）では Alt+] を送る。
        sink.Sent.Clear();
        var f16Down = engine.OnKeyDown("f16", SendModifiers.None);
        Check(
            "prefix.f16.hold.down",
            f16Down == InputDecision.PassThrough && sink.Sent.Count == 1 && sink.Sent[0] == "Space↓",
            $"f16 押下で Space を保持する（実際: {string.Join(" / ", sink.Sent)}）");
        sink.Sent.Clear();
        var f16Up = engine.OnKeyUp("f16", SendModifiers.None);
        Check(
            "prefix.f16.hold.up",
            f16Up == InputDecision.PassThrough && sink.Sent.Count == 1 && sink.Sent[0] == "Space↑",
            $"f16 解放で Space を解放する（実際: {string.Join(" / ", sink.Sent)}）");

        sink.Sent.Clear();
        f16Down = engine.OnKeyDown("f16", SendModifiers.None);
        Check("prefix.f16", f16Down == InputDecision.PassThrough, "*~f16 があるため f16 の元入力は通す");
        sink.Sent.Clear();
        engine.OnKeyDown("Home", SendModifiers.None);
        Check("prefix.f16.combo", sink.Sent.Count == 1 && sink.Sent[0] == "Alt+]", $"f16 & Home は Alt+] を送る（実際: {string.Join(" / ", sink.Sent)}）");
        sink.Sent.Clear();
        f16Up = engine.OnKeyUp("f16", SendModifiers.None);
        Check("prefix.f16.up", f16Up == InputDecision.PassThrough && sink.Sent.Count == 1 && sink.Sent[0] == "Space↑", "組み合わせ済みの f16 解放で Space を解放する");

        // 割り当ての無いキーは触らない。
        sink.Sent.Clear();
        Check("unmapped", engine.OnKeyDown("q", SendModifiers.None) == InputDecision.PassThrough && sink.Sent.Count == 0, "未割り当てのキーは通過させる");

        // 前置キーの物理状態による補正（押下を取りこぼしても組み合わせを成立させる）。
        var blender = profiles.Single(profile => profile.Id == "blender");
        var prefixSink = new RecordingSink();
        var prefixEngine = new InputEngineCore(prefixSink)
        {
            PhysicalKeyState = name => name.Equals("MButton", StringComparison.OrdinalIgnoreCase),
        };
        prefixEngine.SetActiveProfile(blender);
        prefixSink.Sent.Clear();
        var physicalCombo = prefixEngine.OnKeyDown("f22", SendModifiers.None);
        Check(
            "prefix.physical",
            physicalCombo == InputDecision.Suppress && prefixSink.Sent.Count == 1 && prefixSink.Sent[0] == "Shift+i",
            $"押下イベントを逃しても物理的に押されていれば組み合わせを成立させる（実際: {string.Join(" / ", prefixSink.Sent)}）");

        // 前置キーが物理的に離れていれば、残った前置状態を捨てる。
        prefixEngine.PhysicalKeyState = _ => false;
        prefixSink.Sent.Clear();
        prefixEngine.OnKeyDown("f22", SendModifiers.None);
        Check(
            "prefix.physical.release",
            prefixSink.Sent.Count == 1 && prefixSink.Sent[0] == "Shift+Alt+o",
            $"離れていれば単体の割り当てへ戻る（実際: {string.Join(" / ", prefixSink.Sent)}）");

        // 保持の取りこぼし対策（解除の見逃しを補う）。
        engine.SetActiveProfile(explorer);
        sink.Sent.Clear();
        engine.OnKeyDown("f13", SendModifiers.None);
        Check("hold.map", engine.ActiveHolds.TryGetValue("f13", out var held) && held.Contains("Shift"), "解除キーごとに保持を追跡する");
        sink.Sent.Clear();
        engine.ReleaseHolds("f13");
        Check("hold.force-release", sink.Sent.Count == 1 && sink.Sent[0] == "Shift↑" && engine.ActiveHoldModifiers.Count == 0, $"見逃した解除を補って解放できる（実際: {string.Join(" / ", sink.Sent)}）");

        sink.Sent.Clear();
        engine.OnKeyDown("f14", SendModifiers.None);
        sink.Sent.Clear();
        engine.ReassertHolds(_ => false);
        Check("hold.reassert", sink.Sent.Count == 1 && sink.Sent[0] == "Ctrl↓", $"外れた保持を押し直せる（実際: {string.Join(" / ", sink.Sent)}）");
        engine.ReleaseAll();
        Check("hold.release-all.cleared", engine.ActiveHoldModifiers.Count == 0, "解放後は保持が残らない");

        // 保持キーの物理判定に使う仮想キー。
        Check(
            "keys.virtual",
            KeyResolver.TryVirtualKeyFor("f13", out var f13) && f13 == 0x7C &&
            KeyResolver.TryVirtualKeyFor("MButton", out var mbutton) && mbutton == 0x04 &&
            !KeyResolver.TryVirtualKeyFor("NoSuchKey", out _),
            "保持キーの仮想キーを引ける");

        // 解放が効かない環境でも固まらないよう、確認して送り直す。
        var stubbornSink = new StubbornSink();
        var stubbornEngine = new InputEngineCore(stubbornSink);
        stubbornEngine.SetActiveProfile(explorer);
        stubbornEngine.OnKeyDown("f13", SendModifiers.None);
        stubbornSink.Sent.Clear();
        stubbornEngine.OnKeyUp("f13", SendModifiers.None);
        Check(
            "hold.release-retry",
            stubbornSink.Sent.Count == 2 && stubbornSink.Sent[0] == "Shift↑" && stubbornSink.Sent[1] == "Shift↑",
            $"解放が届かなければ送り直す（実際: {string.Join(" / ", stubbornSink.Sent)}）");

        // IME 無効化は設定で切り替えられ、保存時に反映される。
        using (var live = new InputEngine(Array.Empty<HotkeyProfile>(), disableIme: false))
        {
            Check("ime.off", !live.ImeDisableEnabled, "設定で IME 無効化を止められる");
            live.ApplyProfiles(Array.Empty<HotkeyProfile>(), disableIme: true);
            Check("ime.applied", live.ImeDisableEnabled, "保存時に IME 設定が反映される");
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
