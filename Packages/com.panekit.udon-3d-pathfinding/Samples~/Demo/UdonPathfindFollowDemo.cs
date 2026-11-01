using UdonSharp;
using UnityEngine;

namespace nyakomake.udon3dpathfinding
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class UdonPathfindFollowDemo : UdonSharpBehaviour
    {
        [Header("Pathfinding")]
        public UdonPathfindingManager manager;
        private Vector3 start = new Vector3(1f, 1f, 1f);
        public Transform goalTarget = null;

        [Header("Movement")]
        public float moveSpeed = 3f;
        public int startDelayFrames = 60;

        private int frameCount;
        private bool pathReady;
        private bool reachedGoal;

        void Start()
        {
            start = transform.position;
            if (manager == null)
            {
                Debug.LogWarning("[UdonPathfindFollowDemo] manager is not assigned");
                return;
            }
        }

        void Update()
        {
            if (manager == null) return;

            if (!pathReady && !reachedGoal)
            {
                frameCount++;
                if (frameCount >= startDelayFrames)
                {
                    RequestPath();
                }
            }


            if (pathReady && !reachedGoal)
            {
                FollowPath();
            }
        }

        private void RequestPath()
        {
            if (goalTarget == null) return;

            Debug.Log(string.Format("[UdonPathfindFollowDemo] Requesting path: {0} -> {1}", start, goalTarget.position));
            manager.RequestPath(start, goalTarget.position, this);
        }

        private void FollowPath()
        {
            Vector3[] wps = manager.waypoints;
            if (wps == null || wps.Length == 0) return;

            Vector3 pos = transform.position;

            bool reached = false;

            Vector3 nextWayPoint = manager.GetFollowTarget(wps, pos, out reached);

            if (reached)
            {
                reachedGoal = true;
                pathReady = false;
                Debug.Log(string.Format("[UdonPathfindFollowDemo] Reached goal at {0}", pos.ToString("F2")));
                return;
            }

            Vector3 dir = nextWayPoint - pos;
            float dist = dir.magnitude;
            if (dist < 0.001f) return;

            Vector3 move = dir.normalized * moveSpeed * Time.deltaTime;
            if (move.magnitude > dist) move = dir;
            transform.position = pos + move;
        }

        public void OnPathFound()
        {
            if (manager == null) return;

            Vector3[] wps = manager.waypoints;
            int wc = (wps != null) ? wps.Length : 0;
            Debug.Log(string.Format("[UdonPathfindFollowDemo] Path found! waypoints={0} length={1:F1}",
                wc, manager.GetPathLength(wps)));

            pathReady = true;
            reachedGoal = false;
        }

        public void OnPathFailed()
        {
            if (manager == null) return;
            Debug.LogWarning(string.Format("[UdonPathfindFollowDemo] Path failed: {0}", manager.pathError));
            pathReady = false;
        }


    }
}