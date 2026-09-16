# AHK互換台帳（Phase 0）

移行元: `C:/Users/Daichi/OneDrive/AutoHotKey/MySet.ahk`
移行先: `defaults/settings.json`

この文書は「元AHKの有効な割り当てが、どの設定として移植されたか」を確認するための台帳である。

## 1. 共通処理の対応

| AHK | 定義箇所 | 移植先 | 意味 |
| --- | --- | --- | --- |
| `IME_SET(0)` | `MySet.ahk:18-33` | `input.imeDisableBeforeSend` | 送信前にIMEをオフにする |
| `Sleep,2` | `MySet.ahk:40,52` | `input.sendDelayMs` | IME操作後の待機（2ms） |
| `Snd(keys, then)` | `MySet.ahk:38-44` | `action.type = "send"` | `sequence` の各要素を別々の送信として扱う |
| `Hold(mod, trigger, blind)` | `MySet.ahk:49-56` | `action.type = "hold"` | `modifier` を押し、`releaseOn` を離すまで保持 |
| `{Blind}` | `MySet.ahk:50` | `action.blind` | 省略時 `true`、`Hold(...,0)` は `false` |
| `*~key::return` | 各プロファイル | `action.type = "passthrough"` | 前置キーに使いつつ、元の入力を通す |
| `GroupAdd, UEGroup` | `MySet.ahk:58-59` | `unreal.processNames` | UE4Editor.exe と UnrealEditor.exe |

### 送信表記の扱い

- `sequence` の要素は **AHKの送信文字列をそのまま保持** する。実装側で解釈する。
- `^` = Ctrl、`+` = Shift、`!` = Alt。
- `"f5"` と `"{F5}"`、`"{f9}"` と `"{F9}"`、`"+B"` と `"+b"` を**同一視しない**。元の表記を維持する。
- `Snd("{Esc}", "^z")` は 1要素ではなく `["{Esc}", "^z"]` の2回送信。

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
6. Clip Studio と Photoshop は `MButton::{Enter}`（単体）と `*~MButton`（通過）を併記している。実装では AHK の前置キー仕様に合わせ、「押下時は送信せず、組み合わせが使われなかった場合にだけ解放時へ単体動作を発火」「`*~` があるため元の中ボタン入力は常に通す」として解釈している。元 AHK の実挙動との一致は実機確認が必要。
7. UE / Phoenix の Swap で使う `{AltDown}` `{ShiftUp}` は、AHK 標準の `{Alt down}`（空白あり）と異なる表記。実装側は両方を「修飾キーの押下／解放」として解釈しているが、元 AHK で実際に修飾キーとして動作していたかは実機確認が必要。

## 6. 検証方法

- `defaults/settings.json` のルール数が上表と一致すること。
- 各ルールの `sequence` が元AHKの文字列と一致すること。
- AHKと新アプリを同時稼働させず、同じ操作で比較すること。
- 未確認のプロファイルを「完全再現済み」として扱わないこと。
