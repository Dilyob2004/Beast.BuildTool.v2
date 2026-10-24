using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Beast.BuildTool
{
    class FileTraceListener : TextWriterTraceListener
    {
        public FileTraceListener(string filePath) : base(filePath)
        {
            Writer?.WriteLine($"\n\n==== Log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            Writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {eventType,-12} {source} - {message}");
            Writer?.Flush();
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            Writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {eventType,-12} {source} - {string.Format(format ?? string.Empty, args ?? Array.Empty<object?>())}");
            Writer?.Flush();
        }
        public new void Dispose()
        {
            Writer?.Flush();
            Writer?.Dispose();
            base.Dispose();
        }
    }

    class VerbosityFilter : TraceFilter
    {
        public override bool ShouldTrace(TraceEventCache? cache, string source, TraceEventType eventType, int id, string? formatOrMessage, object?[]? args, object? data1, object?[]? data)
        {
            return eventType <= (Debugger.IsAttached ? TraceEventType.Verbose : TraceEventType.Information);
        }
    }


    class ConsoleListener : TextWriterTraceListener
    {
        private static readonly object СonsoleLock = new object();
        private static readonly ConcurrentDictionary<TraceEventType, ConsoleColor> Сolors = new ConcurrentDictionary<TraceEventType, ConsoleColor>
        {
            [TraceEventType.Error] = ConsoleColor.Red,
            [TraceEventType.Warning] = ConsoleColor.Yellow,
            [TraceEventType.Information] = ConsoleColor.White,
            [TraceEventType.Verbose] = ConsoleColor.Gray
        };

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            lock (СonsoleLock)
            {
                var originalColor = Console.ForegroundColor;
                Console.ForegroundColor = Сolors.TryGetValue(eventType, out var color) ? color : originalColor;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {eventType,-12} {source} - {message}");
                Console.ForegroundColor = originalColor;
            }
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            lock (СonsoleLock)
            {
                var originalColor = Console.ForegroundColor;
                Console.ForegroundColor = Сolors.TryGetValue(eventType, out var color) ? color : originalColor;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {eventType,-12} {source} - {string.Format(format ?? string.Empty, args ?? Array.Empty<object?>())}");
                Console.ForegroundColor = originalColor;
            }
        }
    }

    public sealed class Log
    {
        private static readonly object LockLog = new object();
        private static readonly ConcurrentDictionary<string, string> SourceCache = new ConcurrentDictionary<string, string>();
        private static readonly TraceEventCache SharedEventCache = new TraceEventCache();

        private static bool _isInitialized;
        private static bool _disposed;


        public static void Init(bool logToFile = false)
        {
            lock (LockLog)
            {
                if (_isInitialized) return;

                Trace.Listeners.Clear();
                Trace.Listeners.Add(new ConsoleListener());

                if (logToFile)
                {
                    Trace.Listeners.Add(new FileTraceListener("log.txt"));
                }

                _isInitialized = true;
                _disposed = false;
            }
        }
        public static void Cleanup()
        {
            Dispose();
        }

        public static void Dispose()
        {
            lock (LockLog)
            {
                if (!_isInitialized || _disposed) return;

                foreach (var listener in Trace.Listeners.OfType<IDisposable>())
                {
                    listener.Dispose();
                }

                Trace.Listeners.Clear();
                Trace.Flush();

                _disposed = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetSource(int stackFramesToSkip)
        {
            var frame = new StackFrame(2 + stackFramesToSkip);
            var method = frame.GetMethod();
            var key = method == null ? "Unknown" : $"{method.DeclaringType?.Name}.{method.Name}";

            return SourceCache.GetOrAdd(key, k =>
                method == null ? k : $"{method.DeclaringType?.Name}.{method.Name}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteLogInternal(int stackFramesToSkip, TraceEventType verbosity, string format, object?[]? args)
        {
            lock (LockLog)
            {
                var source = GetSource(stackFramesToSkip);
                foreach (TraceListener listener in Trace.Listeners)
                {
                    if (args?.Length > 0)
                    {
                        listener.TraceEvent(SharedEventCache, source, verbosity, 0, format, args);
                    }
                    else
                    {
                        listener.TraceEvent(SharedEventCache, source, verbosity, 0, format);
                    }
                }
            }
        }




        [Conditional("TRACE")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceVerbose(string format, params object[] args)
            => WriteLogInternal(1, TraceEventType.Verbose, format, args);

        [Conditional("TRACE")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceVerbose(string message)
            => WriteLogInternal(1, TraceEventType.Verbose, message, null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceInfo(string format, params object[] args)
            => WriteLogInternal(1, TraceEventType.Information, format, args);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceInfo(string message)
            => WriteLogInternal(1, TraceEventType.Information, message, null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceWarning(string format, params object[] args)
            => WriteLogInternal(1, TraceEventType.Warning, format, args);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceWarning(string message)
        {
            WriteLogInternal(1, TraceEventType.Warning, message, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceError(string format, params object[] args)
            => WriteLogInternal(1, TraceEventType.Error, format, args);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void TraceError(string message)
            => WriteLogInternal(1, TraceEventType.Error, message, null);
    }
}
