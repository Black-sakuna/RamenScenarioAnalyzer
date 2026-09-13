using Gallop;
using System.Text;

namespace RamenScenarioAnalyzer;

public enum RamenDisplayColor
{
    Normal,
    Cyan,
    Green,
    Yellow,
    Red,
    DarkOrange,
    Aqua,
    Lime,
    LightGreen
}

public readonly record struct RamenDisplaySegment(string Text, RamenDisplayColor Color = RamenDisplayColor.Normal);

internal sealed record RamenDisplayLine(IReadOnlyList<RamenDisplaySegment> Segments, bool IsRule = false)
{
    public string Text => string.Concat(Segments.Select(x => x.Text));

    public static RamenDisplayLine Plain(string text) => new([new(text)]);

    public static RamenDisplayLine Colored(string text, RamenDisplayColor color) => new([new(text, color)]);

    public static RamenDisplayLine Styled(params RamenDisplaySegment[] segments) => new(segments);

    public static RamenDisplayLine Rule { get; } = new([new("────────")], IsRule: true);
}

internal sealed class RamenDisplayRows : IReadOnlyList<string>
{
    readonly List<RamenDisplayLine> lines = [];

    public int Count => lines.Count;
    public string this[int index] => lines[index].Text;
    internal IReadOnlyList<RamenDisplayLine> Lines => lines;

    public void Add(string row)
    {
        foreach (var line in row.ReplaceLineEndings("\n").Split('\n'))
            Add(RamenDisplayLine.Plain(line));
    }
    public void Add(RamenDisplayLine row) => lines.Add(row);
    public void InsertRange(int index, IEnumerable<RamenDisplayLine> rows) => lines.InsertRange(index, rows);

    public IEnumerator<string> GetEnumerator() => lines.Select(x => x.Text).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed record RamenExtraSection(string Title, RamenDisplayRows Rows);

public sealed class RamenTrainingDisplayContext(
    object response,
    RamenScenarioResponseData responseData,
    TurnInfoRamen turn,
    IReadOnlyList<TrainStats> trainStats,
    int previousTurn)
{
    public object Response { get; } = response;
    public RamenScenarioResponseData ResponseData { get; } = responseData;
    public TurnInfoRamen Turn { get; } = turn;
    public IReadOnlyList<TrainStats> TrainStats { get; } = trainStats;
    public int PreviousTurn { get; } = previousTurn;
    public SingleModeRamenDataSet DataSet => Turn.DataSet;
}

internal sealed class RamenTrainingDisplayBuilder
{
    public List<RamenDisplayPanel> HeaderPanels { get; } = [];
    public List<RamenDisplayPanel> ScenarioPanels { get; } = [];
    public RamenDisplayRows ImportantRows { get; } = new();
    public List<RamenTrainingCard> TrainingCards { get; } = [];
    public RamenDisplayRows ExtraRows { get; } = new();
    public List<RamenExtraSection> ExtraSections { get; } = [];

    public RamenTrainingCard? FindTrainingCardByCommandId(int commandId)
        => TrainingCards.FirstOrDefault(x => x.CommandId == commandId);

    public RamenTrainingCard? FindTrainingCardByTrainIndex(int trainIndex)
        => TrainingCards.FirstOrDefault(x => x.TrainIndex == trainIndex);

    public RamenDisplayPanel? FindScenarioPanel(string key)
        => ScenarioPanels.FirstOrDefault(x => x.Key == key);

    public static RamenTrainingDisplayBuilder CreateDefault(RamenTrainingDisplayContext context)
    {
        var builder = new RamenTrainingDisplayBuilder();
        var turn = context.Turn;
        var data = context.ResponseData;
        builder.HeaderPanels.Add(new("date", "日期", $"{turn.Year}{RamenDisplayText.Year} {turn.Month}{RamenDisplayText.Month}{turn.HalfMonth}"));
        builder.HeaderPanels.Add(new(
            "total",
            "总属性",
            RamenDisplayLine.Colored($"总属性: {turn.StatsRevised.Sum()}, Pt: {data.CharaInfo.skill_point}", RamenDisplayColor.Cyan)));
        builder.HeaderPanels.Add(new(
            "vital",
            "体力",
            RamenDisplayLine.Styled(
                new($"{RamenDisplayText.Vital}: "),
                new(turn.Vital.ToString(), RamenDisplayColor.Green),
                new($"/{turn.MaxVital}"))));
        builder.HeaderPanels.Add(new(
            "motivation",
            "干劲",
            RamenDisplayLine.Colored(
                RamenDisplayText.Motivation(data.CharaInfo.motivation),
                data.CharaInfo.motivation switch
                {
                    5 => RamenDisplayColor.Green,
                    4 => RamenDisplayColor.Yellow,
                    _ => RamenDisplayColor.Red
                })));

        builder.ScenarioPanels.Add(new("special-feeling", "特殊心得", $"秘方数量: {context.DataSet.special_feeling_num}"));
        //builder.ScenarioPanels.Add(new("feeling", "心得", $"心得: {context.DataSet.feeling_info_array?.Length ?? 0}"));
        builder.ScenarioPanels.Add(new("active-effect", "生效效果", $"效果: {context.DataSet.active_effect_array?.Length ?? 0}"));
        builder.ScenarioPanels.Add(new(
            "uraf",
            "URAF",
            context.DataSet.uraf_effect_info is null
                ? "URAF: -"
                : $"URAF: type {context.DataSet.uraf_effect_info.uraf_effect_type}, state {context.DataSet.uraf_effect_info.uraf_effect_state}"));

        // 剧本状态信息检查：缺少 Load 或与当前 charaId 不一致时提示用户重进育成。
        var stateSnap = RamenScenarioState.Snapshot();
        if (!stateSnap.loaded || stateSnap.single_mode_chara_id != data.CharaInfo.single_mode_chara_id)
        {
            builder.ImportantRows.Add(RamenDisplayLine.Colored(
                "缺少剧本状态信息，需要从游戏主页重新进入育成",
                RamenDisplayColor.Red));
            RamenScenarioState.RamenScenarioStateCheckLoaded(data.CharaInfo.single_mode_chara_id);
        }
        if (stateSnap.selected_region_id_array.Length > 0 && stateSnap.selected_region_id_array[0] != 0)
        {
            var ef = builder.FindScenarioPanel("active-effect");
            ef.Title = "区域信息";
            List<string> regions = stateSnap.selected_region_id_array.
                Select(id => RamenDisplayText.Region_ID_Name.TryGetValue(id, out string name) ? name : null).
                Where(name => name != null).ToList();
            ef.Content = $"区域: {string.Join(",", regions)}";
        }

        if (data.CommandResult is not null)
            builder.ExtraRows.Add($"上次命令: {data.CommandResult.command_id}, result={data.CommandResult.result_state}");
        var homeInfo = data.HomeInfo ?? throw new InvalidOperationException("Ramen 训练显示需要 home_info。");
        if (homeInfo.command_info_array.Count(x => x.is_enable == 1) <= 1)
            builder.ImportantRows.Add(RamenDisplayLine.Colored(
                $"非训练回合 playingState = {data.CharaInfo.playing_state}",
                RamenDisplayColor.Aqua));
        if (data.CharaInfo.skill_point > 9500)
            builder.ImportantRows.Add(RamenDisplayLine.Colored(
                "剩余PT>9500（上限9999），请及时学习技能",
                RamenDisplayColor.Red));

        var maxScore = context.TrainStats.Count == 0 ? 0 : context.TrainStats.Max(x => x.FiveValueGain.Sum());
        foreach (var command in turn.CommandInfoArray)
        {
            var stats = context.TrainStats[command.TrainIndex - 1];
            builder.TrainingCards.Add(CreateTrainingCard(turn, command, stats, maxScore));
        }

        foreach (var item in context.DataSet.training_exec_info_array ?? [])
            builder.ExtraRows.Add($"训练次数: {item.base_command_id} = {item.exec_count}");
        //foreach (var item in context.DataSet.active_effect_array ?? [])
        //    builder.ExtraRows.Add($"效果: category={item.effect_category}, id={item.effect_id}, value={item.effect_value}");
        if (stateSnap.selected_region_id_array.Length > 0 && context.DataSet.feeling_info_array.Length > 0 && stateSnap.selected_region_id_array[0] != 0)
        {
            Dictionary<int, int> feeling_array = new() { { 1, 0 }, { 2, 0 }, { 3, 0 } };
            foreach (SingleModeRamenFeeling info in context.DataSet.feeling_info_array)
            {
                if (info.feeling_id == 0)
                    continue;
                feeling_array[info.feeling_id]++;
            }
            builder.ExtraRows.Add(RamenDisplayLine.Rule);
            builder.ExtraRows.Add(RamenDisplayLine.Plain("食材统计: "));
            foreach (var feels in feeling_array)
                builder.ExtraRows.Add(RamenDisplayLine.Styled(new($"{RamenDisplayText.Feeling_Name(feels.Key)}"), new($"：{feels.Value}")));
            foreach (int region_id in stateSnap.selected_region_id_array)
            {
                builder.ExtraRows.Add(RamenDisplayLine.Rule);
                builder.ExtraRows.Add(GetUseRegionTurnInfo(region_id, feeling_array));
            }
        }

        return builder;
    }

    static RamenTrainingCard CreateTrainingCard(TurnInfoRamen turn, RamenCommandInfo command, TrainStats stats, int maxScore)
    {
        var card = new RamenTrainingCard(command.CommandId, command.TrainIndex)
        {
            StyledTitle = stats.FailureRate > 0
                ? RamenDisplayLine.Styled(
                    new(RamenDisplayText.TrainName(command.TrainIndex)),
                    new(
                        $"({stats.FailureRate}%)",
                        stats.FailureRate switch
                        {
                            >= 40 => RamenDisplayColor.Red,
                            >= 20 => RamenDisplayColor.DarkOrange,
                            _ => RamenDisplayColor.Yellow
                        }))
                : RamenDisplayLine.Plain(RamenDisplayText.TrainName(command.TrainIndex))
        };
        var currentStat = turn.StatsRevised[command.TrainIndex - 1];
        var statUpToMax = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
        card.AddRow(RamenDisplayText.CurrentRemainStat);
        card.AddRow(RamenDisplayLine.Styled(
            new($"{currentStat}:"),
            new(
                statUpToMax.ToString(),
                statUpToMax switch
                {
                    > 400 => RamenDisplayColor.Normal,
                    > 200 => RamenDisplayColor.Yellow,
                    _ => RamenDisplayColor.Red
                })));
        card.AddRule();
        card.AddRow(command.ScenarioRewardTotal is { } rewardTotal
            ? $"Lv{command.TrainLevel} | {rewardTotal}"
            : $"Lv{command.TrainLevel}");
        card.AddRule();

        var score = stats.FiveValueGain.Sum();
        card.AddRow(RamenDisplayLine.Styled(
            new($"{RamenDisplayText.StatSimple}:"),
            new(score.ToString(), score == maxScore ? RamenDisplayColor.Aqua : RamenDisplayColor.Normal),
            new($"|Pt:{stats.PtGain}")));
        foreach (var trainingPartner in command.TrainingPartners)
        {
            card.AddRow(trainingPartner.DisplayLine);
            card.Highlighted |= trainingPartner.Shining;
        }
        for (var i = 8 - command.TrainingPartners.Count; i > 0; i--)
            card.AddRow(string.Empty);
        card.AddRule();

        if (turn.DataSet.feeling_reduce_turn_info_array.Length > 0)
        {
            card.AddRow(RamenDisplayLine.Plain($"{RamenDisplayText.Feeling_Name(1)}: {GetProcFeelingTurnInfo(1, command.CommandId, turn.DataSet)}"));
            card.AddRow(RamenDisplayLine.Plain($"{RamenDisplayText.Feeling_Name(2)}: {GetProcFeelingTurnInfo(2, command.CommandId, turn.DataSet)}"));
            card.AddRow(RamenDisplayLine.Plain($"{RamenDisplayText.Feeling_Name(3)}: {GetProcFeelingTurnInfo(3, command.CommandId, turn.DataSet)}"));
        }

        return card;
    }

    static string GetProcFeelingTurnInfo(int feelingId, int commandId, SingleModeRamenDataSet dataSet)
    {
        int remain_turn = dataSet.feeling_turn_info_array.First(x => x.feeling_id == feelingId).remain_turn;
        int reduce_turn = dataSet.feeling_reduce_turn_info_array.First(y => y.command_id == commandId).feeling_turn_array.First(z => z.feeling_id == feelingId).turn;

        int feeling_turn_info = 7 - remain_turn;

        int total_turn = 7 - remain_turn + reduce_turn;

        if (total_turn >= 7)
        {
            return "进度完成";
        }
        else
        {
            return $"{feeling_turn_info} => {7 - remain_turn + reduce_turn}";
        }
    }
    static string GetUseRegionTurnInfo(int region_id, Dictionary<int, int> feeling_array)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"试食拉面：{RamenDisplayText.Region_ID_Name[region_id]}");
        int[] uraf_effect = RamenDisplayText.Region_Reduce_Info[region_id];
        foreach (var id in feeling_array)
        {
            int feeling_result = id.Value - uraf_effect[id.Key - 1];
            sb.AppendLine($"<{RamenDisplayText.Feeling_Name(id.Key)}> {id.Value} => {feeling_result}");
        }
        return sb.ToString();
    }
}

internal static class RamenDisplayText
{
    static string Culture => Thread.CurrentThread.CurrentUICulture.Name;
    public static string Year => Culture is "en-US" ? "Year" : "年";
    public static string Month => Culture is "en-US" ? "Month" : "月";
    public static string CurrentRemainStat => Culture switch
    {
        "zh-CN" => "当前:可获得",
        "ja-JP" => "現在：可能",
        _ => "Current: Available"
    };
    public static string StatSimple => Culture switch { "zh-CN" => "属", "ja-JP" => "能", _ => "St" };
    public static string Vital => Culture is "en-US" ? "Vital" : "体力";
    public static string TrainName(int trainIndex) => trainIndex switch
    {
        1 => Culture switch { "zh-CN" => "速度", "ja-JP" => "スピード", _ => "Speed" },
        2 => Culture switch { "zh-CN" => "耐力", "ja-JP" => "スタミナ", _ => "Stamina" },
        3 => Culture switch { "zh-CN" => "力量", "ja-JP" => "パワー", _ => "Power" },
        4 => Culture switch { "zh-CN" => "根性", "ja-JP" => "根性", _ => "Nuts" },
        5 => Culture switch { "zh-CN" => "智力", "ja-JP" => "賢さ", _ => "Wiz" },
        _ => throw new InvalidOperationException($"未知训练索引: {trainIndex}")
    };
    public static string Motivation(int motivation) => motivation switch
    {
        5 => MotivationBest,
        4 => MotivationGood,
        3 => MotivationNormal,
        2 => MotivationBad,
        1 => MotivationWorst,
        _ => throw new InvalidOperationException($"未知干劲值: {motivation}")
    };
    public static string WrongTurnAlert(int previousTurn, int currentTurn) => Culture switch
    {
        "zh-CN" => $"警告：回合数不正确，上一个回合为{previousTurn}，当前回合为{currentTurn}",
        "ja-JP" => $"警告：ターン数が正しくありません。前のターンは{previousTurn}、現在のターンは{currentTurn}です",
        _ => $"Warning: Incorrect turn, the previous turn was {previousTurn}, the current turn is {currentTurn}"
    };
    public static string RepeatTurn => Culture switch
    {
        "zh-CN" => "******此回合为重复显示******",
        "ja-JP" => "このターンは重複して表示されます",
        _ => "This turn is a duplicate display"
    };
    public static string Feeling_Name(int feelingId) => feelingId switch
    {
        1 => "面",
        2 => "汤",
        3 => "菜",
        _ => ""
    };
    public static Dictionary<int, string> Region_ID_Name = new Dictionary<int, string>()
        {
            { 1, "札幌" },
            { 2, "函馆" },
            { 3, "新潟" },
            { 4, "福岛" },
            { 5, "东京" },
            { 6, "中山" },
            { 7, "中京" },
            { 8, "京都" },
            { 9, "阪神" },
            { 10, "小仓" },
            { 11, "札幌" },
            { 12, "函馆" },
            { 13, "新潟" },
            { 14, "福岛" },
            { 15, "东京" },
            { 16, "中山" },
            { 17, "中京" },
            { 18, "京都" },
            { 19, "阪神" },
            { 20, "小仓" }
        };
    public static Dictionary<int, int[]> Region_Reduce_Info = new Dictionary<int, int[]>()
        {
            { 1, new int[] { 2, 2, 1 } },
            { 2, new int[] { 1, 2, 2 } },
            { 3, new int[] { 3, 1, 1 } },
            { 4, new int[] { 2, 3, 0 } },
            { 5, new int[] { 1, 1, 3 }},
            { 6, new int[] { 2, 0, 3 } },
            { 7, new int[] { 3, 2, 0 } },
            { 8, new int[] { 0, 3, 2 } },
            { 9, new int[] { 2, 1, 2 } },
            { 10, new int[] { 1, 3, 1 } },
            { 11, new int[] { 2, 2, 1 } },
            { 12, new int[] { 1, 2, 2 } },
            { 13, new int[] { 3, 1, 1 } },
            { 14, new int[] { 2, 3, 0 } },
            { 15, new int[] { 1, 1, 3 }},
            { 16, new int[] { 2, 0, 3 } },
            { 17, new int[] { 3, 2, 0 } },
            { 18, new int[] { 0, 3, 2 } },
            { 19, new int[] { 2, 1, 2 } },
            { 20, new int[] { 1, 3, 1 } }
        };
    static string MotivationBest => Culture switch { "zh-CN" => "绝好调", "ja-JP" => "絶好調", _ => "Best" };
    static string MotivationGood => Culture switch { "zh-CN" => "好调", "ja-JP" => "好調", _ => "Good" };
    static string MotivationNormal => Culture switch { "zh-CN" => "普通", "ja-JP" => "普通", _ => "Normal" };
    static string MotivationBad => Culture switch { "zh-CN" => "不调", "ja-JP" => "不調", _ => "Bad" };
    static string MotivationWorst => Culture switch { "zh-CN" => "绝不调", "ja-JP" => "絶不調", _ => "Worst" };
}

internal sealed class RamenDisplayPanel(string key, string title, string content, bool showHeader = false)
{
    RamenDisplayRows contentRows = CreateRows(content);

    public RamenDisplayPanel(string key, string title, RamenDisplayLine content, bool showHeader = false)
        : this(key, title, content.Text, showHeader)
    {
        contentRows = CreateRows(content);
    }

    public string Key { get; } = key;
    public string Title { get; set; } = title;
    public string Content
    {
        get => string.Join(Environment.NewLine, contentRows);
        set => contentRows = CreateRows(value);
    }
    public bool ShowHeader { get; set; } = showHeader;
    internal IReadOnlyList<RamenDisplayLine> Lines => contentRows.Lines;

    internal void AddRow(string row) => contentRows.Add(row);
    internal void AddRow(RamenDisplayLine row) => contentRows.Add(row);

    static RamenDisplayRows CreateRows(RamenDisplayLine line)
    {
        var rows = new RamenDisplayRows();
        rows.Add(line);
        return rows;
    }

    static RamenDisplayRows CreateRows(string text)
    {
        var rows = new RamenDisplayRows();
        rows.Add(text);
        return rows;
    }
}

internal sealed class RamenTrainingCard(int commandId, int trainIndex)
{
    RamenDisplayLine title = RamenDisplayLine.Plain(commandId.ToString());

    public int CommandId { get; } = commandId;
    public int TrainIndex { get; } = trainIndex;
    public string Title
    {
        get => title.Text;
        set => title = RamenDisplayLine.Plain(value);
    }
    public RamenDisplayRows Rows { get; } = new();
    public bool Highlighted { get; set; }
    internal RamenDisplayLine StyledTitle
    {
        get => title;
        set => title = value;
    }
    public void AddRow(string row) => Rows.Add(row);
    public void AddRow(RamenDisplayLine row) => Rows.Add(row);
    public void AddRule() => Rows.Add(RamenDisplayLine.Rule);
}
