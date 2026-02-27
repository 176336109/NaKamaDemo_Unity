-- data/modules/product_catalog.lua
-- ─────────────────────────────────────────────────────────────────────────────
-- 【IAP 三端一致性】商品配置表 + 收据验证 RPC
--
-- 设计原则：
--   1. CATALOG 是唯一真相源 —— 商品名称、图标、奖励内容只维护在此处
--   2. get_product_catalog RPC —— 客户端拉取后用于 UI 展示
--   3. purchase_validate RPC   —— 验证收据后从 CATALOG 查询奖励并发放
--   结果：Apple/Google 后台只需配置 product_id + 价格，UI 展示和道具发放
--         均以此文件为准，新增/修改商品无需客户端发版
-- ─────────────────────────────────────────────────────────────────────────────
local nk = require("nakama")

-- ─────────────────────────────────────────────────────────────────────────────
-- 商品配置表（唯一真相源）
-- product_id 必须与 Apple/Google 后台完全一致（字节级精确匹配）
-- ─────────────────────────────────────────────────────────────────────────────
local CATALOG = {
    ["com.game.coins_100"] = {
        display_name  = "金币 x100",
        description   = "小包金币",
        icon          = "icon_coins_small",
        reward_type   = "coins",
        reward_amount = 100,
        product_type  = "consumable",
    },
    ["com.game.coins_500"] = {
        display_name  = "金币 x500",
        description   = "中包金币（超值）",
        icon          = "icon_coins_medium",
        reward_type   = "coins",
        reward_amount = 500,
        product_type  = "consumable",
    },
    ["com.game.vip_month"] = {
        display_name  = "月卡 VIP",
        description   = "30天VIP特权",
        icon          = "icon_vip",
        reward_type   = "vip_days",
        reward_amount = 30,
        product_type  = "non_consumable",
    },
}

local PURCHASE_COLLECTION = "purchase_records"

-- ─────────────────────────────────────────────────────────────────────────────
-- RPC 1: get_product_catalog
-- 客户端调用：拉取完整配置表，用于 UI 展示（ProductCatalog.BuildAsync）
-- ─────────────────────────────────────────────────────────────────────────────
local function get_product_catalog(context, payload)
    return nk.json_encode({ products = CATALOG })
end
nk.register_rpc(get_product_catalog, "get_product_catalog")

-- ─────────────────────────────────────────────────────────────────────────────
-- RPC 2: purchase_validate
-- 客户端调用：验证收据 + 从 CATALOG 查询奖励内容 + 发放道具
-- 参数：{ platform: "apple"|"google", receipt: "...", product_id: "..." }
-- 返回：{ success: bool, duplicate: bool, granted: {type, amount}, error: "..." }
-- ─────────────────────────────────────────────────────────────────────────────
local function validate_purchase(context, payload)
    local data       = nk.json_decode(payload)
    local platform   = data.platform
    local receipt    = data.receipt
    local product_id = data.product_id

    -- 1. 查询 CATALOG（与 UI 展示用同一份数据，确保一致性）
    local config = CATALOG[product_id]
    if config == nil then
        return nk.json_encode({ success = false, error = "unknown product: " .. tostring(product_id) })
    end

    -- 2. 平台收据验证
    local _, err
    if platform == "apple" then
        _, err = nk.purchase_validate_apple(context.user_id, receipt, true)
    elseif platform == "google" then
        _, err = nk.purchase_validate_google(context.user_id, receipt, true)
    else
        return nk.json_encode({ success = false, error = "unsupported platform: " .. tostring(platform) })
    end

    if err ~= nil then
        return nk.json_encode({ success = false, error = "receipt invalid: " .. tostring(err) })
    end

    -- 3. 幂等性检查（防重放）
    local receipt_hash = nk.md5_hash(receipt)
    local existing = nk.storage_read({
        { collection = PURCHASE_COLLECTION, key = receipt_hash, user_id = context.user_id }
    })
    if #existing > 0 then
        -- 已发放，幂等返回成功，不重复发放
        return nk.json_encode({
            success   = true,
            duplicate = true,
            granted   = { type = config.reward_type, amount = config.reward_amount }
        })
    end

    -- 4. 写入购买记录（仅服务端可写，防止客户端篡改）
    nk.storage_write({
        {
            collection       = PURCHASE_COLLECTION,
            key              = receipt_hash,
            user_id          = context.user_id,
            value            = { product_id = product_id, granted_at = os.time() },
            permission_read  = 1,
            permission_write = 0,
        }
    })

    -- 5. 按 CATALOG 发放道具（唯一真相源，UI 展示和发放逻辑完全一致）
    if config.reward_type == "coins" then
        nk.wallet_update({
            {
                user_id   = context.user_id,
                changeset = { coins = config.reward_amount },
                metadata  = { source = "iap", product_id = product_id }
            }
        })
    elseif config.reward_type == "vip_days" then
        nk.storage_write({
            {
                collection       = "player_entitlements",
                key              = "vip",
                user_id          = context.user_id,
                value            = { days = config.reward_amount, granted_at = os.time() },
                permission_read  = 1,
                permission_write = 0,
            }
        })
    end
    -- 可在此扩展更多 reward_type（items、gems 等）

    return nk.json_encode({
        success   = true,
        duplicate = false,
        granted   = { type = config.reward_type, amount = config.reward_amount }
    })
end
nk.register_rpc(validate_purchase, "purchase_validate")
