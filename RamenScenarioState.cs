using Gallop;


namespace RamenScenarioAnalyzer;

/// <summary>
/// 拉面杯剧本状态门面。单进程全局唯一快照，写读都通过 <see cref="Gate"/> 同步。
/// 消费方（如 SendGameStatusPlugin）通过 <see cref="RamenScenarioState.Snapshot"/> 读取不可变副本。
///
/// 生命周期：<see cref="UpdateLoad"/> 把 <see cref="RamenStateSnapshot.loaded"/> 置 true；
/// <see cref="Clear"/> 把所有字段复位为初始值；
/// 插件 <c>Dispose</c> 时调用 <see cref="Clear"/>。
/// 带角色标识的部分更新切换育成时先清空旧状态，只有 Load 能将 loaded 置 true。
/// 消费方读 snapshot 时若 <see cref="RamenStateSnapshot.single_mode_chara_id"/>
/// 与当前响应的 charaId 不一致，说明是新的一局，应忽略旧 snapshot 并自行重置。
/// </summary>
public static class RamenScenarioState
{
    static readonly object Gate = new();
    static bool loaded;
    static int single_mode_chara_id;
    static int[] selected_region_id_array = new int[3];
    static int[] reduce_base_turn = new int[3]; // guage_base_distribution
    // 待确认 check_point_info_array
    static int last_ramen;
    static int check_point_pt;
    static int expected_check_point_pt;

    /// <summary>
    /// 由 Load 响应写入 Load 路径独有的状态。
    /// </summary>
    /// <param name="charaId">
    /// 当前 Load 对应的 <c>single_mode_chara_id</c>，
    /// 用于消费方判断是否仍处于同一局。
    /// </param>
    /// <param name="data">Load 响应中的 <c>ramen_data_set_load</c>。</param>
    public static void UpdateLoad(int charaId, SingleModeRamenDataSetLoad data)
    {
        if (data is null)
            return;

        lock (Gate)
        {
            single_mode_chara_id = charaId;
            selected_region_id_array = data.selected_region_id_array ?? new int[3];

            Array.Clear(reduce_base_turn, 0, reduce_base_turn.Length);
            if (data.reduce_base_turn_info_array is not null)
                foreach (var info in data.reduce_base_turn_info_array)
                {
                    var idx = info.feeling_id - 1;
                    if (idx >= 0 && idx < reduce_base_turn.Length)
                        reduce_base_turn[idx] = info.reduce_base_turn;
                }

            // last_tasting_info == null 表示玩家尚未吃过拉面，last_ramen = -1 表示"未吃过"。
            last_ramen = data.last_tasting_info is null
                ? -1
                : data.last_tasting_info.region_id;

            check_point_pt = data.check_point_pt;
            expected_check_point_pt = data.expected_check_point_pt;

            loaded = true;
        }
    }
    public static void RamenScenarioStateCheckLoaded(int charaId)
    {
        bool flag1 = single_mode_chara_id == charaId;
        bool flag2 = selected_region_id_array.Length > 0 && selected_region_id_array[0] > 0;
        bool flag3 = reduce_base_turn.Length > 0 && reduce_base_turn[0] > 0;
        bool flag4 = last_ramen > 0;
        bool flag5 = check_point_pt > 0;
        bool flag6 = expected_check_point_pt > 0;
        if (flag1 && flag2 && flag3 && flag4 && flag5 && flag6)
            loaded = true;
        else
            loaded = false;
    }

    public static void UpdateScenarioStateRegionSelectCheck(SingleModeRamenRegionSelectCheckResponse response)
    {
        var data = response.data;
        Array.Clear(reduce_base_turn, 0, reduce_base_turn.Length);
        if (data.reduce_base_turn_info_array is not null)
            foreach (var info in data.reduce_base_turn_info_array)
            {
                var idx = info.feeling_id - 1;
                if (idx >= 0 && idx < reduce_base_turn.Length)
                    reduce_base_turn[idx] = info.reduce_base_turn;
            }
    }

    public static void UpdateScenarioStateCharaID(RamenScenarioResponseData data)
    {

        if (data is null)
            return;

        lock (Gate)
        {
            single_mode_chara_id = data.CharaInfo.single_mode_chara_id;
        }
    }

    /// <summary>
    /// 由地区选择响应更新 <c>selected_region_id_array</c> 与 charaId。
    /// </summary>
    public static void UpdateRegionSelect(int charaId, SingleModeRamenDataSetLoad data)
    {
        if (data is null)
            return;

        lock (Gate)
        {
            if (single_mode_chara_id != charaId)
                Clear();
            single_mode_chara_id = charaId;
            selected_region_id_array = data.selected_region_id_array ?? new int[3];
        }
    }

    /// <summary>
    /// 由品尝（<c>/umamusume/single_mode_ramen/tasting</c>）响应写入
    /// <c>last_ramen</c>、<c>check_point_pt</c>、<c>expected_check_point_pt</c>，
    /// 并以 <paramref name="charaId"/> 刷新当前角色标识。
    /// </summary>
    /// <param name="charaId"><c>chara_info.single_mode_chara_id</c>。</param>
    /// <param name="lastTastingInfo">响应中的 <c>last_tasting_info</c>；为 null 时 <c>last_ramen</c> 记为 -1（"未吃过"）。</param>
    /// <param name="checkPointPt">响应中的 <c>check_point_pt</c>。</param>
    /// <param name="expectedCheckPointPt">响应中的 <c>expected_check_point_pt</c>。</param>
    public static void UpdateTasting(
        int charaId,
        SingleModeRamenLastTastingInfo lastTastingInfo,
        int checkPointPt,
        int expectedCheckPointPt)
    {
        lock (Gate)
        {
            if (single_mode_chara_id != charaId)
                Clear();
            single_mode_chara_id = charaId;
            last_ramen = lastTastingInfo is null
                ? -1
                : lastTastingInfo.region_id;
            check_point_pt = checkPointPt;
            expected_check_point_pt = expectedCheckPointPt;
        }
    }

    /// <summary>
    /// 由地区选择请求（<c>/umamusume/single_mode_ramen/select_region</c>）写入
    /// <c>selected_region_id_array</c>。请求本身不携带 charaId，复用门内现有的
    /// <see cref="single_mode_chara_id"/>；该字段会在 Load 后被赋值并被本路径覆盖。
    /// </summary>
    /// <param name="regionIdArray">请求体中的 <c>region_id_array</c>，为 null 时跳过写入。</param>
    public static void UpdateRegionSelect(int[] regionIdArray)
    {
        if (regionIdArray is null)
            return;

        lock (Gate)
        {
            selected_region_id_array = (int[])regionIdArray.Clone();
        }
    }

    /// <summary>
    /// 在锁内拷贝全部字段，构造一份不可变快照。
    /// </summary>
    public static RamenStateSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new RamenStateSnapshot(
                loaded,
                single_mode_chara_id,
                (int[])selected_region_id_array.Clone(),
                (int[])reduce_base_turn.Clone(),
                last_ramen,
                check_point_pt,
                expected_check_point_pt);
        }
    }

    /// <summary>
    /// 把所有字段复位为初始值；线程安全。
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            loaded = false;
            single_mode_chara_id = 0;
            selected_region_id_array = new int[3];
            reduce_base_turn = new int[3];
            last_ramen = 0;
            check_point_pt = 0;
            expected_check_point_pt = 0;
        }
    }
}

/// <summary>
/// 不可变快照。消费方通过 <see cref="RamenScenarioState.Snapshot"/> 获取。
/// 内部数组已 Clone，消费方修改不会反向影响门面。
/// </summary>
public sealed class RamenStateSnapshot
{
    public RamenStateSnapshot(
        bool loaded,
        int single_mode_chara_id,
        int[] selected_region_id_array,
        int[] reduce_base_turn,
        int last_ramen,
        int check_point_pt,
        int expected_check_point_pt)
    {
        this.loaded = loaded;
        this.single_mode_chara_id = single_mode_chara_id;
        this.selected_region_id_array = selected_region_id_array;
        this.reduce_base_turn = reduce_base_turn;
        this.last_ramen = last_ramen;
        this.check_point_pt = check_point_pt;
        this.expected_check_point_pt = expected_check_point_pt;
    }

    public bool loaded { get; }
    public int single_mode_chara_id { get; }
    public int[] selected_region_id_array { get; }
    public int[] reduce_base_turn { get; }
    public int last_ramen { get; }
    public int check_point_pt { get; }
    public int expected_check_point_pt { get; }
}
