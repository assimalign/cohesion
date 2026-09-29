using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A gateway capable of reconciling several application models through one controller and observer instance.
/// </summary>
public interface IMultiModelApplicationGateway : IApplicationGateway
{
    /// <summary>Validates all member models without contacting the target platform.</summary>
    /// <param name="models">The models in application-set declaration order.</param>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> is <see langword="null"/> or contains a <see langword="null"/> model.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="models"/> is empty or the gateway options are invalid.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// A configured gateway duration or threshold is outside its supported range.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The model collection contains duplicate applications or invalid resource plans.
    /// </exception>
    void Validate(IReadOnlyList<IApplicationModel> models);

    /// <summary>Starts all member models through one gateway session.</summary>
    /// <param name="models">The models in application-set declaration order.</param>
    /// <param name="cancellationToken">Signals that startup should be abandoned.</param>
    /// <returns>A task that completes after every member has passed its readiness gates.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> is <see langword="null"/> or contains a <see langword="null"/> model.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="models"/> is empty or the gateway options are invalid.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// A configured gateway duration or threshold is outside its supported range.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The model collection is invalid or the gateway is supervising another session.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    Task StartAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a level-triggered reconcile pass over all member models.</summary>
    /// <param name="models">The models in application-set declaration order.</param>
    /// <param name="cancellationToken">Signals that reconciliation should be abandoned.</param>
    /// <returns>A task that completes after the shared reconcile pass.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> is <see langword="null"/> or contains a <see langword="null"/> model.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="models"/> is empty or the gateway options are invalid.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// A configured gateway duration or threshold is outside its supported range.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// The model collection is invalid or the gateway is supervising another session.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    Task ReconcileAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken = default);

    /// <summary>Destructively removes every member model in reverse application and dependency order.</summary>
    /// <param name="models">The models in application-set declaration order.</param>
    /// <param name="cancellationToken">Bounds teardown.</param>
    /// <returns>A task that completes after teardown.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="models"/> is <see langword="null"/> or contains a <see langword="null"/> model.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="models"/> is empty or the gateway options are invalid.
    /// </exception>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// A configured gateway duration or threshold is outside its supported range.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">The model collection is invalid.</exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    Task UninstallAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken = default);
}
