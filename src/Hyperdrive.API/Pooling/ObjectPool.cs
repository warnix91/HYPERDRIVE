using System;
using System.Collections.Generic;

namespace Hyperdrive.API.Pooling
{
    /// <summary>
    /// Generic pool for plain C# objects (buffers, lists, data records). Not thread-safe.
    /// Prewarm at load time so that <see cref="Get"/> never allocates during gameplay; <see cref="Misses"/>
    /// counts the times the pool was empty and had to create an object.
    /// </summary>
    public sealed class ObjectPool<T> where T : class
    {
        private readonly Stack<T> _free;
        private readonly Func<T> _create;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private readonly Action<T> _onDiscard;
        private readonly int _maxInactive;

        public ObjectPool(Func<T> create, Action<T> onGet = null, Action<T> onRelease = null, Action<T> onDiscard = null, int maxInactive = 1024)
        {
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _onGet = onGet;
            _onRelease = onRelease;
            _onDiscard = onDiscard;
            _maxInactive = Math.Max(1, maxInactive);
            _free = new Stack<T>(Math.Min(_maxInactive, 64));
        }

        /// <summary>Objects created by this pool and not discarded.</summary>
        public int CountAll { get; private set; }

        public int CountInactive => _free.Count;

        public int CountActive => CountAll - _free.Count;

        /// <summary>Times <see cref="Get"/> found the pool empty and created a new object.</summary>
        public int Misses { get; private set; }

        /// <summary>Create objects up front (call while loading, not during gameplay).</summary>
        public void Prewarm(int count)
        {
            for (int i = _free.Count; i < count && _free.Count < _maxInactive; i++)
            {
                T item = _create();
                CountAll++;
                _onRelease?.Invoke(item);
                _free.Push(item);
            }
        }

        public T Get()
        {
            T item;
            if (_free.Count > 0)
            {
                item = _free.Pop();
            }
            else
            {
                item = _create();
                CountAll++;
                Misses++;
            }
            _onGet?.Invoke(item);
            return item;
        }

        public void Release(T item)
        {
            if (item == null)
                return;
            _onRelease?.Invoke(item);
            if (_free.Count < _maxInactive)
            {
                _free.Push(item);
            }
            else
            {
                CountAll--;
                _onDiscard?.Invoke(item);
            }
        }

        public void Clear()
        {
            while (_free.Count > 0)
            {
                T item = _free.Pop();
                CountAll--;
                _onDiscard?.Invoke(item);
            }
        }
    }
}
