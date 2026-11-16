namespace Jellyfin.Plugin.Edl.Tests.Contract;

/// <summary>
/// A fresh directory under the system temp path, deleted with its contents on dispose.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("jfedl-contract-").FullName;
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
