# M7 Status — ローグライト強化システム（2026-11-03）

M0-M5 のホードシューターにローグライト形式（ヴァンパイアサバイバーライク）の
強化システムを追加した。PC 版のみ（Quest は従来通り後回し）。

## 確定仕様（ユーザー回答済み）

- ショップ: 無料で3択から1つ選ぶ（通貨なし）
- 発射: トリガー1つで全銃身同時発射、各銃身が独自レート
- 強化: プレイヤー毎に個別ビルド（同期不要、銃は PlayerObject）
- 敵攻撃: 近接のみ。攻撃エフェクトは GPU 描画のテレグラフ

## 実装内容

### M7a — ノルマ制 wave director（commit fd7ed16）

- `HordeNet` 同期フィールド追加: `quotaType`（0=討伐/1=生存/2=ボス）, `quotaTarget`,
  `quotaProgress`, `waveEndRevision`（全消滅用）, `shopReadyBits`
- wave 設定: wave%3 が 討伐/生存/ボス を循環。討伐 30+10/wave、生存 45+10s/wave、
  3wave毎にボス wave（雑魚は少数維持 16+4/wave）
- **常時湧き**: 0.5s 毎に mirror alive 数を維持目標数（40+8/wave、上限 220）まで
  補充（≤16/バッチ、ring-safe）
- **wave 終了時全消滅**: `HordeSim.DespawnAll()` → 1フレームの `_DespawnAll` フラグで
  SimState が全スロットを即 INACTIVE 化（死亡アニメなし、キルイベントなし）。
  リモートは `waveEndRevision` で同期
- gamePhase 2 を「ショップ」に転用（旧インターミッション廃止）。
  全員 READY または 60s タイムアウトで次 wave
- ゲームオーバー自動リセット（phase 3 → 10s → phase 0）は現行維持

### M7b — マルチバレル銃（commit 5b6932f）

- 銃身スロット 5。種類: 0=basic / 1=gatling / 2=shotgun / 3=drill / 4=railgun
  - basic: 12dmg/0.11s、gatling: 6/0.06s、shotgun: 8×6pellet/0.9s、
    drill: 45/1.2s/射程3.5m/当たり半径1.2m、railgun: 60/1.5s
- 各銃身が独自マガジン＋リロード。レベルで dmg+25%/lv、rate+6%/lv
- 強化カード（`GunController.ApplyCard`）: 銃身追加×4、dmg+25%、rate+20%、
  spread-30%、mag+50%、reload-30%、ランダム銃身 Lv+1
- `MAX_RAYS` 8→16。ray 毎当たり半径（`_RayDirTex.w`、0=デフォルト、-1=非アクティブ）
- ロードアウト同期: `[UdonSynced] int gunLoadout`（3bit×5スロット）で他クライアントの
  銃身ビジュアル（BarrelSlot1..4 子オブジェクト）を切替

### M7c — ショップ（commit fe93490 / UI Canvas 版は後続コミット）

- `ShopController`（新規）: gamePhase==2 で各プレイヤーをショップ空間へワープ、
  自分の銃のビルド状態から 3 枚のカードをローカル生成（Fisher-Yates）、
  カード選択で適用＋READY 報告（`_ReportShopReady` → `shopReadyBits`）
- **UI は uGUI Canvas（ユーザー指定）**: ワールドスペース Canvas（12m×4m）に
  クリック可能なカードパネル 3 枚（Image + Button + Text）。
  `VRC_UiShape`（SDK3: `VRC.SDK3.Components.VRCUiShape`）+ GraphicRaycaster +
  EventSystem でポインタ操作。Button.onClick →
  `ShopCardPick._Pick`（`UnityEventTools.AddStringPersistentListener` でバインド、
  カスタムメソッドは名前が保持される）。台座 Interact もフォールバックとして残す
- Canvas の正面は -Z 側（ワープイン側は +Z）なので Canvas を Y+180° 反転。
  TextMesh 看板も同様に反転
- ワープ: `TeleportTo(pos + up*1.4)` — 実機は視点基準オフセット +1.401m のため補償
  （`teleportEyeCompensation`、実機検証で調整）。復帰は突入前の位置をローカル保存
- ショップ空間: アリーナ外 (0,0,-48) に床＋3台座＋カードパネル Canvas＋SHOP 看板
  （`HordeShooter/Setup Shop Space` メニュー、再実行で安全に再構築）
- ラベルは uGUI Text（TextMesh.text は Udon 非対応のため）

### ゲーム開始 UI（uGUI Canvas、ショップと同じ部屋）

- `HordeGameStartUI`（新規）: ショップ部屋のカード Canvas の手前（目線の高さ、
  4m×1.2m パネル）に「GAME START」ボタン＋owner のプレイヤー名表示
  （`START: <owner displayName>`、2Hz で更新 — master 交代に追従）
- **owner 限定**: `_TryStart` が `Networking.IsOwner(hordeNet.gameObject)` を確認
  （非 owner のクリックは無視）。アリーナ内の WaveStart キューブも同様に owner 限定
  （ゲームオーバー自動リセット後にプレイヤーがアリーナへ湧くため、
  アリーナ内のフォールバックとしてキューブを残存）
- パネルはロビー（phase 0）のみ表示、wave 中は非表示
- Button.onClick → `SendCustomEvent("_TryStart")` を
  `UnityEventTools.AddStringPersistentListener` でバインド

### M7d — 敵バリエーション 8 種（commit 435a8ab）

- variant テーブル（EnemySim.compeito / EnemyAnim.cginc / HordeNet.Wave.cs の3箇所で同期）:

| v | 名前 | HP | 速度 | 攻撃範囲 | スケール | ダメージ倍率 |
|---|------|-----|------|---------|---------|------------|
| 0 | walker | 30 | 1.0 | 1.0 | 1.0 | 1.0 |
| 1 | runner | 20 | 1.6 | 0.85 | 0.9 | 0.6 |
| 2 | boss | 400 | 0.6 | 1.6 | 1.9 | 2.5 |
| 3 | brute | 90 | 0.7 | 1.3 | 1.4 | 2.0 |
| 4 | stalker | 15 | 2.0 | 0.8 | 0.8 | 0.8 |
| 5 | swarm | 10 | 1.2 | 0.7 | 0.6 | 0.4 |
| 6 | bomber | 40 | 0.9 | 1.4 | 1.1 | 3.5 |
| 7 | elite | 60 | 1.1 | 1.1 | 1.2 | 1.5 |

- マイクラ風ローポリ箱組みメッシュ 8 種（`EnemyMeshBuilder.BuildVariantMeshes`、
  `Art/EnemyV0..7.asset`）＋ variant 別色（walker 緑灰 / runner 黄緑 / boss 赤 /
  brute 暗赤 / stalker 紫 / swarm 淡色 / bomber オレンジ / elite 青）
- **variant 別描画バッチ**: mirror（10Hz、Meta readback 追加）から variant 毎の
  slot-list テクスチャ（512x1）を作り、variant 専用 material（`_SlotList` +
  `_UseSlotList=1`）で `DrawMeshInstanced`。シェーダは
  `slot = _SlotList.Load(unity_InstanceID)` でマッピング
- 湧き重み: wave 2 から runner 20%、wave 4 stalker 12%、5 swarm 18%、6 brute 10%、
  8 bomber 8%、10 elite 8%（残り walker）
- catch-up も variant を搬送（word2 の空き下位バイト、CMD_WAKE に variant を渡し
  HP/速度が variant 通りに）

### M7e — 攻撃エフェクト（commit 66728de）

- `EnemyFx.shader`（新規、加算合成）: 敵と同じ RT を参照するインスタンス化クアッド
  - `_FxMode 0` 攻撃テレグラフ: ATTACK 状態の敵の正面に展開するリング（timer で拡大・フェード）
  - `_FxMode 1` ヒットスパーク: 被弾フラッシュ中の胸元にソフトフラッシュ
- sim への新パス不要（描画のみ、非該当インスタンスはスケール 0 で潰す）
- 全 alive スロット用の統合 slot-list（`_allSlotListTex`）を追加

## MultiSim 検証（2 エディタ、2026-11-03）

- ✅ wave 1 討伐ノルマ（0/30）、維持湧き 40 体、両エディタ完全同期
- ✅ ノルマ達成 → 全消滅（alive 40→0）→ ショップフェーズ → waveEndRev 同期
- ✅ ショップ: 両者ワープ（(0,0,-44.5)）、プレイヤー毎に別カード生成、
  カード適用（ホスト GATLING 追加 / クローン Lv+1 を確認）、readyBits=3、
  全員 READY で即次 wave、突入前位置へ帰還
- ✅ wave 2 生存ノルマ（55s）進行同期、runner 混合（43 walker + 5 runner）両エディタ同一
- ✅ wave 3 ボス wave: 開始即ボス湧き（bossSlots=1）、雑魚 24 体に減圧、
  ボス撃破で wave 終了
- ✅ ray 毎当たり半径: 直撃(r1.2)命中 / +0.8m オフセット(r1.2)命中 / +2.0m(デフォルト)外れ
- ✅ variant HP テーブル（walker 30 / runner 20 / brute 90 / boss 400）
- ✅ ゲームオーバー自動リセット連鎖（phase 3 → 10s → phase 0）

## 修正したバグ

- **variant material に instancing 未有効** → `DrawMeshInstanced` が例外を投げ
  HordeSim が halt（シム全体凍結、Unity クラッシュの引き金）。全 8 material に
  `enableInstancing = true` を設定
- **variant material に `_SlotW` 等の定数未設定** → slot→texel 計算が 0 除算で破綻し
  描画されない。Init で定数をコピー
- **variant 描画の frustum カリング**: `DrawMeshInstanced` はバッチ全体を
  「最初の count 個のインスタンス行列」の境界でカリングする。count=8 だと
  ダミー行列の先頭 8 個（アリーナの一角）しか境界にならずほぼ常にカリング。
  variant 毎に 512 個をアリーナ全域に散らばったダミー行列配列
  （`(i*61 + v*7) % 512`、61 は 512 と互いに素）を作り解決
- **TextMesh.text は Udon 非対応** → ショップカードラベルは uGUI Text に変更
- **`new Material()` / `Object.Instantiate(Material)` は Udon 非対応** →
  variant/FX material はエディタでアセット作成してワイヤ
- **M7d ダメージが playerDamagePerTick=0 を無視** → variant ダメージを
  「playerDamagePerTick × 倍率」方式に変更
- **孤児 ShopController**: 失敗した Setup 実行の残骸がシーンに残り
  FindObjectOfType がそちらを拾う → Setup 冒頭で全 ShopController を破棄

## 実機テスト項目（ユーザー実施）

- ショップワープの着地位置（TeleportTo 補償 +1.4m の検証。ズレたら
  ShopController.teleportEyeCompensation を調整）
- 5 銃身同時発火の FPS・ray 予算（16/frame を超える分はドロップ）
- 攻撃エフェクト（リング/スパーク）の見た目と大きさ
- variant の見た目・色・強さのバランス
- ノルマ HUD 表示（quotaText）
- 銃身ビジュアルの他クライアント表示（gunLoadout 同期）

## 既知の制限

- ray 予算: 16/frame を超えた分はドロップ（5 銃身フル同時発火時に発生し得る）
- ショップのカードはローカル生成（他プレイヤーの選択内容は見えない）
- catch-up の variant は mirror 経由の 8bit（0-7）のみ
- 遠距離攻撃敵は未実装（将来拡張）
- Quest 対応は従来通り後回し

## 主要ファイル

- Scripts: `HordeNet.cs` / `HordeNet.Wave.cs` / `HordeNet.Apply.cs` / `HordeNetCatchup.cs` /
  `HordeSim.cs` / `HordeSim.Readback.cs` / `GunController.cs` / `HordeHUD.cs` /
  `ShopController.cs`（新規） / `ShopCardPick.cs`（新規）
- GPU: `EnemySim.compeito` / `EnemyAnim.cginc` / `EnemyRender.shader` / `EnemyFx.shader`（新規）
- Editor: `EnemyMeshBuilder.cs` / `HordeShopSetup.cs`（新規） / `HordeGunFxSetup.cs` / `HordeHudSetup.cs`
- Art: `EnemyV0..7.asset` / `FxQuad.asset` / Materials: `EnemyVariantV0..7.mat` /
  `EnemyFxAttack.mat` / `EnemyFxSpark.mat`
