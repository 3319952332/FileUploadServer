-- ============================================
-- 文件上传服务器权限系统升级脚本
-- 版本: v2.0
-- 用途: 添加密钥类型和文件关联字段
-- ============================================

-- 1. 为 ApiKeys 表新增 KeyType 字段 (使用TEXT类型以兼容)
ALTER TABLE "ApiKeys" ADD COLUMN IF NOT EXISTS "KeyType" TEXT DEFAULT 'Admin';

-- 2. 为 Files 表新增 ApiKeyId 字段
ALTER TABLE "Files" ADD COLUMN IF NOT EXISTS "ApiKeyId" INTEGER;

-- 3. 添加外键约束
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints
                   WHERE constraint_name = 'FK_Files_ApiKeys_ApiKeyId') THEN
        ALTER TABLE "Files"
        ADD CONSTRAINT "FK_Files_ApiKeys_ApiKeyId"
        FOREIGN KEY ("ApiKeyId") REFERENCES "ApiKeys"("Id") ON DELETE SET NULL;
    END IF;
END$$;

-- 4. 创建索引提高查询性能
CREATE INDEX IF NOT EXISTS "IX_Files_ApiKeyId" ON "Files"("ApiKeyId");

-- 5. 为现有密钥设置默认类型 (Admin)
UPDATE "ApiKeys" SET "KeyType" = 'Admin' WHERE "KeyType" IS NULL;

-- ============================================
-- 升级完成！
-- ============================================
