using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Sulap.Core;

namespace Sulap.App.Spike;

/// <summary>
/// A timestamped log that any thread can write to without waiting: entries are queued and written
/// out, in timestamp order, when the UI thread calls <see cref="Flush"/>.
/// </summary>
internal sealed class SpikeLog : IDisposable
{
    private readonly ConcurrentQueue<Entry> _pending = new();
    private readonly ConcurrentDictionary<uint, string> _processNames = new();
    private readonly StringBuilder _text = new();
    private readonly StreamWriter _file;
    private readonly long _start = Stopwatch.GetTimestamp();

    public SpikeLog()
    {
        Directory.CreateDirectory(AppIdentity.LogDirectory);
        FilePath = Path.Combine(AppIdentity.LogDirectory, $"drag-spike-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _file = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string FilePath { get; }

    /// <summary>Everything written so far, as it appears in the file.</summary>
    public string Text => _text.ToString();

    public void Write(string category, string message) => Write(Stopwatch.GetTimestamp(), category, message);

    public void Write(long timestamp, string category, string message) => _pending.Enqueue(new Entry(timestamp, category, message));

    /// <summary>Lines without a timestamp, such as headers.</summary>
    public void WriteRaw(string text) => _pending.Enqueue(new Entry(Stopwatch.GetTimestamp(), null, text));

    public double MillisecondsSince(long timestamp) => (Stopwatch.GetTimestamp() - timestamp) * 1000d / Stopwatch.Frequency;

    /// <summary>"explorer.exe" for a process ID, cached. Safe to call from any thread.</summary>
    public string ProcessName(uint processId) => _processNames.GetOrAdd(processId, static id =>
    {
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"process {id}";
        }
    });

    /// <summary>Writes the queued entries to the file. Returns the new text (empty when nothing was queued).</summary>
    public string Flush()
    {
        var batch = new List<Entry>();
        while (_pending.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0)
        {
            return string.Empty;
        }

        batch.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        var text = new StringBuilder();
        foreach (var entry in batch)
        {
            text.AppendLine(entry.Category is null
                ? entry.Message
                : $"{(entry.Timestamp - _start) * 1000d / Stopwatch.Frequency,9:0.0} ms  {entry.Category,-8} {entry.Message}");
        }

        var added = text.ToString();
        _text.Append(added);
        _file.Write(added);
        _file.Flush();
        return added;
    }

    public void Dispose()
    {
        Flush();
        _file.Dispose();
    }

    private readonly record struct Entry(long Timestamp, string? Category, string Message);
}
