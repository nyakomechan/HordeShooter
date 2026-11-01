# Udon 3D Pathfinding

[Compeito](https://github.com/phi16/Compeito) の GPGPU カーネルを使った VRChat ワールド向け 3D 経路探索パッケージ

[English](README_en.md)

## 概要

VRChat の Udon ランタイム上で GPU ベースの幅優先探索（BFS）を実行し、3D ボクセルグリッド上の経路を求める。
壁のコライダーをボクセル化し、Compeito のコンピュートシェーダーでウェーブフロント展開を行う。
得られた経路はスムージングを経て、`Vector3[]` のウェイポイント配列として取り出せる。
取り出したウェイポイント配列を利用するためのユーティリティAPI群も含む。

## 機能

- **任意のコライダータイプに対応**：Box、Sphere、Capsule、Mesh コライダーいずれも使用可能
- **パススムージング**：ボクセルベースで経路探索後、実コライダーベースでパススムージングを実施し不要なウェイポイントを削除
- **カスタムインスペクター**：Scene ビューで範囲設定、壁ボクセルプレビュー
- **フレーム分割処理**：負荷に応じてフレームごとに実行するイテレーション数を指定可能

## 動作要件

| パッケージ | バージョン |
|---|---|
| `com.vrchat.worlds` | >=3.10.3 |
| [com.imaginantia.compeito](https://github.com/phi16/Compeito) | >=1.0.1 |
| Unity | 2022.3 |

## インストール

VRChat Creator Companion でこのリポジトリを VPM リポジトリとして追加し、パッケージをインストールしてください。

```
https://nyakomechan.github.io/vpm-repository/
```

```
https://github.com/nyakomechan/Udon-3D-Pathfinding.git
```

手動で導入する場合は、このフォルダをプロジェクトの `Packages/` ディレクトリ以下に配置してください。

## クイックスタート

1. シーン内の GameObject に `UdonPathfindingManager` を追加する
2. インスペクターで壁となるWall Collidersを設定する（任意のコライダータイプ、回転に対応）
3. インスペクターでEdit Grid Rangeボタンを押して、表示されるハンドルで経路探索する範囲を設定する
4. 任意の UdonSharpBehaviour から `RequestPath` を呼び出す
5. 経路探索が完了すると `OnPathFound()` が呼び出される
6.  `OnPathFound()` 内で `UdonPathfindingManager.waypoints` からウェイポイントを取り出す

```csharp
public UdonPathfindingManager manager;

void Start()
{
    manager.RequestPath(startPos, goalPos, this);
}

public void OnPathFound()
{
    Vector3[] wps = manager.waypoints;
    // パスを使用する
}
```

## API リファレンス

### パスリクエスト

| メソッド | 説明 |
|---|---|
| `RequestPath(Vector3 start, Vector3 goal)` | デフォルトのレシーバーとイベントでパス要求をキューに追加 |
| `RequestPath(Vector3 start, Vector3 goal, UdonSharpBehaviour receiver)` | カスタムレシーバーを指定してキューに追加 |
| `RequestPath(Vector3 start, Vector3 goal, UdonSharpBehaviour receiver, string foundEvent, string failedEvent)` | カスタムレシーバーとイベント名を指定してキューに追加 |

結果はレシーバーへ `SendCustomEvent` で通知される。
`OnPathFound` の呼び出し後に `manager.waypoints`（`Vector3[]`）を読み取る。

### パス結果フィールド

| フィールド | 型 | 説明 |
|---|---|---|
| `waypoints` | `Vector3[]` | スムージング済みのパスウェイポイント |
| `pathFound` | `bool` | 最後の探索が成功したか |
| `pathError` | `string` | 失敗時のエラーメッセージ |
| `isBusy` | `bool` | 探索中かどうか |
| `startWorld` / `goalWorld` | `Vector3` | 実際に使用された開始位置と目標位置 |
| `searchIterationCount` | `int` | ウェーブフロントの総反復回数 |
| `searchFrameCount` | `int` | 経過フレーム数 |

### パスユーティリティ API

`Vector3[] wps` (waypoints)を引数に渡すステートレスなメソッド群。

| メソッド | 戻り値 | 説明 |
|---|---|---|
| `GetPathLength(Vector3[] wps)` | `float` | 全セグメントの距離合計 |
| `GetClosestPointOnPath(Vector3[] wps, Vector3 pos)` | `Vector3` | パス上の `pos` に最も近い点 |
| `GetClosestWaypointIndex(Vector3[] wps, Vector3 pos)` | `int` | `pos` に最も近いウェイポイントのインデックス |
| `ResamplePath(Vector3[] wps, float spacing)` | `Vector3[]` | 等間隔にリサンプリングしたパス |

### ウェイポイント進行 API

`int index` を引数に渡すステートレスなメソッド群。
インデックスの管理は呼び出し側が行う。

| メソッド | 戻り値 | 説明 |
|---|---|---|
| `GetCurrentWaypoint(Vector3[] wps, int index)` | `Vector3` | `index` 番目のウェイポイント |
| `GetRemainingDistance(Vector3[] wps, int index)` | `float` | `index` から終点までの距離 |
| `GetProgress(Vector3[] wps, int index)` | `float` | 進行度 `0..1`（距離ベース） |
| `AdvanceWaypoint(int index, int count)` | `int` | 次のインデックス。終点で `-1` |
| `ResetWaypointProgress(int count)` | `int` | 初期目標インデックス（`count > 1` なら `1`、それ以外は `0`） |

### フォローターゲット API

パス上の最近傍点に移動してからウェイポイント沿いにゴールへ進むためのAPI。
ウェイポイント配列と現在位置から次のウェイポイントの座標を返す。

簡易版（ステートレス、インデックス管理不要）:

```csharp
// ウェイポイント配列wpsと現在位置transform.positionから次のウェイポイントの座標targetを得る
Vector3 target = manager.GetFollowTarget(wps, transform.position);
// 毎フレーム target に向かって移動する
Vector3 dir = target - transform.position;
Vector3 speed = 0.1f;
transform.position = transform.position + dir.normalized + speed * Time.deltaTime;
```

ゴール判定:

```csharp
Vector3 target = manager.GetFollowTarget(wps, transform.position, out bool reachedGoal);
if (reachedGoal) { /* 到達 */ }
```

フル版（インデックス追跡あり、O(1) / フレーム）:

```csharp
int followIndex = -1; // -1 = スナップ段階

void Update()
{
    Vector3 target = manager.GetFollowTarget(
        wps, transform.position,
        followIndex, snapDistance, reachDistance,
        out followIndex, out bool reachedGoal);

    if (reachedGoal) { /* 到達 */ }
    // target に向かって移動する
}
```

`currentIndex` と段階の対応:

| `currentIndex` | 段階 | 動作 |
|---|---|---|
| `-1` | スナップ | パス上の最近傍点へ移動 |
| `>= 0` | 追跡 | `wps[currentIndex]` へ移動。到達で次へ進む |

### グリッド / ボクセル API

| メソッド | 戻り値 | 説明 |
|---|---|---|
| `WorldToLeaf(Vector3 worldPos)` | `int` | ワールド位置のリーフインデックス。存在しない場合は `-1` |
| `LeafToWorld(int leafIdx, bool center)` | `Vector3` | リーフのワールド位置 |
| `FindNearestEmptyLeaf(Vector3 worldPos)` | `int` | `worldPos` に最も近い歩行可能リーフ |
| `Rebuild()` | `void` | コライダーからグリッドを再構築し GPU を再初期化 |
| `GetCellSize()` | `float` | ボクセルセルサイズ |
| `GetGridOrigin()` | `Vector3` | グリッド原点 |
| `GetGridSizeX/Y/Z()` | `int` | グリッド寸法 |

## コンポーネント

| コンポーネント | 型 | 説明 |
|---|---|---|
| `UdonPathfindingManager` | UdonSharpBehaviour | 経路探索エンジン本体（GPU BFS、ボクセルグリッド、パス再構築） |
| `UdonPathfindDrawer` | UdonSharpBehaviour | `VRCGraphics.DrawMeshInstanced` による壁、パス、マーカーのインスタンス描画 |
| `UdonPathfindVisualizerMono` | MonoBehaviour | Editor モード用デバッグビジュアライザ（VRChat ランタイム非対象） |
| `UdonPathfindingManagerEditor` | Custom Editor | バイリンガル HelpBox、Scene ハンドル、壁プレビュー、リビルドボタン付きインスペクター |

## 仕組み

1. **ボクセル化**：壁コライダーを `Physics.OverlapBoxNonAlloc` で密な 3D バイトグリッドにサンプリングする。壁ボクセルがコライダー体積を完全に覆う。
2. **リーフマッピング**：歩行可能ボクセルをリーフインデックスにマッピングする。6 方向の隣接接続を事前計算する。
3. **GPU BFS**：ウェーブフロント展開シェーダー（Compeito）が、テクスチャに格納された隣接グラフ上で開始ノードから伝播する。各反復でゴール検出パスが到達をチェックする。
4. **リードバック**：`VRCAsyncGPUReadback` が親ポインタテクスチャを取得する。ゴールから開始点まで逆追跡でパスを再構築する。
5. **スムージング**：`Physics.SphereCast` で壁が遮らない角を切り、不要なウェイポイントを削除する。

## 制限事項

- **グリッドサイズ**：最大テクスチャ寸法に制限がある。`gridSizeX * gridSizeY * gridSizeZ` は妥当な範囲に収めること（32^3 = 32768 ボクセルまでテスト済み）
- **壁レイヤー**：`wallColliders` のレイヤーから自動検出する。`SphereCast` スムージングでのみ使用する
- **同時に1パスのみ**：リクエストはキューに入り順次処理される

## サンプル

Samples~/Demo以下　フォロー移動の動作例

## ライセンス

[MIT](LICENSE)

## 作者

nyakomake（https://github.com/nyakomechan）
