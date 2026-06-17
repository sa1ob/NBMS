namespace NBMS.Studio.MonoGameViewer;

internal sealed class CompositeVideoBgaDecoderFactory : IVideoBgaDecoderFactory
{
    private readonly IVideoBgaDecoderFactory[] _factories;

    public CompositeVideoBgaDecoderFactory(string? ffmpegPath)
    {
        _factories =
        [
            new FfmpegVideoDecoderFactory(ffmpegPath),
            new WindowsMediaVideoDecoderFactory()
        ];
    }

    public IVideoBgaDecoder Open(string sourcePath, TimeSpan? startOffset = null, bool startPaused = false)
    {
        var errors = new List<string>();
        foreach (var factory in _factories)
        {
            try
            {
                return factory.Open(sourcePath, startOffset, startPaused);
            }
            catch (Exception ex)
            {
                errors.Add($"{factory.GetType().Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException($"No video decoder could open the media. {string.Join(" / ", errors)}");
    }
}
