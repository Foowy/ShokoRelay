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
        var finalRoles = new List<object>();

        void ProcessRoles<T>(IEnumerable<T>? roles, Func<T, ICreator?> getCreator, Func<T, string?> getName, Func<T, string?> getRole, Func<T, T, bool>? tmdbFilter = null)
        {
            var allRoles = roles?.ToList() ?? [];
            var tmdbRoles = allRoles.Where(c => getCreator(c)?.ID.Source == MetadataSource.TMDB).ToList();
            var anidbRoles = allRoles.Where(c => getCreator(c) == null || getCreator(c)!.ID.Source != MetadataSource.TMDB).ToList();
            var matchedTmdb = new HashSet<T>();

            foreach (var c in anidbRoles)
            {
                string? tag = GetName(getCreator(c), getName(c));
                string? role = getRole(c);

                // Filter out non-person entries (characters, vehicles, mecha, etc.) where the actor name matches the role name.
                if (string.Equals(tag, role, StringComparison.Ordinal))
                    continue;

                var candidates = tmdbFilter != null ? tmdbRoles.Where(t => tmdbFilter(c, t)).ToList() : tmdbRoles;
                var match = FindMatchingTmdbEntry(getCreator(c), getName(c) ?? "", candidates, getCreator, getName);
                var thumbImg = getCreator(c)?.PrimaryImage;

                if (match != null)
                {
                    matchedTmdb.Add(match);
                    if (Settings.TmdbCastImages && getCreator(match)?.PrimaryImage != null)
                        thumbImg = getCreator(match)!.PrimaryImage;
                }

                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag,
                        role,
                        thumb = thumbImg != null ? ImageHelper.GetImageUrl(thumbImg, forceRemote: true) : null,
                    }
                );
            }

            foreach (var c in tmdbRoles)
            {
                if (matchedTmdb.Contains(c))
                    continue;

                string? tag = GetName(getCreator(c), getName(c));
                string? role = getRole(c);

                if (string.Equals(tag, role, StringComparison.Ordinal))
                    continue;

                finalRoles.Add(
                    new
                    {
                        order = order++,
                        tag,
                        role,
                        thumb = getCreator(c)?.PrimaryImage is { } img ? ImageHelper.GetImageUrl(img, forceRemote: true) : null,
                    }
                );
            }
        }

        ProcessRoles(item.Cast, c => c.Creator, c => c.Name, c => c.Character?.Name ?? c.Name);

        if (Settings.CrewListings)
            ProcessRoles(item.Crew, c => c.Creator, c => c.Name, c => c.RoleType == CrewRoleType.Music ? "Composer" : CleanCrewRole(c.Name), (a, b) => a.RoleType == b.RoleType);

        return [.. finalRoles];
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
    private static string? GetName(ICreator? creator, string? fallback) => creator?.Name ?? fallback;

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
                var match = FindMatchingTmdbEntry(c.Creator, c.Name ?? "", tmdbCrew, t => t.Creator, t => t.Name);
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

    /// <summary>Finds a matching TMDB entry for an AniDB entry based on name variations.</summary>
    /// <param name="anidbCreator">The AniDB creator metadata object.</param>
    /// <param name="anidbName">The fallback AniDB entry name.</param>
    /// <param name="tmdbList">The list of available TMDB entries.</param>
    /// <param name="getCreator">Function to extract the creator object from an entry.</param>
    /// <param name="getName">Function to extract the name from an entry.</param>
    /// <returns>The matched TMDB entry or null.</returns>
    private static T? FindMatchingTmdbEntry<T>(ICreator? anidbCreator, string anidbName, IReadOnlyList<T> tmdbList, Func<T, ICreator?> getCreator, Func<T, string?> getName)
    {
        string? originalNameNoSpace = anidbCreator?.OriginalName?.Replace(" ", "", StringComparison.Ordinal);
        string name = anidbCreator?.Name ?? anidbName;
        string? flippedName = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                flippedName = $"{parts[1]} {parts[0]}";
        }

        // OriginalName matches OriginalName (whitespace stripped)
        if (!string.IsNullOrWhiteSpace(originalNameNoSpace))
        {
            var match = tmdbList.FirstOrDefault(t => getCreator(t)?.OriginalName?.Replace(" ", "", StringComparison.Ordinal) == originalNameNoSpace);
            if (match != null)
                return match;

            // OriginalName matches TMDB AlternativeNames (whitespace stripped)
            match = tmdbList.FirstOrDefault(t =>
                getCreator(t)?.AlternativeNames?.Any(a => !string.IsNullOrWhiteSpace(a.Value) && a.Value.Replace(" ", "", StringComparison.Ordinal) == originalNameNoSpace) == true
            );
            if (match != null)
                return match;
        }

        // Name matches Name (romanised)
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = tmdbList.FirstOrDefault(t => string.Equals(getCreator(t)?.Name ?? getName(t), name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // Flipped Name matches Name (romanised)
        if (!string.IsNullOrWhiteSpace(flippedName))
        {
            var match = tmdbList.FirstOrDefault(t => string.Equals(getCreator(t)?.Name ?? getName(t), flippedName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // AniDB AlternativeNames against TMDB Name and TMDB AlternativeNames
        if (anidbCreator?.AlternativeNames is { Count: > 0 } altNames)
        {
            foreach (var t in tmdbList)
            {
                var tCreator = getCreator(t);
                string tName = tCreator?.Name ?? getName(t) ?? "";

                foreach (var alt in altNames)
                {
                    if (string.IsNullOrWhiteSpace(alt.Value))
                        continue;
                    if (
                        string.Equals(tName, alt.Value, StringComparison.OrdinalIgnoreCase)
                        || (tCreator?.AlternativeNames?.Any(a => string.Equals(a.Value, alt.Value, StringComparison.OrdinalIgnoreCase)) == true)
                    )
                        return t;
                }
            }
        }

        return default;
    }

    #endregion
}
