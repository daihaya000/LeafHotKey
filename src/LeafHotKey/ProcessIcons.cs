using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LeafHotKey;

/// <summary>
/// プロファイルが対象にする実行ファイルのアイコンを PNG で返す。
/// 見つからない場合は null を返し、呼び出し側は代替表示（モノグラム）にする。
/// </summary>
public static class ProcessIcons
{
    private const int IconSize = 48;

    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>
    /// 実行ファイル名（例: CLIPStudioPaint.exe）に対応するアイコン。
    /// 保存済みのフルパスがあればそれを優先し、アプリが起動していなくてもアイコンを出せるようにする。
    /// </summary>
    public static byte[]? PngFor(string processName, string? knownPath = null)
    {
        var bare = Normalize(processName);
        if (bare is null) return null;

        var stored = NormalizePath(knownPath);
        var key = stored ?? bare;

        lock (Gate)
        {
            // 見つからなかった結果も残し、行を描くたびに探索しない。
            if (Cache.TryGetValue(key, out var cached)) return cached;
            if (Cache.Count >= 256) Cache.Clear();

            var payload = Load(bare, stored);
            Cache[key] = payload;
            return payload;
        }
    }

    /// <summary>実行ファイルのフルパスを探す（起動中のプロセス → App Paths）。見つからなければ null。</summary>
    public static string? ResolveFullPath(string processName)
    {
        var bare = Normalize(processName);
        return bare is null ? null : ResolvePath(bare);
    }

    /// <summary>設定に残したフルパスを確かめる。壊れた値や exe 以外は使わない。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var value = path.Trim();
        if (value.Length > 260) return null;
        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.IsPathFullyQualified(value)) return null;

        return File.Exists(value) ? value : null;
    }

    /// <summary>実行ファイル名だけを受け付ける。パス指定や移動は扱わない。</summary>
    private static string? Normalize(string processName)
    {
        var name = processName.Trim();
        if (name.Length is 0 or > 128) return null;
        if (name.Contains("..", StringComparison.Ordinal)) return null;
        if (name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '%' }) >= 0) return null;

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static byte[]? Load(string bare, string? knownPath)
    {
        try
        {
            var path = knownPath ?? ResolvePath(bare);
            if (path is null) return null;

            using var icon = ExtractIcon(path);
            if (icon is null) return null;

            using var source = icon.ToBitmap();
            using var scaled = new Bitmap(IconSize, IconSize);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, IconSize, IconSize);
            }

            using var stream = new MemoryStream();
            scaled.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException or UnauthorizedAccessException or OutOfMemoryException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static Icon? ExtractIcon(string path) => Icon.ExtractAssociatedIcon(path);

    /// <summary>起動中のプロセス → App Paths → HKCR\Applications の順に実行ファイルの場所を探す。</summary>
    private static string? ResolvePath(string bare)
    {
        foreach (var process in Process.GetProcessesByName(bare))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 権限やビット数の違いで読めない場合は次の候補へ進む。
                }
            }
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{bare}.exe");
                    if (key?.GetValue(null) is string value && !string.IsNullOrEmpty(value) && File.Exists(value)) return value;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }

        // インストール済みのアプリは HKEY_CLASSES_ROOT\Applications に登録されていることが多い。
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"Applications\{bare}.exe\shell\open\command");
            if (key?.GetValue(null) is string command && ParseCommandPath(command) is { } path) return path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }

    /// <summary>"C:\…\app.exe" "%1" のようなコマンド文字列から実行ファイルのパスを取り出す。</summary>
    private static string? ParseCommandPath(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        string path;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            path = text[1..end];
        }
        else
        {
            path = text.Split(' ', 2)[0];
        }

        return File.Exists(path) ? path : null;
    }
}
