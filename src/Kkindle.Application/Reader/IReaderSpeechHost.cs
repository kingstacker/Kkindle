using Kkindle.ApplicationServices.Tts;
using Kkindle.Core;

namespace Kkindle.ApplicationServices;

/// <summary>Speech text and highlights without depending on a UI control.</summary>
public interface IReaderSpeechHost : IReaderHost
{
    ReaderTtsTextSnapshot? GetSpeechText();
    void SetSpeechHighlight(int start, int length);
    void ClearSpeechHighlight();
}
