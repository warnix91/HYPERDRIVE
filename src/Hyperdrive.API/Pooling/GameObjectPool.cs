using System;
using System.Collections.Generic;
using UnityEngine;

namespace Hyperdrive.API.Pooling
{
    /// <summary>
    /// Pool of instances of one prefab (explosion effects, debris, decals...). Main thread only.
    /// Inactive instances are parked, disabled, under a hidden root. <see cref="ReleaseAfter"/> returns an instance
    /// automatically after a delay without any per-effect coroutine. Instances destroyed by someone else
    /// (e.g. on scene change) are detected and replaced.
    /// </summary>
    public sealed class GameObjectPool : IDisposable
    {
        private readonly GameObject _prefab;
        private readonly Stack<GameObject> _free;
        private readonly int _maxInactive;
        private readonly bool _persistAcrossScenes;
        private readonly Action<GameObject> _onGet;
        private readonly Action<GameObject> _onRelease;
        private Transform _root;

        /// <param name="prefab">Template; it is never activated or modified.</param>
        /// <param name="maxInactive">Instances kept when released; extra ones are destroyed.</param>
        /// <param name="persistAcrossScenes">Keep pooled instances when the scene changes (DontDestroyOnLoad root).</param>
        /// <param name="onGet">Called after activation, e.g. to restart particle systems.</param>
        /// <param name="onRelease">Called before deactivation, e.g. to stop sounds.</param>
        public GameObjectPool(GameObject prefab, int maxInactive = 64, bool persistAcrossScenes = false,
                              Action<GameObject> onGet = null, Action<GameObject> onRelease = null)
        {
            _prefab = prefab != null ? prefab : throw new ArgumentNullException(nameof(prefab));
            _maxInactive = Math.Max(1, maxInactive);
            _persistAcrossScenes = persistAcrossScenes;
            _onGet = onGet;
            _onRelease = onRelease;
            _free = new Stack<GameObject>(Math.Min(_maxInactive, 64));
        }

        public int CountInactive => _free.Count;

        /// <summary>Times <see cref="Get"/> had to instantiate because the pool was empty. Prewarm more if this grows.</summary>
        public int Misses { get; private set; }

        /// <summary>Instantiate up front (call while loading or when a scene starts, not during gameplay).</summary>
        public void Prewarm(int count)
        {
            Transform root = Root;
            for (int i = _free.Count; i < count && _free.Count < _maxInactive; i++)
            {
                GameObject go = UnityEngine.Object.Instantiate(_prefab, root, false);
                go.SetActive(false);
                _free.Push(go);
            }
        }

        public GameObject Get(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObject go = null;
            while (_free.Count > 0 && go == null)
                go = _free.Pop();   // skips instances destroyed behind our back (Unity null)

            if (go == null)
            {
                go = UnityEngine.Object.Instantiate(_prefab, Root, false);
                Misses++;
            }

            Transform t = go.transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(position, rotation);
            go.SetActive(true);
            _onGet?.Invoke(go);
            return go;
        }

        public void Release(GameObject go)
        {
            if (go == null)
                return;
            _onRelease?.Invoke(go);
            go.SetActive(false);
            if (_free.Count < _maxInactive)
            {
                go.transform.SetParent(Root, false);
                _free.Push(go);
            }
            else
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>Return <paramref name="go"/> to this pool after <paramref name="seconds"/> (game time).</summary>
        public void ReleaseAfter(GameObject go, float seconds)
        {
            if (go != null)
                PoolTimer.Schedule(this, go, Time.time + Mathf.Max(0f, seconds));
        }

        public void Dispose()
        {
            while (_free.Count > 0)
            {
                GameObject go = _free.Pop();
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }
            if (_root != null)
                UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
        }

        private Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("Hyperdrive.Pool." + _prefab.name);
                    go.SetActive(false);
                    if (_persistAcrossScenes)
                        UnityEngine.Object.DontDestroyOnLoad(go);
                    _root = go.transform;
                }
                return _root;
            }
        }
    }

    /// <summary>Shared timer for delayed releases: fixed-size arrays, no allocation per scheduled release.</summary>
    internal sealed class PoolTimer : MonoBehaviour
    {
        private const int Capacity = 4096;

        private static PoolTimer s_instance;
        private readonly GameObjectPool[] _pools = new GameObjectPool[Capacity];
        private readonly GameObject[] _objects = new GameObject[Capacity];
        private readonly float[] _times = new float[Capacity];
        private int _count;

        public static void Schedule(GameObjectPool pool, GameObject go, float time)
        {
            if (s_instance == null)
            {
                var host = new GameObject("Hyperdrive.PoolTimer");
                DontDestroyOnLoad(host);
                s_instance = host.AddComponent<PoolTimer>();
            }

            PoolTimer timer = s_instance;
            if (timer._count >= Capacity)
            {
                pool.Release(go);   // full: release now rather than leak
                return;
            }
            timer._pools[timer._count] = pool;
            timer._objects[timer._count] = go;
            timer._times[timer._count] = time;
            timer._count++;
        }

        private void Update()
        {
            float now = Time.time;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_times[i] > now)
                    continue;

                GameObjectPool pool = _pools[i];
                GameObject go = _objects[i];
                int last = --_count;
                _pools[i] = _pools[last];
                _objects[i] = _objects[last];
                _times[i] = _times[last];
                _pools[last] = null;
                _objects[last] = null;

                if (go != null)
                {
                    try { pool.Release(go); }
                    catch (Exception e) { Debug.LogException(e); }
                }
            }
        }
    }
}
