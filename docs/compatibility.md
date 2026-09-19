# AHK互換台帳（Phase 0）

移行元: `C:/Users/Daichi/OneDrive/AutoHotKey/MySet.ahk`
移行先: `defaults/settings.json`

この文書は「元AHKの有効な割り当てが、どの設定として移植されたか」を確認するための台帳である。

## 1. 共通処理の対応

| AHK | 定義箇所 | 移植先 | 意味 |
| --- | --- | --- | --- |
| `IME_SET(0)` | `MySet.ahk:18-33` | `input.imeDisableBeforeSend` | 送信前にIMEをオフにする（設定で解除できる） |
| `Sleep,2` | `MySet.ahk:40,52` | なし | `SendMessageTimeout`で同期的にIMEを切るため待機不要。設定項目は持たない |
| `Snd(keys, then)` | `MySet.ahk:38-44` | `action.type = "send"` | `sequence` の各要素を別々の送信として扱う |
| `Hold(mod, trigger, blind)` | `MySet.ahk:49-56` | `action.type = "hold"` | `modifier` を押し、`releaseOn` を離すまで保持 |
| `{Blind}` | `MySet.ahk:50` | なし | 本実装は常に他の修飾キーを維持する（旧 `action.blind` は廃止し、保持の挙動は変えない） |
| `*~key::return` | 各プロファイル | `action.type = "passthrough"` | 前置キーに使いつつ、元の入力を通す |
| `~` 付き前置キーの単体動作 | 各プロファイル | 前置キーの `passThroughNative` | AHK 1.1.14+ と同様、離上ではなく**押下時**に発火する |
| `GroupAdd, UEGroup` | `MySet.ahk:58-59` | `unreal.processNames` | UE4Editor.exe と UnrealEditor.exe |

### 送信表記の扱い

- `sequence` の要素は**アプリ独自の表記**で保持する（AHK の記号は使わない）。
  - 修飾は `Ctrl+` `Shift+` `Alt+` `Win+`（例: `Ctrl+Shift+g`）。
  - 名前付きキーはそのまま（例: `Esc`、`Tab`、`F9`、`PgDn`）。
  - 文字は 1 文字ずつ。続けて送る場合は空白で区切る（例: `f 5`）。
  - 押しっぱなしと解放は `↓` / `↑`（例: `Alt↓ g`、`Alt↑`）。
- 旧 AHK 表記（`^z`、`{Esc}`、`{AltDown}` など）は読み込み時に上記へ自動で移行する（`SettingsMigration`。移行専用の解釈は `LegacyAhkNotation`）。
- 旧表記の意味はそのまま保つ: `^` = Ctrl、`+` = Shift、`!` = Alt、`{XDown}` = `X↓`。
- `"f5"`（文字 f と 5）は `f 5`、`{F5}`（F5 キー）は `F5` として区別を維持する。
- 送信単位の分割（旧 `Snd("{Esc}", "^z")` = `["Esc", "Ctrl+z"]` の2回送信）は変わらない。

### トリガ表記の扱い

- `prefix` は `MButton & f13` の `MButton` のようなカスタム前置キー。
- `modifiers` は `^+e`（Ctrl+Shift+E）のような通常の修飾キー。
- `anyModifier: true` + `passThroughNative: true` は `*~MButton` / `*~f16` に対応する。

## 2. プロファイル別の移植状況

| ID | 表示名 | 対象exe | AHK該当行 | 移植ルール数 |
| --- | --- | --- | --- | --- |
| `explorer` | Explorer | Explorer.EXE | 78-83 | 3 |
| `xnview` | XnView | xnview.exe | 87-91 | 2 |
| `chrome` | Chrome | chrome.exe | 95-98 | 2 |
| `clipstudio` | Clip Studio Paint | CLIPStudioPaint.exe | 102-159 | 38 |
| `photoshop` | Photoshop | Photoshop.exe | 163-206 | 35 |
| `blender` | Blender | blender.exe | 210-252 | 28 |
| `maya` | Maya | Maya.exe | 256-288 | 26 |
| `marmoset` | Marmoset Toolbag | toolbag.exe | 292-324 | 27 |
| `unreal` | Unreal Engine | UE4Editor.exe / UnrealEditor.exe | 58-59, 328-353 | 19 |
| `phoenix` | Phoenix Editor | PhoenixEditor.exe | 357-382 | 19 |
| `moi` | MoI | MoI.exe | 386-420 | 29 |
| `substance-painter` | Adobe Substance 3D Painter | Adobe Substance 3D Painter.exe | 424-464 | 17 |
| `qt-designer` | Qt Designer | designer.exe | 468-477 | 6 |

合計 13プロファイル / 251ルール。

## 3. 移植しなかったもの（意図的）

元AHKでコメントアウトされていたため、移植対象外とする。採用するか否かは実装前に判断する。

| AHK該当行 | 内容 |
| --- | --- |
| `MySet.ahk:104` | Clip Studio: `Pause::SendInput,{Esc}` |
| `MySet.ahk:145-149` | Clip Studio: `MButton & f16` で `MButton` を送る処理 |
| `MySet.ahk:238-242` | Blender: `MButton & f16` で `{MButton}` を送る処理 |
| `MySet.ahk:448-462` | Substance Painter: `MButton & f13/f15/f16-f24` の空定義 |

## 4. ゲーム保護の移植と差分

| 項目 | AHK側 | 移植先 |
| --- | --- | --- |
| 停止トリガ（6種） | `MySet.ahk:66` | `gameProtection.stopTriggerProcessNames` |
| 復帰待機（3種） | `restart_ahk.bat:8-24` | `gameProtection.resumeProcessNames` |
| 監視間隔 1秒 | `MySet.ahk:62` | `gameProtection.pollIntervalMs` |
| 終了前待機 1秒 | `MySet.ahk:71` | `gameProtection.resumeDelayMs` |

**未決事項:** 停止対象6種に対し復帰待機は3種しかなく、`start_protected_game.exe` と EAC系2種は復帰条件に含まれていない。現状は元の挙動をそのまま移植している。EAC系プロセスがゲーム終了後も常駐するかを実測してから、統一するか決める。

## 5. 判断が必要な既知の疑問点

実装時に元AHKの実挙動を確認してから決める。ここでの推測を仕様として固定しない。

1. `MySet.ahk:28` の `DllCall("SendInputMessage", ...)` は Windows API 名として一般的ではない。IME制御が実際に機能しているかを実測する。
2. `MySet.ahk:21-25` の `ptrSize` は未定義変数を参照しており、`A_PtrSize` ではなく常に空になる可能性がある。ハンドル取得が意図どおりか要確認。
3. `MySet.ahk:165` の Photoshop `^+e::Snd("f5", "{Esc}")` は `{F5}` ではなく文字 `f5` を送る表記。意図か誤記か要確認。
4. `MySet.ahk:352` UE の `f22` と `f24` が両方 `^+i` で重複している。
5. `MySet.ahk:419-420` MoI の `MButton & f23` と `f24` が両方 `!o` で重複している。
6. Clip Studio と Photoshop は `MButton::{Enter}`（単体）と `*~MButton`（通過）を併記している。AHK 1.1.14+ では `~` 付き前置キーの単体ホットキーは**押下時**に発火するため、実装も押下時に `{Enter}` を送り、組み合わせ（GShift+ホイール等）はその後に発火する。実機E2E（2026-09-18）で `MButton down → Enter → Wheel → ]` の順を確認済み。
7. UE / Phoenix の Swap で使う `{AltDown}` `{ShiftUp}` は、AHK 標準の `{Alt down}`（空白あり）と異なる表記。実装側は両方を「修飾キーの押下／解放」として解釈しているが、元 AHK で実際に修飾キーとして動作していたかは実機確認が必要。
8. Clip Studio の `f16` は `Hold("Space", "f16")` と `f16 & Home` の前置キーを兼ねる。AHK 1.1.14+ の `~` 付き前置キーと同じく、**押下時**に Space を保持し、離上で解放する（実機E2Eと `--check-engine` の `prefix.f16.*` で確認）。

## 6. 検証方法と現在の状態

### 6.1 自動検証（実行可能）

| コマンド | 確認内容 | 直近の結果 |
| --- | --- | --- |
| `LeafHotKeyEngine.exe --check <report>` | 制御チャネル、多重起動防止、終了理由の区別、状態JSON・ログ・再読み込み | 22 PASS |
| `--check-send <report>` | SendInput の実送信と配列解決 | 13 PASS |
| `--check-engine <report> [settings]` | 判定、前置キー、保持キーの解放、IME 設定 | 43 PASS |
| `--check-hook <report>` | 実フック経由の変換・抑止・停止時解放 | 14 PASS |
| `--check-coverage <report> [settings]` | 全 send / hold ルールを発火させて台帳と照合 | 243 件、不一致 0 |
| `LeafHotKey.exe --check-profiles <report> [settings]` | 台帳の読込みと送信表記の解析 | 30 PASS（251ルール） |
| `--check-settings <report> [settings]` | 保存、競合・不正拒否、破損からの復旧、旧形式の移行、検知した実行ファイルパスの追記・更新 | 44 PASS |
| `--check-server <report> [settings]` | HTTP 配信、Host/Origin 検証、保存反映、アイコン配信、実行ファイルパスの検知・更新 | 39 PASS |
| `--check-watch <report> [settings]` | 退避・待機・再出現・復帰・失敗時の停止・エンジン解決 | 28 PASS |

## 7. 意図的な差分（AHK と異なる点）

| 項目 | AHK | 本実装 | 理由 |
| --- | --- | --- | --- |
| `{Blind}` | `Hold(mod, key, 0)` で他の修飾キーを一時的に離す | 常に他の修飾キーを維持する | ユーザーの押下状態を触らないほうが事故が少ない（差は GShift と修飾キーの同時押しのみ） |
| 保持キーの解放 | `KeyWait` は対象キーの解放まで待つ | 前面アプリが変わった時点でも解放する | 保持したまま別アプリへ移ると修飾キーが残るため |
| `Sleep,2` | IME 操作後に 2ms 待つ | 待機しない | `SendMessageTimeout` で同期的に IME を切るため不要 |
| 送信の待機時間設定 | なし | なし（`input.sendDelayMs` は廃止） | フック内で待機すると入力処理を遅らせるため |
| 設定画面の URL | なし | `http://127.0.0.1:17832/` 固定（トークンなし） | ブックマーク・再読み込みを可能にする。防御はループバック限定＋Host/Origin 検証 |

### 6.2 自動検証で確かめられていること

- 13 プロファイル 251 ルールが読み込め、元 AHK の表記（`f5` と `{F5}`、`+B` の大文字、`:` など）を保っている。
- send / hold ルール 243 件が、宣言どおりの送信内容で発火する（ルール同士の衝突による取りこぼしがない）。
- 変換したキーは押下・解放ともアプリへ流れない。
- プロファイル切替・一時停止・停止時に保持キーが解放される。

### 6.3 未実施（実機での手動照合）

**どのプロファイルも実アプリ上で AHK と比較していない。現時点で「完全再現済み」のプロファイルはない。**

| プロファイル | 自動照合 | 実アプリでの手動照合 |
| --- | --- | --- |
| explorer / xnview / chrome | 済み | 未実施 |
| clipstudio / photoshop | 済み | 未実施 |
| blender / maya / marmoset / moi | 済み | 未実施 |
| unreal / phoenix | 済み | 未実施 |
| substance-painter / qt-designer | 済み | 未実施 |

手動照合で確かめる項目（上記 5 章の未決事項に対応）:

- IME オン状態で送信したときの振る舞い（項目 1, 2）
- Photoshop `^+e` が文字 `f5` で意図どおりか（項目 3）
- 中ボタンの単体押下・ドラッグと前置利用の併存（項目 6）
- UE / Phoenix の Swap で修飾キーが意図どおり保持されるか（項目 7）
- Clip Studio `f16` の Space 押しっぱなし操作感（項目 8）

### 6.4 手動照合の進め方

- AHK と新アプリを同時稼働させず、同じ操作で比較する。
- 確認できたプロファイルから 6.3 の表を更新する。
- 未確認のプロファイルを「完全再現済み」として扱わない。
