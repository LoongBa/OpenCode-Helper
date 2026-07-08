# OpenCode-Helper 助手

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)]()

OpenCode 助手 TUI 工具 — 在终端中浏览、搜索、批量删除、备份 OpenCode 会话。

## 快速上手

```bash
# 直接启动 TUI 交互界面
OpenCode-Helper.exe

# 仅执行全库备份
OpenCode-Helper.exe --backup-only

# 删除指定日期前的所有会话
OpenCode-Helper.exe purge-before 2026-06-01

# 执行数据库收缩
OpenCode-Helper.exe 
```

## 功能概览

- **交互式 TUI** — 终端表格展示会话列表，键盘全操作
- **批量选择** — 空格单选 / A 全选 / Shift+方向键区间选
- **模糊搜索** — 按标题、项目路径关键字过滤
- **多维筛选** — 时间范围（近1月/近6月/全部）+ 项目目录
- **会话预览** — 回车查看单条会话完整内容
- **批量删除** — 带二次确认 + 可选自动备份 + 可选 VACUUM
- **备份管理** — 手动备份、查看历史、恢复、清理旧备份
- **数据库收缩** — VACUUM 回收磁盘空间，显示压缩前后对比
- **命令行模式** — 支持脚本自动化（`--backup-only`、`--purge-before`、`--vacuum`）
- **配置持久化** — 自动保存筛选偏好、备份目录等设置

## 技术栈

| 项目  |                              |
| --- | ---------------------------- |
| 语言  | C# (.NET 10)                 |
| 发布  | Native AOT，单文件独立 exe，无需运行时   |
| CLI | System.CommandLine 2.0 beta4 |
| TUI | Spectre.Console 0.50         |
| 数据库 | Microsoft.Data.Sqlite 10.0   |

## 系统要求

- Windows x64
- OpenCode 全局 SQLite 数据库（自动定位到 `%USERPROFILE%\.local\share\opencode\opencode.db`）

## 项目结构

```
OpenCode-Helper/
├── Program.cs              # 入口 + CLI 参数定义
├── Models/
│   ├── AppConfig.cs        # 配置持久化模型
│   ├── AppConfigJsonContext.cs  # AOT JSON 序列化上下文
│   ├── BackupInfo.cs       # 备份文件信息模型
│   └── Session.cs          # 会话数据模型
├── Services/
│   ├── BackupService.cs    # 备份/恢复/清理
│   ├── DatabaseService.cs  # SQLite CRUD + VACUUM
│   ├── LogService.cs       # 日志记录
│   └── TuiService.cs       # TUI 渲染与交互
└── docs/
    └── 设计方案.md          # 详细设计文档
```
