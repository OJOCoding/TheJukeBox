using Microsoft.EntityFrameworkCore;
using TheJukeBox.Domain;

namespace TheJukeBox.Infrastructure.Persistence;

/// <summary>
/// Writes playlist item positions without tripping the uniqueness constraint.
/// </summary>
/// <remarks>
/// <para>
/// Positions are contiguous 0-based integers with a unique index on
/// (<c>PlaylistId</c>, <c>Position</c>). The index is what makes "two items cannot share a
/// position" a rule the database enforces rather than an intention the code has to remember;
/// without it, a renumbering bug shows up as tracks quietly swapping places, which is close
/// to undebuggable from a report.
/// </para>
/// <para>
/// The cost is that a renumber cannot be written in one pass. EF knows about the index and
/// topologically sorts its UPDATEs to avoid transient duplicates, which is enough for a shift
/// -- 0,1,2 becoming 1,2,3 just has to be written back-to-front. It is not enough for a cycle:
/// two items trading places have no valid write order, and EF refuses the whole save with a
/// circular-dependency error before touching the database. Every genuine reorder contains at
/// least one cycle.
/// </para>
/// <para>
/// The fix is to park every row in the negative range first. Positions are non-negative in
/// every committed state, so the parking values cannot collide with anything, and the second
/// pass then writes the final values into a range it has entirely to itself. This also breaks
/// the cycle EF was complaining about, because neither pass on its own is a permutation.
/// </para>
/// <para>
/// Both halves are pinned by <c>PlaylistOrderingTests</c>: if a future EF learns to park rows
/// itself, that test fails and this can be deleted with evidence rather than hope.
/// </para>
/// </remarks>
internal static class PlaylistOrdering
{
    /// <summary>
    /// Assigns positions 0..n-1 in the order given.
    /// </summary>
    /// <remarks>
    /// Both passes are wrapped in one transaction, so a failure between them cannot leave the
    /// playlist committed with negative positions -- a state every reader would sort wrongly.
    /// A caller already inside a transaction gets that one, not a nested one.
    /// </remarks>
    public static async Task RenumberAsync(
        JukeBoxDbContext context,
        IReadOnlyList<PlaylistItem> ordered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ordered);

        if (ordered.Count == 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        var owned = context.Database.CurrentTransaction is null;

        var transaction = owned
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // Pass one: park below zero, keeping the requested order so the second pass is a
            // straight shift rather than another permutation.
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Position = -1 - index;
            }

            await context.SaveChangesAsync(cancellationToken);

            // Pass two: the real values, into a range nothing occupies.
            for (var index = 0; index < ordered.Count; index++)
            {
                ordered[index].Position = index;
            }

            await context.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
