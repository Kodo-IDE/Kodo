// Licensed under the GNU GPL-v3.0
using System;

namespace Kodo.QuickActions;

public sealed class QuickActionExecution
{
    private QuickActionExecution(Guid id) => Id = id;

    public Guid Id { get; }

    public static QuickActionExecution Begin() => new(Guid.NewGuid());
}
