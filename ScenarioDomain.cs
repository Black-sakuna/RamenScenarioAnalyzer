using System.Collections.Frozen;
using Gallop;
using UmamusumeResponseAnalyzer;

namespace RamenScenarioAnalyzer;

public sealed class RamenScenarioResponseData(
    object response,
    SingleModeChara charaInfo,
    SingleModeRamenDataSet dataSet,
    SingleModeRamenDataSetLoad? dataSetLoad,
    SingleModeHomeInfo? homeInfo,
    SingleModeEventInfo[]? uncheckedEventArray,
    SingleModeCommandResult? commandResult)
{
    public object Response { get; } = response;
    public SingleModeChara CharaInfo { get; } = charaInfo;
    public SingleModeRamenDataSet DataSet { get; } = dataSet;
    public SingleModeHomeInfo? HomeInfo { get; } = homeInfo;
    public SingleModeEventInfo[]? UncheckedEventArray { get; } = uncheckedEventArray;
    public SingleModeCommandResult? CommandResult { get; } = commandResult;
    public SingleModeRamenDataSetLoad? DataSetLoad { get; } = dataSetLoad;
}

public sealed class RamenCommandInfo
{
    public RamenCommandInfo(RamenScenarioResponseData response, int commandId)
    {
        CommandId = commandId;
        TrainIndex = TurnInfoRamen.ToTrainIndex[commandId] + 1;
        var baseCommandId = TurnInfoRamen.ToTrainId[CommandId];

        var homeInfo = response.HomeInfo ?? throw new InvalidOperationException("Ramen 训练显示需要 home_info。");
        var training = response.CharaInfo.training_level_info_array
            .FirstOrDefault(x => x.command_id == CommandId || x.command_id == baseCommandId);
        TrainLevel = training is null ? 0 : training.level;

        var normalCommand = homeInfo.command_info_array
            .First(x => x.command_id == CommandId || x.command_id == baseCommandId);
        TrainingPartners = normalCommand.training_partner_array
            .Select(x => new TrainingPartner(response, x, normalCommand))
            .OrderBy(x => x.Priority)
            .ToArray();

        var commandFeelingTurns = response.DataSet.feeling_reduce_turn_info_array
            ?.FirstOrDefault(x => x.command_id == CommandId)
            ?.feeling_turn_array;
        if (commandFeelingTurns is { Length: > 0 })
        {
            var feelingTurnById = (response.DataSet.feeling_turn_info_array
                ?? throw new InvalidOperationException("Ramen 训练显示缺少 feeling_turn_info_array。"))
                .ToDictionary(x => x.feeling_id, x => x.remain_turn);
            ScenarioRewardTotal = commandFeelingTurns.Sum(x => feelingTurnById.TryGetValue(x.feeling_id, out var remainTurn)
                ? Math.Min(x.turn, remainTurn)
                : throw new InvalidOperationException($"Ramen 训练显示缺少心得剩余值: commandId={CommandId}, feelingId={x.feeling_id}"));
        }
        ExecCount = response.DataSet.training_exec_info_array?.FirstOrDefault(x => x.base_command_id == baseCommandId)?.exec_count;
    }

    public int CommandId { get; }
    public int TrainIndex { get; }
    public int TrainLevel { get; }
    public IReadOnlyList<TrainingPartner> TrainingPartners { get; }
    public int? ScenarioRewardTotal { get; }
    public int? ExecCount { get; }
}

public sealed class TrainingPartner
{
    public TrainingPartner(
        RamenScenarioResponseData response,
        int position,
        SingleModeCommandInfo command)
    {
        // 防御式查询：数据缺失（空卡位/名字库未收录该卡或马娘）时回退为空，避免抛对象找不到异常中断整块训练面板渲染
        var supportCardId = position is >= 1 and <= 6
            ? response.CharaInfo.support_card_array.FirstOrDefault(x => x.position == position)?.support_card_id
            : null;
        var supportCard = supportCardId is { } id && Database.Names.TryGetSupportCard(id, out var card)
            ? card
            : null;
        var rawName = Database.Names.DisplayNickname(supportCardId ?? position);
        var friendship = response.CharaInfo.evaluation_info_array.FirstOrDefault(x => x.target_id == position)?.evaluation ?? 0;
        var trainingType = TurnInfoRamen.ToTrainId.TryGetValue(command.command_id, out var baseCommandId)
            ? baseCommandId
            : command.command_id;

        Priority = position is >= 1 and <= 6 ? 0 : 1;
        Shining = supportCard is not null && friendship >= 80 && supportCard.CanTriggerFriendshipTraining(trainingType);
        var segments = new List<RamenDisplaySegment>();
        if (command.tips_event_partner_array.Contains(position))
            segments.Add(new("!", RamenDisplayColor.Red));
        segments.Add(new(
            rawName,
            supportCard is not null && supportCard.IsFriendCard
                ? RamenDisplayColor.Lime
                : Shining
                    ? RamenDisplayColor.Aqua
                    : RamenDisplayColor.Normal));
        if (friendship is > 0 and < 100)
            segments.Add(new(friendship.ToString(), RamenDisplayColor.Red));

        DisplayLine = new(segments);
        Name = DisplayLine.Text;
    }

    public int Priority { get; }
    public string Name { get; }
    public bool Shining { get; }
    internal RamenDisplayLine DisplayLine { get; }
}

public sealed class TrainStats
{
    public int[] FiveValueGain { get; init; } = [];
    public int PtGain { get; init; }
    public int VitalGain { get; init; }
    public int FailureRate { get; init; }
}

public sealed class CommandInfoLayout(int trainingCardWidth)
{
    public static CommandInfoLayout Current => Thread.CurrentThread.CurrentUICulture.Name switch
    {
        "zh-CN" => new(17),
        "ja-JP" => new(18),
        _ => new(16)
    };

    public int MainSectionWidth => trainingCardWidth * 5 + 10;
}

public static class ScoreUtils
{
    public static int ReviseOver1200(int value) => value > 1200 ? value * 2 - 1200 : value;
}

public sealed class TurnInfoRamen
{
    public static readonly int[] BaseTrainIds = [101, 105, 102, 103, 106];

    public static readonly int[] TrainIds = [.. BaseTrainIds, 601, 602, 603, 604, 605];

    public static readonly FrozenDictionary<int, int> ToTrainId = new Dictionary<int, int>
    {
        [101] = 101,
        [105] = 105,
        [102] = 102,
        [103] = 103,
        [106] = 106,
        [601] = 101,
        [602] = 105,
        [603] = 102,
        [604] = 103,
        [605] = 106
    }.ToFrozenDictionary();

    public static readonly FrozenDictionary<int, int> ToTrainIndex = new Dictionary<int, int>
    {
        [101] = 0,
        [105] = 1,
        [102] = 2,
        [103] = 3,
        [106] = 4,
        [601] = 0,
        [602] = 1,
        [603] = 2,
        [604] = 3,
        [605] = 4
    }.ToFrozenDictionary();

    public static readonly FrozenDictionary<int, int> XiahesuIds = new Dictionary<int, int>
    {
        [101] = 601,
        [105] = 602,
        [102] = 603,
        [103] = 604,
        [106] = 605
    }.ToFrozenDictionary();

    readonly RamenScenarioResponseData response;

    public TurnInfoRamen(RamenScenarioResponseData response)
    {
        this.response = response;
        DataSet = response.DataSet;
        if (response.CharaInfo.turn == 1)
        {
            RamenScenarioState.UpdateScenarioStateCharaID(response);
        }
        var commandsByBaseTrainId = DataSet.command_info_array
            .Where(x => x.command_type == 1 && ToTrainIndex.ContainsKey(x.command_id))
            .GroupBy(x => ToTrainId[x.command_id])
            .ToDictionary(x => x.Key, x => x.First().command_id);
        CommandInfoArray =
        [
            .. BaseTrainIds.Select(trainId =>
                commandsByBaseTrainId.TryGetValue(trainId, out var commandId)
                    ? new RamenCommandInfo(response, commandId)
                    : throw new InvalidOperationException($"Ramen 训练显示缺少训练 command: baseCommandId={trainId}"))
        ];
    }

    public SingleModeRamenDataSet DataSet { get; }
    public int Turn => response.CharaInfo.turn;
    public int Year => (Turn - 1) / 24 + 1;
    public int Month => ((Turn - 1) % 24) / 2 + 1;
    public string HalfMonth => Turn % 2 == 0 ? "后半" : "前半";
    public int Vital => response.CharaInfo.vital;
    public int MaxVital => response.CharaInfo.max_vital;
    public int[] Stats => [response.CharaInfo.speed, response.CharaInfo.stamina, response.CharaInfo.power, response.CharaInfo.guts, response.CharaInfo.wiz];
    public int[] StatsRevised => [.. Stats.Select(ScoreUtils.ReviseOver1200)];
    public int[] MaxStatsRevised =>
    [
        ScoreUtils.ReviseOver1200(response.CharaInfo.max_speed),
        ScoreUtils.ReviseOver1200(response.CharaInfo.max_stamina),
        ScoreUtils.ReviseOver1200(response.CharaInfo.max_power),
        ScoreUtils.ReviseOver1200(response.CharaInfo.max_guts),
        ScoreUtils.ReviseOver1200(response.CharaInfo.max_wiz)
    ];
    public IReadOnlyList<RamenCommandInfo> CommandInfoArray { get; }
    public RamenScenarioResponseData GetResponseData() => response;
}

public static class RamenTrainingStatsCalculator
{
    public static IReadOnlyList<TrainStats> CreateTrainStats(TurnInfoRamen turn)
    {
        var response = turn.GetResponseData();
        var homeInfo = response.HomeInfo ?? throw new InvalidOperationException("Ramen 训练显示需要 home_info。");
        var trainItems = homeInfo.command_info_array
            .Where(x => TurnInfoRamen.ToTrainId.ContainsKey(x.command_id))
            .GroupBy(x => TurnInfoRamen.ToTrainId[x.command_id])
            .ToDictionary(x => x.Key, x => x.First());

        var trainStats = new TrainStats[TurnInfoRamen.BaseTrainIds.Length];
        for (var i = 0; i < TurnInfoRamen.BaseTrainIds.Length; i++)
        {
            var trainId = TurnInfoRamen.BaseTrainIds[i];
            var trainParams = new Dictionary<int, int>
            {
                [1] = 0,
                [2] = 0,
                [3] = 0,
                [4] = 0,
                [5] = 0,
                [30] = 0,
                [10] = 0,
            };

            foreach (var item in homeInfo.command_info_array)
            {
                if (!TurnInfoRamen.ToTrainId.TryGetValue(item.command_id, out var value) || value != trainId)
                    continue;

                foreach (var trainParam in item.params_inc_dec_info_array)
                {
                    if (trainParams.ContainsKey(trainParam.target_type))
                        trainParams[trainParam.target_type] += trainParam.value;
                }
            }

            var vitalGain = trainParams[10];
            if (turn.Vital + vitalGain > turn.MaxVital)
                vitalGain = turn.MaxVital - turn.Vital;
            if (vitalGain < -turn.Vital)
                vitalGain = -turn.Vital;

            var fiveValueGain = new[] { trainParams[1], trainParams[2], trainParams[3], trainParams[4], trainParams[5] };
            var ptGain = trainParams[30];

            var valueGainUpper = turn.DataSet.command_info_array
                .FirstOrDefault(x => x.command_id == trainId || x.command_id == TurnInfoRamen.XiahesuIds[trainId])
                ?.params_inc_dec_info_array;
            if (valueGainUpper is not null)
            {
                foreach (var item in valueGainUpper)
                {
                    if (item.target_type == 30)
                        ptGain += item.value;
                    else if (item.target_type is >= 1 and <= 5)
                        fiveValueGain[item.target_type - 1] += item.value;
                }
            }

            for (var j = 0; j < 5; j++)
                fiveValueGain[j] = ScoreUtils.ReviseOver1200(turn.Stats[j] + fiveValueGain[j]) - ScoreUtils.ReviseOver1200(turn.Stats[j]);

            trainStats[i] = new()
            {
                FailureRate = trainItems[trainId].failure_rate,
                VitalGain = vitalGain,
                FiveValueGain = fiveValueGain,
                PtGain = ptGain
            };
        }

        return trainStats;
    }
}
