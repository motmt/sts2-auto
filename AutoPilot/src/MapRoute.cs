using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Map;

namespace AutoPilot;

/// <summary>
/// 路线前瞻：把「这个候选节点之后会走到什么」渲染成文字，供 LLM 做真正的路线规划。
///
/// v2.5.4 新增。此前 <see cref="GameState.DescribeMapOptions"/> 只把**下一层可达节点的类型名**
/// 丢给 LLM（"普通战斗 / 精英战斗 / 商店"），LLM 看不到这个选择之后整条分支的走向 ——
/// 而"为了走到 3 层后的休息点，先忍受这一场精英"恰恰是路线规划的全部意义。
/// 只给一层 = 让模型靠猜，这就是"路线规划有大问题"的根因。
///
/// 本类用 <see cref="MapPoint.Children"/> 做**有限深度 DFS**（不是 BFS），
/// 枚举每个候选之后的若干条真实路径，让模型看到：
///   · 走 A 之后 6 层内会经过哪些房间（含关键的休息点/精英/商店/宝箱）
///   · A 与 B 分叉在哪一层重新汇合（<see cref="MapPoint.GetFirstCommonDescendant"/>）
/// 并对每条路径给出可计算的量化指标（楼层、战损、休息点数量等），使对比有据可依。
/// </summary>
public static class MapRoute
{
    /// <summary>单条前瞻路径。</summary>
    public sealed class Path
    {
        public List<MapPoint> Points = new();
        /// <summary>该路径终点的层级（用于判断前瞻是否触底）。</summary>
        public int EndRow;
        /// <summary>是否走到了路径尽头（没有更多子节点）。</summary>
        public bool ReachedEnd;

        public int RouteLength => Points.Count;
    }

    /// <summary>某个候选节点的完整评估（含前瞻路径）。</summary>
    public sealed class CandidateEval
    {
        public MapPoint Point;
        public int Index;                       // 1-based，与提示词序号对应
        public string TypeCn = "";
        /// <summary>该候选之后的代表路径（最长的一条，用于提示词展示）。</summary>
        public Path MainPath;
        /// <summary>全部前瞻路径（去重后，最多 N 条）。</summary>
        public List<Path> Paths = new();
        public int LookaheadRows;               // 实际前瞻到的层数

        // ---- 路径聚合指标（在该候选之后 depth 层内统计） ----
        public int RestCount;
        public int EliteCount;
        public int ShopCount;
        public int TreasureCount;
        public int MonsterCount;
        public int EventCount;
        public int MaxEliteStreak;              // 连续精英的最大长度（危险信号）
        public double RiskScore;                // 越高越危险（精英多、休息少、无商店）
    }

    // 统计时只统计真正的房间节点（Unassigned 是占位/未分配）
    private static bool IsRoom(MapPointType t) => t != MapPointType.Unassigned;

    /// <summary>
    /// 对每个候选做 depth 层前瞻。返回与传入顺序一致的评估列表。
    /// </summary>
    public static List<CandidateEval> Evaluate(IReadOnlyList<MapPoint> candidates, int depth = 6, int maxPaths = 4)
    {
        var result = new List<CandidateEval>();
        if (candidates == null) return result;

        for (int i = 0; i < candidates.Count; i++)
        {
            var p = candidates[i];
            if (p == null) continue;

            var ev = new CandidateEval
            {
                Point = p,
                Index = i + 1,
                TypeCn = GameState.PointCn(p.PointType),
            };

            try
            {
                ev.Paths = EnumeratePaths(p, depth, maxPaths);
                ev.MainPath = ev.Paths.OrderByDescending(x => x.RouteLength).FirstOrDefault();
                ev.LookaheadRows = ev.Paths.Count > 0 ? ev.Paths.Max(x => x.RouteLength) : 0;
                Aggregate(ev, depth);
            }
            catch (Exception e)
            {
                Pilot.Warn($"路线前瞻失败（{p.PointType}）: {e.Message}");
            }
            result.Add(ev);
        }
        return result;
    }

    /// <summary>对候选节点做深度受限的 DFS，枚举若干条真实路径。</summary>
    private static List<Path> EnumeratePaths(MapPoint start, int depth, int maxPaths)
    {
        var paths = new List<Path>();
        if (start == null) return paths;

        var stack = new Stack<(MapPoint node, List<MapPoint> trail)>();
        stack.Push((start, new List<MapPoint>()));

        int budget = 400;   // 防病态地图造成指数爆炸
        while (stack.Count > 0 && paths.Count < maxPaths && budget-- > 0)
        {
            var (node, trail) = stack.Pop();
            var newTrail = new List<MapPoint>(trail);
            // 只在真正的房间节点计入路径（起点必计）
            if (node != start) newTrail.Add(node);

            bool hasRoomChild = false;
            var kids = new List<MapPoint>();
            try { foreach (var c in node.Children) if (c != null) kids.Add(c); } catch { }

            // 按列排序，让"左→中→右"的枚举顺序稳定可读
            kids.Sort((a, b) => a.coord.col.CompareTo(b.coord.col));

            bool depthLeft = newTrail.Count < depth;
            foreach (var k in kids)
            {
                if (!IsRoom(k.PointType)) continue;
                hasRoomChild = true;
                if (depthLeft) stack.Push((k, newTrail));
            }

            if (!hasRoomChild || !depthLeft)
            {
                paths.Add(new Path
                {
                    Points = newTrail,
                    EndRow = node.coord.row,
                    ReachedEnd = !hasRoomChild,
                });
            }
        }
        return paths;
    }

    /// <summary>把前瞻路径聚合成可比较的风险指标。</summary>
    private static void Aggregate(CandidateEval ev, int depth)
    {
        // 统计"代表性路径"上的房间构成（用最长路径 + 全部路径去重统计）
        var seen = new HashSet<MapCoord>();
        int streak = 0;
        foreach (var path in ev.Paths)
        {
            foreach (var pt in path.Points)
            {
                if (pt == null) continue;
                // 去重：同一坐标只统计一次，避免多条路径重复计数
                if (!seen.Add(pt.coord)) continue;
                switch (pt.PointType)
                {
                    case MapPointType.RestSite: ev.RestCount++; streak = 0; break;
                    case MapPointType.Elite: ev.EliteCount++; streak++; break;
                    case MapPointType.Shop: ev.ShopCount++; streak = 0; break;
                    case MapPointType.Treasure: ev.TreasureCount++; streak = 0; break;
                    case MapPointType.Monster: ev.MonsterCount++; streak = 0; break;
                    case MapPointType.Unknown: ev.EventCount++; streak = 0; break;
                    case MapPointType.Boss: streak = 0; break;
                    default: break;
                }
                if (streak > ev.MaxEliteStreak) ev.MaxEliteStreak = streak;
            }
        }

        // 风险分：精英多、休息少、无商店 → 更危险
        // 量级刻意与本地启发式同阶（0~100），便于混用
        ev.RiskScore = ev.EliteCount * 22.0
                     + Math.Max(0, ev.MaxEliteStreak - 1) * 18.0
                     - ev.RestCount * 16.0
                     - ev.ShopCount * 8.0
                     - ev.TreasureCount * 6.0;

        // 触底（前瞻已到地图尽头）说明信息完整，不额外修正
        if (ev.Paths.Count > 0 && ev.Paths.All(x => x.ReachedEnd))
            ev.RiskScore *= 1.0;
    }

    // =====================================================================
    //  渲染
    // =====================================================================

    /// <summary>
    /// 渲染全部候选的对比段（供 LLM 提示词）。
    /// 输出形如：
    ///   1. 精英战斗  →  后续：精英 → 商店 → 普通 → 休息点 → 普通
    ///       （前瞻 5 层：精英×1 休息×1 商店×1 普通×2；连续精英1；风险中）
    /// </summary>
    public static string Render(IReadOnlyList<CandidateEval> evals, double hpRatio, int floor)
    {
        var sb = new StringBuilder();
        if (evals == null || evals.Count == 0) return "";

        foreach (var ev in evals)
        {
            if (ev.MainPath == null || ev.MainPath.Points.Count == 0)
            {
                sb.AppendLine($"  {ev.Index}. {ev.TypeCn}  →  后续：该节点之后已无更多分支");
                continue;
            }

            string chain = string.Join(" → ", ev.MainPath.Points.Select(p => GameState.PointCn(p.PointType)));
            sb.AppendLine($"  {ev.Index}. {ev.TypeCn}  →  后续：{chain}");

            var parts = new List<string>();
            if (ev.MonsterCount > 0) parts.Add($"普通×{ev.MonsterCount}");
            if (ev.EliteCount > 0) parts.Add($"精英×{ev.EliteCount}");
            if (ev.EventCount > 0) parts.Add($"事件×{ev.EventCount}");
            if (ev.ShopCount > 0) parts.Add($"商店×{ev.ShopCount}");
            if (ev.TreasureCount > 0) parts.Add($"宝箱×{ev.TreasureCount}");
            if (ev.RestCount > 0) parts.Add($"休息×{ev.RestCount}");

            string risk = ev.RiskScore switch
            {
                >= 40 => "高（精英压力大）",
                >= 15 => "中",
                <= -15 => "低（有回复/收益）",
                _ => "中低",
            };
            string streak = ev.MaxEliteStreak >= 2 ? $"，连续精英{ev.MaxEliteStreak}" : "";

            sb.AppendLine($"      （前瞻 {ev.LookaheadRows} 层：{(parts.Count == 0 ? "无房间" : string.Join(" ", parts))}{streak}；风险{risk}）");

            // 分叉汇合点：告诉模型 A、B 在哪一层重新合流（避免重复权衡）
            if (evals.Count > 1 && ev.Index < evals.Count)
            {
                try
                {
                    var other = evals[ev.Index];   // 下一个候选（1-based → 0-based 即 Index）
                    if (other?.Point != null)
                    {
                        var meet = ev.Point.GetFirstCommonDescendant(other.Point);
                        if (meet != null && meet.coord.row > ev.Point.coord.row)
                        {
                            int gap = meet.coord.row - ev.Point.coord.row;
                            sb.AppendLine($"      与选项{other.Index}在第 {gap} 层后于[{GameState.PointCn(meet.PointType)}]汇合");
                        }
                    }
                }
                catch { }
            }
        }
        return sb.ToString();
    }

    /// <summary>把前瞻指标折算成本地启发式的路径加成（与类型分同阶）。</summary>
    public static double PathBonus(CandidateEval ev, double hpRatio, int floor)
    {
        if (ev == null) return 0;
        double s = 0;

        // 休息点：血越少越值钱
        if (ev.RestCount > 0) s += ev.RestCount * (hpRatio < 0.6 ? 30.0 : 16.0);
        // 宝箱免费遗物
        s += ev.TreasureCount * 12.0;
        // 商店：金币够才值钱（调用方已按金币调过即时分，这里只做路径加分）
        s += ev.ShopCount * 6.0;
        // 精英：与即时分一致的阈值逻辑，但按"路径里有多少个"计
        bool healthy = hpRatio * 100 >= PilotConfig.Current.EliteHpPercent;
        if (ev.EliteCount > 0)
            s += healthy ? ev.EliteCount * 20.0 : -ev.EliteCount * 26.0;
        // 连续精英额外罚（容易连战掉血）
        if (ev.MaxEliteStreak >= 2) s -= (ev.MaxEliteStreak - 1) * 22.0;
        // 深幕残血额外保守
        if (floor > 30 && hpRatio < 0.5 && ev.EliteCount > 0) s -= 18.0;

        return s;
    }
}
