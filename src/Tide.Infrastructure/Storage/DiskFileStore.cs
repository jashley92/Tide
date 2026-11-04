using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tide.Application.Abstractions;
using Tide.Application.Files;

namespace Tide.Infrastructure.Storage;

/// <summary>
/// Keeps uploaded files on disk under <see cref="FileStoreOptions.RootPath"/>, one file per id, fanned out into folders
/// by the id's first two characters. Files are written to a temporary name and moved into place, so a reader never
/// sees half a file. A relative root is under the content root (development only).
/// </summary>
public sealed class DiskFileStore : IFileStore
{
    private readonly string _root;

    public DiskFileStore(IOptions<FileStoreOptions> options, IHostEnvironment env)
    {
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Files:RootPath isn't set.");
        _root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task WriteAsync(Guid id, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await content.CopyToAsync(file, ct);
        File.Move(temp, path, overwrite: true);
    }

    public Stream? OpenRead(Guid id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null;
    }

    public void Delete(Guid id)
    {
        try { File.Delete(PathFor(id)); }
        catch (IOException) { /* in use or gone: an orphan file is harmless, a failed request isn't */ }
        catch (UnauthorizedAccessException) { }
    }

    // The id is a Guid, so the path can't leave the root.
    private string PathFor(Guid id)
    {
        var name = id.ToString("N");
        return Path.Combine(_root, name[..2], name + ".bin");
    }
}
