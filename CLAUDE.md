# CLAUDE.md — FileUploadServer 项目导航与规则

## 项目一句话

带分级权限、透明加密、分布式存储的文件服务器（ASP.NET Core 10 + PostgreSQL）。
架构组成：网关 Web / WS 存储节点 / MCP 接口 / Core + Infrastructure。

## 文档索引（所有详细文档在 doc/，按此索引进具体文件阅读）

| 文档 | 内容 | AI 何时阅读 |
|------|------|------------|
| [doc/00-overview.md](doc/00-overview.md) | 项目总览、功能特点、快速开始、配置参考 | 首次进入项目 |
| [doc/01-architecture.md](doc/01-architecture.md) | 项目框架接口图、分层结构、中间件管线、协议 | 理解整体架构 |
| [doc/02-api-reference.md](doc/02-api-reference.md) | HTTP API 完整参考（23 端点） | 调用 / 实现 API |
| [doc/03-permission.md](doc/03-permission.md) | 分级权限细案（Admin/Temporary Key） | 涉及权限 / 密钥 |
| [doc/04-encryption.md](doc/04-encryption.md) | 文件存储加密细案（AES-256-GCM） | 涉及加解密 |
| [doc/05-key-management.md](doc/05-key-management.md) | 密钥生命周期细案（轮换 / 恢复口令） | 涉及密钥管理 |
| [doc/06-public-access.md](doc/06-public-access.md) | 公共访问细案（当前已屏蔽待整改） | 涉及 /p/ 匿名访问 |
| [doc/07-ws-storage.md](doc/07-ws-storage.md) | WS 分布式存储细案（网关 + 存储节点） | 涉及 WS 节点 / 存储 |
| [doc/08-mcp.md](doc/08-mcp.md) | MCP 接口细案（6 个工具） | 涉及 MCP 工具 |
| [doc/09-rate-limit.md](doc/09-rate-limit.md) | 限流与安全细案 | 涉及限流 / 安全 |
| [doc/10-cli-tools.md](doc/10-cli-tools.md) | CLI 运维工具细案 | 使用 CLI 命令 |
| [doc/11-deployment.md](doc/11-deployment.md) | 部署运维指南 | 部署 / 运维 |
| [doc/12-bug-tracker.md](doc/12-bug-tracker.md) | 踩坑记录 | 排查问题 |
| [doc/13-mcp-baseline.md](doc/13-mcp-baseline.md) | MCP 通用开发规范模板 | MCP 开发参考 |
| [doc/14-dev-log.md](doc/14-dev-log.md) | 开发日志（按日期追加） | 了解开发历史 |

## Skills 索引

| Skill | 用途 | 位置 |
|-------|------|------|
| deploy-file-upload-server | 完整部署工作流（网关 + WS 节点 + MCP） | `.claude/skills/deploy-file-upload-server/SKILL.md` |
| file-upload-server | 文件服务器 API 使用指南 | `.claude/skills/file-upload-server/SKILL.md` |
| file-upload-server-mcp | MCP 接入 Claude Code 配置 | `.claude/skills/file-upload-server-mcp/SKILL.md` |

## 关键规则（必须遵守）

1. ⚠️ 数据库**只用 PostgreSQL，绝不使用 SQLite**
2. 部署前必须 `git` 提交并推送（记录变更、可追溯、可回退）
3. 只上传编译产物到服务器，绝不上传源码
4. 提交 / 部署前检查敏感信息（API Key、加密密钥、密码、连接串），不落入文档或提交
5. 所有详细文档放 `doc/`，命名遵循 `<NN>-<topic>.md`（NN=2位数字序号，topic=kebab-case）；新增文档先查 doc 是否已有对应文档，**优先更新而非新建**
6. 修改代码后同步更新受影响的 `doc/` 功能文档；**CLAUDE.md 不承载实现细节**，细节一律进具体文件
7. 遇到具体问题：先看本索引定位文档 → 进对应 `doc/` 文档 + 源码文件精读

## AI 工作流

- **首次进入项目**：读 [doc/00-overview.md](doc/00-overview.md) 了解全貌。
- **具体任务**：按文档索引定位对应 doc + 进源码文件精读，不在 CLAUDE.md 中查找实现细节。
