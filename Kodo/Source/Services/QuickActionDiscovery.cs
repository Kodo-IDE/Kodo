// Licensed under the GNU GPL-v3.0
using Kodo.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kodo.QuickActions;

public sealed record QuickActionCommandRegistration(
    string Id,
    string Title,
    string Category,
    string Icon,
    Func<bool>? IsAvailable,
    Func<Task> Execute);

public sealed record QuickActionDiscoveryContext(
    string FileType,
    bool HasDocument,
    bool IsFolderOpen,
    bool IsTerminalSupported);

public sealed record DiscoveredQuickAction(QuickActionItem Item, QuickActionCommandRegistration Command);

public static class QuickActionDiscovery
{
    public const double MaximumCommandRelevance = 58;

    private const int MaximumIdentifierLength = 120;
    private const int MaximumCategorySlugLength = 40;

    private static readonly string[] MaintenanceIdPrefixes =
    [
        "cmd.encoding-",
        "cmd.line-ending-",
        "cmd.indentation-",
        "cmd.change-",
    ];

    public static IReadOnlyList<DiscoveredQuickAction> Discover(
        IEnumerable<QuickActionCommandRegistration> commands,
        QuickActionDiscoveryContext context,
        IReadOnlySet<string>? coveredIds = null,
        Action<Exception>? onAvailabilityError = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var discovered = new List<DiscoveredQuickAction>();
        foreach (var command in commands)
        {
            if (!IsDiscoverable(command, coveredIds)) continue;
            if (!IsAvailable(command, onAvailabilityError)) continue;
            if (!seen.Add(command.Id.Trim())) continue;
            discovered.Add(new DiscoveredQuickAction(CreateItem(command, context), command));
        }

        return discovered;
    }

    public static bool IsDiscoverable(QuickActionCommandRegistration command, IReadOnlySet<string>? coveredIds = null)
    {
        if (string.IsNullOrWhiteSpace(command.Id) || string.IsNullOrWhiteSpace(command.Title)) return false;

        var id = command.Id.Trim();
        if (id.Length > MaximumIdentifierLength || id.Any(char.IsControl)) return false;
        if (coveredIds is not null && coveredIds.Contains(id)) return false;
        return !MaintenanceIdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAvailable(QuickActionCommandRegistration command, Action<Exception>? onError)
    {
        if (command.IsAvailable is null) return true;
        try
        {
            return command.IsAvailable();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return false;
        }
    }

    public static string CategorySlug(string? category)
    {
        var primary = (category ?? string.Empty).Split('·')[0].Trim().ToLowerInvariant();
        var builder = new StringBuilder(primary.Length);
        foreach (var character in primary)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) slug = "command";
        return slug.Length <= MaximumCategorySlugLength ? slug : slug[..MaximumCategorySlugLength].TrimEnd('-');
    }

    private static QuickActionItem CreateItem(QuickActionCommandRegistration command, QuickActionDiscoveryContext context)
    {
        var slug = CategorySlug(command.Category);
        var fileType = string.IsNullOrWhiteSpace(context.FileType) ? "unknown" : context.FileType;
        return new QuickActionItem
        {
            Id = command.Id.Trim(),
            Title = command.Title.Trim(),
            Icon = command.Icon,
            ContextKey = $"command|{slug}|{fileType}",
            BaseRelevance = RelevanceFor(slug),
            LearningFeatures =
            [
                $"command-category:{slug}",
                $"file:{fileType}",
                $"project:{context.IsFolderOpen}",
                $"terminal:{context.IsTerminalSupported}",
                $"document:{context.HasDocument}",
            ],
        };
    }

    private static double RelevanceFor(string slug) => slug switch
    {
        "terminal" => MaximumCommandRelevance,
        "run" or "build" => 57,
        "editor" => 56,
        "search" => 54,
        "navigation" or "git" => 50,
        "file" => 48,
        "view" => 46,
        "appearance" or "preferences" => 44,
        "help" => 42,
        _ => 45,
    };
}
