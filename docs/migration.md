# AutoHotkey からの移行手順

対象: `C:/Users/Daichi/OneDrive/AutoHotKey` の `MySet.ahk` 環境から LeafHotKey へ切り替える作業。

**前提:** 実アプリでの手動照合はまだ行っていない（`compatibility.md` 6.3）。この手順は「AHK を残したまま試す」ことを前提にしている。

## 1. 発行と配置

```powershell
powershell -NoProfile -File scripts\Publish.ps1
```

`publish\` に次が揃う。.NET ランタイムを同梱しているため、AutoHotkey も .NET も未導入の環境でそのまま動く。

| ファイル | 役割 |
| --- | --- |
| `LeafHotKey.exe` | トレイ常駐本体（入力変換・WebUI） |
| `LeafHotKeyWatcher.exe` | ゲーム保護の監視と本体の再起動 |
| `wwwroot\` | 設定画面 |
| `defaults\settings.json` | 既定設定（初回起動時のひな形） |

任意のフォルダーへ `publish\` の中身をまとめて置く。2 つの exe と `wwwroot`、`defaults` は同じフォルダーに揃えて配置する。

## 2. 切り替え前の確認

```powershell
powershell -NoProfile -File tests\Run-Checks.ps1 -Configuration Release
```

すべて合格することを確認する。失敗がある状態で AHK を止めない。

## 3. 切り替え

1. 実行中の AutoHotkey を終了する（タスクトレイの AHK アイコンから終了）。
2. スタートアップに登録されている AHK 起動用ショートカットを無効にする。**削除せず、移動または名前変更で戻せるようにする。**
3. `LeafHotKey.exe` を起動する。トレイに常駐する。
4. ゲーム保護を使う場合は `LeafHotKeyWatcher.exe --run` を起動する。

リポジト直下の `Start-LeafHotKey.bat` は 3 と 4 をまとめて行う。発行物があれば `publish\` を、無ければビルド出力を使う。既に動作中なら二重に起動しない。`LEAFHOTKEY_DRYRUN=1` を指定すると、使う exe のパスを表示するだけで起動しない。

**AHK と LeafHotKey を同時に動かさない。** 同じキーを二重に処理して、意図しない入力になる。

### 自動起動について

LeafHotKey は自動起動を自分で登録しない。必要なら利用者がスタートアップへショートカットを置く。`LeafHotKeyWatcher.exe --run` を使う場合は、Watcher 側だけを登録すれば本体は Watcher から起動される。

## 4. 設定

- トレイメニューの「設定を開く」で既定のブラウザに設定画面が開く。
- URL は `http://127.0.0.1:<自動選択ポート>/` で、**起動ごとに変わるトークン**を要求する。ループバック以外からは接続できない。
- 設定の正本は `%LOCALAPPDATA%\LeafHotKey\settings.json`。初回起動時に `defaults\settings.json` から作られる。
- 保存すると検証・原子的置換の後、保持中のキーを解放してから新しい割り当てへ差し替わる。直前の内容は `settings.json.bak` に残る。

## 5. 操作

| 操作 | 方法 |
| --- | --- |
| 設定を開く | トレイメニュー「設定を開く」 |
| 一時停止・再開 | トレイメニュー、または `LeafHotKey.exe --pause` / `--resume` |
| 状態の確認 | `LeafHotKey.exe --status`（0=動作中 / 3=本体へ接続できない） |
| 終了 | トレイメニュー「終了」、または `LeafHotKey.exe --shutdown` |
| 監視の開始 | `LeafHotKeyWatcher.exe --run [settings.json]` |
| 本体の生存確認 | `LeafHotKeyWatcher.exe --ping`（0=応答あり / 3=応答なし） |

## 6. ゲーム保護の挙動

1. `stopTriggerProcessNames`（既定 6 種）のいずれかを検知する。
2. 入力を止め、保持中のキーを解放し、フックを解除してから本体を終了する。
3. `resumeProcessNames`（既定 3 種）がすべて消えるまで待つ。再出現したら待機をやり直す。
4. 待機時間の経過後、停止トリガが無いことを再確認してから本体を再起動する。

- ゲームプロセスは終了させない。
- 手動で終了した場合は自動再起動しない。
- 退避や再起動に失敗した場合は停止状態として扱い、成功したように見せない。
- 停止 6 種と復帰 3 種が一致していないのは元 AHK の挙動をそのまま移したため（`compatibility.md` 4 章）。

## 7. 元に戻す

| 状況 | 戻し方 |
| --- | --- |
| 設定を壊した | 設定画面の「既定に戻す」、または `settings.json.bak` を `settings.json` へ戻す |
| キー変換を一時的に止めたい | トレイの「一時停止」または `--pause` |
| LeafHotKey をやめる | `--shutdown` で終了し、Watcher も終了する。AHK のショートカットを戻して `MySet.ahk` を起動する |

元の AHK 一式（`MySet.ahk` / `restart_ahk.bat` / `launch_arc_raiders.bat`）は削除しない。移行が定着するまで復旧用に残す。

## 8. 既知の制限

- **実アプリでの手動照合は未実施。** 13 プロファイルのどれも「完全再現済み」ではない（`compatibility.md` 6.3）。
- **アンチチートによる検知リスクはゼロにならない。** ゲーム中に本体を終了する設計で露出時間を減らしているだけで、低レベルフックや `SendInput` 自体が検知対象になり得る。
- 自動復帰のため、ゲーム中も `LeafHotKeyWatcher.exe` は動き続ける。プロセスを 1 つも残したくない場合は Watcher を使わず手動起動にする。
- 管理者権限で動くアプリへは、Windows の制約で入力を送れないことがある。常時管理者実行は既定にしていない。
- 実行ファイルに署名していないため、初回起動時に SmartScreen の警告が出ることがある。
- `{AltDown}` 表記や IME 制御など、元 AHK の実挙動が未確認の項目が残る（`compatibility.md` 5 章）。

## 9. 移行完了の条件

1. `tests\Run-Checks.ps1` がすべて合格する。
2. 実アプリで主要プロファイルを AHK と比較し、`compatibility.md` 6.3 の表を更新する。
3. ゲーム保護の退避と復帰を実環境で確認する。
4. 上記が揃ってから、AHK の起動経路を正式に外す。
