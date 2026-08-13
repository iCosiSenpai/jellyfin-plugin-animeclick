using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeClick.Plugin.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AnimeClick.Plugin.Tasks;

/// <summary>
/// Fills in every episode, film and series synopsis the audit can still act on, in bounded batches,
/// until nothing actionable is left.
/// <para>
/// The configuration page can only queue one batch of a hundred per click, then the administrator
/// has to wait, analyse again and click again — four or five times over on a real library. That is
/// bookkeeping a task can do on its own, so it does: each round asks the audit what is still
/// actionable, queues a batch spread across different series, waits for those items to be processed
/// and moves on. Items whose synopsis is waiting on an AI translation are recorded as such and are
/// not queued again: the translation applies itself when the model answers.
/// </para>
/// </summary>
public class AnimeClickRepairSynopsesTask : IScheduledTask
{
    /// <summary>
    /// Upper bound on the rounds of one execution. With a hundred items per round this is two
    /// thousand repairs, far past any single library's backlog, and it guarantees the task ends
    /// even if a source were to start answering inconsistently.
    /// </summary>
    private const int MaximumRounds = 20;

    /// <summary>How long one round waits for its queued items to be processed.</summary>
    private static readonly TimeSpan RoundTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly AnimeClickLibraryQualityService _qualityService;
    private readonly AnimeClickRepairLedger _repairLedger;
    private readonly ILogger<AnimeClickRepairSynopsesTask> _logger;

    public AnimeClickRepairSynopsesTask(
        AnimeClickLibraryQualityService qualityService,
        AnimeClickRepairLedger repairLedger,
        ILogger<AnimeClickRepairSynopsesTask> logger)
    {
        _qualityService = qualityService;
        _repairLedger = repairLedger;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AnimeClick: completa le sinossi mancanti";

    /// <inheritdoc />
    public string Key => "AnimeClickRepairMissingSynopses";

    /// <inheritdoc />
    public string Description =>
        "Completa le sinossi in inglese o mancanti procedendo a lotti finché non resta niente su cui "
        + "agire, senza sovrascrivere testi italiani né campi bloccati. Ogni lotto è distribuito fra "
        + "serie diverse; gli elementi in attesa di una traduzione AI e quelli per cui nessuna fonte "
        + "ha la sinossi non vengono riaccodati.";

    /// <inheritdoc />
    public string Category => "AnimeClick";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(7).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var report = await _qualityService.AuditAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(2);

        var initialActionable = report.RepairableCount;
        if (initialActionable == 0)
        {
            _logger.LogInformation(
                "AnimeClick: nessuna sinossi da completare (in traduzione={Waiting} senza fonte={NoSource})",
                report.WaitingTranslationCount,
                report.NoSourceCount);
            progress.Report(100);
            return;
        }

        var applied = 0;
        var waiting = 0;
        var withoutSource = 0;
        var queuedTotal = 0;
        var rounds = 0;

        while (rounds < MaximumRounds && !cancellationToken.IsCancellationRequested)
        {
            var batch = AnimeClickLibraryQualityService.SelectRepairBatch(report);
            if (batch.Count == 0)
            {
                break;
            }

            var roundStart = DateTimeOffset.UtcNow;
            var result = _qualityService.QueueRepair(batch, force: false);
            if (result.QueuedCount == 0)
            {
                // Nothing was accepted: the library moved under us, and another round would only
                // repeat the same refusal.
                break;
            }

            queuedTotal += result.QueuedCount;
            rounds++;
            await WaitForRoundAsync(batch, roundStart, cancellationToken).ConfigureAwait(false);

            var outcomes = CountOutcomes(batch, roundStart);
            applied += outcomes.Applied;
            waiting += outcomes.Waiting;
            withoutSource += outcomes.NoSource;
            _logger.LogInformation(
                "AnimeClick: lotto {Round} completato: accodati={Queued} applicati={Applied} in traduzione={Waiting} senza fonte={NoSource}",
                rounds.ToString(CultureInfo.InvariantCulture),
                result.QueuedCount,
                outcomes.Applied,
                outcomes.Waiting,
                outcomes.NoSource);

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            report = await _qualityService.AuditAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(ComputeProgress(initialActionable, report.RepairableCount));
        }

        _logger.LogInformation(
            "AnimeClick: completamento sinossi terminato: lotti={Rounds} accodati={Queued} applicati={Applied} in traduzione={Waiting} senza fonte={NoSource} ancora azionabili={Remaining}",
            rounds.ToString(CultureInfo.InvariantCulture),
            queuedTotal,
            applied,
            waiting,
            withoutSource,
            report.RepairableCount);
        progress.Report(100);
    }

    /// <summary>
    /// Progress from what the audit still considers actionable. Held below 100 while work remains so
    /// a finished bar always means a finished task.
    /// </summary>
    internal static double ComputeProgress(int initialActionable, int remainingActionable)
    {
        if (initialActionable <= 0)
        {
            return 100;
        }

        var done = Math.Clamp(initialActionable - remainingActionable, 0, initialActionable);
        var percentage = (double)done / initialActionable * 100;
        return remainingActionable > 0 ? Math.Clamp(percentage, 2, 99) : 100;
    }

    /// <summary>
    /// Waits until every item of the batch carries a recorded outcome, which is what tells the next
    /// round what is left. A queue that stalls is not waited on forever: the round gives up and the
    /// next audit reports the truth either way.
    /// </summary>
    private async Task WaitForRoundAsync(
        IReadOnlyCollection<string> batch,
        DateTimeOffset roundStart,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + RoundTimeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (CountOutcomes(batch, roundStart).Recorded >= batch.Count)
            {
                return;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private RoundOutcomes CountOutcomes(IReadOnlyCollection<string> batch, DateTimeOffset roundStart)
    {
        var recorded = 0;
        var applied = 0;
        var waiting = 0;
        var noSource = 0;
        foreach (var id in batch)
        {
            if (!Guid.TryParse(id, out var itemId)
                || !_repairLedger.TryGetAttempt(itemId, out var attempt)
                || attempt.AttemptedAt < roundStart)
            {
                continue;
            }

            recorded++;
            if (string.Equals(attempt.Outcome, nameof(AnimeClickRepairOutcome.Applied), StringComparison.Ordinal))
            {
                applied++;
            }
            else if (string.Equals(
                         attempt.Outcome,
                         nameof(AnimeClickRepairOutcome.WaitingTranslation),
                         StringComparison.Ordinal))
            {
                waiting++;
            }
            else if (string.Equals(
                         attempt.Outcome,
                         nameof(AnimeClickRepairOutcome.NoSource),
                         StringComparison.Ordinal))
            {
                noSource++;
            }
        }

        return new RoundOutcomes(recorded, applied, waiting, noSource);
    }

    private sealed record RoundOutcomes(int Recorded, int Applied, int Waiting, int NoSource);
}
