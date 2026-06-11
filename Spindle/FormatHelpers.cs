namespace Spindle;

/// <summary>Shared display formatting (file sizes etc.).</summary>
public static class Format
{
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
        : bytes >= 1L << 10 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";
}
