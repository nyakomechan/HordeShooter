# Udon 3D Pathfinding

GPU-based 3D pathfinding for VRChat worlds, powered by [Compeito](https://github.com/phi16/Compeito) GPGPU kernels.

## Features

- **GPU wavefront BFS** on a dense 3D voxel grid using Compeito compute shaders
- **UdonSharp** — fully compatible with VRChat Udon runtime
- **Any Collider type** — Box, Sphere, Capsule, and Mesh colliders all supported
- **OverlapBox-based voxel filling** — wall voxels completely cover collider volumes
- **Path smoothing** — spherecast-based corner cutting along the raw voxel path
- **Instanced rendering** — `VRCGraphics.DrawMeshInstanced` for walls, path, and markers (no GameObjects created)
- **Stateless path utilities** — pass `Vector3[]` waypoints directly, no hidden state
- **Follow target API** — snap-to-path then advance along waypoints, with simple and full overloads
- **Custom Inspector** — bilingual (EN/JA) help boxes, Scene View grid handles, wall voxel preview, rebuild button, auto material assignment
- **PC-only** — Quest not supported (Compeito requires GPU compute)

## Requirements

| Package | Version |
|---|---|
| `com.vrchat.worlds` | >=3.10.3 |
| `com.imaginantia.compeito` | >=1.0.1 |
| Unity | 2022.3 |

## Installation

Add this repo as a VPM repository in the VRChat Creator Companion, then install the package:

```
https://github.com/nyakomechan/Udon-3D-Pathfinding.git
```

Or manually place this folder under your project's `Packages/` directory.

## Quick Start

1. Add `UdonPathfindingManager` to a GameObject in your scene
2. Assign wall `Collider[]` in the Inspector (any collider type, any rotation)
3. Set grid size (`gridSizeX/Y/Z`), `cellSize`, and `gridOrigin`
4. The `PathfindCompeito` material is auto-assigned when the Inspector is opened
5. Call `RequestPath` from any UdonSharpBehaviour:

```csharp
public UdonPathfindingManager manager;

void Start()
{
    manager.RequestPath(startPos, goalPos, this);
}

public void OnPathFound()
{
    Vector3[] wps = manager.waypoints;
    // Use the path...
}
```

## API Reference

### Path Request

| Method | Description |
|---|---|
| `RequestPath(Vector3 start, Vector3 goal)` | Queue a path request using default receiver/events |
| `RequestPath(Vector3 start, Vector3 goal, UdonSharpBehaviour receiver)` | Queue with custom receiver |
| `RequestPath(Vector3 start, Vector3 goal, UdonSharpBehaviour receiver, string foundEvent, string failedEvent)` | Queue with custom receiver and event names |

Results are delivered via `SendCustomEvent` to the receiver. After `OnPathFound`, read `manager.waypoints` (`Vector3[]`).

### Path Result Fields

| Field | Type | Description |
|---|---|---|
| `waypoints` | `Vector3[]` | Smoothed path waypoints |
| `pathFound` | `bool` | Whether the last search succeeded |
| `pathError` | `string` | Error message if failed |
| `isBusy` | `bool` | Whether a search is in progress |
| `startWorld` / `goalWorld` | `Vector3` | Actual start/goal positions used |
| `searchIterationCount` | `int` | Total wavefront iterations |
| `searchFrameCount` | `int` | Total frames elapsed |

### Path Utility API (stateless — pass `Vector3[] wps`)

| Method | Returns | Description |
|---|---|---|
| `GetPathLength(Vector3[] wps)` | `float` | Total distance across all segments |
| `GetClosestPointOnPath(Vector3[] wps, Vector3 pos)` | `Vector3` | Closest point on the path to `pos` |
| `GetClosestWaypointIndex(Vector3[] wps, Vector3 pos)` | `int` | Index of nearest waypoint to `pos` |
| `ResamplePath(Vector3[] wps, float spacing)` | `Vector3[]` | Resampled path at equal intervals |

### Waypoint Progress API (stateless — pass `int index`)

| Method | Returns | Description |
|---|---|---|
| `GetCurrentWaypoint(Vector3[] wps, int index)` | `Vector3` | Waypoint at `index` |
| `GetRemainingDistance(Vector3[] wps, int index)` | `float` | Distance from `index` to end |
| `GetProgress(Vector3[] wps, int index)` | `float` | Progress `0..1` (distance-based) |
| `AdvanceWaypoint(int index, int count)` | `int` | Next index, or `-1` at end |
| `ResetWaypointProgress(int count)` | `int` | Initial target index (`1` if `count > 1`, else `0`) |

### Follow Target API

**Simple (stateless, no index management):**

```csharp
Vector3 target = manager.GetFollowTarget(wps, transform.position);
// Move toward target each frame
```

**Simple with goal check:**

```csharp
Vector3 target = manager.GetFollowTarget(wps, transform.position, out bool reachedGoal);
if (reachedGoal) { /* arrived */ }
```

**Full (with index tracking for O(1) per-frame):**

```csharp
int followIndex = -1; // -1 = snap phase

void Update()
{
    Vector3 target = manager.GetFollowTarget(
        wps, transform.position,
        followIndex, snapDistance, reachDistance,
        out followIndex, out bool reachedGoal);

    if (reachedGoal) { /* arrived */ }
    // Move toward target...
}
```

| `currentIndex` | Phase | Behavior |
|---|---|---|
| `-1` | Snap | Move toward closest point on path |
| `>= 0` | Follow | Move toward `wps[currentIndex]`, advance on reach |

### Grid / Voxel API

| Method | Returns | Description |
|---|---|---|
| `WorldToLeaf(Vector3 worldPos)` | `int` | Leaf index at world position, or `-1` |
| `LeafToWorld(int leafIdx, bool center)` | `Vector3` | World position of a leaf |
| `FindNearestEmptyLeaf(Vector3 worldPos)` | `int` | Nearest walkable leaf to `worldPos` |
| `Rebuild()` | `void` | Rebuild grid from colliders and reinitialize GPU |
| `GetCellSize()` | `float` | Voxel cell size |
| `GetGridOrigin()` | `Vector3` | Grid origin |
| `GetGridSizeX/Y/Z()` | `int` | Grid dimensions |

## Components

| Component | Type | Description |
|---|---|---|
| `UdonPathfindingManager` | UdonSharpBehaviour | Core pathfinding engine (GPU BFS, voxel grid, path reconstruction) |
| `UdonPathfindDrawer` | UdonSharpBehaviour | Instanced mesh rendering of walls/path/markers via `VRCGraphics.DrawMeshInstanced` |
| `UdonPathfindVisualizerMono` | MonoBehaviour | Debug visualizer for Editor mode (not for VRChat runtime) |
| `UdonPathfindingManagerEditor` | Custom Editor | Inspector with bilingual help, Scene handles, wall preview, rebuild button |

## How It Works

1. **Voxelization** — Wall colliders are sampled into a dense 3D byte grid using `Physics.OverlapBoxNonAlloc` per voxel. Wall voxels completely cover collider volumes.
2. **Leaf mapping** — Walkable voxels are mapped to leaf indices. 6-directional neighbor connectivity is precomputed.
3. **GPU BFS** — A wavefront expansion shader (Compeito) propagates from the start node across the neighbor graph stored in textures. A goal-detection pass checks for arrival each iteration.
4. **Readback** — `VRCAsyncGPUReadback` retrieves the parent-pointer texture. Path is reconstructed by backtracking from goal to start.
5. **Smoothing** — `Physics.SphereCast` removes unnecessary waypoints by cutting corners where no wall blocks the direct line.

## Limitations

- **PC-only** — Compeito uses GPU compute not available on Quest
- **Grid size** — Limited by max texture dimension; `gridSizeX * gridSizeY * gridSizeZ` should stay within reasonable bounds (tested up to 32^3 = 32768 voxels)
- **Wall layer** — Auto-detected from `wallColliders` gameObject layers; used for `SphereCast` smoothing only
- **Single path at a time** — Requests are queued and processed serially

## Samples

Import the **Demo Scene** sample via Unity Package Manager to see a working example with follow movement.

## License

MIT

## Author

nyakomake — https://github.com/nyakomechan
