using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph;

public sealed partial class GraphDatabase
{
    /// <summary>Finds the minimum number of relationships between two visible nodes.</summary>
    /// <param name="session">The session whose transaction and statement snapshot the search uses.</param>
    /// <param name="search">The endpoints, direction, optional relationship type and limits.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The distance, or null when either endpoint is invisible or no path exists.
    /// A visible node to itself has distance zero, including with an unknown type filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The direction is invalid (CG05001); session ownership,
    /// state or transaction admission refuses the statement; limits are invalid (CG08021); or the
    /// physical-work (CG05004) or retained-memory (CG05020) budget is exhausted.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <remarks>Uses identity-based breadth-first search with O(visited vertices + frontier) state.
    /// It does not materialize a path. Type names compare ordinally; an unknown type admits no edges.
    /// Budget exhaustion or cancellation after admission fails the statement and aborts an explicit
    /// transaction. Invalid direction and a pre-canceled token are refused before admission.</remarks>
    public ValueTask<int?> FindShortestDistanceAsync(GraphDatabaseSession session, GraphShortestPathSearch search,
        CancellationToken cancellationToken = default)
    {
        ValidateShortestPathSearch(session, search);
        return RunAsync(session, async operation =>
        {
            var budget = session.StartQueryBudget(operation, search.Limits);
            var result = await GraphShortestPathFinder.SearchAsync(this, operation, search, budget, false, cancellationToken).ConfigureAwait(false);
            return result.Distance;
        }, cancellationToken);
    }

    /// <summary>Finds one shortest path and its actual distance between two visible nodes.</summary>
    /// <param name="session">The session whose transaction and statement snapshot the search uses.</param>
    /// <param name="search">The endpoints, direction, optional relationship type and limits.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The selected path and distance, or null when either endpoint is invisible or no path
    /// exists. A visible node to itself returns a singleton path of distance zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The direction is invalid (CG05001); session ownership,
    /// state or transaction admission refuses the statement; limits are invalid (CG08021); or the
    /// physical-work (CG05004) or retained-memory (CG05020) budget is exhausted.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (CG06012).</exception>
    /// <remarks>Equal-length ties select the lexicographically smallest relationship-identity sequence.
    /// Only the selected path is materialized. The search keeps one predecessor per visited identity,
    /// with O(visited vertices + frontier) search state, plus the returned entity payloads and bounded
    /// adjacency cursor buffers. Cancellation and budget failures never return partial paths.
    /// The same type, zero-length and transaction rules as FindShortestDistanceAsync apply.</remarks>
    public ValueTask<GraphShortestPathResult?> FindShortestPathAsync(GraphDatabaseSession session, GraphShortestPathSearch search,
        CancellationToken cancellationToken = default)
    {
        ValidateShortestPathSearch(session, search);
        return RunAsync(session, async operation =>
        {
            var budget = session.StartQueryBudget(operation, search.Limits);
            var result = await GraphShortestPathFinder.SearchAsync(this, operation, search, budget, true, cancellationToken).ConfigureAwait(false);
            return result.Path is { } path ? new GraphShortestPathResult(path) : null;
        }, cancellationToken);
    }

    private void ValidateShortestPathSearch(GraphDatabaseSession session, GraphShortestPathSearch search)
    {
        RequireOwnSession(session);
        if (!Enum.IsDefined(search.Direction))
        { throw DatabaseException.CreateGraphFailure("CG05001", ErrorMessages.ShortestPathDirectionInvalid); }
    }
}
