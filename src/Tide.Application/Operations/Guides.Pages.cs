using Tide.Domain.Enums;

namespace Tide.Application.Operations;

public static partial class Guides
{
    // One guide per page, in Help's order. Each step points at a real control on its page.
    private static readonly Guide[] PageGuides = [];
}
