using System;
using System.Collections.Generic;
using Hyperdrive.Core;
using Hyperdrive.Patching;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hyperdrive.Diagnostics
{
    /// <summary>
    /// Mods that log the same message every frame cost a lot: string formatting, native logging,
    /// stack-trace capture and two log files written synchronously. This subsystem wraps Unity's
    /// public log handler (no Harmony patch): identical messages beyond a few per time window are
    /// dropped and later summarized in a single line.
    /// Known limit: exceptions that Unity logs natively (uncaught in MonoBehaviour callbacks) may not go
    /// through Debug.unityLogger and are then not covered. To be verified in game.
    /// </summary>
    internal sealed class LogSpamGuard : Subsystem
    {
        private int _maxRepeats = 5;
        private int _windowSeconds = 10;
        private int _maxTrackedMessages = 2048;
        private const float SweepIntervalSeconds = 2f;

        private GuardedLogHandler _handler;
        private ILogHandler _previous;
        private float _nextSweep;

        public override string Name => "LogSpamGuard";
        public override bool ReadOnly => true;

        protected internal override void LoadSettings(SettingsSection settings)
        {
            _maxRepeats = Math.Max(1, settings.GetInt("maxRepeats", _maxRepeats));
            _windowSeconds = Math.Max(1, settings.GetInt("windowSeconds", _windowSeconds));
            _maxTrackedMessages = Math.Max(64, settings.GetInt("maxTrackedMessages", _maxTrackedMessages));
        }

        protected internal override void OnEnable()
        {
            ILogger logger = Debug.unityLogger;
            _previous = logger.logHandler ?? throw new InvalidOperationException("Debug.unityLogger has no log handler");
            _handler = new GuardedLogHandler(_previous, _maxRepeats, _windowSeconds * 1000, _maxTrackedMessages);
            logger.logHandler = _handler;
            Log.Info("LogSpamGuard active: identical messages beyond " + _maxRepeats + " per " + _windowSeconds + " s are summarized.");
        }

        protected internal override void OnDisable()
        {
            if (_handler == null)
                return;

            _handler.FlushAll();
            _handler.Bypass = true;

            ILogger logger = Debug.unityLogger;
            if (ReferenceEquals(logger.logHandler, _handler))
                logger.logHandler = _previous;
            // Otherwise another mod wrapped our handler after us: unhooking would cut it off,
            // so we stay in the chain as a transparent pass-through (Bypass).
            _handler = null;
        }

        protected internal override void OnUpdate()
        {
            float now = Time.unscaledTime;
            if (now < _nextSweep)
                return;
            _nextSweep = now + SweepIntervalSeconds;
            _handler?.FlushExpired();
        }
    }

    /// <summary>
    /// Thread-safe ILogHandler decorator (Unity calls log handlers from any thread).
    /// Steady state is allocation-free apart from what the logging call itself already allocates.
    /// </summary>
    internal sealed class GuardedLogHandler : ILogHandler
    {
        private const int MaxPooledEntries = 256;
        private const int MaxSummaryMessageLength = 300;

        private sealed class Entry
        {
            public int WindowStart;
            public int Count;
            public int Suppressed;
            public LogType Type;
        }

        private struct Summary
        {
            public string Message;
            public int Suppressed;
            public LogType Type;
        }

        private readonly ILogHandler _inner;
        private readonly int _maxRepeats;
        private readonly int _windowMs;
        private readonly int _maxTracked;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _entries;
        private readonly Stack<Entry> _pool = new Stack<Entry>();
        private readonly List<string> _expiredKeys = new List<string>(64);
        private readonly List<Summary> _pending = new List<Summary>(16);

        // Spam comes in bursts of the same call site: the same format string instance over and over. Its key and entry
        // are kept here so that a burst costs a reference comparison instead of hashing the text each time.
        private string _lastFormat;
        private bool _lastFormatIsKey;
        private string _lastKey;
        private Entry _lastEntry;

        public volatile bool Bypass;

        /// <summary>Total number of messages dropped since start.</summary>
        public long TotalSuppressed { get; private set; }

        public GuardedLogHandler(ILogHandler inner, int maxRepeats, int windowMs, int maxTracked)
        {
            _inner = inner;
            _maxRepeats = maxRepeats;
            _windowMs = windowMs;
            _maxTracked = maxTracked;
            _entries = new Dictionary<string, Entry>(Math.Min(maxTracked, 256), StringComparer.Ordinal);
        }

        public void LogFormat(LogType logType, Object context, string format, params object[] args)
        {
            if (Bypass || format == null)
            {
                _inner.LogFormat(logType, context, format, args);
                return;
            }

            string key;
            try
            {
                key = FastFormatKey(format, args) ?? MessageKey(format, args);
            }
            catch (Exception)
            {
                // Malformed format string: let the original handler produce its usual error.
                _inner.LogFormat(logType, context, format, args);
                return;
            }

            bool admitted = Admit(key, logType, out int previousSuppressed);
            if (previousSuppressed > 0)
                EmitSummary(key, previousSuppressed, logType);
            if (admitted)
                _inner.LogFormat(logType, context, format, args);
        }

        public void LogException(Exception exception, Object context)
        {
            if (Bypass || exception == null)
            {
                _inner.LogException(exception, context);
                return;
            }

            string key;
            try
            {
                key = ExceptionKey(exception);
            }
            catch (Exception)
            {
                _inner.LogException(exception, context);
                return;
            }

            bool admitted = Admit(key, LogType.Exception, out int previousSuppressed);
            if (previousSuppressed > 0)
                EmitSummary(key, previousSuppressed, LogType.Exception);
            if (admitted)
                _inner.LogException(exception, context);
        }

        /// <summary>Emit summaries for windows that ended and forget idle messages. Main thread, every few seconds.</summary>
        public void FlushExpired() => Flush(false);

        public void FlushAll() => Flush(true);

        private bool Admit(string key, LogType type, out int previousSuppressed)
        {
            previousSuppressed = 0;
            int now = Environment.TickCount;

            lock (_lock)
            {
                Entry entry;
                if (ReferenceEquals(key, _lastKey) && _lastEntry != null)
                    entry = _lastEntry;
                else if (!_entries.TryGetValue(key, out entry))
                {
                    // Table full (thousands of distinct messages): stop tracking new ones rather than evicting.
                    if (_entries.Count >= _maxTracked)
                        return true;

                    entry = _pool.Count > 0 ? _pool.Pop() : new Entry();
                    entry.WindowStart = now;
                    entry.Count = 0;
                    entry.Suppressed = 0;
                    entry.Type = type;
                    _entries.Add(key, entry);
                }
                else if (unchecked(now - entry.WindowStart) >= _windowMs)
                {
                    previousSuppressed = entry.Suppressed;
                    entry.WindowStart = now;
                    entry.Count = 0;
                    entry.Suppressed = 0;
                }

                _lastKey = key;
                _lastEntry = entry;

                if (++entry.Count <= _maxRepeats)
                    return true;

                entry.Suppressed++;
                TotalSuppressed++;
                return false;
            }
        }

        private void Flush(bool all)
        {
            Summary[] toEmit = null;
            int now = Environment.TickCount;

            lock (_lock)
            {
                if (_entries.Count == 0)
                    return;

                foreach (KeyValuePair<string, Entry> pair in _entries)
                {
                    Entry entry = pair.Value;
                    if (!all && unchecked(now - entry.WindowStart) < _windowMs)
                        continue;

                    if (entry.Suppressed > 0)
                        _pending.Add(new Summary { Message = pair.Key, Suppressed = entry.Suppressed, Type = entry.Type });
                    _expiredKeys.Add(pair.Key);
                }

                // Entries go back to the pool and may be reused for another message.
                _lastKey = null;
                _lastEntry = null;
                for (int i = 0; i < _expiredKeys.Count; i++)
                {
                    string key = _expiredKeys[i];
                    if (_pool.Count < MaxPooledEntries)
                        _pool.Push(_entries[key]);
                    _entries.Remove(key);
                }
                _expiredKeys.Clear();

                if (_pending.Count > 0)
                {
                    toEmit = _pending.ToArray();
                    _pending.Clear();
                }
            }

            // Emitted outside the lock: log callbacks of other mods may log again and re-enter this handler.
            if (toEmit == null)
                return;
            for (int i = 0; i < toEmit.Length; i++)
                EmitSummary(toEmit[i].Message, toEmit[i].Suppressed, toEmit[i].Type);
        }

        private void EmitSummary(string message, int suppressed, LogType type)
        {
            if (message.Length > MaxSummaryMessageLength)
                message = message.Substring(0, MaxSummaryMessageLength) + "...";

            string text = Log.Prefix + "LogSpamGuard: suppressed " + suppressed + " repeats within " + (_windowMs / 1000)
                          + " s of: " + message;
            _inner.LogFormat(type == LogType.Exception ? LogType.Error : type, null, "{0}", new object[] { text });
        }

        private static string MessageKey(string format, object[] args)
        {
            if (args == null || args.Length == 0)
                return format;

            // Debug.Log(message) arrives as LogFormat(type, ctx, "{0}", message): no formatting needed.
            if (args.Length == 1 && format == "{0}")
                return args[0] as string ?? args[0]?.ToString() ?? "Null";

            // A real format string identifies its call site: the same line with changing numbers (ids, counters) is
            // still the same spam, e.g. KSP's "[PartSet]: Failed to add Resource {0} to Simulation PartSet:{1}..."
            // logged for every part on every delta-v simulation. Short generic formats ("{0}: {1}") keep the
            // formatted text as key so that unrelated messages are never merged.
            if (LiteralLength(format) >= MinLiteralFormatLength)
                return format;

            return string.Format(format, args);
        }

        private const int MinLiteralFormatLength = 16;

        // Same format string instance as the previous call (a literal of the calling code): reuse the decision taken for
        // it instead of scanning it again. Returns null when the full key must be computed.
        private string FastFormatKey(string format, object[] args)
        {
            if (args == null || args.Length == 0 || (args.Length == 1 && format == "{0}"))
                return null;
            lock (_lock)
            {
                if (!ReferenceEquals(format, _lastFormat))
                {
                    _lastFormat = format;
                    _lastFormatIsKey = LiteralLength(format) >= MinLiteralFormatLength;
                }
                return _lastFormatIsKey ? format : null;
            }
        }

        // Characters of a format string outside its {placeholders}.
        private static int LiteralLength(string format)
        {
            int literal = 0, depth = 0;
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c == '{') depth++;
                else if (c == '}') { if (depth > 0) depth--; }
                else if (depth == 0) literal++;
            }
            return literal;
        }

        // Type + message alone would merge unrelated NullReferenceExceptions from different mods,
        // so the throwing method is part of the key.
        private static string ExceptionKey(Exception exception)
        {
            var site = exception.TargetSite;
            return exception.GetType().FullName + ": " + exception.Message
                   + " @ " + site?.DeclaringType?.FullName + "." + site?.Name;
        }
    }
}
