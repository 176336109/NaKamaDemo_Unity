using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Nakama;
using UnityEngine;
using UnityEngine.Purchasing;

/// <summary>
/// 合并后的商品信息：
///   - 服务端字段（来自 Nakama CATALOG，可热更无需发版）
///   - 平台字段（来自 Apple/Google，客户端无法伪造）
/// </summary>
public class MergedProduct
{
    // ── 来自 Nakama 服务端 CATALOG ──────────────────────────

    /// <summary>商品 ID（与 Apple/Google 后台、服务端 CATALOG 三端一致）</summary>
    public string ProductId     { get; set; }

    /// <summary>商品显示名称（服务端配置，可热更）</summary>
    public string DisplayName   { get; set; }

    /// <summary>商品描述（服务端配置，可热更）</summary>
    public string Description   { get; set; }

    /// <summary>图标资源 key（服务端配置，客户端按 key 加载 Sprite）</summary>
    public string Icon          { get; set; }

    /// <summary>奖励类型（"coins" / "vip_days" 等）</summary>
    public string RewardType    { get; set; }

    /// <summary>奖励数量</summary>
    public int    RewardAmount  { get; set; }

    // ── 来自 Apple / Google 平台（Unity IAP Product.metadata）────

    /// <summary>本地化价格字符串，如 "¥6.00"（由平台决定，不可客户端伪造）</summary>
    public string  LocalizedPrice      { get; set; }

    /// <summary>精确价格数值</summary>
    public decimal PriceDecimal        { get; set; }

    /// <summary>ISO 货币代码，如 "CNY"</summary>
    public string  IsoCurrencyCode     { get; set; }

    /// <summary>当前是否可购买（平台上架状态）</summary>
    public bool    AvailableToPurchase { get; set; }
}

/// <summary>
/// 商品目录管理器（三端一致性）。
///
/// 工作原理：
///   1. 通过 RPC "get_product_catalog" 从 Nakama 拉取服务端 CATALOG 配置表。
///   2. 将配置表与 Unity IAP StoreController.GetProducts() 的平台价格合并。
///   3. UI 和购买逻辑统一使用 ProductCatalog.Products，不再各自维护商品数据。
///
/// 调用时机：
///   IAPManager.HandleProductsFetched 之后、触发 OnIAPReady 事件之前。
///   即在 <see cref="BuildAsync"/> 的 await 完成后再 IsReady = true。
/// </summary>
public static class ProductCatalog
{
    // ──────────────────────────────────────────────
    // 公开数据
    // ──────────────────────────────────────────────

    /// <summary>合并后的商品列表，就绪后供 UI 和 IAPManager 使用。</summary>
    public static List<MergedProduct> Products { get; private set; } = new();

    // ──────────────────────────────────────────────
    // Nakama RPC 返回的原始结构（用于 JSON 解析）
    // ──────────────────────────────────────────────

    [Serializable]
    private class ServerProductConfig
    {
        public string display_name;
        public string description;
        public string icon;
        public string reward_type;
        public int    reward_amount;
        public string product_type;   // "consumable" | "non_consumable"
    }

    // ──────────────────────────────────────────────
    // 核心方法
    // ──────────────────────────────────────────────

    /// <summary>
    /// 从 Nakama 拉取商品配置表，与 Unity IAP 平台价格合并，填充 <see cref="Products"/>。
    /// </summary>
    /// <param name="client">Nakama IClient 实例（来自 Connector）</param>
    /// <param name="session">已登录的 ISession（来自 Connector）</param>
    /// <param name="iapProducts">Unity IAP v5 的 GetProducts() 返回值</param>
    public static async Task BuildAsync(
        IClient client,
        ISession session,
        ReadOnlyObservableCollection<Product> iapProducts)
    {
        // ── Step 1: 从服务端拉取 CATALOG ──────────────────────
        var serverConfigs = new Dictionary<string, ServerProductConfig>();
        try
        {
            var rpc = await client.RpcAsync(session, "get_product_catalog", "{}");
            serverConfigs = ParseCatalogPayload(rpc.Payload);
            Debug.Log($"[ProductCatalog] 服务端配置拉取成功，{serverConfigs.Count} 个商品。");
        }
        catch (Exception ex)
        {
            // 降级处理：服务端不可用时，仍用平台商品数据（名称/图标将缺失）
            Debug.LogWarning($"[ProductCatalog] 拉取服务端配置失败，降级使用平台数据: {ex.Message}");
        }

        // ── Step 2: 合并平台价格 + 服务端配置 ─────────────────
        Products.Clear();
        foreach (var p in iapProducts)
        {
            serverConfigs.TryGetValue(p.definition.id, out var cfg);

            Products.Add(new MergedProduct
            {
                // 服务端字段（可热更）
                ProductId           = p.definition.id,
                DisplayName         = cfg?.display_name ?? p.definition.id,
                Description         = cfg?.description  ?? string.Empty,
                Icon                = cfg?.icon          ?? string.Empty,
                RewardType          = cfg?.reward_type   ?? string.Empty,
                RewardAmount        = cfg?.reward_amount ?? 0,

                // 平台字段（由 Apple/Google 决定，不可伪造）
                LocalizedPrice      = p.metadata.localizedPriceString,
                PriceDecimal        = p.metadata.localizedPrice,
                IsoCurrencyCode     = p.metadata.isoCurrencyCode,
                AvailableToPurchase = p.availableToPurchase,
            });
        }

        Debug.Log($"[ProductCatalog] 合并完成，共 {Products.Count} 个商品。");
    }

    /// <summary>根据 product ID 快速查找合并后的商品。</summary>
    public static MergedProduct Find(string productId)
        => Products.Find(p => p.ProductId == productId);

    // ──────────────────────────────────────────────
    // 内部：解析 RPC Payload
    // ──────────────────────────────────────────────

    /// <summary>
    /// 解析服务端 get_product_catalog RPC 的返回 JSON。
    /// 格式：{ "products": { "product_id_1": {...}, "product_id_2": {...} } }
    ///
    /// 注意：Unity 内置 JsonUtility 不支持 Dictionary，此处使用简单的手动解析。
    /// 如果项目已引入 Newtonsoft.Json，可替换为 JsonConvert.DeserializeObject。
    /// </summary>
    private static Dictionary<string, ServerProductConfig> ParseCatalogPayload(string payload)
    {
        var result = new Dictionary<string, ServerProductConfig>();
        if (string.IsNullOrEmpty(payload)) return result;

        // 尝试用 Newtonsoft.Json（推荐），如果没有则降级为空字典
#if NAKAMA_USE_NEWTONSOFT
        try
        {
            var root = Newtonsoft.Json.Linq.JObject.Parse(payload);
            var productsNode = root["products"] as Newtonsoft.Json.Linq.JObject;
            if (productsNode != null)
            {
                foreach (var kv in productsNode)
                {
                    var cfg = kv.Value.ToObject<ServerProductConfig>();
                    if (cfg != null) result[kv.Key] = cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ProductCatalog] JSON 解析失败: {ex.Message}");
        }
#else
        // 降级：没有 Newtonsoft 时，直接跳过解析（价格/类型来自平台，名称空白）
        // 推荐在 Player Settings → Scripting Define Symbols 中添加 NAKAMA_USE_NEWTONSOFT
        Debug.LogWarning("[ProductCatalog] 未定义 NAKAMA_USE_NEWTONSOFT，服务端配置将不被解析。" +
                         " 请在 Player Settings 中添加 Scripting Define Symbol: NAKAMA_USE_NEWTONSOFT");
#endif
        return result;
    }
}
