extern alias FFmpegAutoGen;
using System.Runtime.InteropServices;
using FFmpegAutoGen::FFmpeg.AutoGen;
using FFmpegNative = FFmpegAutoGen::FFmpeg.AutoGen.ffmpeg;
using showroom.Utils;

namespace showroom.WebRtc;

internal sealed record FfmpegAbi(int AvFormatMajor, int AvCodecMajor, int AvUtilMajor, string Version)
{
    public static FfmpegAbi Expected => new(
        FFmpegNative.LIBAVFORMAT_VERSION_MAJOR,
        FFmpegNative.LIBAVCODEC_VERSION_MAJOR,
        FFmpegNative.LIBAVUTIL_VERSION_MAJOR,
        "the FFmpeg.AutoGen build ABI");

    public bool Matches(FfmpegAbi expected) =>
        AvFormatMajor == expected.AvFormatMajor &&
        AvCodecMajor == expected.AvCodecMajor &&
        AvUtilMajor == expected.AvUtilMajor;

    public string AbiDescription =>
        $"libavformat {AvFormatMajor}, libavcodec {AvCodecMajor}, libavutil {AvUtilMajor}";
}

internal interface IFfmpegNativeRuntime
{
    FfmpegAbi Load(string libraryPath);
}

internal static class FfmpegLibraryPathComparer
{
    public static StringComparer ForCurrentPlatform => ForPlatform(OperatingSystem.IsWindows());

    public static StringComparer ForPlatform(bool isWindows) =>
        isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

internal sealed class FfmpegAutoGenRuntime : IFfmpegNativeRuntime
{
    private readonly object _sync = new();
    private string? _loadedPath;
    private FfmpegAbi? _loadedAbi;

    public FfmpegAbi Load(string libraryPath)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(libraryPath)
            ? string.Empty
            : Path.GetFullPath(libraryPath);

        lock (_sync)
        {
            if (_loadedAbi is not null)
            {
                if (!FfmpegLibraryPathComparer.ForCurrentPlatform.Equals(_loadedPath, normalizedPath))
                    throw new InvalidOperationException(
                        $"FFmpeg is already loaded from '{DisplayPath(_loadedPath)}' and cannot be reloaded from '{DisplayPath(normalizedPath)}'. Restart the process to change WebRtc.NativeLibraryPath.");

                return _loadedAbi;
            }

            if (normalizedPath.Length > 0 && !Directory.Exists(normalizedPath))
                throw new DirectoryNotFoundException($"FFmpeg native library directory does not exist: '{normalizedPath}'.");

            EnsureSupportedRuntime();
            FFmpegNative.RootPath = normalizedPath;
            try
            {
                if (OperatingSystem.IsWindows() && normalizedPath.Length > 0 && !NativeLibrarySearch.SetDllDirectory(normalizedPath))
                    throw new InvalidOperationException($"Windows could not add FFmpeg directory '{normalizedPath}' to the native DLL search path (error {Marshal.GetLastWin32Error()}).");

                var abi = new FfmpegAbi(
                    (int)(FFmpegNative.avformat_version() >> 16),
                    (int)(FFmpegNative.avcodec_version() >> 16),
                    (int)(FFmpegNative.avutil_version() >> 16),
                    FFmpegNative.av_version_info());

                // FFmpeg.AutoGen loads native modules lazily; remember the path even if a later
                // ABI comparison rejects it. A different DLL set cannot safely replace modules
                // already loaded into this process, so a restart is required after mismatch.
                _loadedPath = normalizedPath;
                _loadedAbi = abi;
                return abi;
            }
            catch
            {
                // A failed native load can have loaded part of the FFmpeg dependency set. Do not
                // claim that resetting the search path makes correcting it safe in this process.
                FFmpegNative.RootPath = string.Empty;
                throw;
            }
        }
    }

    private static string DisplayPath(string? path) =>
        string.IsNullOrEmpty(path) ? "the system library search paths" : path;

    private static void EnsureSupportedRuntime()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()))
        {
            throw new PlatformNotSupportedException(
                $"WebRTC native FFmpeg is supported only on win-x64 and linux-x64; current runtime is '{RuntimeInformation.RuntimeIdentifier}'.");
        }
    }

    private static class NativeLibrarySearch
    {
        [DllImport("kernel32", EntryPoint = "SetDllDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDllDirectory(string path);
    }
}

internal sealed class FfmpegWebRtcDependencyPreflight : IWebRtcDependencyPreflight
{
    private readonly IFfmpegNativeRuntime _runtime;
    private static readonly FfmpegAbi ExpectedAbi = FfmpegAbi.Expected;

    public FfmpegWebRtcDependencyPreflight() : this(new FfmpegAutoGenRuntime())
    {
    }

    internal FfmpegWebRtcDependencyPreflight(IFfmpegNativeRuntime runtime)
    {
        _runtime = runtime;
    }

    public void Validate(string nativeLibraryPath)
    {
        var searchPath = string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? "the system library search paths"
            : $"'{Path.GetFullPath(nativeLibraryPath)}'";

        FfmpegAbi actual;
        try
        {
            actual = _runtime.Load(nativeLibraryPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Protocol is configured as 'webrtc', but compatible FFmpeg native libraries could not be loaded from {searchPath}. " +
                $"This build supports win-x64 and linux-x64 and requires {ExpectedAbi.AbiDescription}. Install matching shared libraries or set WebRtc.NativeLibraryPath to their directory. " +
                $"A native load may have partially pinned libraries in this process; restart after correcting the library set. Load failure: {ex.Message}",
                ex);
        }

        if (actual.Matches(ExpectedAbi))
            return;

        throw new InvalidOperationException(
            $"Protocol is configured as 'webrtc', but FFmpeg ABI at {searchPath} is incompatible. " +
            $"Loaded {actual.Version} ({actual.AbiDescription}); this build requires {ExpectedAbi.AbiDescription}. " +
            "Install matching shared libraries or set WebRtc.NativeLibraryPath to their directory, then restart the process because the incompatible native modules are already loaded.");
    }
}
