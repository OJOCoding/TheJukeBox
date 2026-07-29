namespace TheJukeBox.API;

/// <summary>
/// Human-readable byte sizes.
/// </summary>
/// <remarks>
/// This logic previously existed in three near-identical copies: twice in
/// <c>AudioFilesController</c> (as <c>FormatFileSize</c> and <c>FormatTotalSize</c>,
/// byte-for-byte the same) and once more on the <c>AudioFile</c> model.
/// </remarks>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var unit = 0;
        double size = bytes;

        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {Units[unit]}";
    }
}
