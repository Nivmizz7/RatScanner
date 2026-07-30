#nullable enable

using RatScanner;
using RatScanner.FetchModels.TarkovTracker;
using RatScanner.TarkovDev;
using Xunit;
using Task = RatScanner.TarkovDev.Task;

namespace RatScanner.Tests;

/// <summary>
/// Regression tests for applicability-aware quest need classification:
/// conditional/future requirements must never inflate "needed now" counts.
/// </summary>
public class QuestNeedClassifierTests
{
    private const string ItemId = "item-m4";

    private static Item TestItem(string type = "gun") =>
        new()
        {
            Id = ItemId,
            Name = "Test Rifle",
            Types = [type, "barter"],
        };

    private static Task MakeTask(
        string id,
        int minLevel = 0,
        string? faction = null,
        TaskPrerequisite[]? prerequisites = null,
        TaskTraderRequirement[]? traderRequirements = null,
        bool unmodeled = false,
        TaskObjective[]? objectives = null
    ) =>
        new()
        {
            Id = id,
            Name = id,
            MinPlayerLevel = minLevel,
            FactionName = faction,
            TaskRequirements = prerequisites,
            TraderRequirements = traderRequirements,
            HasUnmodeledRequirements = unmodeled,
            Objectives = objectives ?? FirGivePair(id),
        };

    private static TaskObjective[] FirGivePair(string taskId, int count = 1) =>
        [
            new()
            {
                Id = $"{taskId}-find",
                Type = "findItem",
                Count = count,
                FoundInRaid = true,
                ItemIds = [ItemId],
            },
            new()
            {
                Id = $"{taskId}-give",
                Type = "giveItem",
                Count = count,
                FoundInRaid = true,
                ItemIds = [ItemId],
            },
        ];

    private static UserProgress ProgressAtLevel(int? level) =>
        new()
        {
            UserId = "u",
            PlayerLevel = level,
            PmcFaction = "USEC",
        };

    private static QuestNeedReport ClassifySingle(Task task, UserProgress progress, bool showNonFir = true) =>
        QuestNeedClassifier.Classify(TestItem(), [task], progress, showNonFir);

    [Fact]
    public void Open_task_with_progress_counts_as_active()
    {
        Task task = MakeTask("punisher-5");
        UserProgress progress = ProgressAtLevel(25);
        // Started but unfinished: tracker records objective progress.
        progress.TaskObjectives.Add(new Progress { Id = "punisher-5-find", Count = 0 });

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(1, report.ActiveNow);
        Assert.Equal(1, report.ActiveNowFir);
        Assert.Equal(0, report.AvailableNow);
        Assert.Equal(0, report.FutureKnown);
        Assert.Equal(0, report.ConditionalUnknown);
    }

    [Fact]
    public void Open_task_without_progress_counts_as_available_not_active()
    {
        Task task = MakeTask("punisher-5");
        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(25));

        Assert.Equal(0, report.ActiveNow);
        Assert.Equal(1, report.AvailableNow);
        Assert.Equal(1, report.AvailableNowFir);
    }

    [Fact]
    public void Level_locked_task_is_future_known_and_never_active()
    {
        Task task = MakeTask("punisher-5", minLevel: 20);
        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(12));

        Assert.Equal(0, report.ActiveNow);
        Assert.Equal(0, report.AvailableNow);
        Assert.Equal(1, report.FutureKnown);
        Assert.Equal(20, report.UnlockLevel);
    }

    [Fact]
    public void Known_level_unlocks_level_gated_task()
    {
        Task task = MakeTask("punisher-5", minLevel: 20);
        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(42));

        Assert.Equal(1, report.AvailableNow);
        Assert.Equal(0, report.FutureKnown);
    }

    [Fact]
    public void Unknown_level_makes_level_gate_conditional_not_active()
    {
        // Old tokens may omit playerLevel: uncertainty must be preserved.
        Task task = MakeTask("punisher-5", minLevel: 20);
        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(null));

        Assert.Equal(0, report.ActiveNow);
        Assert.Equal(0, report.FutureKnown);
        Assert.Equal(1, report.ConditionalUnknown);
    }

    [Fact]
    public void Incomplete_prerequisite_locks_task_as_future_known()
    {
        Task prereq = MakeTask("punisher-4");
        Task task = MakeTask(
            "punisher-5",
            prerequisites: [new TaskPrerequisite { TaskId = "punisher-4", Statuses = ["complete"] }]
        );

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [prereq, task], ProgressAtLevel(42), true);

        // The prereq itself contributes its own (applicable) need.
        Assert.Equal(1, report.AvailableNow);
        Assert.Equal(1, report.FutureKnown);
        Assert.Equal(0, report.ActiveNow);
    }

    [Fact]
    public void Completed_prerequisite_unlocks_task()
    {
        Task prereq = MakeTask("punisher-4");
        Task task = MakeTask(
            "punisher-5",
            prerequisites: [new TaskPrerequisite { TaskId = "punisher-4", Statuses = ["complete"] }]
        );
        UserProgress progress = ProgressAtLevel(42);
        progress.Tasks.Add(new Progress { Id = "punisher-4", Complete = true });

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [prereq, task], progress, true);

        Assert.Equal(1, report.AvailableNow);
        Assert.Equal(0, report.FutureKnown);
    }

    [Fact]
    public void Failed_prerequisite_with_complete_only_requirement_is_not_future()
    {
        Task prereq = MakeTask("punisher-4");
        Task task = MakeTask(
            "punisher-5",
            prerequisites: [new TaskPrerequisite { TaskId = "punisher-4", Statuses = ["complete"] }]
        );
        UserProgress progress = ProgressAtLevel(42);
        progress.Tasks.Add(new Progress { Id = "punisher-4", Failed = true });

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [prereq, task], progress, true);

        // The failed prereq ended in a state that cannot satisfy the requirement
        // → the dependent task must not show up as a future or active need.
        Assert.Equal(0, report.GrandTotal);
    }

    [Fact]
    public void Trader_reputation_gate_stays_conditional_unknown()
    {
        // The Fence "Compensation for Damage" series: reputation <= -1 is a
        // condition RatScanner cannot evaluate with tracker data.
        Task task = MakeTask(
            "collection",
            traderRequirements:
            [
                new TaskTraderRequirement
                {
                    RequirementType = "reputation",
                    CompareMethod = "<=",
                    Value = -1,
                    TraderId = "579dc571d53a0658a154fbec",
                },
            ]
        );

        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(42));

        Assert.Equal(0, report.ActiveNow);
        Assert.Equal(0, report.AvailableNow);
        Assert.Equal(0, report.FutureKnown);
        Assert.Equal(1, report.ConditionalUnknown);
        Assert.Equal(1, report.ConditionalUnknownFir);
    }

    [Fact]
    public void Observed_progress_outranks_unverifiable_unlock_gates()
    {
        Task task = MakeTask(
            "collection",
            traderRequirements:
            [
                new TaskTraderRequirement
                {
                    RequirementType = "reputation",
                    CompareMethod = "<=",
                    Value = -1,
                },
            ],
            unmodeled: true
        );
        UserProgress progress = ProgressAtLevel(42);
        progress.TaskObjectives.Add(new Progress { Id = "collection-find", Count = 0 });

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(1, report.ActiveNow);
        Assert.Equal(0, report.ConditionalUnknown);
        Assert.Equal(1, report.CurrentTotal);
    }

    [Fact]
    public void Completed_active_only_prerequisite_is_conditional_not_impossible()
    {
        Task prerequisite = MakeTask("prerequisite");
        Task dependent = MakeTask(
            "dependent",
            prerequisites: [new TaskPrerequisite { TaskId = "prerequisite", Statuses = ["active"] }]
        );
        UserProgress progress = ProgressAtLevel(42);
        progress.Tasks.Add(new Progress { Id = "prerequisite", Complete = true });

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [prerequisite, dependent], progress, true);

        Assert.Equal(1, report.ConditionalUnknown);
        Assert.Equal(0, report.FutureKnown);
    }

    [Fact]
    public void Dependent_progress_outranks_completed_active_only_prerequisite()
    {
        Task prerequisite = MakeTask("prerequisite");
        Task dependent = MakeTask(
            "dependent",
            prerequisites: [new TaskPrerequisite { TaskId = "prerequisite", Statuses = ["active"] }]
        );
        UserProgress progress = ProgressAtLevel(42);
        progress.Tasks.Add(new Progress { Id = "prerequisite", Complete = true });
        progress.TaskObjectives.Add(new Progress { Id = "dependent-find", Count = 0 });

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [prerequisite, dependent], progress, true);

        Assert.Equal(1, report.ActiveNow);
        Assert.Equal(0, report.ConditionalUnknown);
    }

    [Fact]
    public void Faction_mismatch_makes_task_inapplicable()
    {
        Task task = MakeTask("usec-only", faction: "BEAR");
        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(42)); // USEC player

        Assert.Equal(0, report.GrandTotal);
    }

    [Fact]
    public void Faction_match_is_known_true_and_stays_applicable()
    {
        Task task = MakeTask("bear-only", faction: "BEAR");
        UserProgress progress = ProgressAtLevel(42);
        progress.PmcFaction = "BEAR";

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(1, report.AvailableNow);
        Assert.Equal(0, report.ConditionalUnknown);
    }

    [Fact]
    public void Unknown_faction_makes_faction_gate_conditional()
    {
        Task task = MakeTask("usec-only", faction: "USEC");
        UserProgress progress = ProgressAtLevel(42);
        progress.PmcFaction = null;

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(1, report.ConditionalUnknown);
        Assert.Equal(0, report.ActiveNow);
    }

    [Fact]
    public void Unmodeled_requirements_make_task_conditional()
    {
        Task task = MakeTask("lightkeeper", unmodeled: true);
        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(42));

        Assert.Equal(1, report.ConditionalUnknown);
    }

    [Fact]
    public void Completed_and_failed_tasks_never_count()
    {
        Task finished = MakeTask("done");
        Task failedTask = MakeTask("blown");
        UserProgress progress = ProgressAtLevel(42);
        progress.Tasks.Add(new Progress { Id = "done", Complete = true });
        progress.Tasks.Add(new Progress { Id = "blown", Failed = true });

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [finished, failedTask], progress, true);

        Assert.Equal(0, report.GrandTotal);
    }

    [Fact]
    public void Invalidated_progress_entries_never_count()
    {
        Task task = MakeTask("wrong-faction-completion");
        UserProgress progress = ProgressAtLevel(42);
        progress.Tasks.Add(
            new Progress
            {
                Id = "wrong-faction-completion",
                Complete = true,
                Invalid = true,
            }
        );

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(0, report.GrandTotal);
    }

    [Fact]
    public void M4_mixed_states_aggregate_without_merging()
    {
        // Mirrors the reported bug: Punisher Part 5 (Lv20 + prereq active),
        // Gunsmith builds, and the conditional Fence collection quest must not
        // collapse into one unconditional "3 needed for active quests".
        Task prereq = MakeTask("punisher-4");
        Task punisher5 = MakeTask(
            "punisher-5",
            minLevel: 20,
            prerequisites: [new TaskPrerequisite { TaskId = "punisher-4", Statuses = ["complete"] }]
        );
        Task gunsmith = new()
        {
            Id = "gunsmith-4",
            Name = "gunsmith-4",
            MinPlayerLevel = 15,
            Objectives =
            [
                new()
                {
                    Id = "gunsmith-4-build",
                    Type = "buildWeapon",
                    BuildItemId = ItemId,
                    Count = 1,
                },
            ],
        };
        Task collection = MakeTask(
            "collection",
            objectives: FirGivePair("collection", count: 2),
            traderRequirements:
            [
                new TaskTraderRequirement
                {
                    RequirementType = "reputation",
                    CompareMethod = "<=",
                    Value = -1,
                    TraderId = "579dc571d53a0658a154fbec",
                },
            ]
        );

        UserProgress progress = ProgressAtLevel(12); // below both level gates
        progress.Tasks.Add(new Progress { Id = "punisher-4", Complete = true });

        QuestNeedReport report = QuestNeedClassifier.Classify(
            TestItem(),
            [prereq, punisher5, gunsmith, collection],
            progress,
            true
        );

        Assert.Equal(0, report.ActiveNow);
        Assert.Equal(0, report.AvailableNow);
        // Punisher 5 (1) + Gunsmith build (1) are locked behind known levels.
        Assert.Equal(2, report.FutureKnown);
        Assert.Equal(15, report.UnlockLevel);
        // The conditional two are reported separately and never as active.
        Assert.Equal(2, report.ConditionalUnknown);
        Assert.Equal(2, report.ConditionalUnknownFir);
    }

    [Fact]
    public void Fir_weapon_hand_in_is_tracked_for_usability_advisory()
    {
        Task task = MakeTask("punisher-5");
        UserProgress progress = ProgressAtLevel(25);
        progress.TaskObjectives.Add(new Progress { Id = "punisher-5-find", Count = 0 });

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(1, report.ActiveWeaponHandIn);
        Assert.Equal(1, report.ActiveNowFir);
    }

    [Fact]
    public void Optional_objectives_are_not_required()
    {
        Task task = new()
        {
            Id = "optional-quest",
            Name = "optional-quest",
            Objectives =
            [
                new()
                {
                    Id = "optional-quest-give",
                    Type = "giveItem",
                    Count = 3,
                    Optional = true,
                    ItemIds = [ItemId],
                },
            ],
        };

        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(42));

        Assert.Equal(0, report.GrandTotal);
    }

    [Fact]
    public void Objective_progress_subtracts_from_required_count()
    {
        Task task = MakeTask("collection-quest", objectives: FirGivePair("collection-quest", count: 2));
        UserProgress progress = ProgressAtLevel(42);
        progress.TaskObjectives.Add(new Progress { Id = "collection-quest-give", Count = 1 });
        progress.TaskObjectives.Add(new Progress { Id = "collection-quest-find", Count = 1 });

        QuestNeedReport report = ClassifySingle(task, progress);

        Assert.Equal(1, report.GrandTotal);
        // Objective progress exists → this task is active, not merely available.
        Assert.Equal(1, report.ActiveNow);
    }

    [Fact]
    public void No_relevant_objectives_produce_empty_report()
    {
        Task task = new()
        {
            Id = "unrelated",
            Name = "unrelated",
            Objectives =
            [
                new()
                {
                    Id = "unrelated-find",
                    Type = "findItem",
                    Count = 1,
                    FoundInRaid = true,
                    ItemIds = ["other-item"],
                },
            ],
        };

        QuestNeedReport report = ClassifySingle(task, ProgressAtLevel(42));

        Assert.False(report.Any);
        Assert.Equal(0, report.FirTotal);
    }

    [Fact]
    public void Kappa_totals_span_all_buckets()
    {
        Task active = MakeTask("kappa-a", minLevel: 0);
        active.KappaRequired = true;
        Task locked = MakeTask("kappa-b", minLevel: 20);
        locked.KappaRequired = true;
        UserProgress progress = ProgressAtLevel(10);
        progress.TaskObjectives.Add(new Progress { Id = "kappa-a-find", Count = 0 });

        QuestNeedReport report = QuestNeedClassifier.Classify(TestItem(), [active, locked], progress, true);

        Assert.Equal(2, report.KappaTotal);
        Assert.Equal(1, report.CurrentTotal);
    }

    [Fact]
    public void Collector_style_tasks_are_excluded_from_all_totals()
    {
        Task collector = MakeTask("61e6e5e0f5b9633f6719ed95");
        collector.KappaRequired = true;

        QuestNeedReport report = ClassifySingle(collector, ProgressAtLevel(42));

        Assert.Equal(0, report.GrandTotal);
        Assert.Equal(0, report.KappaTotal);
    }
}
