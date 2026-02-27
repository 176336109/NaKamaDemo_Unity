using System;
using System.Threading.Tasks;
using Nakama;
using UnityEngine;

/// <summary>
/// Nakama 排行榜 DEMO
/// 演示如何创建、提交分数、查询排行榜、查询自身排名、翻页。
///
/// ⚠️ 前提：需在 Nakama 服务端控制台（或 config）中预先创建排行榜。
///    Console → Leaderboards → Add，填写：
///      ID       : weekly_score
///      Operator : best   （保留最高分）
///      Sort     : desc   （降序，分越高排越前）
///      Reset    : 0 0 * * 1  （每周一 00:00 重置，可选）
///
/// 使用方式：
///   1. 确保场景中已有挂载 Connector.cs 的 GameObject（负责登录）。
///   2. 将本脚本挂载到任意 GameObject 上。
///   3. 登录成功后自动执行 RunDemoAsync()。
/// </summary>
public class LeaderboardDemo : MonoBehaviour
{
    // ─── 排行榜常量 ────────────────────────────────────────────────────────
    /// <summary>排行榜 ID，需与服务端创建时一致</summary>
    private const string LeaderboardId = "weekly_score";

    /// <summary>每页显示条数</summary>
    private const int PageSize = 10;

    // ─── Unity 生命周期 ────────────────────────────────────────────────────
    private void OnEnable()
    {
        Connector.OnLoginSuccess += HandleLoginSuccess;
    }

    private void OnDisable()
    {
        Connector.OnLoginSuccess -= HandleLoginSuccess;
    }

    private void HandleLoginSuccess(ISession session)
    {
        _ = RunDemoAsync();
    }

    // ─── DEMO 主流程 ───────────────────────────────────────────────────────
    /// <summary>
    /// 依次演示：提交分数 → 查询全局榜 → 查询自身排名 → 查询好友榜
    /// </summary>
    private async Task RunDemoAsync()
    {
        Debug.Log("========== 排行榜 DEMO 开始 ==========");

        // 1. 提交一个分数（模拟本局游戏结算）
        long myScore = UnityEngine.Random.Range(1000, 9999);
        await SubmitScoreAsync(myScore);

        // 2. 查询全局排行榜（第一页，降序）
        await ListTopRecordsAsync();

        // 3. 查询本玩家在榜中的排名与分数
        await GetOwnRankAsync();

        // 4. 查询好友排行榜（仅显示互相关注的好友）
        await ListFriendRecordsAsync();

        Debug.Log("========== 排行榜 DEMO 结束 ==========");
    }

    // ─── 核心排行榜方法 ────────────────────────────────────────────────────

    /// <summary>
    /// 向排行榜提交分数。
    /// Operator 为 best  时：服务端自动保留历史最高分。
    /// Operator 为 incr  时：分数累加。
    /// Operator 为 set   时：直接覆盖。
    /// </summary>
    /// <param name="score">本次得分</param>
    /// <param name="subscore">次级排序分（分数相同时用于二次排序，默认 0）</param>
    /// <param name="metadata">附加元数据 JSON，例如使用的角色、地图等</param>
    public static async Task SubmitScoreAsync(long score, long subscore = 0, string metadata = null)
    {
        try
        {
            IApiLeaderboardRecord record = await Connector.Client.WriteLeaderboardRecordAsync(
                Connector.Session,
                LeaderboardId,
                score,
                subscore,
                metadata
            );

            Debug.Log($"[Leaderboard] 提交成功！" +
                      $"分数={record.Score} | 次级分={record.Subscore} | " +
                      $"排名={record.Rank} | 玩家={record.Username}");
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[Leaderboard] 提交失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Leaderboard] 提交异常：{e.Message}");
        }
    }

    /// <summary>
    /// 查询全局排行榜 Top N 记录（第一页）。
    /// </summary>
    /// <param name="limit">返回条数，默认 PageSize</param>
    public static async Task ListTopRecordsAsync(int limit = PageSize)
    {
        try
        {
            // ownerIds 为空 → 不附带特定玩家记录
            // cursor  为空 → 从第一页开始
            IApiLeaderboardRecordList result = await Connector.Client.ListLeaderboardRecordsAsync(
                Connector.Session,
                LeaderboardId,
                ownerIds: null,
                expiry: null,
                limit: limit,
                cursor: null
            );

            Debug.Log($"[Leaderboard] ===== 全局排行榜 Top {limit} =====");
            PrintRecordList(result);

            // 演示翻页：如果有下一页，继续拉取一次
            if (!string.IsNullOrEmpty(result.NextCursor))
            {
                Debug.Log("[Leaderboard] 检测到下一页，正在拉取…");
                await ListRecordsByPageAsync(result.NextCursor, limit);
            }
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[Leaderboard] 查询全局榜失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Leaderboard] 查询全局榜异常：{e.Message}");
        }
    }

    /// <summary>
    /// 通过 cursor 翻页查询排行榜。
    /// </summary>
    /// <param name="cursor">上一次查询返回的 NextCursor</param>
    /// <param name="limit">返回条数</param>
    public static async Task ListRecordsByPageAsync(string cursor, int limit = PageSize)
    {
        try
        {
            IApiLeaderboardRecordList result = await Connector.Client.ListLeaderboardRecordsAsync(
                Connector.Session,
                LeaderboardId,
                ownerIds: null,
                expiry: null,
                limit: limit,
                cursor: cursor
            );

            Debug.Log($"[Leaderboard] ===== 下一页记录 =====");
            PrintRecordList(result);
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[Leaderboard] 翻页失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Leaderboard] 翻页异常：{e.Message}");
        }
    }

    /// <summary>
    /// 查询本玩家自身在排行榜中的排名（"我在第几名"视图）。
    /// Nakama 会以本玩家记录为中心，返回其上下各若干条。
    /// </summary>
    public static async Task GetOwnRankAsync()
    {
        try
        {
            // ownerIds 传入自己的 UserId → 服务端在结果中附带该玩家记录
            IApiLeaderboardRecordList result = await Connector.Client.ListLeaderboardRecordsAroundOwnerAsync(
                Connector.Session,
                LeaderboardId,
                Connector.Session.UserId,
                expiry: null,
                limit: 5   // 自己上下各 ~2 条
            );

            Debug.Log("[Leaderboard] ===== 我的排名（上下文视图）=====");
            PrintRecordList(result);
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[Leaderboard] 查询自身排名失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Leaderboard] 查询自身排名异常：{e.Message}");
        }
    }

    /// <summary>
    /// 查询好友排行榜（只显示已互相关注的好友 + 自己）。
    /// </summary>
    public static async Task ListFriendRecordsAsync()
    {
        try
        {
            // 先拉取好友列表，获取好友的 UserId
            IApiFriendList friendList = await Connector.Client.ListFriendsAsync(
                Connector.Session,
                state: 0,   // 0 = 已互相关注的好友
                limit: 50,
                cursor: null
            );

            // 收集好友 ID 列表（含自己）
            var ownerIds = new System.Collections.Generic.List<string>
            {
                Connector.Session.UserId  // 加入自己
            };

            foreach (var friend in friendList.Friends)
            {
                ownerIds.Add(friend.User.Id);
            }

            Debug.Log($"[Leaderboard] 好友数量（含自己）：{ownerIds.Count}");

            // 用 ownerIds 过滤，只返回这些玩家的记录
            IApiLeaderboardRecordList result = await Connector.Client.ListLeaderboardRecordsAsync(
                Connector.Session,
                LeaderboardId,
                ownerIds: ownerIds,
                expiry: null,
                limit: PageSize,
                cursor: null
            );

            Debug.Log("[Leaderboard] ===== 好友排行榜 =====");
            PrintRecordList(result);
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[Leaderboard] 查询好友榜失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Leaderboard] 查询好友榜异常：{e.Message}");
        }
    }

    /// <summary>
    /// 删除本玩家在排行榜中的记录（退出比赛 / 注销账号时使用）。
    /// </summary>
    public static async Task DeleteOwnRecordAsync()
    {
        try
        {
            await Connector.Client.DeleteLeaderboardRecordAsync(
                Connector.Session,
                LeaderboardId
            );
            Debug.Log("[Leaderboard] 已删除本玩家的排行榜记录");
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[Leaderboard] 删除记录失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Leaderboard] 删除记录异常：{e.Message}");
        }
    }

    // ─── 辅助打印 ──────────────────────────────────────────────────────────
    private static void PrintRecordList(IApiLeaderboardRecordList list)
    {
        if (list?.Records == null)
        {
            Debug.Log("[Leaderboard] 暂无记录");
            return;
        }

        int count = 0;
        foreach (var r in list.Records)
        {
            count++;
            // 高亮标记自己的记录
            bool isMe = r.OwnerId == Connector.Session.UserId;
            string tag = isMe ? " ◀ 我" : "";

            Debug.Log($"[Leaderboard] #{r.Rank,3}  {r.Username,-16} " +
                      $"分数={r.Score,8}  次级分={r.Subscore,6}  " +
                      $"更新时间={r.UpdateTime}{tag}");
        }

        if (count == 0)
        {
            Debug.Log("[Leaderboard] 暂无记录");
        }
        else
        {
            string pageInfo = string.IsNullOrEmpty(list.NextCursor) ? "无下一页" : "有下一页";
            Debug.Log($"[Leaderboard] 共 {count} 条，{pageInfo}。PrevCursor={(string.IsNullOrEmpty(list.PrevCursor) ? "无" : "有")}");
        }
    }
}
