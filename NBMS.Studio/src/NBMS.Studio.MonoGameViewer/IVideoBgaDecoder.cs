namespace NBMS.Studio.MonoGameViewer;

// Viewer本体がffmpegなどの具体実装に依存しないための最小decoder境界。
internal interface IVideoBgaDecoder : IDisposable
{
    string SourcePath { get; }
    int OutputWidth { get; }
    int OutputHeight { get; }
    bool IsRunning { get; }
    bool IsEnded { get; }
    bool SupportsPlaybackControl { get; }
    string LastError { get; }

    bool Play();
    bool Pause();
    bool Seek(TimeSpan position);
    bool TryGetFrame(out VideoBgaFrame frame);
    bool TryCopyFrame(byte[] destination, TimeSpan presentationTime);
    bool TryCopyFrame(byte[] destination);
}

internal sealed record VideoBgaFrame(byte[] Rgba, int Width, int Height);

internal interface IVideoBgaDecoderFactory
{
    IVideoBgaDecoder Open(string sourcePath, TimeSpan? startOffset = null, bool startPaused = false);
}
