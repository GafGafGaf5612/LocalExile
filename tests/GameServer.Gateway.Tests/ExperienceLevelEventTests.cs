using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    [Fact]
    public async Task A_failed_extra_point_grant_does_not_publish_points_before_the_save_commits()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        string saved = game.SavedJson;
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAsync<IOException>(() => game.Command("/add_passive_points 1")); }
        finally { Saves.FailWrite = null; }

        Assert.Equal(saved, game.SavedJson);
        Assert.Equal((ushort)0, game.State.ExtraPassivePoints);

        await game.Command("/add_passive_points 1");
        Assert.Equal((ushort)1, game.State.ExtraPassivePoints);
        Assert.Equal((ushort)1, CharacterSaves.Load(game.Character)!.ExtraPassivePoints);
    }

    [Fact]
    public async Task In_flight_extra_point_grant_and_level_publication_keep_both_successful_grants()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        var gain = Program.RecordExperienceGain(game.State,
            CharacterStats.ExperienceFor(3) - CharacterStats.ExperienceFor(2))!;
        var writing = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Saves.FailWrite = name =>
        {
            if (name != game.Character) return false;
            writing.TrySetResult(game.State.ExtraPassivePoints);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("grant save was not released");
            return false;
        };
        var grant = Task.Run(() => game.Command("/add_passive_points 1"));
        Task<(string? Error, byte[] Bytes)>? level = null;
        ushort beforeCommit;
        try
        {
            beforeCommit = await writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            level = game.CompleteGain(gain, new LevelControl().Call);
        }
        finally
        {
            release.Set();
            try
            {
                await grant;
                if (level is not null) await level;
            }
            finally { Saves.FailWrite = null; }
        }

        Assert.Equal((ushort)0, beforeCommit);
        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal((ushort)1, game.State.ExtraPassivePoints);
        await game.Command("/add_passive_points 1");
        Assert.Equal((ushort)2, game.State.ExtraPassivePoints);
        Assert.Equal((ushort)2, CharacterSaves.Load(game.Character)!.ExtraPassivePoints);
    }

    [Fact]
    public async Task Extra_point_grants_use_the_saved_total_across_connections_and_saturate_without_overflow()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        var oldArea = game.Reload();
        await game.Command("/add_passive_points 1");
        await game.CommandOn(oldArea, "/add_passive_points 1");
        await game.Command("/add_passive_points 1");

        Assert.Equal((ushort)3, game.State.ExtraPassivePoints);
        Assert.Equal((ushort)3, CharacterSaves.Load(game.Character)!.ExtraPassivePoints);

        await game.Command("/add_passive_points 2147483647");
        Assert.Equal(ushort.MaxValue, game.State.ExtraPassivePoints);
        Assert.Equal(ushort.MaxValue, CharacterSaves.Load(game.Character)!.ExtraPassivePoints);
    }

    [Fact]
    public async Task An_old_area_gain_cannot_suppress_the_live_connections_current_level_publication()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725], allocate: true)).Error);
        var oldArea = game.Reload();
        var liveGain = Program.RecordExperienceGain(game.State,
            CharacterStats.ExperienceFor(3) - CharacterStats.ExperienceFor(2))!;
        var oldGain = Program.RecordExperienceGain(oldArea, 1)!;
        var auth = new LevelControl();
        await game.CompleteGain(oldGain, auth.Call, oldArea);

        var live = await game.CompleteGain(liveGain, auth.Call);

        uint experience = CharacterStats.ExperienceFor(3) + 1;
        AssertExperiencePrefix(live.Bytes, experience);
        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal(3, oldArea.Pools.Level);
        Assert.Equal(experience, game.State.Experience);
        Assert.Equal(experience, CharacterSaves.Load(game.Character)!.Experience);
        Assert.Equal(3, auth.Level);
        Assert.Null((await game.Change([24377], allocate: true)).Error);
        Assert.Equal(new ushort[] { 39725, 24377 }, CharacterSaves.Load(game.Character)!.Passives);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
    }

    [Fact]
    public async Task An_old_area_gain_cannot_suppress_an_intentional_downward_controls_runtime_publication()
    {
        using var game = new GatewayCharacter(level: 4, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        var oldArea = game.Reload();
        var reset = ProgramValue<Program.ExperienceChange>("RecordExperienceReset", game.State, 3);
        var oldGain = Program.RecordExperienceGain(oldArea, 1)!;
        var auth = new LevelControl();
        await game.CompleteGain(oldGain, auth.Call, oldArea);
        string saved = game.SavedJson;

        var live = await game.PublishGain(reset);
        await Program.SynchronizeAccountLevel(game.State, auth.Call);

        uint experience = CharacterStats.ExperienceFor(3) + 1;
        AssertExperiencePrefix(live.Bytes, experience);
        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal(3, oldArea.Pools.Level);
        Assert.Equal(experience, game.State.Experience);
        Assert.Equal(experience, CharacterSaves.Load(game.Character)!.Experience);
        Assert.Equal(3, auth.Level);
        Assert.Equal(saved, game.SavedJson);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
    }

    [Fact]
    public async Task A_delayed_level_gain_adopts_a_newer_paid_refund_without_restoring_the_node_or_payment()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725], allocate: true)).Error);
        var oldArea = game.Reload();
        var gain = Program.RecordExperienceGain(game.State,
            CharacterStats.ExperienceFor(3) - CharacterStats.ExperienceFor(2))!;
        Assert.Null((await game.Change([39725], allocate: false, refundPoints: true, state: oldArea)).Error);
        string refunded = game.SavedJson;

        await game.CompleteGain(gain, new LevelControl().Call);

        Assert.Equal(3, game.State.Pools.Level);
        Assert.Empty(game.State.Passives);
        Assert.Equal((ushort)19, game.State.RefundPoints);
        Assert.Equal(refunded, game.SavedJson);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
    }

    [Fact]
    public async Task A_failed_experience_trim_publishes_no_runtime_tree_and_can_retry_from_current_progress()
    {
        using var game = new GatewayCharacter(level: 4, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        var reset = ProgramValue<Program.ExperienceChange>("RecordExperienceReset", game.State, 3);
        string saved = game.SavedJson;
        var snapshot = game.State.SavedPassiveProgress;
        var pools = game.State.Pools;
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAsync<IOException>(() => game.PublishGain(reset)); }
        finally { Saves.FailWrite = null; }

        Assert.Equal(saved, game.SavedJson);
        Assert.Equal(new ushort[] { 39725, 24377, 35568 }, game.State.Passives);
        Assert.Same(snapshot, game.State.SavedPassiveProgress);
        Assert.Same(pools, game.State.Pools);
        Assert.Equal(InstanceEntities.PlayerExperienceUpdate(0x1000, CharacterStats.ExperienceFor(3)), game.LastBytes);

        await game.PublishGain(reset);

        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal((ushort)20, game.State.RefundPoints);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
    }

    [Fact]
    public async Task Reverse_completed_XP_events_keep_the_latest_level_and_every_allocated_passive()
    {
        using var game = new GatewayCharacter(level: 2, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725], allocate: true)).Error);
        var first = Program.RecordExperienceGain(game.State, CharacterStats.ExperienceFor(3) - CharacterStats.ExperienceFor(2))!;
        var second = Program.RecordExperienceGain(game.State, CharacterStats.ExperienceFor(4) - CharacterStats.ExperienceFor(3))!;
        var auth = new LevelControl();
        await game.CompleteGain(second, auth.Call);
        Assert.Null((await game.Change([24377, 35568], allocate: true)).Error);
        string before = game.SavedJson;

        var late = await game.CompleteGain(first, auth.Call);

        Assert.Equal(4, game.State.Pools.Level);
        Assert.Equal(CharacterStats.ExperienceFor(4), game.State.Experience);
        Assert.Equal(CharacterStats.ExperienceFor(4), CharacterSaves.Load(game.Character)!.Experience);
        Assert.Equal(new ushort[] { 39725, 24377, 35568 }, game.State.Passives);
        Assert.Equal(before, game.SavedJson);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
        Assert.Equal(4, auth.Level);
        Assert.All(auth.Requests, request => Assert.Equal(4, request.Level));
        AssertExperiencePrefix(late.Bytes, CharacterStats.ExperienceFor(4));
    }

    [Fact]
    public async Task A_pre_reset_automatic_event_cannot_revive_its_old_target_or_undo_a_downward_dev_control()
    {
        using var game = new GatewayCharacter(level: 4, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        var old = Program.RecordExperienceGain(game.State, CharacterStats.ExperienceFor(5) - CharacterStats.ExperienceFor(4))!;
        await game.SetLevel(3);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(3, game.State.Pools.Level);
        string reset = game.SavedJson;
        var auth = new LevelControl();

        var late = await game.CompleteGain(old, auth.Call);

        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal(CharacterStats.ExperienceFor(3), game.State.Experience);
        Assert.Equal(CharacterStats.ExperienceFor(3), CharacterSaves.Load(game.Character)!.Experience);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(reset, game.SavedJson);
        Assert.Equal(3, auth.Level);
        Assert.All(auth.Requests, request => Assert.Equal(3, request.Level));
        AssertExperiencePrefix(late.Bytes, CharacterStats.ExperienceFor(3));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task In_flight_Auth_requests_and_answers_cannot_finish_with_an_obsolete_level(
        bool applyBeforeReply, bool reset)
    {
        int initial = reset ? 4 : 2;
        using var game = new GatewayCharacter(level: initial, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725], allocate: true)).Error);
        var auth = new DelayedLevelControl(applyBeforeReply);
        var first = game.Gain(CharacterStats.ExperienceFor(initial + 1) - CharacterStats.ExperienceFor(initial), auth.Call);
        Task<ControlResponse>? secondAuth = null;
        string? settled = null;
        try
        {
            var request = await auth.First.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(initial + 1, request.Level);
            Assert.Equal(initial + 1, game.State.Pools.Level); // Runtime application never waits for Auth's answer.
            var second = Program.RecordExperienceGain(game.State,
                CharacterStats.ExperienceFor(initial + 2) - CharacterStats.ExperienceFor(initial + 1))!;
            await game.PublishGain(second);
            Assert.Equal(initial + 2, game.State.Pools.Level);
            Assert.Null((await game.Change([24377, 35568], allocate: true)).Error);
            if (reset) await game.SetLevel(3);
            settled = game.SavedJson;

            secondAuth = Program.SynchronizeAccountLevel(game.State, auth.Call);
            Assert.False(secondAuth.IsCompleted); // The first request/response owns the character's Auth gate.
            Assert.Single(auth.Requests);
        }
        finally
        {
            auth.Release();
            await first;
            if (secondAuth is not null) await secondAuth;
        }

        int expected = reset ? 3 : 4;
        Assert.Equal(expected, auth.Level);
        Assert.All(auth.Requests.Skip(1), request => Assert.Equal(expected, request.Level));
        Assert.Equal(expected, (await secondAuth!).Level);
        Assert.Equal(expected, game.State.Pools.Level);
        Assert.Equal(CharacterStats.ExperienceFor(expected), game.State.Experience);
        Assert.Equal(CharacterStats.ExperienceFor(expected), CharacterSaves.Load(game.Character)!.Experience);
        Assert.Equal(reset ? new ushort[] { 39725, 24377 } : [39725, 24377, 35568], game.State.Passives);
        Assert.Equal(settled, game.SavedJson);
        Assert.True(game.State.SavedPassiveProgress!.Matches(CharacterSaves.Load(game.Character)!));
    }

    [Fact]
    public async Task Queued_automatic_XP_publication_samples_after_an_intentional_downward_control()
    {
        using var game = new GatewayCharacter(level: 4, family: AscendancyFamily.Normal, choice: "Slayer");
        Assert.Null((await game.Change([39725, 24377, 35568], allocate: true)).Error);
        var old = Program.RecordExperienceGain(game.State, CharacterStats.ExperienceFor(5) - CharacterStats.ExperienceFor(4))!;
        var auth = new LevelControl();
        var queued = await game.QueuedGain(old, auth.Call, async () => { await game.SetLevel(3); });

        AssertExperiencePrefix(queued.Bytes, CharacterStats.ExperienceFor(3));
        Assert.Equal(3, game.State.Pools.Level);
        Assert.Equal(3, auth.Level);
        Assert.Equal(new ushort[] { 39725, 24377 }, game.State.Passives);
        Assert.Equal(CharacterStats.ExperienceFor(3), CharacterSaves.Load(game.Character)!.Experience);
    }

    static void AssertExperiencePrefix(byte[] bytes, uint experience)
    {
        byte[] expected = InstanceEntities.PlayerExperienceUpdate(0x1000, experience);
        Assert.Equal(expected, bytes[..expected.Length]);
    }

    sealed class LevelControl
    {
        public List<ControlRequest> Requests { get; } = [];
        public int Level { get; private set; }

        public Task<ControlResponse> Call(ControlRequest request)
        {
            Requests.Add(request);
            Level = request.Level;
            return Task.FromResult(new ControlResponse { Ok = true, Level = Level });
        }
    }

    sealed class DelayedLevelControl(bool applyBeforeReply)
    {
        readonly Lock _gate = new();
        readonly List<ControlRequest> _requests = [];
        readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _level;
        public TaskCompletionSource<ControlRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ControlRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }
        public int Level { get { lock (_gate) return _level; } }
        public void Release() => _release.TrySetResult(true);

        public async Task<ControlResponse> Call(ControlRequest request)
        {
            bool first;
            lock (_gate)
            {
                first = _requests.Count == 0;
                _requests.Add(request);
                if (!first || applyBeforeReply) _level = request.Level;
            }
            if (first)
            {
                First.TrySetResult(request);
                await _release.Task;
                if (!applyBeforeReply) lock (_gate) _level = request.Level;
            }
            return new ControlResponse { Ok = true, Level = request.Level };
        }
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> CommandOn(ClientState state, string line) => Exchange(async (stream, cipher) =>
        {
            state.Commands = ProgramValue<ChatCommands>("Commands", "xp-grant-test", stream, cipher, state);
            Assert.True(await state.Commands.RunAsync(line));
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> CompleteGain(Program.ExperienceChange change,
            Func<ControlRequest, Task<ControlResponse>> control, ClientState? state = null) => Exchange(async (stream, cipher) =>
        {
            await Program.CompleteExperienceGain("xp-event-test", stream, cipher, state ?? State, change, "test", control);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> Gain(uint amount,
            Func<ControlRequest, Task<ControlResponse>> control) => Exchange(async (stream, cipher) =>
        {
            await Program.GainExperience("xp-event-test", stream, cipher, State, amount, "test", control);
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> PublishGain(Program.ExperienceChange change) => Exchange(async (stream, cipher) =>
        {
            await Program.PublishExperienceChange("xp-event-test", stream, cipher, State, change, "test");
            return null;
        });

        public Task<(string? Error, byte[] Bytes)> QueuedGain(Program.ExperienceChange change,
            Func<ControlRequest, Task<ControlResponse>> control, Func<Task> mutation) => Exchange(async (stream, cipher) =>
        {
            var gate = Program.SendGates.GetValue(cipher, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            var publication = Program.CompleteExperienceGain("xp-event-test", stream, cipher, State, change, "test", control);
            Assert.False(publication.IsCompleted);
            try { await mutation(); }
            finally { gate.Release(); }
            await publication;
            return null;
        });
    }
}
