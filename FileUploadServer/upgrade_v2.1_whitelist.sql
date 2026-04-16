-- ============================================
-- IP白名单功能升级脚本
-- 版本: v2.1
-- ============================================

-- 1. 创建IpWhitelists表
CREATE TABLE IF NOT EXISTS "IpWhitelists" (
    "Id" SERIAL PRIMARY KEY,
    "IpAddress" TEXT NOT NULL,
    "Description" TEXT DEFAULT '',
    "CreatedAt" TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    "IsEnabled" BOOLEAN DEFAULT TRUE
);

-- 2. 为IpAddress创建唯一索引
CREATE UNIQUE INDEX IF NOT EXISTS "IX_IpWhitelists_IpAddress" ON "IpWhitelists"("IpAddress");

-- 3. 为IsEnabled创建索引
CREATE INDEX IF NOT EXISTS "IX_IpWhitelists_IsEnabled" ON "IpWhitelists"("IsEnabled");

-- ============================================
-- 升级完成！
-- ============================================
