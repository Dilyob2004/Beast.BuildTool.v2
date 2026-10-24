using System.Text;

namespace Beast.BuildTool;

/// <summary>Drain both pipes concurrently; bounded capture, live complete lines (or bounded fragments).</summary>
public static class ProcessDiagnostics
{
    public static async Task<string> ReadAsync(StreamReader reader, Action<string>? emit = null)
    {
        const int captureLimit = 4 * 1024 * 1024;
        var capture = new StringBuilder();
        var pending = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            int keep = Math.Min(count, captureLimit - capture.Length);
            capture.Append(buffer, 0, keep);
            truncated |= keep != count;
            for (int i = 0; i < count; ++i)
            {
                char c = buffer[i];
                if (c == '\n' || pending.Length == 4096)
                {
                    if (pending.Length > 0) emit?.Invoke(pending.ToString().TrimEnd('\r'));
                    pending.Clear();
                }
                if (c != '\n') pending.Append(c);
            }
        }
        if (pending.Length > 0) emit?.Invoke(pending.ToString().TrimEnd('\r'));
        if (truncated) capture.Append("\n[diagnostic capture truncated at 4 MiB; live output was drained]\n");
        return capture.ToString();
    }

    public static void Emit(string line)
    {
        if (line.Contains("warning", StringComparison.OrdinalIgnoreCase)) Log.TraceWarning(line);
        else Log.TraceInfo(line);
    }
}
