# LeafHotKey 実装計画

## 1. 目的

AutoHotkey の `MySet.ahk` を Windows ネイティブアプリへ移行し、次を実現する。

- タスクトレイ常駐
- WebUIからの設定変更
- アプリ別ホットキーの完全再現
- 検知リスクのあるゲーム起動時の入力処理停止と本体終了
- ゲーム終了後の自動再起動
- AutoHotkey本体、AHKスクリプト、再起動バッチへの依存廃止

## 2. 前提と対象範囲

- 移行元は `C:/Users/Daichi/OneDrive/AutoHotKey/MySet.ahk` とする。
- 現在の `LeafHotKey` には検討用モック `mockup/index.html` がある。
- `MySet.ahk:18–56` の `IME_SET`、`Snd`、`Hold` を共通互換処理として扱う。
- `MySet.ahk:77–478` のアプリ別割り当てを移植対象とする。
- コメントアウトされた割り当ては、明示的に採用を決めるまで移植しない。
- 旧AHKは移行完了・復旧手順確認まで削除しない。

## 3. 基本方針

- まずゲーム保護とライフサイクルを完成させ、その後に入力割り当てを移植する。
- WebUI・ゲーム保護監視・設定ストアを持つ本体（LeafHotKey.exe）と、入力フックを持つ入力エンジン（LeafHotKeyEngine.exe）を分離する。ゲーム保護で退避するのは入力エンジンだけで、設定画面は生かし続ける。
- 入力フック、`SendInput`、DLL注入、ドライバー、メモリ操作は行わない。
- ゲーム中に入力エンジンを動かさないことで検知リスクへの露出時間を減らす。ただし検知ゼロは保証しない。
- 設定の正本は本体側のJSONとし、WebUIの状態を正本にしない。
- 機能を増やすより、AHKの既存動作を忠実に再現することを優先する。

## 4. 全体構成

```text
LeafHotKey.exe（本体）
├─ NotifyIcon / トレイメニュー
├─ WebUIサーバー（127.0.0.1のみ）
├─ 設定ストア
├─ ゲーム保護の対象プロセス監視
└─ 入力エンジンの起動・監督

LeafHotKeyEngine.exe（入力エンジン）
├─ キーボード／マウスフックと入力変換
└─ 制御チャネル（STATUS / PAUSE / RESUME / SHUTDOWN / RELOAD）
```

### ゲーム保護の順序

```text
危険プロセス検知（本体）
→ 入力エンジンへ退避要求
→ 新規入力停止
→ 保持中の修飾キーを解放
→ キーボード／マウスフック解除
→ 入力エンジン終了（本体とWebUIは残る）
→ 対象プロセスの終了待機
→ 停止トリガ再確認の上で入力エンジンを再起動
```

入力エンジンは入力フックと`SendInput`を持ち、WebUIを持たない。本体はフックを持たない。ゲーム中も設定画面は開ける。

## 5. Phase 0 — 仕様固定・移行台帳

### 対象

- `docs/compatibility.md`
- `defaults/settings.json`
- 移行元 `../AutoHotKey/MySet.ahk`
- `../AutoHotKey/restart_ahk.bat`
- `../AutoHotKey/launch_arc_raiders.bat`

### 作業

1. 13プロファイルの全有効割り当てを一覧化する。
2. 各ルールを次の項目へ分解する。
   - 対象exe
   - 入力キー
   - 前置キー
   - 修飾キー
   - 送信キー列
   - 送信を分けるか
   - 元イベントを通過させるか
   - 保持と解除条件
   - IME操作
3. `Snd("f5")` と `Snd("{F5}")`、`{Blind}`、大文字小文字、分割送信を同一視しない。
4. ゲームプロセス設定を次の2種類に分ける。
   - `stopTriggerProcessNames`: AHKが停止していた現行6種
   - `resumeProcessNames`: バッチが終了待ちしていた現行3種
5. EAC補助プロセスが常駐するかを実測し、復帰条件を確定する。
6. 実装開始前にGit管理方法を確定する。現在の `LeafHotKey` はGitリポジトリではない。

### 完了条件

- 移植漏れを機械的に確認できる台帳がある。
- 停止対象と復帰対象の不一致について判断が記録されている。
- AHKの意図と実際の挙動が異なる箇所を未確定のまま明示できる。

## 6. Phase 1 — ネイティブ常駐基盤

### 新規ファイル

- `src/LeafHotKey/LeafHotKey.csproj`
- `src/LeafHotKey/Program.cs`
- `src/LeafHotKey/TrayApplication.cs`
- `src/LeafHotKey/SingleInstance.cs`
- `src/LeafHotKeyEngine/LeafHotKeyEngine.csproj`
- `src/LeafHotKeyEngine/Program.cs`

### 作業

- C#／.NETのトレイ常駐本体を作る。
- 本体と入力エンジンの多重起動をMutexで防止する。
- 本体と入力エンジンの制御通信を、同一ユーザー限定の名前付きパイプで行う。
- 本体の起動、入力エンジンの起動・停止、手動一時停止を実装する。
- WebUIや入力フックがなくても本体が正常終了できるようにする。

### 完了条件

- トレイアイコンと終了メニューが表示される。
- 二重起動が発生しない。
- 手動終了では自動再起動しない。
- 入力エンジンとの接続断をエラーとして扱える。

## 7. Phase 2 — ゲーム保護・終了・復帰

### 新規ファイル

- `src/LeafHotKey/GameMonitor.cs`
- `src/LeafHotKey/LifecycleController.cs`
- `src/LeafHotKey/EngineControl.cs`
- `src/LeafHotKey/HostSupervisor.cs`

### 作業

- `MySet.ahk:61–75` のゲーム検知をネイティブ化する。
- `restart_ahk.bat:4–40` の待機・再起動をネイティブ化する。
- 危険プロセス検知時に、ゲーム自体を終了せず入力エンジンだけを停止する。
- 複数の対象プロセスがある場合は、すべての復帰条件を満たすまで再起動しない。
- 対象プロセスが終了した後、待機時間を置いて再確認する。
- 待機中に対象プロセスが再出現した場合は、待機をリセットする。
- 入力エンジンの再起動失敗時は再試行するが、無限起動ループは防止する。
- 手動停止とゲーム検知による自動停止を区別する。
- `launch_arc_raiders.bat` のAHK終了・バッチ呼び出しを、ネイティブ起動経路へ置き換える。

### 安全条件

- フック解除とキー解放が完了する前にエンジンを終了させない。
- 対象プロセスが残っている間は、入力エンジンを起動しない。
- 再起動直前にも対象プロセスを再確認する。
- プロセス監視失敗時は安全側として入力を有効化しない。

### 完了条件

- ダミーの監視対象プロセスで、検知→エンジン終了→終了待機→エンジン再起動を確認できる。
- 複数対象、再出現、再起動失敗、手動停止を確認できる。
- ゲームプロセスを強制終了しない。

## 8. Phase 3 — AHK互換入力エンジン

### 新規ファイル

- `src/LeafHotKeyEngine/InputEngine.cs`
- `src/LeafHotKeyEngine/KeySender.cs`
- `src/LeafHotKeyEngine/ModifierState.cs`
- `src/LeafHotKeyEngine/NativeMethods.cs`
- `src/LeafHotKeyEngine/ProfileMatcher.cs`

### 移植順

1. Explorer、Chrome、XnView
2. Clip Studio Paint、Photoshop
3. Blender、Maya、Marmoset、MoI
4. Unreal Engine、Phoenix、Substance Painter、Qt Designer

### 作業

- 前面ウィンドウのexeに応じたプロファイル選択を実装する。
- キーボード／マウスフックと`SendInput`を実装する。
- `Snd`相当のIME無効化、待機、送信列を実装する。
- `Hold`相当の押下・トリガー解放・修飾キー状態を実装する。
- 中ボタン、F16、ホイール、Shift＋数字などの前置キーを実装する。
- `*`、`~`、`{Blind}`に相当する通過・修飾挙動を明示的に扱う。
- JIS配列記号、左右修飾キー、キーリピートを検証する。
- 自身が送信した入力イベントの再帰を防止する。
- アプリ切替、設定更新、停止時に保持中キーを必ず解放する。
- フック内で待機、ディスクアクセス、WebUI通信を行わない。

### 完了条件

- 各プロファイルの実動作が移行台帳と一致する。
- AHKと新アプリの比較結果が記録されている。
- 未確認のプロファイルを完全再現済みと扱わない。

## 9. Phase 4 — WebUI・設定管理

### 新規ファイル

- `src/LeafHotKey/SettingsStore.cs`
- `src/LeafHotKey/SettingsServer.cs`
- `src/LeafHotKey/SettingsValidation.cs`
- `src/LeafHotKey/wwwroot/index.html`
- `src/LeafHotKey/wwwroot/styles.css`
- `src/LeafHotKey/wwwroot/app.js`

### 作業

- `mockup/index.html` の画面構成と操作感を実UIへ移す。
- LeafCodePiのサイドバー、設定カード、タブ、バッジ、テーマ切替を踏襲する。
- 概要、プロファイル、ゲーム保護、設定の4画面を実データ化する。
- プロファイルの追加・編集・有効無効切替を実装する。
- ゲーム監視対象、復帰条件、待機時間を編集できるようにする。
- 設定をUTF-8 JSONで原子的に保存する。
- 保存済み、適用済み、入力停止中、接続断を別状態として表示する。
- ループバック限定、Host／Origin検証、認証トークン、入力サイズ制限を実装する。
- 任意コマンド実行APIや入力内容の常時記録は設けない。
- `DESIGN.md`にない色・余白・ダークモード値を独自追加しない。仕様不足は既存tokenの再利用または判断事項として記録する。

### 完了条件

- WebUIから設定を保存できる。
- 保存後、入力エンジンへ安全に反映できる。
- 不正JSON、空の対象リスト、保存失敗、競合更新を拒否できる。
- ループバック以外から接続できない。
- 680px以下でも主要操作が利用できる。

## 10. Phase 5 — 検証・切替・配布

### 新規ファイル

- `tests/LeafHotKey.Checks/*`
- `docs/migration.md`
- 配布用設定・起動ショートカット

### 作業

- 入力ルールとライフサイクルの小さな実行可能チェックを追加する。
- Windows x64向け自己完結配布を作る。
- AHK未導入環境で起動・設定・停止・復帰を確認する。
- AHKと新アプリを同時稼働させず、同じ操作で比較する。
- `launch_arc_raiders.bat` と `restart_ahk.bat` に依存しないことを確認する。
- 移行完了後、AHKは起動経路から外し、復旧用に保管する。
- 移行手順、復旧手順、既知の制限を `docs/migration.md` に記録する。

## 11. 受け入れ条件

### 入力互換性

- 13プロファイルの全有効ルールが移植されている。
- Explorer、Chrome、XnView、Clip Studio Paint、Photoshop、Blender、Maya、Marmoset、Unreal、Phoenix、MoI、Substance Painter、Qt Designerで確認できる。
- IME ON、JIS記号、長押し、中ボタン併用、前置キー、アプリ切替でキーが残らない。

### ゲーム保護

- 危険プロセス検知時に本体の入力フックが解除される。
- 保持キーが解放される。
- ゲーム終了前に本体が再起動しない。
- ゲーム終了後に本体が一度だけ自動再起動する。
- 手動停止は勝手に復帰しない。

### WebUI

- トレイからWebUIを開ける。
- 4画面を表示できる。
- 設定保存の成功・失敗・適用状態を区別できる。
- ライト／ダーク、キーボード操作、狭幅表示に対応する。

### 検証

- `dotnet build`
- 対象テスト・自己チェック
- WebUI JavaScript構文検査
- 設定のUTF-8ラウンドトリップ確認
- ダミープロセスによる停止・復帰確認
- 実ゲームでの検証は、利用規約と提供元の許可を確認した環境に限定する

## 12. リスクと未決事項

- C#化しても低レベルフックや`SendInput`の検知リスクはゼロにならない。
- 自動復帰のため、ゲーム中も本体（WebUI）は残る。プロセスを残したくない場合は本体を使わず、入力エンジンだけを手動で起動・終了する。
- `EAAntiCheat.GameService.exe`などが常駐する環境では、復帰条件を実測で決める必要がある。
- `IME_SET`のDLL呼び出しとハンドル型は実機で確認し、AHKの記述を盲目的に転記しない。
- 管理者権限のアプリへの入力送信にはWindowsの制約がある。常時管理者実行は既定にしない。
- 本体・入力エンジン・WebUIのいずれかが異常終了した場合の復旧手順を配布前に確認する。

## 13. 対象外

- アンチチート回避。
- DLL注入、ドライバー、メモリ操作。
- ゲームプロセスの強制終了。
- 汎用AHKインタープリター。
- クラウド同期、LAN公開、不要な外部UIフレームワーク。
- 既存キー割り当ての独断での整理・変更。
