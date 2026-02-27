using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nakama;
using UnityEngine;
using UnityEngine.Purchasing;

/// <summary>
/// IAP 内购管理器（Unity IAP v5）。
/// 职责：初始化 Unity IAP → 发起购买 → 将收据交给 Nakama 服务端验证 → 确认发放。
///
/// 使用方式：
///   1. 将此脚本挂载到场景中的 GameObject（建议与 Connector 同一 GameObject）。
///   2. 在 Inspector 中配置 <see cref="productDefinitions"/>。
///   3. 确保场景中已有 Connector 且登录完成后再调用 <see cref="BuyProduct"/>。
/// </summary>
public class IAPManager : MonoBehaviour
{
    // ──────────────────────────────────────────────
    // Inspector 配置
    // ──────────────────────────────────────────────

    [Header("商品定义（在 Inspector 中配置）")]
    [SerializeField] private List<ProductDefinitionData> productDefinitions = new()
    {
        new() { productId = "com.game.coins_100",  productType = ProductType.Consumable    },
        new() { productId = "com.game.coins_500",  productType = ProductType.Consumable    },
        new() { productId = "com.game.vip_month",  productType = ProductType.NonConsumable },
    };

    // ──────────────────────────────────────────────
    // 公开事件
    // ──────────────────────────────────────────────

    /// <summary>IAP 初始化成功（Connect + FetchProducts + FetchPurchases 均完成）</summary>
    public static event Action OnIAPReady;

    /// <summary>IAP 连接到商店失败</summary>
    public static event Action<StoreConnectionFailureDescription> OnIAPConnectFailed;

    /// <summary>商品列表拉取失败</summary>
    public static event Action<ProductFetchFailed> OnIAPProductsFetchFailed;

    /// <summary>购买并服务端验证全部完成（无论成功/失败均触发）</summary>
    public static event Action<PurchaseResult> OnPurchaseFinished;

    // ──────────────────────────────────────────────
    // 单例 & 状态
    // ──────────────────────────────────────────────

    public static IAPManager Instance { get; private set; }

    /// <summary>IAP 系统是否已就绪</summary>
    public static bool IsReady { get; private set; }

    // IAP v5：通过 UnityIAPServices 访问 StoreController
    private StoreController _store;

    // Nakama 服务端 RPC 名称（与 iap.lua 中注册名一致）
    private const string RpcPurchaseValidate = "purchase_validate";

    // ──────────────────────────────────────────────
    // 生命周期
    // ──────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        // Connector 登录完成后再初始化 IAP（确保 Session 可用）
        Connector.OnLoginSuccess += HandleLoginSuccess;
    }

    private void OnDisable()
    {
        Connector.OnLoginSuccess -= HandleLoginSuccess;
        UnregisterStoreEvents();
    }

    private void HandleLoginSuccess(ISession session) { InitializeIAP(); }

    // ──────────────────────────────────────────────
    // IAP 初始化（v5：Connect 为 async，FetchProducts/FetchPurchases 为 void）
    // ──────────────────────────────────────────────

    private async void InitializeIAP()
    {
        if (IsReady) { Debug.Log("[IAP] 已初始化，跳过。"); return; }

        _store = UnityIAPServices.StoreController();
        RegisterStoreEvents();

        Debug.Log("[IAP] 正在连接商店...");
        await _store.Connect();

        // FetchProducts / FetchPurchases 结果通过事件回调返回
        Debug.Log("[IAP] 正在拉取商品...");
        var defs = new List<ProductDefinition>();
        foreach (var d in productDefinitions)
            defs.Add(new ProductDefinition(d.productId, d.productType));
        _store.FetchProducts(defs);   // void，结果通过 OnProductsFetched / OnProductsFetchFailed 返回
    }

    private void RegisterStoreEvents()
    {
        if (_store == null) return;
        _store.OnStoreDisconnected   += HandleStoreDisconnected;
        _store.OnProductsFetched     += HandleProductsFetched;
        _store.OnProductsFetchFailed += HandleProductsFetchFailed;
        _store.OnPurchasePending     += HandlePurchasePending;
        _store.OnPurchaseFailed      += HandlePurchaseFailed;
        _store.OnPurchaseConfirmed   += HandlePurchaseConfirmed;
    }

    private void UnregisterStoreEvents()
    {
        if (_store == null) return;
        _store.OnStoreDisconnected   -= HandleStoreDisconnected;
        _store.OnProductsFetched     -= HandleProductsFetched;
        _store.OnProductsFetchFailed -= HandleProductsFetchFailed;
        _store.OnPurchasePending     -= HandlePurchasePending;
        _store.OnPurchaseFailed      -= HandlePurchaseFailed;
        _store.OnPurchaseConfirmed   -= HandlePurchaseConfirmed;
    }

    // ──────────────────────────────────────────────
    // 商店事件处理
    // ──────────────────────────────────────────────

    private void HandleStoreDisconnected(StoreConnectionFailureDescription desc)
    {
        Debug.LogError($"[IAP] 商店连接失败: {desc.message}");
        OnIAPConnectFailed?.Invoke(desc);
    }

    private async void HandleProductsFetched(List<Product> products)
    {
        Debug.Log($"[IAP] 商品拉取成功，共 {products.Count} 个。");

        // 从服务端拉取 CATALOG 并与平台价格合并（三端一致性）
        // 合并完成后 ProductCatalog.Products 即可供 UI 直接使用
        if (Connector.Client != null && Connector.Session != null)
        {
            await ProductCatalog.BuildAsync(
                Connector.Client,
                Connector.Session,
                _store.GetProducts());
        }
        else
        {
            Debug.LogWarning("[IAP] Nakama 未登录，跳过 ProductCatalog 合并（将使用纯平台数据）。");
        }

        IsReady = true;
        OnIAPReady?.Invoke();
    }

    private void HandleProductsFetchFailed(ProductFetchFailed failed)
    {
        Debug.LogError($"[IAP] 商品拉取失败: {failed.FailureReason} - {failed.FailedFetchProducts.Count} 个商品");
        OnIAPProductsFetchFailed?.Invoke(failed);
    }

    /// <summary>
    /// v5 替代 ProcessPurchase：平台支付成功后触发，订单处于 Pending 状态。
    /// 必须在服务端验证通过后调用 _store.ConfirmPurchase(order) 才会真正消费。
    /// </summary>
    private void HandlePurchasePending(PendingOrder order)
    {
        var product = order.CartOrdered.Items().FirstOrDefault()?.Product;
        Debug.Log($"[IAP] 平台支付成功，等待服务端验证: {product?.definition.id}");
        _ = ValidateWithNakamaAsync(order);
    }

    private void HandlePurchaseFailed(FailedOrder order)
    {
        var product   = order.CartOrdered.Items().FirstOrDefault()?.Product;
        var productId = product?.definition.id ?? "unknown";
        Debug.LogWarning($"[IAP] 购买失败: {productId}, reason={order.FailureReason}");
        OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId, order.FailureReason.ToString()));
    }

    private void HandlePurchaseConfirmed(Order order)
    {
        // ConfirmPurchase 结果在此回调，仅用于日志记录
        var product   = order.CartOrdered.Items().FirstOrDefault()?.Product;
        var productId = product?.definition.id ?? "unknown";
        switch (order)
        {
            case ConfirmedOrder:
                Debug.Log($"[IAP] 订单确认成功: {productId}");
                break;
            case FailedOrder failedOrder:
                Debug.LogWarning($"[IAP] 订单确认失败: {productId}, reason={failedOrder.FailureReason}");
                break;
        }
    }

    // ──────────────────────────────────────────────
    // 公开购买接口
    // ──────────────────────────────────────────────

    /// <summary>发起商品购买。</summary>
    public void BuyProduct(string productId)
    {
        if (!IsReady)
        {
            Debug.LogWarning("[IAP] IAP 尚未就绪，无法购买。");
            OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId, "IAP not ready."));
            return;
        }
        // v5: GetProducts() 返回列表，用 LINQ 查找
        var product = _store.GetProducts().FirstOrDefault(p => p.definition.id == productId);
        if (product == null || !product.availableToPurchase)
        {
            Debug.LogWarning($"[IAP] 商品不可购买: {productId}");
            OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId, "Product not available."));
            return;
        }
        Debug.Log($"[IAP] 发起购买: {productId}");
        _store.PurchaseProduct(product);   // v5: PurchaseProduct
    }

    /// <summary>查询商品本地价格（初始化成功后可用）。</summary>
    public string GetLocalizedPrice(string productId)
    {
        if (!IsReady) return "--";
        var product = _store.GetProducts().FirstOrDefault(p => p.definition.id == productId);
        return product?.metadata.localizedPriceString ?? "--";
    }

    /// <summary>
    /// 恢复购买（iOS 合规要求必须提供此入口）。
    /// v5 中使用 RestoreTransactions(callback)。
    /// </summary>
    public void RestorePurchases()
    {
        if (!IsReady) { Debug.LogWarning("[IAP] IAP 尚未就绪。"); return; }
        Debug.Log("[IAP] 正在恢复购买...");
        _store.RestoreTransactions(OnTransactionsRestored);
    }

    private void OnTransactionsRestored(bool success, string error)
    {
        if (success)
            Debug.Log("[IAP] 恢复购买成功。");
        else
            Debug.LogWarning($"[IAP] 恢复购买失败: {error}");
    }

    // ──────────────────────────────────────────────
    // Nakama 服务端收据验证
    // ──────────────────────────────────────────────

    /// <summary>
    /// 将收据发送到 Nakama 服务端 RPC 进行验证，验证通过后再 ConfirmPurchase。
    /// </summary>
    private async Task ValidateWithNakamaAsync(PendingOrder order)
    {
        var product   = order.CartOrdered.Items().FirstOrDefault()?.Product;
        var productId = product?.definition.id ?? "unknown";

        if (Connector.Client == null || Connector.Session == null)
        {
            Debug.LogError("[IAP] Nakama 未登录，无法验证收据。");
            OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId, "Nakama session not available."));
            return;
        }

        var platform = GetPlatformString();

        // v5：通过 order.Info 获取平台专属收据
        // Apple：JWS Representation（StoreKit 2）
        // Android：Google 收据通过 product.receipt 获取（v5 暂无专属接口）
        // Editor / PC：留空，服务端沙盒处理
        var receipt = "";
#if UNITY_IOS
        receipt = order.Info.Apple?.jwsRepresentation ?? order.Info.Apple?.AppReceipt ?? "";
#elif UNITY_ANDROID
        // Google v5 暂时仍使用 Product.receipt（Base64 encoded JSON）
#pragma warning disable CS0618
        receipt = order.CartOrdered.Items().FirstOrDefault()?.Product?.receipt ?? "";
#pragma warning restore CS0618
#endif

        var payload = JsonUtility.ToJson(new PurchaseRequest
        {
            platform   = platform,
            receipt    = receipt,
            product_id = productId,
        });
        Debug.Log($"[IAP] 发送收据验证 RPC，platform={platform}, product={productId}");

        try
        {
            var response = await Connector.Client.RpcAsync(
                Connector.Session, RpcPurchaseValidate, payload);

            var result = JsonUtility.FromJson<PurchaseResponse>(response.Payload);
            if (result.success)
            {
                Debug.Log($"[IAP] 服务端验证通过！product={productId}, duplicate={result.duplicate}");
                _store.ConfirmPurchase(order);   // v5: ConfirmPurchase(PendingOrder)
                OnPurchaseFinished?.Invoke(PurchaseResult.Success(productId, result.duplicate, result.granted));
            }
            else
            {
                Debug.LogError($"[IAP] 服务端拒绝: {result.error}");
                // 不确认 → SDK 下次启动时重新触发 OnPurchasePending
                OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId, result.error));
            }
        }
        catch (ApiResponseException apiEx)
        {
            Debug.LogError($"[IAP] Nakama RPC 错误: status={apiEx.StatusCode}, msg={apiEx.Message}");
            OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId,
                $"RPC error {apiEx.StatusCode}: {apiEx.Message}"));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[IAP] 收据验证异常: {ex.Message}");
            OnPurchaseFinished?.Invoke(PurchaseResult.Failure(productId, ex.Message));
        }
    }

    /// <summary>获取当前平台字符串，与 Nakama 服务端 Lua 约定一致。</summary>
    private static string GetPlatformString()
    {
#if UNITY_IOS
        return "apple";
#elif UNITY_ANDROID
        return "google";
#else
        return "apple";
#endif
    }

    // ──────────────────────────────────────────────
    // 嵌套数据结构
    // ──────────────────────────────────────────────

    [Serializable]
    public class ProductDefinitionData
    {
        public string      productId;
        public ProductType productType = ProductType.Consumable;
    }

    [Serializable]
    private class PurchaseRequest
    {
        public string platform;
        public string receipt;
        public string product_id;
    }

    [Serializable]
    private class PurchaseResponse
    {
        public bool   success;
        public bool   duplicate;
        public string error;
        public string granted;
    }

    public class PurchaseResult
    {
        public bool   IsSuccess    { get; private set; }
        public string ProductId    { get; private set; }
        public bool   IsDuplicate  { get; private set; }
        public string GrantedJson  { get; private set; }
        public string ErrorMessage { get; private set; }

        private PurchaseResult() { }

        internal static PurchaseResult Success(string productId, bool duplicate, string grantedJson) =>
            new() { IsSuccess = true,  ProductId = productId, IsDuplicate = duplicate, GrantedJson = grantedJson };

        internal static PurchaseResult Failure(string productId, string error) =>
            new() { IsSuccess = false, ProductId = productId, ErrorMessage = error };

        public override string ToString() =>
            IsSuccess
                ? $"[SUCCESS] {ProductId} duplicate={IsDuplicate} granted={GrantedJson}"
                : $"[FAILURE] {ProductId} error={ErrorMessage}";
    }
}
