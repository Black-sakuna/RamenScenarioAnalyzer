using Gallop;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;

namespace RamenScenarioAnalyzer;

public sealed class RamenScenarioAnalyzer : IPlugin
{
    const string WorkspaceTitle = "RamenScenarioAnalyzer";
    const string TrainingPanelKey = "training";
    const string CommonResponseEndpoints =
        "^/umamusume/single_mode_ramen/(?:change_short_cut|check_event|check_point|continue|exec_command|finish_claw_crane|gain_skills|race_end|race_entry|race_out|ramen_live|uraf_effect_apply|tasting|select_region)$";

    readonly object stateGate = new();
    readonly RamenDisplayHistory history = new(WorkspaceTitle, TrainingPanelKey, "拉面杯训练");
    Workspace? workspace;
    int currentTurn;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        history.Initialize(
            context.Application,
            key => RamenTrainingDisplay.Show(new(key.SingleModeCharaId, key.Turn)),
            key => RamenTrainingDisplay.Remove(
                this,
                new(key.SingleModeCharaId, key.Turn)));
        context.Analyzers.Register<SingleModeRamenExecCommandResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Regex(CommonResponseEndpoints)],
            invocation => Analyze(invocation.Payload),
            priority: 1);
        context.Analyzers.Register<SingleModeRamenLoadResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Exact("/umamusume/single_mode_ramen/load")],
            invocation => Analyze(invocation.Payload),  // 同名但是不同类型重载，实现不同
            priority: 1);
        context.Analyzers.Register<SingleModeRamenTastingResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Exact("/umamusume/single_mode_ramen/tasting")],
            invocation => AnalyzeTasting(invocation.Payload),
            priority: 0);
        context.Analyzers.Register<SingleModeRamenSelectRegionRequest>(
            AnalyzerKind.Request,
            [EndpointPattern.Exact("/umamusume/single_mode_ramen/select_region")],
            invocation => AnalyzeRegionSelect(invocation.Payload),
            priority: 0);
        context.Analyzers.Register<SingleModeRamenRegionSelectCheckResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Exact("/umamusume/single_mode_ramen/region_select_check")],
            invocation => AnalyzeRegionSelectCheck(invocation.Payload),
            priority: 0);
    }

    static ValueTask AnalyzeRegionSelectCheck(SingleModeRamenRegionSelectCheckResponse response)
    {
        RamenScenarioState.UpdateScenarioStateRegionSelectCheck(response);
        return ValueTask.CompletedTask;
    }

    static ValueTask AnalyzeTasting(SingleModeRamenTastingResponse response)
    {
        var data = response.data;
        
        RamenScenarioState.UpdateTasting(
            data.chara_info.single_mode_chara_id,
            data.last_tasting_info,
            data.check_point_pt,
            data.expected_check_point_pt);
        return ValueTask.CompletedTask;
    }

    static ValueTask AnalyzeRegionSelect(SingleModeRamenSelectRegionRequest request)
    {

        RamenScenarioState.UpdateRegionSelect(request.region_id_array);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        history.Stop();
        RamenTrainingDisplay.Clear(this);
        lock (stateGate)
        {
            RamenScenarioState.Clear();
            workspace?.RemovePanel(TrainingPanelKey);
            workspace = null;
        }
    }

    public Task ConfigPromptAsync(
        Terminal.Gui.App.IApplication application,
        CancellationToken cancellationToken = default)
        => history.ConfigPromptAsync(application, cancellationToken);

    ValueTask Analyze(SingleModeRamenLoadResponse response)
    {
        var loadCommon = response.data.single_mode_load_common;
        return AnalyzeRamenResponse(new(
            response,
            loadCommon.chara_info,
            response.data.ramen_data_set,
            response.data.ramen_data_set_load,
            loadCommon.home_info,
            loadCommon.unchecked_event_array,
            commandResult: null));
    }

    ValueTask Analyze(SingleModeRamenExecCommandResponse response)
        => AnalyzeRamenResponse(new(
            response,
            response.data.chara_info,
            response.data.ramen_data_set,
            null,
            response.data.home_info,
            response.data.unchecked_event_array,
            response.data.command_result));

    ValueTask AnalyzeRamenResponse(RamenScenarioResponseData data)
    {
        lock (stateGate)
        {
            if (data.DataSetLoad is not null)
                RamenScenarioState.UpdateLoad(data.CharaInfo.single_mode_chara_id, data.DataSetLoad);

            if (!CanRenderTrainingPanel(data))
                return ValueTask.CompletedTask;

            var turn = new TurnInfoRamen(data);
            var trainStats = RamenTrainingStatsCalculator.CreateTrainStats(turn);
            var context = new RamenTrainingDisplayContext(
                data.Response,
                data,
                turn,
                trainStats,
                currentTurn);
            var target = Workspace.Create(WorkspaceTitle);
            var displayId = new RamenTrainingDisplayId(
                data.CharaInfo.single_mode_chara_id,
                data.CharaInfo.turn);
            var historyKey = new RamenDisplayHistory.Key(
                displayId.SingleModeCharaId,
                displayId.Turn);
            RamenTrainingDisplay.Update(
                this,
                displayId,
                context,
                displayContext =>
                {
                    var builder = RamenTrainingDisplayBuilder.CreateDefault(displayContext);
                    AddTurnStateImportantRows(displayContext, builder);
                    return builder;
                },
                (_, content, switchToWorkspace) =>
                {
                    lock (stateGate)
                    {
                        history.Show(
                            target,
                            historyKey,
                            content,
                            switchToWorkspace);
                        workspace = target;
                    }
                });
            if (history.ShouldShow(historyKey))
            {
                if (!RamenTrainingDisplay.Show(displayId, switchToWorkspace: true))
                    throw new InvalidOperationException($"拉面杯 DisplayId 不存在: {displayId}。");
            }
            else
            {
                history.Track(target, historyKey);
            }
            if (data.CharaInfo.playing_state == 1)
                currentTurn = turn.Turn;
        }

        return ValueTask.CompletedTask;
    }

    static bool CanRenderTrainingPanel(RamenScenarioResponseData data)
    {
        if (data.CharaInfo.state is 2 or 3)
            return false;
        if (data.UncheckedEventArray is { Length: > 0 })
            return false;
        if (data.HomeInfo?.command_info_array is not { } commands)
            return false;
        if (data.DataSet.command_info_array is null)
            return false;

        return TurnInfoRamen.BaseTrainIds.All(trainId =>
            commands.Any(command =>
                TurnInfoRamen.ToTrainId.TryGetValue(command.command_id, out var baseTrainId)
                && baseTrainId == trainId));
    }

    static void AddTurnStateImportantRows(
        RamenTrainingDisplayContext context,
        RamenTrainingDisplayBuilder builder)
    {
        var rows = new List<RamenDisplayLine>();
        if (context.PreviousTurn != context.Turn.Turn - 1
            && context.PreviousTurn != context.Turn.Turn
            && context.Turn.Turn != 1)
        {
            rows.Add(RamenDisplayLine.Colored(
                RamenDisplayText.WrongTurnAlert(context.PreviousTurn, context.Turn.Turn),
                RamenDisplayColor.Red));
        }

        if (context.ResponseData.CharaInfo.playing_state != 1)
        {
            rows.Add(RamenDisplayLine.Colored(RamenDisplayText.RepeatTurn, RamenDisplayColor.Yellow));
        }

        if (rows.Count > 0)
            builder.ImportantRows.InsertRange(0, rows);
    }
}
