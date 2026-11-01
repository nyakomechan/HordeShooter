# M5 実装記録 — ウェーブディレクタ / ゲームループ / HUD / 音響（2026-11-01）

## 実装コンポーネント

| コンポーネント | ファイル | 内容 |
|---|---|---|
| ウェーブディレクタ | `Scripts/HordeNet.Wave.cs` | オーナー権威の状態機械（gamePhase: 0=ロビー / 1=ウェーブ中 / 2=インターミッション）。スポーンは ≤16体/0.4s チャンク（リング安全 pacing）。5ウェーブごとに最後の敵がボス（variant 2, bossSlots/bossHp に登録, HP 400）。敵数 = 24 + (wave-1)×12, cap 460 |
| プレイヤーダメージ | 同上 | ATTACK状態の敵が射程内にいると 0.5s ごとに 10dmg（スウォームでも1tick 1hit = 20dmg/s）。playerHp[] は同期。HP0 → 各クライアントが自分をスポーン地点へテレポート → `_ReviveMe` [NetworkCallable] でオーナーが HP100 に復帰 |
| HUD | `Scripts/HordeHUD.cs` | ウェーブ/フェーズ表示・残数・ボスHPバー（bossHp[]/bossSlots[] + ローカルミラーの生存確認）・プレイヤーHPバー・群衆アンビエント音量（最寄り敵距離 25m でフェード）・ウェーブ開始ホーン（phase 2→1 遷移をローカル検知） |
| ウェーブ開始キューブ | `Scripts/HordeWaveStart.cs` | Interact → `_StartWave`（オーナーのみ実行） |
| 音響クリップ | `Editor/HordeAudioBuilder.cs` | 手続き生成（GunShot / WaveHorn / HordeAmbient(ループ) / HitMarker）→ `Assets/HordeShooter/Audio/*.wav` |
| 銃UI・マズルフラッシュ | `Scripts/GunController.cs` + `Editor/HordeGunFxSetup.cs` | 銃の後ろに WorldSpace Canvas（弾数 "30 / 30" + RELOADING 表示、銃のスケール 0.12 を補正して実幅 ~13cm・ホルダー側を向く）。発射時に Muzzle 子オブジェクトの ParticleSystem を Emit(4)（メニュー「HordeShooter/Setup Gun UI + Muzzle FX」— HUD キャンバスには触らない） |
| 銃 = PlayerObject | `Scripts/GunController.cs` + VRCPlayerObject/VRCObjectSync | 銃に `VRCPlayerObject`（各プレイヤーが参加時に自分の銃のコピーを取得・所有権は移譲不可）+ `VRCObjectSync`（位置同期）を付与。**リスポーン**: 所有者が未所持で銃までの水平距離 > `respawnDistance`(5m) なら、プレイヤーの前方 1.2m・高さ 0.6m にテレポート（`VRCObjectSync.TeleportTo(Transform)` が Transform 引数のみのため、専用の RespawnAnchor 子オブジェクトに目標ポーズを書いてからテレポート）。**ダウン連動**: GunController 自身が playerHp を読み、ダウン中は自分の pickup を Drop + pickupable=false、復帰で pickupable=true（HUD から銃管理を移譲）。※実クライアントテストでユーザーが VRCObjectSync を削除 — GunCanvas/Muzzle/音声配線も同時に失われたため再配線済み（GunFxSetup 再実行 + 音声再配線）。VRCObjectSync なしではリスポーンはオーナーのローカルのみ反映（他クライアントには同期されない） |
| ダウン/復活/ゲームオーバー | `Scripts/HordeNet.Wave.cs` + `Scripts/HordeHUD.cs` + `Scripts/HordeGameRestart.cs` | HP0 → **ダウン**（PlayerModSetter 方式: `SetWalkSpeed/SetRunSpeed/SetStrafeSpeed/SetJumpImpulse` を 0 にして移動ロック。視界は遮らない — バナー「DOWNED - 行動不能 (WAVE CLEAR で復帰)」を表示。銃は強制ドロップ + pickupable=false。復帰時はデフォルト値 walk 2 / run 4 / strafe 2 / jump 3 に復元）。**ウェーブクリアで全ダウン者復活**（オーナーが phase 1→2 で playerHp を全員 100 に）。**全員ダウンで gamePhase=3（ゲームオーバー）** → `gameOverResetSeconds`(10s) 後に**自動リセット**（`_RestartGame`: clearRevision++ でリモートのローカルシムも全消去、phase 0・HP全員 100 → 各クライアントは復帰時にロビースポーンへテレポート）。**GameRestart キューブ**での手動リスタートも併存 |
| シーン構築 | `Editor/HordeHudSetup.cs` | メニュー「HordeShooter/Setup M5」: HUDキャンバス + 音源 + WaveStartキューブ + ガン音源をシーンに組み込み（冪等） |

## MultiSim スモークテスト結果

| 項目 | 結果 |
|---|---|
| ウェーブ開始（phase 0→1） | ✅ wave=1, 24スポーン（eventRev=24） |
| スポーン再現（クローン） | ✅ eventRev=48, alive 48/48 |
| ウェーブ終了検知（全滅→phase 2） | ✅ シムリセット後 ~1-2s でインターミッション |
| 次ウェーブ自動開始 | ✅ 8s 後に wave=2 |
| ボスウェーブ（wave 5） | ✅ boss0: slot=71, hp=400（72体目=最後の敵） |

## 検証で修正したバグ

1. **HPバーが動かない（実クライアントで発見）**: `Image.fillAmount` は
   `Image.type = Filled` のときしか効かない。セットアップスクリプトが type を
   指定していなかった（デフォルト Simple → fillAmount 無視で常に満タン表示）。
   `MakeFill` で `img.type = Image.Type.Filled` を設定して修正。
   ※ダメージ・ダウン・テレポート・復帰のロジック自体は実クライアントで動作確認済み
   （バーの見た目だけの問題だった）。
2. **音が鳴らない（実クライアントで発見）**: 手続き生成した AudioClip `.asset`
   （AudioClip.Create + CreateAsset）はビルドバンドルに正しく入らない可能性。
   **実体 .wav ファイル（16bit PCM モノラル、標準インポータ経由）**に方式を変更
   （`HordeAudioBuilder` を WAV ライタに書き直し、旧 .asset は削除）。
   シーン参照は .wav 側へ再配線済み（GunShot/WaveHorn/HordeAmbient とも参照確認済み）。
   → **ガンショットは実クライアントで鳴ることを確認（WAV 方式は有効）**。
3. **ホーン/アンビエントが鳴らない（実クライアントで発見）**: 2原因。
   ① ホーンは phase 2→1 遷移でしか鳴らず、最初のウェーブ開始（0→1）で鳴らない
   → 1への遷移全てで鳴るように修正。② ホーンとアンビエントが同一 AudioSource を
   共有し、アンビエントの近接フェード音量（ウェーブ開始時は敵が遠く volume=0）が
   PlayOneShot にも効く → **ホーン用の独立 oneShotSrc（volume 1）を追加**。
   アンビエントは最小音量 0.12 のフロアを追加。
   ※ユーザーが手動で PlayerHPFill に Source Image を設定・Fill 型に修正済みのため、
   **HordeHudSetup の再実行はしない**（キャンバス再構築で手動修正が消える）。
   oneShotSrc の配線はスニペットで HUD 挙動のみに適用。
1. **ボスが最後の敵にならない**: `_waveSpawnRemaining == 1` 判定がチャンクループ内で常に偽
   （remaining はループ後に減算）→ `_waveSpawnRemaining - i == 1`（反復ごとの残数）に修正。
2. **UdonSharp のVRCイベントオーバーライド名**: `Interact` オーバーライドは
   `_interact`（小文字+アンダースコア）でエクスポートされる。`SendCustomEvent("Interact")`
   は無音で失敗する（カスタムメソッドは C# 名そのまま: `_StartWave` 等は OK）。
   実クライアントの SDK は内部で `_interact` を呼ぶためゲーム内のキューブは動作する。
3. **シーン配列サイズ変更の落とし穴（M4からの続き）**: 公開配列のサイズを変えたら
   シーンのシリアライズ済みデータも更新必須。未更新だと `_PackSnapshot` が範囲外
   書き込みで Udon 実行時例外 → 挙動 halt → 同期全停止。
4. **クローンエディタとのシーン競合**: クローン（Assetsシンボリックリンク）がシーンを
   保存するとホストのダーティシーンと競合しコンパイルがブロックされる →
   ホスト側で `OpenScene` でディスクから再読込して解消。

## 未検証（実クライアント再テスト項目）

- HUD の描画（Wave/残数/ボスHPバー/プレイヤーHPバー）
- 音響（ガンショット・ホーン・アンビエント）
- プレイヤーダメージ → ダウン → テレポート → 復帰の一連の流れ
- ボスHPバーがガンダメージで減ること（bossHp はオーナーCPU権威）
- 負荷: ウェーブ進行での FPS（wave 5 = 72体, 以降増加）
