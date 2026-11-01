# M1 検証記録 — GPU群衆コア（2026-10-31）

## 受け入れ基準と結果

| 項目 | 基準 | 結果 |
|---|---|---|
| 同時敵数 | 500体 | ✅ alive=500（mirror確認） |
| Udonフレームコスト | ≤0.5ms | ✅ **0.072ms**（SimStep全体: Dispatch×3 + DrawMeshInstanced + テクスチャ更新 + readback要求、120フレーム平均） |
| GCアロケーション | 0/frame | ✅ 設計上ゼロ（全バッファ事前確保。Debug.LogはprofileSimStepフラグ配下のみ） |
| 追跡動作 | プレイヤーへ収束 | ✅ 8スポーン地点（~35m）から約11秒で収束 ≈ 3.2m/s、攻撃状態(state=3)へ移行 |
| 分離 | 群衆が点に潰れない | ✅ 分離力で円盤状の群衆を維持 |
| 描画 | unity_InstanceIDテクスチャフェッチ | ✅ エディタClientSimで動作確認（**Build&Testでの実機確認は後続チェックポイントで実施**） |

## 検証で判明した実装上の重要事項

1. **CGPROGRAMでは`Texture2D<float4>`宣言がMaterialのテクスチャプロパティとして登録されない** → `SetTexture`が無効。
   対策: EnemyRender.shaderは**HLSLPROGRAM**を使用し、テクスチャをPropertiesブロックに明示（`_PosTex/_StateTex/_MetaTex`）。
   Compeito生成シェーダ（HLSLPROGRAM・Propertiesなし）へのSetTextureは有効（経路探索ライブラリと本シムで実証）。
2. **非インスタンシングバリアントで`unity_InstanceID`は未宣言** → `EsInstanceSlot()`で`#if defined(UNITY_INSTANCING_ENABLED)...`ガード必須。
3. **エディタPlayModeでUdonSharpのプログラムアセット検索キャッシュが作成順により古い場合がある** → 新規スクリプト追加後はドメインリロードを伴う`uloop compile`＋`AssetDatabase.SaveAssets()`を習慣化。
4. **エディタPlayModeのロジック駆動はプロキシメソッド直接呼び出しではなく** `udon.SetProgramVariable` + `udon.SendCustomEvent("_Xxx")`（stale field問題回避）。
5. **ClientSimはVRC_SceneDescriptor（VRCWorldプレハブ）がないとローカルプレイヤーを生成しない** → アリーナシーンにVRCWorldプレハブを配置済み。プレイヤーなしでは敵はtarget=-1となり移動しない（シムは正常）。
6. コマンドキュー上限は1024（初回テストで128上限に500スポーンが引っかかりドロップ → 引き上げ済み）。8コマンド/フレームのドレインで500スポーンは約63フレーム。

## 計測環境
- Unity 2022.3.22f1 editor, Game View 1469x895, エディタ非フォーカス（フレームレート無制限環境）
- smoothFrameMs ≈ 12.5ms（Game View描画時・エディタオーバーヘッド込み）。実機90fps判定はBuild&Testで実施予定。

## スクリーンショット
- `.uloop/outputs/Screenshots/Rendering_20261031_001519_515.png`（ClientSim視点・群衆中心）
- `.uloop/outputs/Screenshots/Scene_20261031_001621_117.png`（Sceneビュー・群衆全体）
