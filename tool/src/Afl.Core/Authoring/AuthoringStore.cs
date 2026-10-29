using System.Security.Cryptography;
using Afl.Core.Constants;
using Afl.Core.Exceptions;
using Afl.Core.Helpers;
using Afl.Core.Storage;

namespace Afl.Core.Authoring;

/// <summary>
///     The AI authoring helper's sessions: <c>~/.afl/authoring/&lt;id&gt;.json</c>, each with a work folder of the same
///     name in <see cref="AflPaths.AuthoringWorkspacesDirectory" /> holding its draft and what its agent reads. Only the
///     records are watched, so the agent writing its draft doesn't count as a change.
/// </summary>
/// <param name="paths">The afl paths.</param>
public class AuthoringStore(AflPaths paths) : AbstractJsonCollectionStore<AuthoringSession, string>(paths.AuthoringDirectory)
{
    /// <summary>
    ///     Gets the afl paths.
    /// </summary>
    public AflPaths Paths { get; } = paths;

    /// <inheritdoc />
    public override string GetKey(AuthoringSession item)
    {
        return item.Id;
    }

    /// <summary>
    ///     Gets a session by id, or throws.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The session.</returns>
    /// <exception cref="AflException">There's no such session.</exception>
    public AuthoringSession GetRequired(string id)
    {
        return this.Get(id) ?? throw new AflException(AflErrorKind.NotFound, $"No AI draft '{id}'. See afl author-list.");
    }

    /// <summary>
    ///     Lists the sessions, newest first.
    /// </summary>
    /// <param name="kind">Only sessions drafting this kind of document, or null for every kind.</param>
    /// <returns>The sessions.</returns>
    public IReadOnlyList<AuthoringSession> List(string? kind = null)
    {
        return this.All()
            .Where(s => kind is null || string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    /// <summary>
    ///     Returns a session's folder, where its run works.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <returns>The folder.</returns>
    public string FolderFor(string id)
    {
        return Path.Combine(this.Paths.AuthoringWorkspacesDirectory, id);
    }

    /// <summary>
    ///     Returns a session's draft file.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <returns>The file.</returns>
    public string DraftPathFor(string id)
    {
        return Path.Combine(this.FolderFor(id), AuthoringConstants.DraftFileName);
    }

    /// <summary>
    ///     Picks an id no session has yet.
    /// </summary>
    /// <returns>The id.</returns>
    public string NewId()
    {
        while (true)
        {
            var id = $"{DateTime.Now:yyMMdd}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
            if (this.Get(id) is null && !System.IO.Directory.Exists(this.FolderFor(id)))
            {
                return id;
            }
        }
    }

    /// <summary>
    ///     Deletes a session's record and its work folder.
    /// </summary>
    /// <param name="id">The session's id.</param>
    /// <returns>True if the record existed.</returns>
    public bool DeleteWithFolder(string id)
    {
        var folder = this.FolderFor(id);
        if (System.IO.Directory.Exists(folder))
        {
            System.IO.Directory.Delete(folder, true);
        }

        return this.Delete(id);
    }
}
