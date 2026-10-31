namespace Kkindle.Core;

public enum ReaderHostFormat
{
    Epub,
    Pdf
}

/// <summary>Creates independent rendering surfaces and controls format reuse.</summary>
public interface IReaderHostFactory
{
    IReaderHost Create(ReaderHostFormat format);
    bool IsCompatible(IReaderHost host, ReaderHostFormat format);
}

/// <summary>Optional native-reader capability, independent of its UI control type.</summary>
public interface IReaderAppearanceHost
{
    void SetAppearance(ReaderAppearanceSettings appearance);
}
