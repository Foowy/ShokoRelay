using System.Collections.Frozen;
using Newtonsoft.Json;
using Shoko.Abstractions.Metadata;
using Shoko.Abstractions.Metadata.Containers;
using Shoko.Abstractions.Metadata.Enums;

namespace ShokoRelay.Helpers;

#region Data Models

/// <summary>Represents a metadata tag item for Plex responses.</summary>
public sealed class TagItem
{
    /// <summary>The display string for the tag.</summary>
    [JsonProperty("tag")]
    public string Tag { get; init; } = "";
}

#endregion

/// <summary>Helper utilities for generating cast, crew and studio tag information suitable for Plex metadata responses.</summary>
public static class CastHelper
{
    #region Static Configuration

    // csharpier-ignore
    /// <summary>TMDB crew departments used to strip category prefixes from role names.</summary>
    private static readonly FrozenSet<string> s_tmdbDepartments = new[] {
        "Production", "Costume & Make-Up", "Lighting", "Actors", "Crew", "Directing", "Writing", "Editing", "Sound", "Visual Effects", "Camera", "Art"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    #endregion

    #region Cast and Crew Logic

    /// <summary>Return an array of objects representing cast (and optionally crew) for the specified media item. The crew portion is included based on the <c>CrewListings</c> setting.</summary>
    /// <param name="item">Item implementing <see cref="IWithCastAndCrew"/>.</param>
    /// <returns>An array of anonymous cast/crew objects suitable for Plex metadata responses.</returns>
    public static object[] GetCastAndCrew(IWithCastAndCrew item)
    {
        int order = 1;
        var allCast = item.Cast?.ToList() ?? [];
        var tmdbCast = allCast.Where(c => c.Creator?.ID.Source == MetadataSource.TMDB).ToList();
        var anidbCast = allCast.Where(c => c.Creator == null || c.Creator.ID.Source != MetadataSource.TMDB).ToList();

        var matchedTmdbEntries = new HashSet<ICast>();
        var finalCast = new List<object>();

        foreach (var c in anidbCast)
        {
            string? tag = GetName(c.Creator, c.Name);
            string? role = c.Character?.Title ?? c.Name;

            // Filter out non-person entries (characters, vehicles, mecha, etc.) where the actor name matches the role name.
            if (string.Equals(tag, role, StringComparison.Ordinal))
                continue;

            var match = FindMatchingTmdbCast(c, tmdbCast);
            var thumbImg = c.Creator?.PrimaryImage;

            if (match != null)
            {
                matchedTmdbEntries.Add(match);
                if (Settings.TmdbCastImages && match.Creator?.PrimaryImage != null)
                    thumbImg = match.Creator.PrimaryImage;
            }

            finalCast.Add(
                new
                {
                    order = order++,
                    tag,
                    role,
                    thumb = thumbImg != null ? ImageHelper.GetImageUrl(thumbImg, forceRemote: true) : null,
                }
            );
        }

        foreach (var c in tmdbCast)
        {
            if (matchedTmdbEntries.Contains(c))
                continue;

            string? tag = GetName(c.Creator, c.Name);
            string? role = c.Character?.Title ?? c.Name;

            if (string.Equals(tag, role, StringComparison.Ordinal))
                continue;

            finalCast.Add(
                new
                {
                    order = order++,
                    tag,
                    role,
                    thumb = c.Creator?.PrimaryImage is { } img ? ImageHelper.GetImageUrl(img, forceRemote: true) : null,
                }
            );
        }

        if (!Settings.CrewListings)
            return [.. finalCast];

        var allCrew = item.Crew?.ToList() ?? [];
        var tmdbCrew = allCrew.Where(c => c.Creator?.ID.Source == MetadataSource.TMDB).ToList();
        var anidbCrew = allCrew.Where(c => c.Creator == null || c.Creator.ID.Source != MetadataSource.TMDB).ToList();

        var matchedTmdbCrewEntries = new HashSet<ICrew>();
        var finalCrew = new List<object>();

        foreach (var c in anidbCrew)
        {
            string? tag = GetName(c.Creator, c.Name);
            string role = c.RoleType == CrewRoleType.Music ? "Composer" : CleanCrewRole(c.Name);

            if (string.Equals(tag, role, StringComparison.Ordinal))
                continue;

            var match = FindMatchingTmdbCrew(c, tmdbCrew);
            var thumbImg = c.Creator?.PrimaryImage;

            if (match != null)
            {
                matchedTmdbCrewEntries.Add(match);
                if (Settings.TmdbCastImages && match.Creator?.PrimaryImage != null)
                    thumbImg = match.Creator.PrimaryImage;
            }

            finalCrew.Add(
                new
                {
                    order = order++,
                    tag,
                    role,
                    thumb = thumbImg != null ? ImageHelper.GetImageUrl(thumbImg, forceRemote: true) : null,
                }
            );
        }

        foreach (var c in tmdbCrew)
        {
            if (matchedTmdbCrewEntries.Contains(c))
                continue;

            string? tag = GetName(c.Creator, c.Name);
            string role = c.RoleType == CrewRoleType.Music ? "Composer" : CleanCrewRole(c.Name);

            if (string.Equals(tag, role, StringComparison.Ordinal))
                continue;

            finalCrew.Add(
                new
                {
                    order = order++,
                    tag,
                    role,
                    thumb = c.Creator?.PrimaryImage is { } img ? ImageHelper.GetImageUrl(img, forceRemote: true) : null,
                }
            );
        }

        return [.. finalCast, .. finalCrew];
    }

    /// <summary>Return director credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <returns>An array of anonymous objects containing director names.</returns>
    public static object[] GetDirectors(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Director);

    /// <summary>Retrieve writing credits (series composers or source work) for the item.</summary>
    /// <param name="item">Source item.</param>
    /// <returns>An array of anonymous objects containing writer names.</returns>
    public static object[] GetWriters(IWithCastAndCrew item) =>
        DeduplicateCrew(item.Crew?.Where(c => c.RoleType is CrewRoleType.SeriesComposer or CrewRoleType.SourceWork))
            .Select(c => GetName(c.Creator, c.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (object)new { tag = name })
            .ToArray()
        ?? [];

    /// <summary>Retrieve producer credits for the given item.</summary>
    /// <param name="item">Item with cast/crew data.</param>
    /// <returns>An array of anonymous objects containing producer names.</returns>
    public static object[] GetProducers(IWithCastAndCrew item) => FilterCrew(item, CrewRoleType.Producer);

    #endregion

    #region Studio Logic

    /// <summary>Assemble an array of <see cref="TagItem"/> objects representing the series' studios.</summary>
    /// <param name="series">The Shoko series metadata.</param>
    /// <returns>An array of <see cref="TagItem"/> instances, one per distinct studio.</returns>
    public static TagItem[] GetStudioTags(ISeries series) =>
        series.Studios?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new TagItem { Tag = name }).ToArray() ?? [];

    #endregion

    #region Internal Helpers

    /// <summary>Resolves the creator's name with a fallback value.</summary>
    /// <param name="creator">The creator metadata object.</param>
    /// <param name="fallback">The fallback name.</param>
    /// <returns>The resolved name string.</returns>
    private static string? GetName(ICreator? creator, string? fallback) => creator?.Title ?? fallback;

    /// <summary>Strips department category prefixes from TMDB crew role names.</summary>
    /// <param name="role">The raw crew role string.</param>
    /// <returns>The cleaned role string.</returns>
    private static string CleanCrewRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "";
        int commaIdx = role.IndexOf(',');
        return commaIdx > 0 && s_tmdbDepartments.Contains(role[..commaIdx].Trim()) ? role[(commaIdx + 1)..].Trim() : role.Trim();
    }

    /// <summary>Filters the crew by a specific role type and returns a formatted array of tags.</summary>
    /// <param name="item">The media item containing cast and crew.</param>
    /// <param name="roleType">The role type to filter for.</param>
    /// <returns>An array of anonymous objects containing crew tags.</returns>
    private static object[] FilterCrew(IWithCastAndCrew item, CrewRoleType roleType) =>
        DeduplicateCrew(item.Crew?.Where(c => c.RoleType == roleType))
            .Select(c => GetName(c.Creator, c.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (object)new { tag = name })
            .ToArray()
        ?? [];

    /// <summary>Deduplicates a collection of crew members per role type, preferring AniDB entries over matching TMDB entries.</summary>
    /// <param name="crewItems">The raw collection of crew items.</param>
    /// <returns>A deduplicated collection of crew items.</returns>
    private static IEnumerable<ICrew> DeduplicateCrew(IEnumerable<ICrew>? crewItems)
    {
        if (crewItems == null)
            return [];

        var grouped = crewItems.GroupBy(c => c.RoleType);
        var result = new List<ICrew>();

        foreach (var group in grouped)
        {
            var allCrew = group.ToList();
            var tmdbCrew = allCrew.Where(c => c.Creator?.ID.Source == MetadataSource.TMDB).ToList();
            var anidbCrew = allCrew.Where(c => c.Creator == null || c.Creator.ID.Source != MetadataSource.TMDB).ToList();

            var matchedTmdbEntries = new HashSet<ICrew>();

            foreach (var c in anidbCrew)
            {
                result.Add(c);
                var match = FindMatchingTmdbCrew(c, tmdbCrew);
                if (match != null)
                    matchedTmdbEntries.Add(match);
            }

            foreach (var c in tmdbCrew)
            {
                if (!matchedTmdbEntries.Contains(c))
                    result.Add(c);
            }
        }

        return result;
    }

    /// <summary>Finds a matching TMDB cast entry for an AniDB cast entry based on name variations.</summary>
    /// <param name="anidbCast">The AniDB cast entry.</param>
    /// <param name="tmdbCastList">The list of available TMDB cast entries.</param>
    /// <returns>The matched TMDB cast entry or null.</returns>
    private static ICast? FindMatchingTmdbCast(ICast anidbCast, IReadOnlyList<ICast> tmdbCastList)
    {
        string? originalName = anidbCast.Creator?.OriginalTitle?.Value;
        string? name = anidbCast.Creator?.Title ?? anidbCast.Name;
        string? flippedName = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                flippedName = $"{parts[1]} {parts[0]}";
        }

        if (!string.IsNullOrWhiteSpace(originalName))
            if (tmdbCastList.FirstOrDefault(t => string.Equals(t.Creator?.OriginalTitle?.Value, originalName, StringComparison.OrdinalIgnoreCase)) is { } match1)
                return match1;

        if (!string.IsNullOrWhiteSpace(name))
            if (tmdbCastList.FirstOrDefault(t => string.Equals(t.Creator?.Title ?? t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match2)
                return match2;

        if (!string.IsNullOrWhiteSpace(flippedName))
            if (tmdbCastList.FirstOrDefault(t => string.Equals(t.Creator?.Title ?? t.Name, flippedName, StringComparison.OrdinalIgnoreCase)) is { } match3)
                return match3;

        if (anidbCast.Creator?.Titles is { Count: > 0 } altNames)
            foreach (var alt in altNames)
            {
                if (string.IsNullOrWhiteSpace(alt.Value))
                    continue;
                if (
                    tmdbCastList.FirstOrDefault(t =>
                        string.Equals(t.Creator?.Title ?? t.Name, alt.Value, StringComparison.OrdinalIgnoreCase)
                        || (t.Creator?.Titles != null && t.Creator.Titles.Any(a => string.Equals(a.Value, alt.Value, StringComparison.OrdinalIgnoreCase)))
                    ) is
                    { } match4
                )
                    return match4;
            }

        return null;
    }

    /// <summary>Finds a matching TMDB crew entry for an AniDB crew entry based on role and name variations.</summary>
    /// <param name="anidbCrew">The AniDB crew entry.</param>
    /// <param name="tmdbCrewList">The list of available TMDB crew entries.</param>
    /// <returns>The matched TMDB crew entry or null.</returns>
    private static ICrew? FindMatchingTmdbCrew(ICrew anidbCrew, IReadOnlyList<ICrew> tmdbCrewList)
    {
        var sameRoleTmdb = tmdbCrewList.Where(t => t.RoleType == anidbCrew.RoleType).ToList();
        if (sameRoleTmdb.Count == 0)
            return null;

        string? originalName = anidbCrew.Creator?.OriginalTitle?.Value;
        string? name = anidbCrew.Creator?.Title ?? anidbCrew.Name;
        string? flippedName = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                flippedName = $"{parts[1]} {parts[0]}";
        }

        if (!string.IsNullOrWhiteSpace(originalName))
            if (sameRoleTmdb.FirstOrDefault(t => string.Equals(t.Creator?.OriginalTitle?.Value, originalName, StringComparison.OrdinalIgnoreCase)) is { } match1)
                return match1;

        if (!string.IsNullOrWhiteSpace(name))
            if (sameRoleTmdb.FirstOrDefault(t => string.Equals(t.Creator?.Title ?? t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } match2)
                return match2;

        if (!string.IsNullOrWhiteSpace(flippedName))
            if (sameRoleTmdb.FirstOrDefault(t => string.Equals(t.Creator?.Title ?? t.Name, flippedName, StringComparison.OrdinalIgnoreCase)) is { } match3)
                return match3;

        if (anidbCrew.Creator?.Titles is { Count: > 0 } altNames)
            foreach (var alt in altNames)
            {
                if (string.IsNullOrWhiteSpace(alt.Value))
                    continue;
                if (
                    sameRoleTmdb.FirstOrDefault(t =>
                        string.Equals(t.Creator?.Title ?? t.Name, alt.Value, StringComparison.OrdinalIgnoreCase)
                        || (t.Creator?.Titles != null && t.Creator.Titles.Any(a => string.Equals(a.Value, alt.Value, StringComparison.OrdinalIgnoreCase)))
                    ) is
                    { } match4
                )
                    return match4;
            }

        return null;
    }

    #endregion
}
