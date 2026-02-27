using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nakama;
using UnityEngine;

/// <summary>
/// Nakama 云存档 DEMO
/// 演示如何将游戏存档（GameSaveData）写入、读取、删除 Nakama Storage。
///
/// 使用方式：
///   1. 确保场景中已有挂载 Connector.cs 的 GameObject（负责登录）。
///   2. 将本脚本挂载到任意 GameObject 上。
///   3. 运行后，在 Inspector 里通过右键菜单或代码按钮触发操作。
///   4. 登录成功后会自动调用 RunDemoAsync() 依次演示写入、读取、删除。
/// </summary>
public class CloudSaveDemo : MonoBehaviour
{
    // ─── Nakama Storage 常量 ───────────────────────────────────────────────
    /// <summary>集合名称（相当于数据库表名，自定义即可）</summary>
    private const string CollectionName = "game_saves";

    /// <summary>存档的键名</summary>
    private const string SaveKey = "slot_1";

    // ─── 存档数据结构 ──────────────────────────────────────────────────────
    /// <summary>
    /// 游戏存档数据，可根据实际游戏自由扩展字段。
    /// 使用 [Serializable] 以便 JsonUtility 序列化。
    /// </summary>
    [Serializable]
    public class GameSaveData
    {
        public string playerName = "勇者";
        public int level = 1;
        public int hp = 100;
        public int gold = 0;
        public string currentScene = "Village";
        public string saveTime = "";     // 存档时间（UTC）

        public override string ToString() =>
            $"[存档] 玩家:{playerName} | 等级:{level} | HP:{hp} | 金币:{gold} | 场景:{currentScene} | 时间:{saveTime}";
    }

    // ─── Unity 生命周期 ────────────────────────────────────────────────────
    private void OnEnable()
    {
        // 等待 Connector 登录成功后自动执行 DEMO
        Connector.OnLoginSuccess += HandleLoginSuccess;
    }

    private void OnDisable()
    {
        Connector.OnLoginSuccess -= HandleLoginSuccess;
    }

    private void HandleLoginSuccess(ISession session)
    {
        // 登录成功后启动演示
        _ = RunDemoAsync();
    }

    // ─── DEMO 主流程 ───────────────────────────────────────────────────────
    /// <summary>
    /// 顺序演示：写入 → 读取 → 修改并再次写入 → 读取 → 删除 → 验证删除
    /// </summary>
    private async Task RunDemoAsync()
    {
        Debug.Log("========== 云存档 DEMO 开始 ==========");

        // 1. 构造一个初始存档并写入云端
        var saveData = new GameSaveData
        {
            playerName = "勇者小明",
            level = 5,
            hp = 300,
            gold = 1200,
            currentScene = "DungeonB2",
            saveTime = DateTime.UtcNow.ToString("O")
        };
        await WriteAsync(saveData);

        // 2. 从云端读取存档并打印
        var loaded = await ReadAsync();
        if (loaded != null)
        {
            Debug.Log($"读取存档成功：{loaded}");
        }

        // 3. 模拟游戏进度更新后覆盖写入
        saveData.level = 6;
        saveData.gold += 500;
        saveData.saveTime = DateTime.UtcNow.ToString("O");
        await WriteAsync(saveData);

        // 4. 再次读取，验证更新
        loaded = await ReadAsync();
        if (loaded != null)
        {
            Debug.Log($"更新后存档：{loaded}");
        }

        // // 5. 删除存档
        // await DeleteAsync();

        // // 6. 尝试再次读取，应返回 null
        // loaded = await ReadAsync();
        // Debug.Log(loaded == null ? "存档已删除，读取返回空 ✓" : $"删除失败，仍读到：{loaded}");

        Debug.Log("========== 云存档 DEMO 结束 ==========");
    }

    // ─── 核心存储方法 ──────────────────────────────────────────────────────

    /// <summary>
    /// 写入（新增或覆盖）存档到 Nakama Storage。
    /// </summary>
    /// <param name="data">要保存的存档数据</param>
    public static async Task WriteAsync(GameSaveData data)
    {
        try
        {
            // 将存档序列化为 JSON 字符串
            string json = JsonUtility.ToJson(data, prettyPrint: false);

            // 构造写入对象
            // PermissionRead  : 1 = 仅自己可读, 2 = 所有人可读
            // PermissionWrite : 1 = 仅自己可写, 0 = 禁止客户端写（需要服务端）
            var writeObject = new WriteStorageObject
            {
                Collection = CollectionName,
                Key = SaveKey,
                Value = json,
                PermissionRead = 1,   // 仅本玩家可读
                PermissionWrite = 1   // 本玩家可写
            };

            await Connector.Client.WriteStorageObjectsAsync(
                Connector.Session,
                new[] { writeObject }
            );

            Debug.Log($"[CloudSave] 写入成功 → Collection={CollectionName}, Key={SaveKey}");
            Debug.Log($"[CloudSave] 写入内容：{json}");
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[CloudSave] 写入失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[CloudSave] 写入异常：{e.Message}");
        }
    }

    /// <summary>
    /// 从 Nakama Storage 读取存档。
    /// </summary>
    /// <returns>存档数据，读取失败或不存在时返回 null</returns>
    public static async Task<GameSaveData> ReadAsync()
    {
        try
        {
            // 构造读取查询：通过 UserId + Collection + Key 精确定位
            var readObject = new StorageObjectId
            {
                Collection = CollectionName,
                Key = SaveKey,
                UserId = Connector.Session.UserId
            };

            IApiStorageObjects result = await Connector.Client.ReadStorageObjectsAsync(
                Connector.Session,
                new[] { readObject }
            );

            // 遍历返回列表，找到匹配的对象
            foreach (var obj in result.Objects)
            {
                if (obj.Key == SaveKey)
                {
                    Debug.Log($"[CloudSave] 读取成功 → Version={obj.Version}, UpdatedAt={obj.UpdateTime}");
                    Debug.Log($"[CloudSave] 原始 JSON：{obj.Value}");

                    // 反序列化 JSON 为存档结构体
                    return JsonUtility.FromJson<GameSaveData>(obj.Value);
                }
            }

            Debug.Log("[CloudSave] 未找到存档（可能尚未写入或已删除）");
            return null;
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[CloudSave] 读取失败 (HTTP {e.StatusCode})：{e.Message}");
            return null;
        }
        catch (Exception e)
        {
            Debug.LogError($"[CloudSave] 读取异常：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 删除 Nakama Storage 中的存档。
    /// </summary>
    public static async Task DeleteAsync()
    {
        try
        {
            var deleteObject = new StorageObjectId
            {
                Collection = CollectionName,
                Key = SaveKey,
                UserId = Connector.Session.UserId
            };

            await Connector.Client.DeleteStorageObjectsAsync(
                Connector.Session,
                new[] { deleteObject }
            );

            Debug.Log($"[CloudSave] 删除成功 → Collection={CollectionName}, Key={SaveKey}");
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[CloudSave] 删除失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[CloudSave] 删除异常：{e.Message}");
        }
    }

    /// <summary>
    /// 列出当前玩家在指定集合下的所有存档（适用于多存档槽位场景）。
    /// </summary>
    /// <param name="limit">每次最多返回条数，默认 10</param>
    public static async Task ListAllSavesAsync(int limit = 10)
    {
        try
        {
            // ListStorageObjects 列出自己在某个 Collection 下的所有 Key
            IApiStorageObjectList result = await Connector.Client.ListStorageObjectsAsync(
                Connector.Session,
                CollectionName,
                limit
            );

            if (result.Objects == null)
            {
                Debug.Log("[CloudSave] 该集合下暂无存档");
                return;
            }

            int count = 0;
            foreach (var obj in result.Objects)
            {
                count++;
                Debug.Log($"[CloudSave] 存档{count}：Key={obj.Key}, Version={obj.Version}, " +
                          $"UpdatedAt={obj.UpdateTime}, Value={obj.Value}");
            }
            Debug.Log($"[CloudSave] 共查询到 {count} 条存档" +
                      (string.IsNullOrEmpty(result.Cursor) ? "（无更多）" : "（有下一页）"));
        }
        catch (ApiResponseException e)
        {
            Debug.LogError($"[CloudSave] 列出存档失败 (HTTP {e.StatusCode})：{e.Message}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[CloudSave] 列出存档异常：{e.Message}");
        }
    }
}
