// Licensed under GPL-v3.0
using System;

namespace Kodo.Models;

public sealed class NewsItem
{
    public string Title     { get; init; } = string.Empty;
    public string Body      { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public bool HasTitle     => !string.IsNullOrWhiteSpace(Title);
    public bool HasBody      => !string.IsNullOrWhiteSpace(Body);
    public bool HasUpdatedAt => !string.IsNullOrWhiteSpace(UpdatedAt);
}